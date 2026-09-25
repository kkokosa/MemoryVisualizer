namespace MemoryVisualizer.Core

open System
open System.Collections.Generic
open System.Threading

[<RequireQualifiedAccess>]
type SnapshotIndexError =
    | InvalidInput of message: string
    | LimitExceeded of message: string
    | Disposed

/// Limits may be lowered, but not raised above defaults in this provisional M1 implementation.
type SnapshotIndexLimits = {
    MaxObjects: int
    MaxTypes: int
    MaxSegments: int
    /// Runtimes, heaps, nested segment ranges, edges, roots, handles and diagnostics combined.
    MaxAuxiliaryEntries: int
    MaxPageSize: int
}

[<RequireQualifiedAccess>]
module SnapshotIndexLimits =
    let defaults = {
        MaxObjects = 1_000_000
        MaxTypes = 100_000
        MaxSegments = 100_000
        MaxAuxiliaryEntries = 5_000_000
        MaxPageSize = 4096
    }

[<RequireQualifiedAccess>]
type HeapEntrySelection =
    | All
    | Allocated
    | Free

[<RequireQualifiedAccess>]
type ObjectRangeSelection =
    | StartsIn of AddressRange
    | Overlaps of AddressRange

type ObjectSelection = {
    Runtime: RuntimeIdentity option
    Type: TypeIdentity option
    HeapIndex: int option
    SegmentAddress: uint64 option
    Generation: int option
    HeapKind: HeapKind option
    Entries: HeapEntrySelection
    Range: ObjectRangeSelection option
}

[<RequireQualifiedAccess>]
module ObjectSelection =
    let all = {
        Runtime = None
        Type = None
        HeapIndex = None
        SegmentAddress = None
        Generation = None
        HeapKind = None
        Entries = HeapEntrySelection.All
        Range = None
    }

/// Offset counts matching entries, not addresses or rows in the source snapshot.
type SnapshotPageRequest = { Offset: int; Limit: int }

type SnapshotObjectPage = {
    Items: IReadOnlyList<HeapObject>
    NextOffset: int option
    /// True only if at least one matching entry remains after this page.
    IsTruncated: bool
    /// Source extraction completeness, independent of pagination.
    IsPartial: bool
}

/// HeapSegment's array containers are projected as owned read-only collections.
type SnapshotSegmentInfo = {
    Runtime: RuntimeIdentity
    HeapIndex: int
    Address: uint64
    Kind: HeapKind
    IsPinned: bool
    ObjectRange: AddressRange
    CommittedRange: AddressRange
    ReservedRange: AddressRange
    Generations: IReadOnlyList<GenerationRange>
    AllocationContexts: IReadOnlyList<AddressRange>
}

type SnapshotIndexInfo = {
    Metadata: SnapshotMetadata
    Target: TargetInfo
    Runtimes: IReadOnlyList<RuntimeSnapshot>
    Heaps: IReadOnlyList<HeapInfo>
    Segments: IReadOnlyList<SnapshotSegmentInfo>
    Types: IReadOnlyList<HeapType>
    Edges: IReadOnlyList<ObjectEdge>
    Roots: IReadOnlyList<HeapRoot>
    Handles: IReadOnlyList<HeapHandle>
    StringDetails: ExtractionCompleteness
    Diagnostics: IReadOnlyList<SnapshotDiagnostic>
    IsPartial: bool
}

exception private SnapshotIndexFailure of SnapshotIndexError

type private ObjectOrder = {
    Rows: int array
    /// Inclusive last-byte prefix maxima within each runtime or type scope.
    Last: uint64 array
}

type private SnapshotIndexState = {
    Info: SnapshotIndexInfo
    Limits: SnapshotIndexLimits
    Objects: HeapObject array
    ByAddress: ObjectOrder
    ByType: ObjectOrder
    Types: Dictionary<TypeIdentity, HeapType>
    Segments: Dictionary<struct (RuntimeIdentity * uint64), SnapshotSegmentInfo>
}

