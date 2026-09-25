module MemoryVisualizer.Hosting.Tests.ProtocolTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading
open MemoryVisualizer.Worker
open Xunit

let private bytes (value: string) = Encoding.UTF8.GetBytes(value)
let private hello = """{"tag":"hello","versions":[1],"extensions":[]}"""

[<Fact>]
let ``Both directions validate every shared accepted and rejected fixture`` () =
    use document =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures.json")))

    for name, validator, valid in
        [
            "validInbound", (fun frame -> Protocol.parseInbound frame |> ignore), true
            "invalidInbound", (fun frame -> Protocol.parseInbound frame |> ignore), false
            "validOutbound", Protocol.validateOutbound, true
            "invalidOutbound", Protocol.validateOutbound, false
        ] do
        let cases = document.RootElement.GetProperty(name)
        Assert.True(cases.GetArrayLength() > 0, $"Fixture group {name} must not be empty.")

        for fixture in cases.EnumerateArray() do
            let frame = JsonSerializer.SerializeToUtf8Bytes(fixture)

            if valid then
                validator frame
            else
                Assert.Throws<ProtocolFailure>(fun () -> validator frame) |> ignore

[<Theory>]
[<InlineData("""{"tag":"hello","versions":[1],"extensions":[],"tag":"hello"}""")>]
[<InlineData("""{"tag":"hello","\u0074ag":"hello","versions":[1],"extensions":[]}""")>]
[<InlineData("""{"tag":"hello","versions":[1],"extensions":[],"extra":{"x":1,"x":2}}""")>]
[<InlineData("""{"tag":"hello","versions":[1.0],"extensions":[]}""")>]
[<InlineData("""{"tag":"hello","versions":[1e0],"extensions":[]}""")>]
[<InlineData("""{"tag":"hello","versions":[-0],"extensions":[]}""")>]
[<InlineData("""{"tag":"hello","versions":[1],"extensions":["x.\ud800"]}""")>]
[<InlineData("""{"tag":"hello","versions":[1],"extensions":["x.\udc00"]}""")>]
[<InlineData("""{"tag":"hello","versions":[1],"extensions":[],"\ud800":0}""")>]
[<InlineData("""{"tag":"hello","versions":[1],"extensions":[]}{}""")>]
[<InlineData("""{"tag":"hello","versions":[1],"extensions":[],}""")>]
[<InlineData("""{"tag":"hello",/* comment */"versions":[1],"extensions":[]}""")>]
[<InlineData("""[]""")>]
[<InlineData("""null""")>]
let ``Raw JSON forms cannot bypass schema Unicode or duplicate validation`` value =
    Assert.Throws<ProtocolFailure>(fun () -> bytes value |> Protocol.parseInbound |> ignore)
    |> ignore

[<Fact>]
let ``Shared scalar boundaries and raw lexical fixtures match TypeScript`` () =
    use document =
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "fixtures.json")))

    let make query =
        JsonSerializer.SerializeToUtf8Bytes(
            {|
                tag = "request"
                version = 1
                requestId = "1"
                snapshotId = (null: string)
                operation = "recipe.validate"
                args = {|
                    recipe = {| schemaVersion = 1; query = query |}
                |}
            |}
        )

    for fixture in document.RootElement.GetProperty("textBoundaryCases").EnumerateArray() do
        let query =
            String.replicate (fixture.GetProperty("repeat").GetInt32()) (fixture.GetProperty("textUnit").GetString())

        let frame = make query

        if fixture.GetProperty("valid").GetBoolean() then
            frame |> Protocol.parseInbound |> ignore
        else
            Assert.Throws<ProtocolFailure>(fun () -> frame |> Protocol.parseInbound |> ignore)
            |> ignore

    for name, valid in [ "validJson", true; "invalidJson", false ] do
        let cases = document.RootElement.GetProperty(name)
        Assert.True(cases.GetArrayLength() > 0, $"Fixture group {name} must not be empty.")

        for fixture in cases.EnumerateArray() do
            let frame = fixture.GetString() |> bytes

            if valid then
                Protocol.validateJson frame
            else
                Assert.Throws<ProtocolFailure>(fun () -> Protocol.validateJson frame) |> ignore

