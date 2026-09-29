module MemoryVisualizer.Core.Tests.SceneTests

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open System.Xml.Linq
open MemoryVisualizer.Core
open MemoryVisualizer.Query
open MemoryVisualizer.Scene
open Xunit

let private unwrap =
    function
    | Ok value -> value
    | Error error -> failwithf "%A" error

let private token = CancellationToken.None

let private id =
    SnapshotId.create (Guid.Parse "fc644f7b-cf63-4c20-82fa-7efea63e09e1") |> unwrap

let private runtime index : RuntimeIdentity = { SnapshotId = id; Index = index }
let private context () = SceneExecutionContext.create token

let private directive runtimeIndex heap address size : DrawingDirective = {
    StatementIndex = 0
    Kind = DrawingKind.Box
    Runtime = runtime runtimeIndex
    Heap = heap
    Address = address
    Size = size
    Entity = None
    Label = None
    LabelPosition = LabelPosition.InnerCenter
    Background = "Red"
    Width = 16
}

let private query directives : QueryResult = {
    SnapshotId = id
    Status = QueryStatus.Complete
    SourcePartial = false
    SourceAvailable = true
    SourceDiagnostics = Array.AsReadOnly [||]
    Rows = []
    Directives = directives
    Candidates = 0
}

let private build options result = Scene.build options result (context ())

let private scene result =
    Assert.Equal(SceneStatus.Complete, result.Status)
    Assert.Empty result.Diagnostics
    result.Scene |> Option.defaultWith (fun () -> failwith "Missing scene")

let private draw directives =
    build SceneOptions.defaults (query directives) |> scene

let private svg value =
    use stream = new MemoryStream()
    let count = Svg.write SvgLimits.defaults value stream token |> unwrap
    Assert.Equal(int stream.Length, count)
    Encoding.UTF8.GetString(stream.ToArray())

let private close (expected: float) (actual: float) = Assert.Equal(expected, actual, 10)

[<Fact>]
let ``Global address geometry preserves gaps overlaps and explicit sorted runtime heap lanes`` () =
    let value =
        draw [
            directive 1 0 0x120UL 16UL
            directive 0 1 0x120UL 16UL
            directive 0 0 0x100UL 64UL
            directive 0 0 0x110UL 32UL
            directive 0 0 0x180UL 128UL
        ]

    Assert.Equal(id, value.SnapshotId)
    Assert.Equal(1, value.SchemaVersion)
    Assert.Equal(3, value.Lanes.Length)

    Assert.Equal<(int option * int option) list>(
        [ Some 0, Some 0; Some 0, Some 1; Some 1, Some 0 ],
        value.Lanes |> List.map (fun lane -> lane.Runtime, lane.Heap)
    )

    let byId = value.Elements |> List.sortBy _.Id
    close 656.0 byId[0].Bounds.X
    close 656.0 byId[1].Bounds.X
    close 528.0 byId[2].Bounds.X
    close 256.0 byId[2].Bounds.Width
    close 592.0 byId[3].Bounds.X
    close 128.0 byId[3].Bounds.Width
    close 1040.0 byId[4].Bounds.X
    close 512.0 byId[4].Bounds.Width
    close 16.0 value.Lanes[0].Bounds.Y
    close 128.0 value.Lanes[1].Bounds.Y
    close 240.0 value.Lanes[2].Bounds.Y
    Assert.NotEqual<string>(byId[0].LaneId, byId[1].LaneId)

[<Theory>]
[<InlineData(0UL)>]
[<InlineData(9007199254740993UL)>]
[<InlineData(18446744073709551360UL)>]
let ``Relative arithmetic occurs before float conversion including exclusive end two to64`` origin =
    let value =
        draw [
            directive 0 0 origin 256UL
            directive 0 0 (origin + 1UL) 1UL
            directive 0 0 (origin + 255UL) 1UL
        ]

    close 1024.0 value.Elements[0].Bounds.Width
    close 532.0 value.Elements[1].Bounds.X
    close 4.0 value.Elements[1].Bounds.Width
    close 1548.0 value.Elements[2].Bounds.X
    close 4.0 value.Elements[2].Bounds.Width

