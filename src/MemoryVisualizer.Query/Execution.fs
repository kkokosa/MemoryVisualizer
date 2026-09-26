namespace MemoryVisualizer.Query

open System
open MemoryVisualizer.Core

exception private ExecutionStopped of QueryStatus

module internal Execution =
    let private literal =
        function
        | Literal.Unsigned value -> QueryValue.Unsigned value
        | Literal.Text value -> QueryValue.Text value
        | Literal.Boolean value -> QueryValue.Boolean value

    let private kind =
        function
        | HeapKind.Small -> "Small"
        | HeapKind.Large -> "Large"
        | HeapKind.Pinned -> "Pinned"
        | HeapKind.Frozen -> "Frozen"
        | HeapKind.Unknown name -> name

    let private property (entity: SelectedEntity) =
        function
        | Property.Address -> QueryValue.Unsigned entity.Address
        | Property.Size -> QueryValue.Unsigned entity.Size
        | Property.Runtime -> QueryValue.Unsigned(uint64 entity.Runtime.Index)
        | Property.Heap -> QueryValue.Unsigned(uint64 entity.Heap)
        | Property.HeapKind -> QueryValue.Text(kind entity.HeapKind)
        | Property.Type ->
            entity.TypeName
            |> Option.map QueryValue.Text
            |> Option.defaultValue QueryValue.Missing
        | Property.MethodTable ->
            entity.TypeIdentity
            |> Option.map (fun value -> QueryValue.Unsigned value.MethodTable)
            |> Option.defaultValue QueryValue.Missing
        | Property.Generation ->
            entity.Generation
            |> Option.map (uint64 >> QueryValue.Unsigned)
            |> Option.defaultValue QueryValue.Missing
        | Property.IsFree ->
            entity.IsFree
            |> Option.map QueryValue.Boolean
            |> Option.defaultValue QueryValue.Missing
        | Property.End ->
            entity.End
            |> Option.map QueryValue.Unsigned
            |> Option.defaultValue QueryValue.Missing

    let private expression entity =
        function
        | BoundExpression.Entity -> QueryValue.Entity entity
        | BoundExpression.Property field -> property entity field
        | BoundExpression.Constant value -> literal value

    let execute limits (plan: QueryPlan) (store: IndexedHeapSnapshot) context =
        ArgumentNullException.ThrowIfNull plan
        let rows = ResizeArray<QueryRow>()
        let directives = ResizeArray<DrawingDirective>()
        let mutable candidates = 0
        let mutable partial = false
        let mutable sourceAvailable = false

        let mutable sourceDiagnostics: Collections.Generic.IReadOnlyList<SnapshotDiagnostic> =
            Array.AsReadOnly [||]

        let mutable status = QueryStatus.Complete

        let failure code span message =
            raise (
                ExecutionStopped(
                    QueryStatus.Failed [
                        {
                            Code = code
                            Span = span
                            Message = message
                        }
                    ]
                )
            )

        let indexResult result =
            match result with
            | Ok value -> value
            | Error error -> failure "MQL302" QueryLimits.origin $"Snapshot store is unavailable: {error}."

        try
            match QueryLimits.validate limits with
            | Error errors -> raise (ExecutionStopped(QueryStatus.Failed errors))
            | Ok() -> ()

            if
                obj.ReferenceEquals(context, null)
                || isNull context.TimeProvider
                || obj.ReferenceEquals(context.IsSnapshotCurrent, null)
            then
                failure "MQL001" QueryLimits.origin "Execution context requires a clock and active-snapshot predicate."

            if obj.ReferenceEquals(store, null) then
                failure "MQL302" QueryLimits.origin "Snapshot store must not be null."

            let started = context.TimeProvider.GetTimestamp()

            let guard () =
                context.CancellationToken.ThrowIfCancellationRequested()

                if not (context.IsSnapshotCurrent plan.Snapshot) then
                    failure
                        "MQL301"
                        QueryLimits.origin
                        "The active snapshot changed; discard this query and compile against the new snapshot."

                if
                    context.TimeProvider.GetElapsedTime(started).TotalMilliseconds
                    >= float limits.MaxElapsedMilliseconds
                then
                    raise (ExecutionStopped(QueryStatus.Truncated [ TruncationReason.ElapsedTime ]))

            let inspect () =
                guard ()

                if candidates = limits.MaxCandidates then
                    raise (ExecutionStopped(QueryStatus.Truncated [ TruncationReason.Candidates ]))

                candidates <- candidates + 1

            guard ()
            let info = store.GetInfo(context.CancellationToken) |> indexResult
            guard ()

            if info.Metadata.Id <> plan.Snapshot then
                failure
                    "MQL301"
                    QueryLimits.origin
                    "Plan belongs to another snapshot; compile against this store's snapshot ID."

            partial <- info.IsPartial
            sourceAvailable <- true
            sourceDiagnostics <- info.Diagnostics

            let rec matches (entity: SelectedEntity) predicate =
                guard ()

                match predicate with
                | BoundPredicate.And predicates -> predicates |> List.forall (matches entity)
                | BoundPredicate.Range(overlaps, range) ->
                    if range.Start = range.End then
                        false
                    elif overlaps then
                        entity.Size > 0UL
                        && entity.Address < range.End
                        && (entity.Address >= range.Start || entity.Size > range.Start - entity.Address)
                    else
                        AddressRange.contains entity.Address range
                | BoundPredicate.Compare(field, operation, value) ->
                    let actual = property entity field

                    if actual = QueryValue.Missing then
                        false
                    else
                        let comparison = compare actual (literal value)

                        match operation with
                        | Comparison.Equal -> comparison = 0
                        | Comparison.NotEqual -> comparison <> 0
                        | Comparison.Less -> comparison < 0
                        | Comparison.LessOrEqual -> comparison <= 0
                        | Comparison.Greater -> comparison > 0
                        | Comparison.GreaterOrEqual -> comparison >= 0

            let checkCapacity addRow addDirective =
                let reasons = [
                    if addRow && rows.Count = limits.MaxResults then
                        TruncationReason.Results
                    if addDirective && directives.Count = limits.MaxDirectives then
                        TruncationReason.Directives
                ]

                if not reasons.IsEmpty then
                    raise (ExecutionStopped(QueryStatus.Truncated reasons))

            let emit
                statement
                (selection: SelectionPlan)
                (presentation: PresentationPlan option)
                (entity: SelectedEntity)
                =
                if selection.Predicate |> Option.forall (matches entity) then
                    checkCapacity true presentation.IsSome

                    let values =
                        selection.Projection
                        |> List.map (fun (name, value) ->
                            guard ()
                            name, expression entity value)

                    let drawing =
                        presentation
                        |> Option.map (fun style ->
                            guard ()

                            {
                                StatementIndex = statement
                                Kind = style.Kind
                                Runtime = entity.Runtime
                                Heap = entity.Heap
                                Address = entity.Address
                                Size = entity.Size
                                Entity = Some entity
                                Label = style.Label |> Option.map (expression entity)
                                LabelPosition = style.LabelPosition
                                Background = style.Background
                                Width = style.Width
                            })

                    guard ()

                    rows.Add {
                        StatementIndex = statement
                        Entity = entity
                        Values = values
                    }

                    drawing |> Option.iter directives.Add

            let segmentEntity (segment: SnapshotSegmentInfo) = {
                Kind = Selector.Segment
                Runtime = segment.Runtime
                Heap = segment.HeapIndex
                SegmentAddress = segment.Address
                Address = segment.ObjectRange.Start
                Size = AddressRange.length segment.ObjectRange
                End = Some segment.ObjectRange.End
                TypeIdentity = None
                TypeName = None
                Generation = None
                IsFree = None
                HeapKind = segment.Kind
            }

            for statementIndex, statement in List.indexed plan.Statements do
                guard ()

                match statement with
                | PlannedStatement.Select(selection, presentation) ->
                    match selection.Selector with
                    | Selector.Object ->
                        store.VisitObjects(
                            (fun item typ segment ->
                                inspect ()

                                let entity = {
                                    Kind = Selector.Object
                                    Runtime = item.Identity.Runtime
                                    Heap = segment.HeapIndex
                                    SegmentAddress = item.SegmentAddress
                                    Address = item.Identity.Address
                                    Size = item.SizeBytes
                                    End = None
                                    TypeIdentity = Some item.Type
                                    TypeName = typ.Name
                                    Generation = item.Generation
                                    IsFree = Some item.IsFree
                                    HeapKind = segment.Kind
                                }

                                emit statementIndex selection presentation entity
                                true),
                            context.CancellationToken
                        )
                        |> indexResult
                        |> ignore
                    | Selector.Segment
                    | Selector.Generation ->
                        store.VisitSegments(
                            (fun segment ->
                                inspect ()
                                let entity = segmentEntity segment

                                if selection.Selector = Selector.Segment then
                                    emit statementIndex selection presentation entity
                                else
                                    for generation in segment.Generations do
                                        inspect ()

                                        emit statementIndex selection presentation {
                                            entity with
                                                Kind = Selector.Generation
                                                Address = generation.Range.Start
                                                Size = AddressRange.length generation.Range
                                                End = Some generation.Range.End
                                                Generation = Some generation.Generation
                                        }

                                true),
                            context.CancellationToken
                        )
                        |> indexResult
                        |> ignore
                | PlannedStatement.Draw(draw, width) ->
                    let mutable found = None
                    let mutable runtimeSeen = false
                    let mutable index = 0

                    while index < info.Heaps.Count && found.IsNone do
                        inspect ()
                        let heap = info.Heaps[index]

                        if heap.Runtime.Index = int draw.Runtime.Value then
                            runtimeSeen <- true

                        if heap.Runtime.Index = int draw.Runtime.Value && heap.Index = int draw.Heap.Value then
                            found <- Some heap

                        index <- index + 1

                    match found with
                    | None ->
                        failure
                            "MQL303"
                            (if runtimeSeen then draw.Heap.Span else draw.Runtime.Span)
                            "DRAW Memory requires an existing Runtime/Heap lane; inspect the snapshot's heaps."
                    | Some heap ->
                        if draw.Start.Value <> draw.End.Value then
                            checkCapacity false true
                            guard ()

                            directives.Add {
                                StatementIndex = statementIndex
                                Kind = DrawingKind.Memory
                                Runtime = heap.Runtime
                                Heap = heap.Index
                                Address = draw.Start.Value
                                Size = draw.End.Value - draw.Start.Value
                                Entity = None
                                Label = None
                                LabelPosition = LabelPosition.InnerCenter
                                Background = "Grey"
                                Width = width
                            }

            guard ()
        with
        | ExecutionStopped value -> status <- value
        | :? OperationCanceledException when
            not (obj.ReferenceEquals(context, null))
            && context.CancellationToken.IsCancellationRequested
            ->
            status <- QueryStatus.Cancelled
        // A failed or stale query must never publish earlier statements' data.
        match status with
        | QueryStatus.Failed _ ->
            rows.Clear()
            directives.Clear()
        | _ -> ()

        {
            SnapshotId = plan.Snapshot
            Status = status
            SourcePartial = partial
            SourceAvailable = sourceAvailable
            SourceDiagnostics = sourceDiagnostics
            Rows = List.ofSeq rows
            Directives = List.ofSeq directives
            Candidates = candidates
        }
