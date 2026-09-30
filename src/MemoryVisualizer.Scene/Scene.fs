namespace MemoryVisualizer.Scene

open System
open System.Collections.Generic
open System.Globalization
open System.Text
open MemoryVisualizer.Core
open MemoryVisualizer.Query

exception private SceneFailure of SceneDiagnostic
exception private SceneStopped of SceneTruncation

type private CompactRange = {
    Start: bigint
    Prefix: bigint
    GapCount: int
}

type private CompactLayout = {
    Ranges: CompactRange array
    Bytes: bigint
    DataWidth: float
    GapWidth: float
    Offsets: float list
}

[<RequireQualifiedAccess>]
module Scene =
    let private invariant = CultureInfo.InvariantCulture
    let private number (value: uint64) = value.ToString(invariant)
    let private address (value: uint64) = "0x" + value.ToString("x16", invariant)
    let private spaceEnd = bigint.One <<< 64

    let private fail code message =
        raise (SceneFailure { Code = code; Message = message })

    let private color (value: string) =
        match value with
        | "Black" -> "#000000"
        | "White" -> "#ffffff"
        | "Grey" -> "#808080"
        | "Red" -> "#ff0000"
        | "Green" -> "#008000"
        | "Blue" -> "#0000ff"
        | "Yellow" -> "#ffff00"
        | value when
            not (isNull value)
            && value.Length = 7
            && value[0] = '#'
            && (value.AsSpan(1).ToArray() |> Array.forall Char.IsAsciiHexDigit)
            ->
            value.ToLowerInvariant()
        | _ -> fail "SCN001" "Colors must be a supported MQL color or #RRGGBB."

    let validate (options: SceneOptions) =
        try
            if
                obj.ReferenceEquals(options, null)
                || obj.ReferenceEquals(options.Limits, null)
                || obj.ReferenceEquals(options.Theme, null)
                || obj.ReferenceEquals(options.Redaction, null)
                || obj.ReferenceEquals(options.Layout, null)
            then
                fail "SCN001" "Scene options, limits, theme, redaction and layout must not be null."

            let defaults = SceneLimits.defaults

            for name, value, ceiling in
                [
                    "MaxDirectives", options.Limits.MaxDirectives, defaults.MaxDirectives
                    "MaxElements", options.Limits.MaxElements, defaults.MaxElements
                    "MaxLanes", options.Limits.MaxLanes, defaults.MaxLanes
                    "MaxLabelCharacters", options.Limits.MaxLabelCharacters, defaults.MaxLabelCharacters
                    "MaxTotalLabelCharacters", options.Limits.MaxTotalLabelCharacters, defaults.MaxTotalLabelCharacters
                    "MaxElapsedMilliseconds", options.Limits.MaxElapsedMilliseconds, defaults.MaxElapsedMilliseconds
                ] do
                if value < 1 || value > ceiling then
                    fail "SCN001" $"{name} must be between 1 and {ceiling}."

            if options.PlotWidth < 64 || options.PlotWidth > 4096 then
                fail "SCN001" "PlotWidth must be between 64 and 4096."

            options.Viewport
            |> Option.iter (fun view ->
                if
                    obj.ReferenceEquals(view, null)
                    || bigint view.Start + bigint view.Size > spaceEnd
                then
                    fail "SCN001" "Viewport end must not exceed 2^64.")

            color options.Theme.Background |> ignore
            color options.Theme.Stroke |> ignore
            color options.Theme.Text |> ignore
            Ok()
        with SceneFailure diagnostic ->
            Error [ diagnostic ]

    let private layer (directive: DrawingDirective) =
        match directive.Kind, directive.Entity with
        | DrawingKind.Memory, _ -> 0
        | DrawingKind.Pin, _ -> 5
        | _, Some entity when entity.Kind = Selector.Segment -> 1
        | _, Some entity when entity.Kind = Selector.Generation -> 2
        | _, Some entity when entity.IsFree = Some true -> 3
        | _ -> 4

    let private contrastingText background =
        let hex = color background

        let channel offset =
            let value =
                float (Int32.Parse(hex.AsSpan(offset, 2), NumberStyles.HexNumber, invariant))
                / 255.0

            if value <= 0.04045 then
                value / 12.92
            else
                ((value + 0.055) / 1.055) ** 2.4

        let luminance = 0.2126 * channel 1 + 0.7152 * channel 3 + 0.0722 * channel 5

        if (luminance + 0.05) / 0.05 >= 1.05 / (luminance + 0.05) then
            "#000000"
        else
            "#ffffff"

    let private labelValue redaction =
        function
        | _ when redaction.Addresses || redaction.Labels -> None
        | value when obj.ReferenceEquals(value, null) -> fail "SCN001" "Label value must not be null."
        | QueryValue.Text _ when redaction.Strings || redaction.Paths -> None
        | QueryValue.Text text ->
            if isNull text then
                fail "SCN001" "Label text must not be null."

            Some text
        | QueryValue.Unsigned value -> Some(number value)
        | QueryValue.Boolean value -> Some(if value then "true" else "false")
        | QueryValue.Missing -> None
        | QueryValue.Entity _ -> fail "SCN001" "Entity-valued labels are not supported."

    let private queryState =
        function
        | QueryStatus.Complete -> "complete", []
        | QueryStatus.Truncated reasons ->
            if obj.ReferenceEquals(reasons, null) then
                fail "SCN001" "Query truncation reasons must not be null."

            let mutable remaining = reasons
            let values = ResizeArray<string>()

            while not remaining.IsEmpty && values.Count < 4 do
                values.Add(string remaining.Head)
                remaining <- remaining.Tail

            if not remaining.IsEmpty then
                fail "SCN001" "Query truncation reason count exceeds four."

            "truncated", List.ofSeq values
        | QueryStatus.Cancelled -> "cancelled", []
        | QueryStatus.Failed _ -> "failed", []

    let private compactLayout
        guard
        (admitted: ResizeArray<DrawingDirective * bigint * bigint>)
        origin
        finish
        plotX
        plotWidth
        =
        guard ()

        let sorted =
            admitted
            |> Seq.map (fun (_, first, last) -> first, last)
            |> Seq.sort
            |> Seq.toArray

        guard ()
        let merged = ResizeArray<bigint * bigint>()

        for first, last in sorted do
            guard ()

            if merged.Count = 0 then
                merged.Add(first, last)
            else
                let previous, previousEnd = merged[merged.Count - 1]

                if first <= previousEnd then
                    merged[merged.Count - 1] <- previous, max previousEnd last
                else
                    merged.Add(first, last)

        let leading = fst merged[0] > origin
        let trailing = snd merged[merged.Count - 1] < finish

        let count =
            merged.Count - 1 + (if leading then 1 else 0) + (if trailing then 1 else 0)

        if count = 0 then
            None
        else
            let bytes = merged |> Seq.sumBy (fun (first, last) -> last - first)
            let gapWidth = min 12.0 (plotWidth * 0.2 / float count)
            let dataWidth = plotWidth - float count * gapWidth
            let ranges = ResizeArray<CompactRange>()
            let offsets = ResizeArray<float>()
            let mutable prefix = bigint.Zero
            let mutable previousEnd = origin

            for first, last in merged do
                guard ()

                if first > previousEnd then
                    offsets.Add(plotX + float prefix / float bytes * dataWidth + float offsets.Count * gapWidth)

                ranges.Add {
                    Start = first
                    Prefix = prefix
                    GapCount = offsets.Count
                }

                prefix <- prefix + last - first
                previousEnd <- last

            if trailing then
                offsets.Add(plotX + dataWidth + float offsets.Count * gapWidth)

            guard ()

            Some {
                Ranges = ranges.ToArray()
                Bytes = bytes
                DataWidth = dataWidth
                GapWidth = gapWidth
                Offsets = List.ofSeq offsets
            }

    let private translateElement dy (element: SceneElement) =
        if dy = 0.0 then
            element
        else
            let bounds = {
                element.Bounds with
                    Y = element.Bounds.Y + dy
            }

            {
                element with
                    Bounds = bounds
                    Geometry =
                        match element.Geometry with
                        | SceneGeometry.Rectangle _ -> SceneGeometry.Rectangle bounds
                        | SceneGeometry.Line(first, last) ->
                            SceneGeometry.Line({ first with Y = first.Y + dy }, { last with Y = last.Y + dy })
                    Text =
                        element.Text
                        |> Option.map (fun text -> {
                            text with
                                Bounds = {
                                    text.Bounds with
                                        Y = text.Bounds.Y + dy
                                }
                                Lines =
                                    text.Lines
                                    |> List.map (fun line -> {
                                        line with
                                            Baseline = line.Baseline + dy
                                    })
                        })
            }

    let private packPinRows
        guard
        (admitted: ResizeArray<DrawingDirective * bigint * bigint>)
        (lanes: ResizeArray<SceneLane>)
        (elements: ResizeArray<SceneElement>)
        =
        let members = Dictionary<string, ResizeArray<int>>()

        for index in 0 .. elements.Count - 1 do
            guard ()
            let lane = elements[index].LaneId

            match members.TryGetValue lane with
            | true, indices -> indices.Add index
            | _ -> members.Add(lane, ResizeArray [ index ])

        let mutable shift = 0.0

        for laneIndex in 0 .. lanes.Count - 1 do
            guard ()
            let original = lanes[laneIndex]

            let lane = {
                original with
                    Bounds = {
                        original.Bounds with
                            Y = original.Bounds.Y + shift
                    }
            }

            let contentHeight = lane.Bounds.Height - 80.0
            let candidates = ResizeArray<float * float * int>()
            let mutable rowHeight = 0.0
            let mutable hasBaseElements = false
            let mutable baseBottom = lane.Bounds.Y + 40.0 + contentHeight

            for index in members[lane.Id] do
                guard ()
                let element = translateElement shift elements[index]
                elements[index] <- element
                let directive, _, _ = admitted[index]

                match directive.Kind, directive.LabelPosition, element.Text with
                | DrawingKind.Pin, LabelPosition.OuterLeft, Some text ->
                    let left = min text.Bounds.X (element.Bounds.X - element.Style.StrokeWidth / 2.0)

                    let right =
                        max (text.Bounds.X + text.Bounds.Width) (element.Bounds.X + element.Style.StrokeWidth / 2.0)

                    candidates.Add(left, right, index)
                    rowHeight <- max rowHeight (max element.Bounds.Height text.Bounds.Height)
                | _ ->
                    hasBaseElements <- true

                    baseBottom <-
                        max baseBottom (element.Bounds.Y + element.Bounds.Height + element.Style.StrokeWidth / 2.0)

                    element.Text
                    |> Option.iter (fun text -> baseBottom <- max baseBottom (text.Bounds.Y + text.Bounds.Height))

            let mutable height = lane.Bounds.Height

            if candidates.Count > 0 then
                guard ()
                // Sort interval starts, not pin addresses: label lengths can reverse their order.
                let sorted =
                    candidates |> Seq.sortBy (fun (left, _, index) -> left, index) |> Seq.toArray

                guard ()
                let rows = PriorityQueue<int, struct (float * int)>()

                let firstRow =
                    if hasBaseElements then
                        baseBottom + 8.0
                    else
                        lane.Bounds.Y + 40.0 + (contentHeight - rowHeight) / 2.0

                for left, right, index in sorted do
                    guard ()

                    let row =
                        match rows.TryPeek() with
                        | true, _, struct (finish, _) when finish + 8.0 <= left -> rows.Dequeue()
                        | _ -> rows.Count

                    rows.Enqueue(row, struct (right, row))
                    let element = elements[index]
                    let center = firstRow + float row * (rowHeight + 8.0) + rowHeight / 2.0

                    elements[index] <-
                        translateElement (center - element.Bounds.Y - element.Bounds.Height / 2.0) element

                let bottom = firstRow + float (rows.Count - 1) * (rowHeight + 8.0) + rowHeight
                height <- max height (bottom - lane.Bounds.Y + 40.0)

            lanes[laneIndex] <- {
                lane with
                    Bounds = { lane.Bounds with Height = height }
            }

            shift <- shift + height - original.Bounds.Height

        guard ()

    let build options (result: QueryResult) (context: SceneExecutionContext) =
        let mutable status = SceneStatus.Complete
        let mutable scene = None
        let mutable diagnostics = []

        try
            match validate options with
            | Error errors -> raise (SceneFailure errors.Head)
            | Ok() -> ()

            if
                obj.ReferenceEquals(result, null)
                || obj.ReferenceEquals(result.Directives, null)
                || obj.ReferenceEquals(result.Status, null)
                || obj.ReferenceEquals(result.SourceDiagnostics, null)
            then
                fail "SCN001" "Query result, status, directives and source diagnostics must not be null."

            if
                obj.ReferenceEquals(context, null)
                || isNull context.TimeProvider
                || obj.ReferenceEquals(context.IsSnapshotCurrent, null)
            then
                fail "SCN001" "Scene context requires a clock and active-snapshot predicate."

            if result.SnapshotId = Unchecked.defaultof<SnapshotId> then
                fail "SCN001" "Query snapshot identity must not be empty."

            let started = context.TimeProvider.GetTimestamp()

            let guard () =
                context.CancellationToken.ThrowIfCancellationRequested()

                if not (context.IsSnapshotCurrent result.SnapshotId) then
                    fail "SCN002" "Snapshot changed; discard this scene."

                if
                    context.TimeProvider.GetElapsedTime(started).TotalMilliseconds
                    >= float options.Limits.MaxElapsedMilliseconds
                then
                    raise (SceneStopped SceneTruncation.ElapsedTime)

                context.CancellationToken.ThrowIfCancellationRequested()

            guard ()

            match result.Status with
            | QueryStatus.Cancelled ->
                context.CancellationToken.ThrowIfCancellationRequested()
                status <- SceneStatus.Cancelled
            | QueryStatus.Failed _ -> fail "SCN003" "Cannot construct a scene from failed query execution."
            | _ -> ()

            if status <> SceneStatus.Cancelled then
                if not result.SourceAvailable then
                    fail "SCN003" "Query source metadata is unavailable."

                let admitted = ResizeArray<DrawingDirective * bigint * bigint>()
                let laneHeights = SortedDictionary<struct (int * int), float>()
                let reasons = ResizeArray<SceneTruncation>()

                let reason value =
                    if not (reasons.Contains value) then
                        reasons.Add value

                let mutable inspected = 0
                let mutable rest = result.Directives
                let mutable accepting = true
                // No full list length, sort or label copy before admission budgets.
                while accepting && not rest.IsEmpty do
                    guard ()

                    if inspected = options.Limits.MaxDirectives then
                        reason SceneTruncation.Directives
                        accepting <- false
                    else
                        let directive = rest.Head
                        rest <- rest.Tail
                        inspected <- inspected + 1

                        if
                            obj.ReferenceEquals(directive, null)
                            || obj.ReferenceEquals(directive.Kind, null)
                            || obj.ReferenceEquals(directive.LabelPosition, null)
                            || directive.Runtime.SnapshotId <> result.SnapshotId
                            || directive.Runtime.Index < 0
                            || directive.Heap < 0
                            || directive.StatementIndex < 0
                            || directive.Width < 1
                            || directive.Width > 4096
                        then
                            fail "SCN001" "Directive identity, lane, statement or width is invalid."

                        color directive.Background |> ignore
                        let first = bigint directive.Address
                        let finish = first + bigint directive.Size

                        if finish > spaceEnd then
                            fail "SCN001" "Directive end must not exceed 2^64."

                        directive.Entity
                        |> Option.iter (fun entity ->
                            if
                                obj.ReferenceEquals(entity, null)
                                || obj.ReferenceEquals(entity.Kind, null)
                                || entity.Runtime <> directive.Runtime
                                || entity.Heap <> directive.Heap
                                || entity.Address <> directive.Address
                                || entity.Size <> directive.Size
                            then
                                fail "SCN001" "Directive source association does not match its geometry."

                            if entity.TypeIdentity |> Option.exists (fun typ -> typ.Runtime <> entity.Runtime) then
                                fail "SCN001" "Source type belongs to another runtime.")

                        let left, right =
                            match options.Viewport with
                            | Some view ->
                                max first (bigint view.Start), min finish (bigint view.Start + bigint view.Size)
                            | None -> first, finish

                        if right > left then
                            let key = struct (directive.Runtime.Index, directive.Heap)
                            let newLane = not (laneHeights.ContainsKey key)

                            if admitted.Count = options.Limits.MaxElements then
                                reason SceneTruncation.Elements
                                accepting <- false

                            if newLane && laneHeights.Count = options.Limits.MaxLanes then
                                reason SceneTruncation.Lanes
                                accepting <- false

                            if accepting then
                                let height =
                                    if options.Redaction.Addresses then
                                        16.0
                                    else
                                        max 16.0 (float directive.Width)

                                laneHeights[key] <- max height (if newLane then 0.0 else laneHeights[key])
                                admitted.Add(directive, left, right)

                guard ()

                let origin, finish =
                    match options.Viewport with
                    | Some view -> bigint view.Start, bigint view.Start + bigint view.Size
                    | None when admitted.Count > 0 ->
                        admitted |> Seq.map (fun (_, first, _) -> first) |> Seq.min,
                        admitted |> Seq.map (fun (_, _, last) -> last) |> Seq.max
                    | None -> bigint.Zero, bigint.One

                let span = max bigint.One (finish - origin)
                let plotX = 528.0
                let plotWidth = float options.PlotWidth

                let compact =
                    if
                        options.Layout = SceneLayout.Compact
                        && not options.Redaction.Addresses
                        && admitted.Count > 0
                    then
                        compactLayout guard admitted origin finish plotX plotWidth
                    else
                        None

                let x offset =
                    match compact with
                    | None -> plotX + float (offset - origin) / float span * plotWidth
                    | Some layout ->
                        // Find the containing occupied range without scanning gaps per element.
                        let mutable low = 0
                        let mutable high = layout.Ranges.Length - 1

                        while low < high do
                            guard ()
                            let middle = low + (high - low + 1) / 2

                            if layout.Ranges[middle].Start <= offset then
                                low <- middle
                            else
                                high <- middle - 1

                        let range = layout.Ranges[low]

                        plotX
                        + float (range.Prefix + offset - range.Start) / float layout.Bytes
                          * layout.DataWidth
                        + float range.GapCount * layout.GapWidth

                let intervalWidth (size: bigint) =
                    match compact with
                    | None -> float size / float span * plotWidth
                    | Some layout -> float size / float layout.Bytes * layout.DataWidth

                let lanes = ResizeArray<SceneLane>()
                let laneInfo = Dictionary<struct (int * int), SceneLane * float>()
                let laneTop = if compact.IsSome then 32.0 else 16.0
                let mutable top = laneTop

                for pair in laneHeights do
                    guard ()
                    let struct (runtime, heap) = pair.Key

                    let lane = {
                        Id = "lane-" + lanes.Count.ToString(invariant)
                        Runtime = if options.Redaction.Addresses then None else Some runtime
                        Heap = if options.Redaction.Addresses then None else Some heap
                        Bounds = {
                            X = plotX
                            Y = top
                            Width = plotWidth
                            Height = pair.Value + 80.0
                        }
                    }

                    lanes.Add lane
                    laneInfo.Add(pair.Key, (lane, pair.Value))
                    top <- top + lane.Bounds.Height + 16.0

                let elements = ResizeArray<SceneElement>()
                let laneOrdinals = Dictionary<string, int>()
                let mutable textBudget = options.Limits.MaxTotalLabelCharacters

                for index in 0 .. admitted.Count - 1 do
                    guard ()
                    let directive, left, right = admitted[index]
                    let lane, height = laneInfo[struct (directive.Runtime.Index, directive.Heap)]

                    let ordinal =
                        match laneOrdinals.TryGetValue lane.Id with
                        | true, value -> value
                        | _ -> 0

                    laneOrdinals[lane.Id] <- ordinal + 1

                    let shapeHeight =
                        if options.Redaction.Addresses then
                            16.0
                        else
                            max 16.0 (float directive.Width)

                    let shapeX, shapeWidth =
                        if options.Redaction.Addresses then
                            // Fixed cells encode neither byte sizes nor relative/absolute addresses.
                            plotX + float ordinal * 24.0, 16.0
                        else
                            x left, intervalWidth (right - left)

                    let shape = {
                        X = shapeX
                        Y = lane.Bounds.Y + 40.0 + (height - shapeHeight) / 2.0
                        Width = if directive.Kind = DrawingKind.Pin then 0.0 else shapeWidth
                        Height = shapeHeight
                    }

                    let geometry =
                        if directive.Kind = DrawingKind.Pin then
                            SceneGeometry.Line(
                                { X = shape.X; Y = shape.Y },
                                {
                                    X = shape.X
                                    Y = shape.Y + shape.Height
                                }
                            )
                        else
                            SceneGeometry.Rectangle shape

                    let label = directive.Label |> Option.bind (labelValue options.Redaction)

                    let text =
                        label
                        |> Option.bind (fun raw ->
                            guard ()

                            if raw.Length = 0 then
                                None
                            else
                                let cap = min options.Limits.MaxLabelCharacters textBudget
                                let count = min cap raw.Length

                                if raw.Length > options.Limits.MaxLabelCharacters then
                                    reason SceneTruncation.LabelCharacters

                                if min options.Limits.MaxLabelCharacters raw.Length > textBudget then
                                    reason SceneTruncation.TotalLabelCharacters

                                textBudget <- textBudget - count

                                if count = 0 then
                                    None
                                else
                                    let cellsPerLine, maxLines =
                                        if
                                            directive.Kind = DrawingKind.Pin
                                            && directive.LabelPosition = LabelPosition.OuterLeft
                                        then
                                            32, 4
                                        else
                                            64, 2

                                    let lines = ResizeArray<string>()
                                    let current = StringBuilder(cellsPerLine)
                                    let mutable position = 0
                                    let mutable replaced = 0

                                    while position < count do
                                        guard ()
                                        let ch = raw[position]

                                        if ch = '\r' || ch = '\n' then
                                            lines.Add(current.ToString())
                                            current.Clear() |> ignore

                                            if ch = '\r' && position + 1 < count && raw[position + 1] = '\n' then
                                                position <- position + 1
                                        else
                                            if current.Length = cellsPerLine then
                                                lines.Add(current.ToString())
                                                current.Clear() |> ignore

                                            let printable =
                                                if ch = '\t' then
                                                    replaced <- replaced + 1
                                                    ' '
                                                elif ch < ' ' || ch > '~' then
                                                    replaced <- replaced + 1
                                                    '?'
                                                else
                                                    ch

                                            current.Append printable |> ignore

                                        position <- position + 1

                                    if current.Length > 0 then
                                        lines.Add(current.ToString())

                                    let truncated = raw.Length > count || lines.Count > maxLines

                                    if lines.Count > maxLines then
                                        reason SceneTruncation.LabelCharacters

                                    let shown = lines |> Seq.truncate maxLines |> Seq.toList

                                    let width =
                                        shown |> List.map (fun line -> float line.Length * 8.0) |> List.fold max 0.0

                                    let textHeight = max 14.0 (float shown.Length * 14.0)

                                    let labelX =
                                        if directive.LabelPosition = LabelPosition.OuterLeft then
                                            shape.X - width - 8.0
                                        else
                                            shape.X + shape.Width / 2.0 - width / 2.0

                                    let labelTop = shape.Y + shape.Height / 2.0 - textHeight / 2.0

                                    Some {
                                        Bounds = {
                                            X = labelX
                                            Y = labelTop
                                            Width = width
                                            Height = textHeight
                                        }
                                        Lines =
                                            shown
                                            |> List.mapi (fun row text -> {
                                                Text = text
                                                X = labelX
                                                Baseline = labelTop + 11.0 + float row * 14.0
                                                Width = float text.Length * 8.0
                                            })
                                        CellWidth = 8.0
                                        FontSize = 12.0
                                        LineHeight = 14.0
                                        Fill =
                                            if
                                                directive.Kind <> DrawingKind.Pin
                                                && directive.LabelPosition = LabelPosition.InnerCenter
                                            then
                                                contrastingText directive.Background
                                            else
                                                color options.Theme.Text
                                        ReplacedCodeUnits = replaced
                                        IsTruncated = truncated
                                    })

                    let source =
                        if options.Redaction.Addresses then
                            None
                        else
                            Some {
                                Runtime = directive.Runtime.Index
                                Heap = directive.Heap
                                StatementIndex = directive.StatementIndex
                                Kind =
                                    match directive.Entity with
                                    | Some entity when entity.IsFree = Some true -> "Free"
                                    | Some entity -> string entity.Kind
                                    | None -> string directive.Kind
                                Address = address directive.Address
                                Size = number directive.Size
                                SegmentAddress =
                                    directive.Entity |> Option.map (fun entity -> address entity.SegmentAddress)
                                MethodTable =
                                    directive.Entity
                                    |> Option.bind _.TypeIdentity
                                    |> Option.map (fun typ -> address typ.MethodTable)
                            }

                    elements.Add {
                        Id = "element-" + index.ToString(invariant)
                        LaneId = lane.Id
                        Layer = layer directive
                        Geometry = geometry
                        Bounds = shape
                        Style = {
                            Fill = color directive.Background
                            Stroke =
                                color (
                                    if directive.Kind = DrawingKind.Pin then
                                        directive.Background
                                    else
                                        options.Theme.Stroke
                                )
                            StrokeWidth = 1.0
                        }
                        Text = text
                        Source = source
                        IsClipped =
                            not options.Redaction.Addresses
                            && (left <> bigint directive.Address
                                || right <> bigint directive.Address + bigint directive.Size)
                    }

                guard ()

                if not options.Redaction.Addresses then
                    packPinRows guard admitted lanes elements

                    if lanes.Count > 0 then
                        let last = lanes[lanes.Count - 1].Bounds
                        top <- last.Y + last.Height + 16.0

                let width =
                    if options.Redaction.Addresses && laneOrdinals.Count > 0 then
                        max plotWidth (float (laneOrdinals.Values |> Seq.max) * 24.0)
                    else
                        plotWidth

                let finalLanes =
                    lanes
                    |> Seq.map (fun lane -> {
                        lane with
                            Bounds = { lane.Bounds with Width = width }
                    })
                    |> Seq.toList

                let gaps =
                    compact
                    |> Option.map (fun layout ->
                        let legend =
                            "Compressed gaps contain no selected ranges; not necessarily free or unmapped memory. Horizontal distance is not linear."

                        let legendWidth = float legend.Length * 8.0

                        {
                            Offsets = layout.Offsets
                            Band = {
                                X = 0.0
                                Y = laneTop
                                Width = layout.GapWidth
                                Height = top - 16.0 - laneTop
                            }
                            Lines = [
                                ({ X = layout.GapWidth * 0.25; Y = 28.0 }, { X = layout.GapWidth * 0.5; Y = 20.0 })
                                ({ X = layout.GapWidth * 0.5; Y = 28.0 }, { X = layout.GapWidth * 0.75; Y = 20.0 })
                            ]
                            Style = {
                                Fill = "#fff1cc"
                                Stroke = "#9a6700"
                                StrokeWidth = min 1.0 (layout.GapWidth / 8.0)
                            }
                            Legend = {
                                Bounds = {
                                    X = plotX
                                    Y = 8.0
                                    Width = legendWidth
                                    Height = 14.0
                                }
                                Lines = [
                                    {
                                        Text = legend
                                        X = plotX
                                        Baseline = 19.0
                                        Width = legendWidth
                                    }
                                ]
                                CellWidth = 8.0
                                FontSize = 12.0
                                LineHeight = 14.0
                                Fill = color options.Theme.Text
                                ReplacedCodeUnits = 0
                                IsTruncated = false
                            }
                        })

                let contentRight =
                    elements
                    |> Seq.choose _.Text
                    |> Seq.fold (fun right text -> max right (text.Bounds.X + text.Bounds.Width)) (plotX + width)

                status <-
                    if reasons.Count = 0 then
                        SceneStatus.Complete
                    else
                        SceneStatus.Truncated(List.ofSeq reasons)

                let queryStatus, queryTruncation = queryState result.Status

                scene <-
                    Some {
                        Version = 2
                        OwningSnapshot = result.SnapshotId
                        SceneBounds = {
                            X = 0.0
                            Y = 0.0
                            Width = contentRight + 16.0
                            Height = max 32.0 top
                        }
                        SceneLanes = finalLanes
                        SceneLayout = options.Layout
                        SceneGaps = gaps
                        SceneElements =
                            elements
                            |> Seq.mapi (fun index element ->
                                let directive, _, _ = admitted[index]
                                (element.Layer, directive.StatementIndex, index), element)
                            |> Seq.sortBy fst
                            |> Seq.map snd
                            |> Seq.toList
                        SceneTheme = {
                            Background = color options.Theme.Background
                            Stroke = color options.Theme.Stroke
                            Text = color options.Theme.Text
                        }
                        SceneRedaction = options.Redaction
                        SceneCompleteness = {
                            QueryStatus = queryStatus
                            QueryTruncation = queryTruncation
                            SourceAvailable = result.SourceAvailable
                            SourcePartial = result.SourcePartial
                            SourceDiagnosticCount = result.SourceDiagnostics.Count
                            SceneStatus = status
                        }
                    }

                guard ()
        with
        | SceneFailure diagnostic ->
            status <- SceneStatus.Failed
            diagnostics <- [ diagnostic ]
            scene <- None
        | SceneStopped reason ->
            status <- SceneStatus.Truncated [ reason ]
            scene <- None
        | :? OperationCanceledException when
            not (obj.ReferenceEquals(context, null))
            && context.CancellationToken.IsCancellationRequested
            ->
            status <- SceneStatus.Cancelled
            scene <- None

        {
            Status = status
            Scene = scene
            Diagnostics = diagnostics
        }