[<Fact>]
let ``Viewport clips intervals and pins at boundary without wrapping or stretching gaps`` () =
    let options = {
        SceneOptions.defaults with
            Viewport =
                Some {
                    Start = UInt64.MaxValue - 15UL
                    Size = 16UL
                }
    }

    let value =
        build
            options
            (query [
                directive 0 0 (UInt64.MaxValue - 31UL) 32UL
                {
                    directive 0 0 (UInt64.MaxValue - 31UL) 32UL with
                        Kind = DrawingKind.Pin
                }
                directive 0 0 100UL 16UL
                directive 0 0 UInt64.MaxValue 1UL
            ])
        |> scene

    Assert.Equal(3, value.Elements.Length)
    Assert.True value.Elements[0].IsClipped
    close 528.0 value.Elements[0].Bounds.X
    close 1024.0 value.Elements[0].Bounds.Width
    close 1488.0 value.Elements[1].Bounds.X

    match value.Elements[2].Geometry with
    | SceneGeometry.Line(first, _) -> close 528.0 first.X
    | _ -> failwith "Expected pin"

[<Fact>]
let ``Shared MQL segment generation free object pin and memory composition has semantic layer order`` () =
    let snapshot = IndexedHeapSnapshotTests.fixture ()

    use store =
        IndexedHeapSnapshot.Create(snapshot, SnapshotIndexLimits.defaults, token)
        |> unwrap

    let source =
        "MATCH(o:Object) WHERE o.Runtime=0 AND o.Heap=0 RETURN o AS PIN(Background=Blue);"
        + "MATCH(o:Object) WHERE o.Runtime=0 AND o.Heap=0 RETURN o AS BOX;"
        + "MATCH(g:Generation) WHERE g.Runtime=0 AND g.Heap=0 RETURN g AS BOX(Label=g.Generation);"
        + "MATCH(s:Segment) WHERE s.Runtime=0 AND s.Heap=0 RETURN s AS BOX;"
        + "DRAW Memory(0x100,0x200,Runtime=0,Heap=0)"

    let plan = Mql.compile QueryLimits.defaults snapshot.Metadata.Id source |> unwrap

    let result =
        Mql.execute QueryLimits.defaults plan store (QueryExecutionContext.create token)

    let value = build SceneOptions.defaults result |> scene
    Assert.Equal<int list>([ 0; 1; 2; 3; 4; 4; 4; 5; 5; 5; 5 ], value.Elements |> List.map _.Layer)
    Assert.Equal("Memory", value.Elements[0].Source.Value.Kind)
    Assert.Equal("Free", value.Elements[3].Source.Value.Kind)

    Assert.All(
        value.Elements |> List.filter (fun item -> item.Layer = 5),
        fun item -> Assert.Equal("#0000ff", item.Style.Stroke)
    )

[<Fact>]
let ``Labels are centered or outer left relative to each shape and narrow plots expand scene extent`` () =
    for position in [ LabelPosition.InnerCenter; LabelPosition.OuterLeft ] do
        let value =
            build
                {
                    SceneOptions.defaults with
                        PlotWidth = 64
                }
                (query [
                    {
                        directive 0 0 100UL 1UL with
                            Label = Some(QueryValue.Text(String.replicate 64 "A"))
                            LabelPosition = position
                    }
                ])
            |> scene

        let element = value.Elements.Head
        let text = element.Text.Value

        let expected =
            if position = LabelPosition.InnerCenter then
                element.Bounds.X + element.Bounds.Width / 2.0 - 256.0
            else
                element.Bounds.X - 520.0

        close expected text.Bounds.X
        close (element.Bounds.Y + 1.0) text.Bounds.Y
        close 512.0 text.Bounds.Width
        Assert.True(text.Bounds.X >= value.Bounds.X)
        Assert.True(text.Bounds.X + text.Bounds.Width <= value.Bounds.Width)
        Assert.True(text.Bounds.Y + text.Bounds.Height <= value.Bounds.Height)
        Assert.False text.IsTruncated

[<Fact>]
let ``Text uses deterministic ASCII cells explicit fallback and XML escaping without active content`` () =
    let raw = "<script a=\"x\">&'\u0000\u0001\u001f\u007f\u00e9\ud800\t\r\nOK"

    let value =
        draw [
            {
                directive 0 0 1UL 10UL with
                    Label = Some(QueryValue.Text raw)
            }
        ]

    let text = value.Elements.Head.Text.Value
    Assert.Equal(7, text.ReplacedCodeUnits)
    Assert.Equal("<script a=\"x\">&'?????? ", text.Lines[0].Text)
    Assert.Equal("OK", text.Lines[1].Text)
    close (float text.Lines[0].Text.Length * 8.0) text.Lines[0].Width
    let output = svg value
    let doc = XDocument.Parse output
    let ns = XNamespace.Get "http://www.w3.org/2000/svg"
    Assert.Empty(doc.Descendants(ns + "script"))
    Assert.Empty(doc.Descendants(ns + "foreignObject"))
    Assert.Empty(doc.Descendants(ns + "style"))
    Assert.Contains("&lt;script", output)
    Assert.Contains("data-text-replacements=\"7\"", output)
    Assert.DoesNotContain("\u0000", output, StringComparison.Ordinal)
    Assert.DoesNotContain("href=", output)
    Assert.DoesNotContain(SnapshotId.format id, output)

