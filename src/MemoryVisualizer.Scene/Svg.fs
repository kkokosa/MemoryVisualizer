namespace MemoryVisualizer.Scene

open System
open System.Globalization
open System.IO
open System.Text
open System.Threading
open System.Xml

exception private SvgLimitExceeded

type private LimitedStream(inner: Stream, maximum: int, token: CancellationToken) =
    inherit Stream()
    let mutable written = 0
    member _.Written = written
    override _.CanRead = false
    override _.CanSeek = false
    override _.CanWrite = true
    override _.Length = int64 written

    override _.Position
        with get () = int64 written
        and set _ = raise (NotSupportedException())

    override _.Flush() =
        token.ThrowIfCancellationRequested()
        inner.Flush()

    override _.Read(_, _, _) = raise (NotSupportedException())
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())

    override _.Write(buffer, offset, count) =
        token.ThrowIfCancellationRequested()

        if count > maximum - written then
            raise SvgLimitExceeded

        inner.Write(buffer, offset, count)
        written <- written + count

[<RequireQualifiedAccess>]
module Svg =
    let validate (limits: SvgLimits) =
        if
            obj.ReferenceEquals(limits, null)
            || limits.MaxBytes < 1
            || limits.MaxBytes > SvgLimits.defaults.MaxBytes
        then
            Error(SvgError.InvalidInput "MaxBytes must be between 1 and 8388608.")
        else
            Ok()

    /// On failure the caller must discard the stream. It remains open and never exceeds MaxBytes.
    let write limits (scene: PositionedScene) (stream: Stream) (token: CancellationToken) =
        match validate limits with
        | Error error -> Error error
        | Ok() when obj.ReferenceEquals(scene, null) || isNull stream || not stream.CanWrite ->
            Error(SvgError.InvalidInput "A positioned scene and writable output stream are required.")
        | Ok() ->
            try
                token.ThrowIfCancellationRequested()
                use bounded = new LimitedStream(stream, limits.MaxBytes, token)

                let settings =
                    XmlWriterSettings(
                        Encoding = UTF8Encoding(false),
                        Indent = false,
                        CloseOutput = false,
                        OmitXmlDeclaration = true
                    )

                use xml = XmlWriter.Create(bounded, settings)

                let number (value: float) =
                    value.ToString("G17", CultureInfo.InvariantCulture)

                let integer (value: int) =
                    value.ToString(CultureInfo.InvariantCulture)

                let boolean value = if value then "true" else "false"
                let attr (name: string) (value: string) = xml.WriteAttributeString(name, value)

                let start name =
                    xml.WriteStartElement(name, "http://www.w3.org/2000/svg")

                let finish () = xml.WriteEndElement()

                let rect (bounds: SceneBounds) =
                    attr "x" (number bounds.X)
                    attr "y" (number bounds.Y)
                    attr "width" (number bounds.Width)
                    attr "height" (number bounds.Height)

                let style (value: ResolvedStyle) =
                    attr "fill" value.Fill
                    attr "stroke" value.Stroke
                    attr "stroke-width" (number value.StrokeWidth)

                let line (first: ScenePoint) (last: ScenePoint) =
                    attr "x1" (number first.X)
                    attr "y1" (number first.Y)
                    attr "x2" (number last.X)
                    attr "y2" (number last.Y)

                let text id (text: SceneText) =
                    start "defs"
                    start "clipPath"
                    attr "id" (id + "-text-clip")
                    attr "clipPathUnits" "userSpaceOnUse"
                    start "rect"
                    rect text.Bounds
                    finish ()
                    finish ()
                    finish ()
                    start "g"
                    attr "clip-path" ("url(#" + id + "-text-clip)")
                    attr "data-text-replacements" (integer text.ReplacedCodeUnits)
                    attr "data-text-truncated" (boolean text.IsTruncated)

                    for line in text.Lines do
                        token.ThrowIfCancellationRequested()

                        if line.Text.Length > 0 then
                            start "text"
                            attr "x" (number line.X)
                            attr "y" (number line.Baseline)
                            attr "font-family" "monospace"
                            attr "font-size" (number text.FontSize)
                            attr "font-variant-ligatures" "none"
                            attr "fill" text.Fill
                            attr "textLength" (number line.Width)
                            attr "lengthAdjust" "spacingAndGlyphs"

                            xml.WriteAttributeString("xml", "space", "http://www.w3.org/XML/1998/namespace", "preserve")

                            xml.WriteString line.Text
                            finish ()

                    finish ()

                let status, reasons =
                    match scene.Completeness.SceneStatus with
                    | SceneStatus.Complete -> "complete", []
                    | SceneStatus.Truncated reasons -> "truncated", reasons |> List.map string
                    | SceneStatus.Cancelled -> "cancelled", []
                    | SceneStatus.Failed -> "failed", []

                start "svg"
                attr "version" "1.1"
                attr "width" (number scene.Bounds.Width)
                attr "height" (number scene.Bounds.Height)

                attr
                    "viewBox"
                    (String.Join(
                        " ",
                        [|
                            number scene.Bounds.X
                            number scene.Bounds.Y
                            number scene.Bounds.Width
                            number scene.Bounds.Height
                        |]
                    ))

                attr "data-scene-version" (integer scene.SchemaVersion)
                attr "data-scene-status" status
                attr "data-scene-truncation" (String.concat "," reasons)
                attr "data-query-status" scene.Completeness.QueryStatus
                attr "data-query-truncation" (String.concat "," scene.Completeness.QueryTruncation)
                attr "data-source-available" (boolean scene.Completeness.SourceAvailable)
                attr "data-source-partial" (boolean scene.Completeness.SourcePartial)
                attr "data-source-diagnostic-count" (integer scene.Completeness.SourceDiagnosticCount)

                attr
                    "data-address-layout"
                    (if scene.Redaction.Addresses then "schematic"
                     elif scene.Layout = SceneLayout.Compact then "compact"
                     else "relative")

                attr "data-redact-addresses" (boolean scene.Redaction.Addresses)
                attr "data-redact-strings" (boolean scene.Redaction.Strings)
                attr "data-redact-paths" (boolean scene.Redaction.Paths)
                attr "data-redact-labels" (boolean scene.Redaction.Labels)

                if scene.Gaps.IsSome && not scene.Redaction.Addresses then
                    attr "aria-describedby" "address-layout-description"

                start "rect"
                rect scene.Bounds
                attr "fill" scene.Theme.Background
                finish ()

                scene.Gaps
                |> Option.iter (fun markers ->
                    for index, offset in markers.Offsets |> List.indexed do
                        token.ThrowIfCancellationRequested()
                        start "g"
                        attr "id" ("address-gap-" + integer index)
                        attr "data-kind" "address-gap"
                        attr "transform" ("translate(" + number offset + ",0)")
                        attr "pointer-events" "none"
                        start "rect"
                        rect markers.Band
                        attr "fill" markers.Style.Fill
                        attr "stroke" "none"
                        finish ()

                        for first, last in markers.Lines do
                            start "line"
                            line first last
                            attr "fill" "none"
                            attr "stroke" markers.Style.Stroke
                            attr "stroke-width" (number markers.Style.StrokeWidth)
                            finish ()

                        finish ()

                    start "desc"
                    attr "id" "address-layout-description"
                    xml.WriteString(markers.Legend.Lines |> List.map _.Text |> String.concat " ")
                    finish ())

                for lane in scene.Lanes do
                    token.ThrowIfCancellationRequested()
                    start "g"
                    attr "id" lane.Id
                    attr "data-kind" "lane"
                    lane.Runtime |> Option.iter (integer >> attr "data-runtime")
                    lane.Heap |> Option.iter (integer >> attr "data-heap")
                    finish ()

                for element in scene.Elements do
                    token.ThrowIfCancellationRequested()
                    start "g"
                    attr "id" element.Id
                    attr "data-lane" element.LaneId
                    attr "data-layer" (integer element.Layer)
                    attr "data-clipped" (boolean element.IsClipped)

                    element.Source
                    |> Option.iter (fun source ->
                        attr "data-runtime" (integer source.Runtime)
                        attr "data-heap" (integer source.Heap)
                        attr "data-statement" (integer source.StatementIndex)
                        attr "data-kind" source.Kind
                        attr "data-address" source.Address
                        attr "data-size" source.Size
                        source.SegmentAddress |> Option.iter (attr "data-segment-address")
                        source.MethodTable |> Option.iter (attr "data-method-table"))

                    match element.Geometry with
                    | SceneGeometry.Rectangle bounds ->
                        start "rect"
                        rect bounds
                    | SceneGeometry.Line(first, last) ->
                        start "line"
                        line first last

                    style element.Style
                    finish ()

                    element.Text |> Option.iter (text element.Id)

                    finish ()

                finish ()
                xml.Flush()
                token.ThrowIfCancellationRequested()
                Ok bounded.Written
            with
            | SvgLimitExceeded -> Error SvgError.OutputLimitExceeded
            | :? OperationCanceledException when token.IsCancellationRequested -> Error SvgError.Cancelled
            | :? IOException as error -> Error(SvgError.WriteFailed error.Message)
            | :? UnauthorizedAccessException as error -> Error(SvgError.WriteFailed error.Message)
