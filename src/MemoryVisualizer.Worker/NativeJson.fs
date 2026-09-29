namespace MemoryVisualizer.Worker

open System
open System.Globalization
open System.Text.Json
open MemoryVisualizer.Core
open MemoryVisualizer.Query
open MemoryVisualizer.Scene

[<RequireQualifiedAccess>]
module NativeJson =
    let private nullable mapping =
        function
        | Some value -> box (mapping value)
        | None -> null

    let private text (value: string) =
        // JsonSerializer may allocate an escaping buffer before writing to the bounded stream.
        if not (isNull value) && value.Length > Protocol.MaxFrameBytes then
            raise (NativeFailure "OutputLimit")

        value

    let private address (value: uint64) =
        "0x" + value.ToString("x16", CultureInfo.InvariantCulture)

    let entity (value: SelectedEntity) : obj =
        box {|
            kind = string value.Kind
            snapshotId = SnapshotId.format value.Runtime.SnapshotId
            runtime = value.Runtime.Index
            heap = value.Heap
            segmentAddress = address value.SegmentAddress
            address = address value.Address
            size = Protocol.idValue value.Size
            heapKind =
                (match value.HeapKind with
                 | HeapKind.Unknown name -> text name
                 | kind -> string kind)
            ``end`` = value.End |> nullable address
            methodTable = value.TypeIdentity |> nullable (fun identity -> address identity.MethodTable)
            ``type`` = value.TypeName |> nullable text
            generation = value.Generation |> nullable id
            isFree = value.IsFree |> nullable id
        |}

    let private value =
        function
        | QueryValue.Unsigned value ->
            box {|
                kind = "uint64"
                value = Protocol.idValue value
            |}
        | QueryValue.Text value -> box {| kind = "text"; value = text value |}
        | QueryValue.Boolean value -> box {| kind = "boolean"; value = value |}
        | QueryValue.Entity value ->
            box {|
                kind = "entity"
                value = entity value
            |}
        | QueryValue.Missing ->
            box {|
                kind = "missing"
                value = (null: obj)
            |}

    let row (item: QueryRow) : obj =
        box {|
            statementIndex = item.StatementIndex
            entity = entity item.Entity
            values =
                item.Values
                |> List.map (fun (name, item) -> {|
                    name = text name
                    value = value item
                |})
                |> List.toArray
        |}

    let private bounds (value: SceneBounds) = {|
        x = value.X
        y = value.Y
        width = value.Width
        height = value.Height
    |}

    let private point (value: ScenePoint) = {| x = value.X; y = value.Y |}

    let element (item: SceneElement) : obj =
        box {|
            id = item.Id
            laneId = item.LaneId
            layer = item.Layer
            geometry =
                match item.Geometry with
                | SceneGeometry.Rectangle value ->
                    box {|
                        kind = "rectangle"
                        bounds = bounds value
                    |}
                | SceneGeometry.Line(start, finish) ->
                    box {|
                        kind = "line"
                        start = point start
                        finish = point finish
                    |}
            bounds = bounds item.Bounds
            style = {|
                fill = item.Style.Fill
                stroke = item.Style.Stroke
                strokeWidth = item.Style.StrokeWidth
            |}
            text =
                item.Text
                |> nullable (fun value -> {|
                    bounds = bounds value.Bounds
                    lines =
                        value.Lines
                        |> List.map (fun line -> {|
                            text = line.Text
                            x = line.X
                            baseline = line.Baseline
                            width = line.Width
                        |})
                        |> List.toArray
                    cellWidth = value.CellWidth
                    fontSize = value.FontSize
                    lineHeight = value.LineHeight
                    fill = value.Fill
                    replacedCodeUnits = value.ReplacedCodeUnits
                    isTruncated = value.IsTruncated
                |})
            source =
                item.Source
                |> nullable (fun value -> {|
                    runtime = value.Runtime
                    heap = value.Heap
                    statementIndex = value.StatementIndex
                    kind = value.Kind
                    address = value.Address
                    size = value.Size
                    segmentAddress = Option.toObj value.SegmentAddress
                    methodTable = Option.toObj value.MethodTable
                |})
            isClipped = item.IsClipped
        |}

    let private sceneStatus =
        function
        | SceneStatus.Complete -> "complete", []
        | SceneStatus.Truncated reasons -> "truncated", List.map string reasons
        | SceneStatus.Cancelled -> "cancelled", []
        | SceneStatus.Failed -> "failed", []

    let sceneInfo sceneId (scene: PositionedScene) : obj =
        let status, reasons = sceneStatus scene.Completeness.SceneStatus

        box {|
            schemaVersion = scene.SchemaVersion
            sceneId = Protocol.idValue sceneId
            snapshotId = SnapshotId.format scene.SnapshotId
            bounds = bounds scene.Bounds
            lanes =
                scene.Lanes
                |> List.map (fun lane -> {|
                    id = lane.Id
                    runtime = nullable id lane.Runtime
                    heap = nullable id lane.Heap
                    bounds = bounds lane.Bounds
                |})
                |> List.toArray
            theme = {|
                background = scene.Theme.Background
                stroke = scene.Theme.Stroke
                text = scene.Theme.Text
            |}
            redaction = {|
                addresses = scene.Redaction.Addresses
                strings = scene.Redaction.Strings
                paths = scene.Redaction.Paths
                labels = scene.Redaction.Labels
            |}
            elementCount = scene.Elements.Length
            status = status
            truncationReasons = List.toArray reasons
        |}

    let queryInfo queryId sceneId (result: QueryResult) (scene: PositionedScene option) : obj =
        let status, reasons, diagnostics =
            match result.Status with
            | QueryStatus.Complete -> "complete", [], []
            | QueryStatus.Truncated reasons -> "truncated", List.map string reasons, []
            | QueryStatus.Cancelled -> "cancelled", [], []
            | QueryStatus.Failed errors -> "failed", [], errors

        box {|
            tag = "query"
            queryId = Protocol.idValue queryId
            status = status
            sourceAvailable = result.SourceAvailable
            sourcePartial = result.SourcePartial
            sourceDiagnosticCount = result.SourceDiagnostics.Count
            rowCount = result.Rows.Length
            candidates = Protocol.idValue (uint64 result.Candidates)
            truncationReasons = List.toArray reasons
            diagnostics =
                diagnostics
                |> List.map (fun diagnostic -> {|
                    code = diagnostic.Code
                    message = text diagnostic.Message
                    span = {|
                        offset = diagnostic.Span.Offset
                        length = diagnostic.Span.Length
                        line = diagnostic.Span.Line
                        column = diagnostic.Span.Column
                    |}
                |})
                |> List.toArray
            scene = scene |> nullable (sceneInfo sceneId)
        |}

    /// Every candidate item is serialized once into a bounded buffer, never an unbounded result array.
    let page (request: NativeRequest) tag owner cursor size (items: 'T array) convert =
        let offset = defaultArg cursor 0UL

        if offset > uint64 items.Length then
            raise (NativeFailure "InvalidRequest")

        let selected = ResizeArray<JsonElement>()
        let mutable budget = Protocol.MaxFrameBytes - 1024
        let mutable index = int offset
        let mutable full = false

        while not full && index < items.Length && selected.Count < size do
            let bytes =
                NativeProtocol.serialize (Protocol.MaxFrameBytes - 1024) (convert items[index])

            if bytes.Length + 1 > budget then
                if selected.Count = 0 then
                    raise (NativeFailure "OutputLimit")

                full <- true
            else
                use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
                selected.Add(document.RootElement.Clone())
                budget <- budget - bytes.Length - 1
                index <- index + 1

        let next =
            if index < items.Length then
                Protocol.idValue (uint64 index)
            else
                null

        let result: obj =
            match tag with
            | "rows" ->
                box {|
                    tag = tag
                    queryId = Protocol.idValue owner
                    items = selected.ToArray()
                    nextCursor = next
                |}
            | "elements" ->
                box {|
                    tag = tag
                    sceneId = Protocol.idValue owner
                    items = selected.ToArray()
                    nextCursor = next
                |}
            | _ ->
                box {|
                    tag = "details"
                    items = selected.ToArray()
                    nextCursor = next
                |}

        NativeProtocol.success request request.Snapshot result