[<Theory>]
[<InlineData("Black", "#ffffff")>]
[<InlineData("Blue", "#ffffff")>]
[<InlineData("Green", "#ffffff")>]
[<InlineData("Red", "#000000")>]
[<InlineData("White", "#000000")>]
[<InlineData("Yellow", "#000000")>]
let ``Inner labels resolve contrasting colors and PIN honors declared marker color`` background expected =
    let value =
        draw [
            {
                directive 0 0 1UL 10UL with
                    Background = background
                    Label = Some(QueryValue.Text "readable")
            }
        ]

    Assert.Equal(expected, value.Elements.Head.Text.Value.Fill)

    let pin =
        draw [
            {
                directive 0 0 1UL 10UL with
                    Kind = DrawingKind.Pin
                    Background = background
            }
        ]

    Assert.Equal(pin.Elements.Head.Style.Fill, pin.Elements.Head.Style.Stroke)

[<Fact>]
let ``PIN label and bounds refer to the marker rather than the object's byte extent`` () =
    let value =
        draw [
            {
                directive 0 0 1UL 100UL with
                    Kind = DrawingKind.Pin
                    Label = Some(QueryValue.Text "PIN")
            }
        ]

    let pin = value.Elements.Head
    close 0.0 pin.Bounds.Width
    close 528.0 pin.Bounds.X
    close 516.0 pin.Text.Value.Bounds.X
    Assert.Equal("100", pin.Source.Value.Size)

[<Theory>]
[<InlineData("addresses")>]
[<InlineData("strings")>]
[<InlineData("paths")>]
[<InlineData("labels")>]
let ``Redaction conservatively removes opaque text on every serialized surface`` policy =
    let redaction =
        match policy with
        | "addresses" -> {
            RedactionPolicy.none with
                Addresses = true
          }
        | "strings" -> {
            RedactionPolicy.none with
                Strings = true
          }
        | "paths" -> {
            RedactionPolicy.none with
                Paths = true
          }
        | _ -> {
            RedactionPolicy.none with
                Labels = true
          }

    let value =
        build
            {
                SceneOptions.defaults with
                    Redaction = redaction
            }
            (query [
                {
                    directive 0 0 0xdeadbeefUL 16UL with
                        Label = Some(QueryValue.Text "SECRET C:\\private\\file 3735928559")
                }
            ])
        |> scene

    let output = svg value
    Assert.DoesNotContain("SECRET", output)
    Assert.DoesNotContain("private", output)
    Assert.DoesNotContain("3735928559", output)
    Assert.True value.Elements.Head.Text.IsNone

    if policy = "addresses" then
        Assert.True value.Elements.Head.Source.IsNone
        Assert.True value.Lanes.Head.Runtime.IsNone
        Assert.DoesNotContain("deadbeef", output)
        Assert.DoesNotContain("data-size=", output)

[<Fact>]
let ``Address redaction removes geometry derived leaks sizes widths gaps and numeric labels`` () =
    let options = {
        SceneOptions.defaults with
            Redaction = {
                RedactionPolicy.none with
                    Addresses = true
            }
    }

    let first = [
        {
            directive 0 0 1UL 100UL with
                Width = 4096
                Label = Some(QueryValue.Unsigned 123UL)
        }
        directive 0 0 500000UL 16UL
    ]

    let second = [
        {
            directive 6 9 9007199254740993UL 1UL with
                Width = 1
                Label = Some(QueryValue.Text "different secret")
        }
        directive 6 9 UInt64.MaxValue 1UL
    ]

    let one = build options (query first) |> scene
    let two = build options (query second) |> scene
    Assert.Equal(svg one, svg two)
    close 16.0 one.Elements.Head.Bounds.Width
    close 16.0 one.Elements.Head.Bounds.Height
    close 552.0 one.Elements[1].Bounds.X