module private SnapshotIndex =
    let invalid message =
        raise (SnapshotIndexFailure(SnapshotIndexError.InvalidInput message))

    let limit message =
        raise (SnapshotIndexFailure(SnapshotIndexError.LimitExceeded message))

    let attempt action =
        try
            Ok(action ())
        with SnapshotIndexFailure error ->
            Error error

    let notNull name value =
        if obj.ReferenceEquals(value, null) then
            invalid $"{name} must not be null."

    let readonly (items: 'T array) =
        Array.AsReadOnly items :> IReadOnlyList<'T>

    let copy (token: CancellationToken) (items: 'T array) =
        Array.init items.Length (fun index ->
            token.ThrowIfCancellationRequested()
            notNull "Snapshot entry" items[index]
            items[index])

    let lowerBound start finish before =
        let mutable low = start
        let mutable high = finish

        while low < high do
            let middle = low + (high - low) / 2

            if before middle then low <- middle + 1 else high <- middle

        low

    // A cancellable merge sort avoids framework sort wrapping cancellation in comparer exceptions.
    let sort (token: CancellationToken) (rows: int array) (scratch: int array) compareRows =
        let mutable width = 1

        while width < rows.Length do
            let mutable start = 0

            while start < rows.Length do
                let middle = min rows.Length (start + width)
                let finish = min rows.Length (middle + width)
                let mutable left = start
                let mutable right = middle

                for output in start .. finish - 1 do
                    token.ThrowIfCancellationRequested()

                    if right = finish || (left < middle && compareRows rows[left] rows[right] <= 0) then
                        scratch[output] <- rows[left]
                        left <- left + 1
                    else
                        scratch[output] <- rows[right]
                        right <- right + 1

                start <- finish

            for index in 0 .. rows.Length - 1 do
                token.ThrowIfCancellationRequested()
                rows[index] <- scratch[index]

            width <- width * 2

    let checkRuntime snapshotId (identity: RuntimeIdentity) =
        if identity.SnapshotId <> snapshotId || identity.Index < 0 then
            invalid "Runtime identity must have this snapshot's ID and a nonnegative index."

    let checkRange range =
        if range.End < range.Start then
            invalid "Address ranges must be half-open and non-inverted."

    let compareRuntime (left: RuntimeIdentity) (right: RuntimeIdentity) = compare left.Index right.Index

    let compareType (left: TypeIdentity) (right: TypeIdentity) =
        let runtime = compareRuntime left.Runtime right.Runtime

        if runtime <> 0 then
            runtime
        else
            compare left.MethodTable right.MethodTable

    let compareObject (left: ObjectIdentity) (right: ObjectIdentity) =
        let runtime = compareRuntime left.Runtime right.Runtime

        if runtime <> 0 then
            runtime
        else
            compare left.Address right.Address

    let build (snapshot: HeapSnapshot) (limits: SnapshotIndexLimits) (token: CancellationToken) =
        token.ThrowIfCancellationRequested()
        notNull "Snapshot" snapshot
        notNull "Limits" limits
        notNull "Metadata" snapshot.Metadata
        notNull "Target" snapshot.Target

        if snapshot.Metadata.Id = Unchecked.defaultof<SnapshotId> then
            invalid "Snapshot identity must not be empty."

        let maximum = SnapshotIndexLimits.defaults

        for name, value, ceiling in
            [
                "MaxObjects", limits.MaxObjects, maximum.MaxObjects
                "MaxTypes", limits.MaxTypes, maximum.MaxTypes
                "MaxSegments", limits.MaxSegments, maximum.MaxSegments
                "MaxAuxiliaryEntries", limits.MaxAuxiliaryEntries, maximum.MaxAuxiliaryEntries
                "MaxPageSize", limits.MaxPageSize, maximum.MaxPageSize
            ] do
            if value < 1 || value > ceiling then
                invalid $"{name} must be between 1 and {ceiling}."

        let count name (items: 'T array) maximum =
            notNull name items

            if items.Length > maximum then
                limit $"{name} exceeds the configured limit of {maximum} entries."

            int64 items.Length

        count "Objects" snapshot.Objects limits.MaxObjects |> ignore
        count "Types" snapshot.Types limits.MaxTypes |> ignore
        count "Segments" snapshot.Segments limits.MaxSegments |> ignore

        let mutable auxiliary =
            count "Runtimes" snapshot.Runtimes 32
            + count "Heaps" snapshot.Heaps limits.MaxAuxiliaryEntries
            + count "Edges" snapshot.Edges limits.MaxAuxiliaryEntries
            + count "Roots" snapshot.Roots limits.MaxAuxiliaryEntries
            + count "Handles" snapshot.Handles limits.MaxAuxiliaryEntries
            + count "Diagnostics" snapshot.Diagnostics limits.MaxAuxiliaryEntries

        let checkAuxiliary () =
            if auxiliary > int64 limits.MaxAuxiliaryEntries then
                limit $"Auxiliary metadata exceeds the configured limit of {limits.MaxAuxiliaryEntries} entries."

        checkAuxiliary ()

        for segment in snapshot.Segments do
            token.ThrowIfCancellationRequested()
            notNull "Segment" segment
            auxiliary <- auxiliary + count "Generations" segment.Generations limits.MaxAuxiliaryEntries

            auxiliary <-
                auxiliary
                + count "AllocationContexts" segment.AllocationContexts limits.MaxAuxiliaryEntries

            checkAuxiliary ()

        let runtimes = copy token snapshot.Runtimes
        let runtimeIds = HashSet<RuntimeIdentity>()
        let snapshotId = snapshot.Metadata.Id

        for runtime in runtimes do
            token.ThrowIfCancellationRequested()
            checkRuntime snapshotId runtime.Identity

            if not (runtimeIds.Add runtime.Identity) then
                invalid "Duplicate runtime identity."

        Array.sortInPlaceBy (fun (runtime: RuntimeSnapshot) -> runtime.Identity.Index) runtimes

        let requireRuntime identity =
            checkRuntime snapshotId identity

            if not (runtimeIds.Contains identity) then
                invalid "An indexed entry refers to a runtime absent from the snapshot."

        let heaps = copy token snapshot.Heaps
        let heapIds = HashSet<struct (RuntimeIdentity * int)>()

        for heap in heaps do
            token.ThrowIfCancellationRequested()
            requireRuntime heap.Runtime

            if heap.Index < 0 || not (heapIds.Add(struct (heap.Runtime, heap.Index))) then
                invalid "Heap indices must be nonnegative and unique within a runtime."

        let segments = Dictionary<struct (RuntimeIdentity * uint64), SnapshotSegmentInfo>()

        let segmentInfo =
            snapshot.Segments
            |> Array.map (fun segment ->
                token.ThrowIfCancellationRequested()
                requireRuntime segment.Runtime

                if not (heapIds.Contains(struct (segment.Runtime, segment.HeapIndex))) then
                    invalid "A segment refers to a heap absent from its runtime."

                let info = {
                    Runtime = segment.Runtime
                    HeapIndex = segment.HeapIndex
                    Address = segment.Address
                    Kind = segment.Kind
                    IsPinned = segment.IsPinned
                    ObjectRange = segment.ObjectRange
                    CommittedRange = segment.CommittedRange
                    ReservedRange = segment.ReservedRange
                    Generations = copy token segment.Generations |> readonly
                    AllocationContexts = copy token segment.AllocationContexts |> readonly
                }

                checkRange info.ObjectRange
                checkRange info.CommittedRange
                checkRange info.ReservedRange

                for generation in info.Generations do
                    token.ThrowIfCancellationRequested()
                    checkRange generation.Range

                for context in info.AllocationContexts do
                    token.ThrowIfCancellationRequested()
                    checkRange context

                if not (segments.TryAdd(struct (info.Runtime, info.Address), info)) then
                    invalid "Duplicate segment address within a runtime."

                info)

        let typeInfo = copy token snapshot.Types
        let types = Dictionary<TypeIdentity, HeapType>()

        for typ in typeInfo do
            token.ThrowIfCancellationRequested()
            requireRuntime typ.Identity.Runtime

            if not (types.TryAdd(typ.Identity, typ)) then
                invalid "Duplicate type identity."

        let objects = copy token snapshot.Objects
        let mutable allocated = 0UL

        for item in objects do
            token.ThrowIfCancellationRequested()
            requireRuntime item.Identity.Runtime

            if item.Type.Runtime <> item.Identity.Runtime || not (types.ContainsKey item.Type) then
                invalid "An object must refer to an existing type in its runtime."

            if not (segments.ContainsKey(struct (item.Identity.Runtime, item.SegmentAddress))) then
                invalid "An object must refer to an existing segment in its runtime."

            if
                item.SizeBytes = 0UL
                || item.SizeBytes - 1UL > UInt64.MaxValue - item.Identity.Address
            then
                invalid "Object sizes must be nonzero and their last byte must fit in the uint64 address space."

            if item.Generation |> Option.exists (fun generation -> generation < 0) then
                invalid "Object generations must be nonnegative when known."

            if not item.IsFree then
                allocated <- allocated + 1UL

        if allocated <> snapshot.Metadata.ObjectCount then
            invalid "Metadata.ObjectCount must equal the number of captured non-free entries."

        let byAddress = Array.init objects.Length id
        let byType = Array.init objects.Length id
        let scratch = Array.zeroCreate objects.Length
        sort token byAddress scratch (fun left right -> compareObject objects[left].Identity objects[right].Identity)

        sort token byType scratch (fun left right ->
            let scope = compareType objects[left].Type objects[right].Type

            if scope <> 0 then
                scope
            else
                compare objects[left].Identity.Address objects[right].Identity.Address)

        let order (rows: int array) sameScope =
            let last = Array.zeroCreate<uint64> objects.Length

            for index in 0 .. objects.Length - 1 do
                token.ThrowIfCancellationRequested()
                let item = objects[rows[index]]
                let finalByte = item.Identity.Address + (item.SizeBytes - 1UL)

                last[index] <-
                    if index > 0 && sameScope objects[rows[index - 1]] item then
                        max last[index - 1] finalByte
                    else
                        finalByte

            { Rows = rows; Last = last }

        for index in 1 .. objects.Length - 1 do
            token.ThrowIfCancellationRequested()

            if objects[byAddress[index - 1]].Identity = objects[byAddress[index]].Identity then
                invalid "Duplicate object address within a runtime."

        let state = {
            Info = {
                Metadata = snapshot.Metadata
                Target = snapshot.Target
                Runtimes = readonly runtimes
                Heaps = readonly heaps
                Segments = readonly segmentInfo
                Types = readonly typeInfo
                Edges = copy token snapshot.Edges |> readonly
                Roots = copy token snapshot.Roots |> readonly
                Handles = copy token snapshot.Handles |> readonly
                StringDetails = snapshot.StringDetails
                Diagnostics = copy token snapshot.Diagnostics |> readonly
                IsPartial = snapshot.IsPartial
            }
            Limits = limits
            Objects = objects
            ByAddress = order byAddress (fun left right -> left.Identity.Runtime = right.Identity.Runtime)
            ByType = order byType (fun left right -> left.Type = right.Type)
            Types = types
            Segments = segments
        }

        token.ThrowIfCancellationRequested()
        state

    let select
        (state: SnapshotIndexState)
        (selection: ObjectSelection)
        (page: SnapshotPageRequest)
        (token: CancellationToken)
        =
        notNull "Selection" selection
        notNull "Page" page

        if page.Offset < 0 || page.Limit < 1 || page.Limit > state.Limits.MaxPageSize then
            invalid $"Page offset must be nonnegative and limit between 1 and {state.Limits.MaxPageSize}."

        selection.Runtime |> Option.iter (checkRuntime state.Info.Metadata.Id)

        selection.Type
        |> Option.iter (fun typ -> checkRuntime state.Info.Metadata.Id typ.Runtime)

        if selection.HeapIndex |> Option.exists (fun index -> index < 0) then
            invalid "Heap index must be nonnegative."

        if selection.Generation |> Option.exists (fun generation -> generation < 0) then
            invalid "Generation must be nonnegative."

        selection.Range
        |> Option.iter (fun filter ->
            let range =
                match filter with
                | ObjectRangeSelection.StartsIn range
                | ObjectRangeSelection.Overlaps range -> range

            checkRange range)

        let items = ResizeArray<HeapObject>(page.Limit)
        let mutable skipped = 0
        let mutable more = false
        let matches expected actual = expected |> Option.forall ((=) actual)

        let visit (order: ObjectOrder) start finish =
            let address index =
                state.Objects[order.Rows[index]].Identity.Address

            let first, last =
                match selection.Range with
                | None -> start, finish
                | Some(ObjectRangeSelection.StartsIn range) ->
                    lowerBound start finish (fun index -> address index < range.Start),
                    lowerBound start finish (fun index -> address index < range.End)
                | Some(ObjectRangeSelection.Overlaps range) ->
                    if range.Start = range.End then
                        start, start
                    else
                        lowerBound start finish (fun index -> order.Last[index] < range.Start),
                        lowerBound start finish (fun index -> address index < range.End)

            let mutable index = first

            while index < last && not more do
                token.ThrowIfCancellationRequested()
                let item = state.Objects[order.Rows[index]]
                let segment = state.Segments[struct (item.Identity.Runtime, item.SegmentAddress)]

                let inRange =
                    match selection.Range with
                    | Some(ObjectRangeSelection.Overlaps range) ->
                        // Subtraction avoids overflowing when the final byte is UInt64.MaxValue.
                        item.Identity.Address >= range.Start
                        || item.SizeBytes > range.Start - item.Identity.Address
                    | _ -> true

                let entryMatches =
                    match selection.Entries with
                    | HeapEntrySelection.All -> true
                    | HeapEntrySelection.Allocated -> not item.IsFree
                    | HeapEntrySelection.Free -> item.IsFree

                if
                    inRange
                    && entryMatches
                    && matches selection.Runtime item.Identity.Runtime
                    && matches selection.HeapIndex segment.HeapIndex
                    && matches selection.SegmentAddress item.SegmentAddress
                    && matches selection.HeapKind segment.Kind
                    && (selection.Generation
                        |> Option.forall (fun value -> item.Generation = Some value))
                then
                    if skipped < page.Offset then skipped <- skipped + 1
                    elif items.Count < page.Limit then items.Add item
                    else more <- true

                index <- index + 1

        match selection.Type with
        | Some typ ->
            let order = state.ByType

            let compareAt index =
                compareType state.Objects[order.Rows[index]].Type typ

            let start = lowerBound 0 order.Rows.Length (fun index -> compareAt index < 0)
            let finish = lowerBound start order.Rows.Length (fun index -> compareAt index <= 0)
            visit order start finish
        | None ->
            let order = state.ByAddress

            for runtime in state.Info.Runtimes do
                token.ThrowIfCancellationRequested()

                if not more && matches selection.Runtime runtime.Identity then
                    let indexAt index =
                        state.Objects[order.Rows[index]].Identity.Runtime.Index

                    let start =
                        lowerBound 0 order.Rows.Length (fun index -> indexAt index < runtime.Identity.Index)

                    let finish =
                        lowerBound start order.Rows.Length (fun index -> indexAt index <= runtime.Identity.Index)

                    visit order start finish

        token.ThrowIfCancellationRequested()

        {
            Items = items.ToArray() |> readonly
            NextOffset = if more then Some(page.Offset + items.Count) else None
            IsTruncated = more
            IsPartial = state.Info.IsPartial
        }

/// Owned, read-only indexes over one materialized snapshot. Build before swapping an owner's active store.
type IndexedHeapSnapshot private (initial: SnapshotIndexState) =
    let gate = obj ()
    let mutable state = Some initial

    let access (token: CancellationToken) action =
        token.ThrowIfCancellationRequested()

        lock gate (fun () ->
            token.ThrowIfCancellationRequested()

            match state with
            | None -> Error SnapshotIndexError.Disposed
            | Some value ->
                SnapshotIndex.attempt (fun () ->
                    let result = action value
                    token.ThrowIfCancellationRequested()
                    result))

    static member Create(snapshot: HeapSnapshot, limits: SnapshotIndexLimits, cancellationToken: CancellationToken) =
        SnapshotIndex.attempt (fun () ->
            let state = SnapshotIndex.build snapshot limits cancellationToken
            new IndexedHeapSnapshot(state))

    member _.GetInfo(cancellationToken: CancellationToken) = access cancellationToken _.Info

    member _.TryGetObject(identity: ObjectIdentity, cancellationToken: CancellationToken) =
        access cancellationToken (fun value ->
            SnapshotIndex.checkRuntime value.Info.Metadata.Id identity.Runtime
            let rows = value.ByAddress.Rows

            let index =
                SnapshotIndex.lowerBound 0 rows.Length (fun index ->
                    SnapshotIndex.compareObject value.Objects[rows[index]].Identity identity < 0)

            if index < rows.Length && value.Objects[rows[index]].Identity = identity then
                Some value.Objects[rows[index]]
            else
                None)

    member _.TryGetType(identity: TypeIdentity, cancellationToken: CancellationToken) =
        access cancellationToken (fun value ->
            SnapshotIndex.checkRuntime value.Info.Metadata.Id identity.Runtime

            match value.Types.TryGetValue identity with
            | true, typ -> Some typ
            | _ -> None)

    member _.SelectObjects
        (selection: ObjectSelection, page: SnapshotPageRequest, cancellationToken: CancellationToken)
        =
        access cancellationToken (fun value -> SnapshotIndex.select value selection page cancellationToken)

    member _.Dispose() = lock gate (fun () -> state <- None)

    interface IDisposable with
        member this.Dispose() = this.Dispose()