[<Theory>]
[<InlineData(256, true)>]
[<InlineData(257, false)>]
let ``Outbound message limits count Unicode scalars rather than UTF16 units`` length valid =
    let frame =
        JsonSerializer.SerializeToUtf8Bytes(
            {|
                tag = "fatal"
                code = "InternalError"
                message = String.replicate length "\U0001F600"
            |}
        )

    if valid then
        Protocol.validateOutbound frame
    else
        Assert.Throws<ProtocolFailure>(fun () -> Protocol.validateOutbound frame)
        |> ignore

[<Theory>]
[<InlineData(16, true)>]
[<InlineData(17, false)>]
let ``Raw JSON depth accepts exactly sixteen containers`` depth valid =
    let arrays = depth - 1

    let frame =
        bytes (
            "{\"nested\":"
            + String.replicate arrays "["
            + "0"
            + String.replicate arrays "]"
            + "}"
        )

    if valid then
        Protocol.validateJson frame
    else
        Assert.Throws<ProtocolFailure>(fun () -> Protocol.validateJson frame) |> ignore

[<Fact>]
let ``Malformed UTF8 BOM and depth bombs are rejected`` () =
    let badEncoding =
        Array.append (bytes "{\"tag\":\"") [| 0xc0uy; 0xafuy; 34uy; 125uy |]

    let bom = Array.append [| 0xefuy; 0xbbuy; 0xbfuy |] (bytes hello)
    let deep = bytes (String.replicate 1000 "[" + "0" + String.replicate 1000 "]")

    for frame in [ badEncoding; bom; deep ] do
        Assert.Throws<ProtocolFailure>(fun () -> Protocol.parseInbound frame |> ignore)
        |> ignore

[<Fact>]
let ``Outbound validation rejects duplicates unpaired surrogates and wrong scopes`` () =
    for frame in
        [
            """{"tag":"bye","version":1,"version":1}"""
            """{"tag":"fatal","code":"InternalError","message":"\ud800"}"""
            """{"tag":"success","version":1,"requestId":"1","snapshotId":null,"result":{"tag":"disposed"}}"""
        ] do
        Assert.Throws<ProtocolFailure>(fun () -> Protocol.validateOutbound (bytes frame))
        |> ignore

[<Theory>]
[<InlineData(65536, true)>]
[<InlineData(65537, false)>]
let ``Frame byte limit excludes LF and includes trailing whitespace`` size valid =
    task {
        let frame = hello.PadRight(size, ' ') + "\n"
        use input = new MemoryStream(bytes frame)
        let reader = FrameReader(input)

        if valid then
            let! result = reader.ReadAsync(CancellationToken.None)
            Assert.Equal(65536, result.Value.Length)
            Protocol.parseInbound result.Value |> ignore
        else
            let! _ =
                Assert.ThrowsAsync<ProtocolFailure>(fun () -> reader.ReadAsync(CancellationToken.None))

            ()
    }

[<Theory>]
[<InlineData("\n")>]
[<InlineData("\r\n")>]
[<InlineData("{\"tag\":\"hello\"}\r\n")>]
[<InlineData("{")>]
let ``Empty CRLF and unterminated frames fail`` value =
    task {
        use input = new MemoryStream(bytes value)
        let reader = FrameReader(input)

        let! _ =
            Assert.ThrowsAsync<ProtocolFailure>(fun () -> reader.ReadAsync(CancellationToken.None))

        ()
    }

type private FragmentedStream(payload: byte array) =
    inherit MemoryStream(payload)

    override this.ReadAsync(buffer: Memory<byte>, cancellation: CancellationToken) =
        base.ReadAsync(buffer.Slice(0, min 1 buffer.Length), cancellation)

[<Fact>]
let ``Fragmented UTF8 and concatenated frames are reassembled without loss`` () =
    task {
        let frame =
            """{"tag":"request","version":1,"requestId":"1","snapshotId":null,"operation":"recipe.validate","args":{"recipe":{"schemaVersion":1,"query":"😀"}}}"""

        use input = new FragmentedStream(bytes (hello + "\n" + frame + "\n"))
        let reader = FrameReader(input)
        let! first = reader.ReadAsync(CancellationToken.None)
        let! second = reader.ReadAsync(CancellationToken.None)
        let! eof = reader.ReadAsync(CancellationToken.None)
        Assert.Equal(hello, Protocol.utf8.GetString(first.Value))
        Assert.Equal(frame, Protocol.utf8.GetString(second.Value))
        Protocol.parseInbound second.Value |> ignore
        Assert.True(eof.IsNone)
    }