[<Theory>]
[<InlineData("directives")>]
[<InlineData("elements")>]
[<InlineData("lanes")>]
[<InlineData("label")>]
[<InlineData("total")>]
let ``Each scene budget is complete at exact capacity and explicit on one beyond`` budget =
    let limits, expected =
        match budget with
        | "directives" ->
            {
                SceneLimits.defaults with
                    MaxDirectives = 1
            },
            SceneTruncation.Directives
        | "elements" ->
            {
                SceneLimits.defaults with
                    MaxElements = 1
            },
            SceneTruncation.Elements
        | "lanes" ->
            {
                SceneLimits.defaults with
                    MaxLanes = 1
            },
            SceneTruncation.Lanes
        | "label" ->
            {
                SceneLimits.defaults with
                    MaxLabelCharacters = 2
            },
            SceneTruncation.LabelCharacters
        | _ ->
            {
                SceneLimits.defaults with
                    MaxTotalLabelCharacters = 2
            },
            SceneTruncation.TotalLabelCharacters

    let options = {
        SceneOptions.defaults with
            Limits = limits
    }

    let one = {
        directive 0 0 1UL 1UL with
            Label = Some(QueryValue.Text "AB")
    }

    build options (query [ one ]) |> scene |> ignore

    let extra =
        if budget = "label" then
            [
                {
                    one with
                        Label = Some(QueryValue.Text "ABC")
                }
            ]
        else
            [
                one
                directive 0 1 2UL 1UL
                |> fun item -> {
                    item with
                        Label = Some(QueryValue.Text "C")
                }
            ]

    let actual = build options (query extra)
    Assert.Equal(SceneStatus.Truncated [ expected ], actual.Status)
    Assert.True actual.Scene.IsSome

[<Fact>]
let ``Empty ranges still cost inspection and simultaneous element lane budgets report both`` () =
    let options = {
        SceneOptions.defaults with
            Limits = {
                SceneLimits.defaults with
                    MaxDirectives = 1
            }
    }

    let result = build options (query [ directive 0 0 1UL 0UL; directive 0 0 1UL 0UL ])
    Assert.Equal(SceneStatus.Truncated [ SceneTruncation.Directives ], result.Status)
    Assert.Empty result.Scene.Value.Elements

    let options = {
        SceneOptions.defaults with
            Limits = {
                SceneLimits.defaults with
                    MaxElements = 1
                    MaxLanes = 1
            }
    }

    let result = build options (query [ directive 0 0 1UL 1UL; directive 0 1 1UL 1UL ])
    Assert.Equal(SceneStatus.Truncated [ SceneTruncation.Elements; SceneTruncation.Lanes ], result.Status)

[<Fact>]
let ``Huge manually constructed input is never sorted scanned or labeled beyond admission budget`` () =
    let hugeLabel = String('x', 1_000_000)

    let one = {
        directive 0 0 1UL 1UL with
            Label = Some(QueryValue.Text hugeLabel)
    }

    let invalidTail = Unchecked.defaultof<DrawingDirective>

    let result =
        build
            {
                SceneOptions.defaults with
                    Limits = {
                        SceneLimits.defaults with
                            MaxDirectives = 1
                    }
            }
            (query (one :: List.replicate 100000 invalidTail))

    Assert.Equal(SceneStatus.Truncated [ SceneTruncation.Directives; SceneTruncation.LabelCharacters ], result.Status)
    Assert.Single result.Scene.Value.Elements |> ignore

    Assert.Equal(
        128,
        result.Scene.Value.Elements.Head.Text.Value.Lines
        |> List.sumBy (fun line -> line.Text.Length)
    )

[<Fact>]
let ``Line budget counts explicit newlines and reports truncation rather than silent loss`` () =
    let result =
        build
            SceneOptions.defaults
            (query [
                {
                    directive 0 0 1UL 1UL with
                        Label = Some(QueryValue.Text "a\nb\nc")
                }
            ])

    Assert.Equal(SceneStatus.Truncated [ SceneTruncation.LabelCharacters ], result.Status)
    Assert.True result.Scene.Value.Elements.Head.Text.Value.IsTruncated

[<Theory>]
[<InlineData("\n")>]
[<InlineData("\r")>]
[<InlineData("\r\n")>]
let ``Explicit newline at cell boundary does not insert a spurious third line`` newline =
    let first = String.replicate 64 "A"

    let value =
        draw [
            {
                directive 0 0 1UL 1UL with
                    Label = Some(QueryValue.Text(first + newline + "B"))
            }
        ]

    Assert.Equal<string list>([ first; "B" ], value.Elements.Head.Text.Value.Lines |> List.map _.Text)

type private Clock() =
    inherit TimeProvider()
    member val Timestamp = 0L with get, set
    override _.TimestampFrequency = 1000L
    override this.GetTimestamp() = this.Timestamp

[<Theory>]
[<InlineData(9L, false)>]
[<InlineData(10L, true)>]
let ``Elapsed deadline is exclusive and publishes no unfinished geometry`` elapsed stopped =
    let clock = Clock()

    let options = {
        SceneOptions.defaults with
            Limits = {
                SceneLimits.defaults with
                    MaxElapsedMilliseconds = 10
            }
    }

    let ctx = {
        context () with
            TimeProvider = clock
            IsSnapshotCurrent =
                fun _ ->
                    clock.Timestamp <- elapsed
                    true
    }

    let result = Scene.build options (query [ directive 0 0 1UL 1UL ]) ctx

    Assert.Equal(
        (if stopped then
             SceneStatus.Truncated [ SceneTruncation.ElapsedTime ]
         else
             SceneStatus.Complete),
        result.Status
    )

    Assert.Equal(not stopped, result.Scene.IsSome)

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``Cancellation and staleness discard even already built geometry`` stale =
    use cancellation = new CancellationTokenSource()
    let mutable checks = 0

    let ctx = {
        SceneExecutionContext.create cancellation.Token with
            IsSnapshotCurrent =
                fun _ ->
                    checks <- checks + 1

                    if checks = 6 && not stale then
                        cancellation.Cancel()

                    not (stale && checks = 6)
    }

    let result = Scene.build SceneOptions.defaults (query [ directive 0 0 1UL 1UL ]) ctx
    Assert.True(result.Scene.IsNone)
    Assert.Equal((if stale then SceneStatus.Failed else SceneStatus.Cancelled), result.Status)

[<Fact>]
let ``Query source completeness is orthogonal and failed cancelled unavailable sources do not publish`` () =
    let source = query []

    let value =
        build SceneOptions.defaults {
            source with
                SourcePartial = true
                Status = QueryStatus.Truncated [ TruncationReason.Results ]
        }
        |> scene

    Assert.True value.Completeness.SourcePartial
    Assert.Equal("truncated", value.Completeness.QueryStatus)
    Assert.Contains("data-query-truncation=\"Results\"", svg value)

    for result in
        [
            {
                source with
                    Status = QueryStatus.Failed []
            }
            {
                source with
                    Status = QueryStatus.Cancelled
            }
            { source with SourceAvailable = false }
        ] do
        Assert.True((build SceneOptions.defaults result).Scene.IsNone)

    let empty = draw []
    Assert.Empty empty.Elements
    Assert.Empty empty.Lanes
    close 32.0 empty.Bounds.Height

[<Theory>]
[<InlineData("color")>]
[<InlineData("theme")>]
[<InlineData("overflow")>]
[<InlineData("identity")>]
[<InlineData("width")>]
[<InlineData("viewport")>]
[<InlineData("limits")>]
[<InlineData("kind-null")>]
[<InlineData("position-null")>]
[<InlineData("label-null")>]
let ``Invalid scene inputs fail explicitly without partial publication`` input =
    let mutable options = SceneOptions.defaults
    let mutable item = directive 0 0 1UL 1UL

    match input with
    | "kind-null" ->
        item <- {
            item with
                Kind = Unchecked.defaultof<DrawingKind>
        }
    | "position-null" ->
        item <- {
            item with
                LabelPosition = Unchecked.defaultof<LabelPosition>
        }
    | "label-null" ->
        item <- {
            item with
                Label = Some Unchecked.defaultof<QueryValue>
        }
    | "color" ->
        item <- {
            item with
                Background = "url(https://example.invalid/a)"
        }
    | "theme" ->
        options <- {
            options with
                Theme = {
                    options.Theme with
                        Text = "red;script"
                }
        }
    | "overflow" ->
        item <- {
            item with
                Address = UInt64.MaxValue
                Size = 2UL
        }
    | "identity" ->
        item <- {
            item with
                Runtime = { item.Runtime with Index = -1 }
        }
    | "width" -> item <- { item with Width = 4097 }
    | "viewport" ->
        options <- {
            options with
                Viewport = Some { Start = UInt64.MaxValue; Size = 2UL }
        }
    | _ ->
        options <- {
            options with
                Limits = {
                    options.Limits with
                        MaxElements = 4097
                }
        }

    let result = build options (query [ item ])
    Assert.Equal(SceneStatus.Failed, result.Status)
    Assert.True result.Scene.IsNone
    Assert.NotEmpty result.Diagnostics

[<Fact>]
let ``Null query status and truncation reason lists fail without throwing`` () =
    for status in
        [
            Unchecked.defaultof<QueryStatus>
            QueryStatus.Truncated Unchecked.defaultof<TruncationReason list>
        ] do
        let result = build SceneOptions.defaults { query [] with Status = status }
        Assert.Equal(SceneStatus.Failed, result.Status)
        Assert.True result.Scene.IsNone

[<Fact>]
let ``Repeat imports with different snapshot GUIDs and cultures produce byte identical SVG`` () =
    let first =
        query [
            {
                directive 0 0 UInt64.MaxValue 1UL with
                    Label = Some(QueryValue.Unsigned UInt64.MaxValue)
            }
        ]

    let nextId =
        SnapshotId.create (Guid.Parse "2f75183c-9c89-48b4-bc67-1dcd1bf5f7c4") |> unwrap

    let second = {
        first with
            SnapshotId = nextId
            Directives =
                first.Directives
                |> List.map (fun item -> {
                    item with
                        Runtime = {
                            item.Runtime with
                                SnapshotId = nextId
                        }
                })
    }

    let original = CultureInfo.CurrentCulture

    try
        CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo "fr-FR"
        let a = build SceneOptions.defaults first |> scene |> svg
        CultureInfo.CurrentCulture <- CultureInfo.GetCultureInfo "ar-SA"
        let b = build SceneOptions.defaults second |> scene |> svg
        Assert.Equal(a, b)
    finally
        CultureInfo.CurrentCulture <- original

[<Fact>]
let ``SVG exact byte budget accepts exact output and never writes past cap`` () =
    let value =
        draw [
            {
                directive 0 0 1UL 1UL with
                    Label = Some(QueryValue.Text "<>&")
            }
        ]

    let expected = svg value
    let bytes = Encoding.UTF8.GetByteCount expected
    use exact = new MemoryStream()
    Assert.Equal(Ok bytes, Svg.write { MaxBytes = bytes } value exact token)
    use short = new MemoryStream()
    Assert.Equal(Error SvgError.OutputLimitExceeded, Svg.write { MaxBytes = bytes - 1 } value short token)
    Assert.True(short.Length < int64 bytes)
    Assert.True short.CanWrite
    use cancelled = new CancellationTokenSource()
    cancelled.Cancel()
    Assert.Equal(Error SvgError.Cancelled, Svg.write SvgLimits.defaults value short cancelled.Token)
    Assert.True((Svg.write { MaxBytes = 0 } value short token).IsError)

type private FailedStream() =
    inherit MemoryStream()

    override _.Write(_, _, _) =
        raise (IOException "injected write failure")

[<Fact>]
let ``SVG preserves actionable I O failure and never closes caller stream`` () =
    use stream = new FailedStream()

    Assert.Equal(
        Error(SvgError.WriteFailed "injected write failure"),
        Svg.write SvgLimits.defaults (draw []) stream token
    )

    Assert.True stream.CanWrite

[<Fact>]
let ``Positioned scene and SVG match deterministic versioned golden fixtures`` () =
    let value =
        draw [
            {
                directive 0 0 256UL 16UL with
                    Label = Some(QueryValue.Text "A&B")
            }
        ]

    let projected = {|
        version = value.SchemaVersion
        bounds = value.Bounds
        lanes =
            value.Lanes
            |> List.map (fun lane -> {|
                id = lane.Id
                runtime = lane.Runtime.Value
                heap = lane.Heap.Value
                bounds = lane.Bounds
            |})
        elements =
            value.Elements
            |> List.map (fun item -> {|
                id = item.Id
                lane = item.LaneId
                layer = item.Layer
                bounds = item.Bounds
                style = item.Style
                text = item.Text.Value
                source = item.Source.Value
            |})
    |}

    let json =
        JsonSerializer.Serialize(projected, JsonSerializerOptions(WriteIndented = true)).Replace("\r\n", "\n")

    let directory = Path.Combine(AppContext.BaseDirectory, "fixtures")
    Assert.Equal(File.ReadAllText(Path.Combine(directory, "scene-v1.json")).TrimEnd(), json)
    Assert.Equal(File.ReadAllText(Path.Combine(directory, "scene-v1.svg")).TrimEnd(), svg value)
