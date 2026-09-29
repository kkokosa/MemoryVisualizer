module MemoryVisualizer.Hosting.Tests.NativeWorkerTests

open System
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open MemoryVisualizer.Core
open MemoryVisualizer.Core.Analysis
open MemoryVisualizer.Query
open MemoryVisualizer.Scene
open MemoryVisualizer.Worker
open Xunit

let private unwrap value =
    value |> Result.defaultWith (fun error -> failwithf "%A" error)

let private property (name: string) (value: JsonElement) = value.GetProperty(name)
let private text name value = (property name value).GetString()
let private scope = "6ee40211-7b2b-48b2-a2ed-43549d709620"

let private settings =
    """{"plotWidth":1024,"viewport":null,"redaction":{"addresses":false,"strings":false,"paths":false,"labels":false},"maxResults":4096,"maxElements":1024}"""

let private request id snapshot operation args =
    let scope = if snapshot then "\"" + scope + "\"" else "null"
    $"{{\"tag\":\"request\",\"version\":2,\"requestId\":\"{id}\",\"snapshotId\":{scope},\"operation\":\"{operation}\",\"args\":{args}}}"

let private dumpPath = Path.GetFullPath("synthetic-only.dmp")

let private load id =
    request
        id
        false
        "snapshot.load"
        (JsonSerializer.Serialize(
            {|
                path = dumpPath
                dacPath = (null: string)
                cachePath = (null: string)
                allowNetwork = false
            |}
        ))

let private run id source =
    request
        id
        true
        "query.run"
        ("{\"text\":"
         + JsonSerializer.Serialize(source: string)
         + ",\"settings\":"
         + settings
         + "}")

let private rows id query cursor =
    request id true "query.page" $"{{\"queryId\":\"{query}\",\"cursor\":{cursor},\"pageSize\":32}}"

let private fixture () =
    let id = SnapshotId.create (Guid.Parse scope) |> unwrap
    let runtime: RuntimeIdentity = { SnapshotId = id; Index = 0 }

    let typ: TypeIdentity = {
        Runtime = runtime
        MethodTable = UInt64.MaxValue
    }

    {
        Metadata = {
            Id = id
            PointerSizeBytes = 8
            RuntimeVersion = "fixture"
            ObjectCount = 40UL
        }
        Target = {
            OperatingSystem = "fixture"
            Architecture = "x64"
            PointerSizeBytes = 8
        }
        Runtimes = [|
            {
                Identity = runtime
                Version = "fixture"
                VersionSource = "fixture"
                Flavor = "Core"
                ModuleAddress = 0UL
                CanWalkHeap = true
                MemoryMap = ExtractionCompleteness.Complete
                References = ExtractionCompleteness.NotRequested
                Roots = ExtractionCompleteness.NotRequested
                Handles = ExtractionCompleteness.NotRequested
            }
        |]
        Heaps = [|
            {
                Runtime = runtime
                Index = 0
                Address = 0UL
                IsServer = false
                HasRegions = false
                HasPinnedObjectHeap = false
            }
        |]
        Segments = [|
            {
                Runtime = runtime
                HeapIndex = 0
                Address = 0x1000UL
                Kind = HeapKind.Small
                IsPinned = false
                ObjectRange = { Start = 0x1000UL; End = 0x2000UL }
                CommittedRange = { Start = 0x1000UL; End = 0x2000UL }
                ReservedRange = { Start = 0x2000UL; End = 0x2000UL }
                Generations = [||]
                AllocationContexts = [||]
            }
        |]
        Types = [|
            {
                Identity = typ
                Name = Some "Synthetic.Type"
                ModuleAddress = None
                MetadataToken = 0
                IsArray = false
                IsString = false
                IsFree = false
                ContainsPointers = false
            }
        |]
        Objects =
            Array.init 40 (fun index -> {
                Identity = {
                    Runtime = runtime
                    Address = 0x1000UL + uint64 index * 24UL
                }
                Type = typ
                SegmentAddress = 0x1000UL
                SizeBytes = 24UL
                Generation = None
                IsFree = false
                StringDetail = None
            })
        Edges = [||]
        Roots = [||]
        Handles = [||]
        StringDetails = ExtractionCompleteness.NotRequested
        Diagnostics = [||]
        IsPartial = false
    }

type private Session(readSnapshot, ?sceneClock: TimeProvider) =
    let input = new WorkerTests.TestPipe()
    let output = new WorkerTests.TestPipe()
    let diagnostics = new StringWriter()

    let running =
        NativeWorker.runWithSceneClockAsync
            (defaultArg sceneClock TimeProvider.System)
            readSnapshot
            input
            output
            diagnostics

    let reader = FrameReader(output)
    member _.Input = input
    member _.Output = output
    member _.Running = running
    member _.Diagnostics = diagnostics.ToString()

    member _.SendMany(frames: string list) =
        input
            .WriteAsync((Protocol.utf8.GetBytes(String.concat "\n" frames + "\n")).AsMemory(), CancellationToken.None)
            .AsTask()

    member this.Send(frame) = this.SendMany [ frame ]

    member _.Read() =
        task {
            let! frame =
                reader.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(4.0))

            Assert.True(frame.IsSome)
            NativeProtocol.validateOutbound frame.Value
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(frame.Value))
            return document.RootElement.Clone()
        }

    member this.Terminal() =
        task {
            let mutable result = Unchecked.defaultof<JsonElement>

            while result.ValueKind = JsonValueKind.Undefined do
                let! frame = this.Read()

                if text "tag" frame <> "progress" then
                    result <- frame

            return result
        }

    member this.Start() =
        task {
            do! this.Send """{"tag":"hello","versions":[2],"extensions":[]}"""
            let! ready = this.Read()
            Assert.Equal("native", ready |> property "capabilities" |> text "backend")
            Assert.Equal(7, (ready |> property "capabilities" |> property "operations").GetArrayLength())
        }

    member this.Stop() =
        task {
            do! this.Send """{"tag":"shutdown","version":2}"""
            let! bye = this.Terminal()
            Assert.Equal("bye", text "tag" bye)
            let! code = running.WaitAsync(TimeSpan.FromSeconds(3.0))
            Assert.Equal(0, code)
            Assert.Equal("", diagnostics.ToString())
        }

    interface IDisposable with
        member _.Dispose() =
            input.Dispose()
            output.Dispose()
            diagnostics.Dispose()

let private synthetic _ options _ _ =
    Assert.False(options.IncludeReferences)
    Assert.False(options.IncludeRoots)
    Assert.False(options.IncludeStringDetails)
    Assert.False(options.Dac.AllowNetwork)
    Task.FromResult(Ok(fixture ()))

[<Theory>]
[<InlineData("""{"tag":"hello","versions":[2.0],"extensions":[]}""")>]
[<InlineData("""{"tag":"hello","versions":[2],"extensions":[],"unknown":0}""")>]
[<InlineData("""{"tag":"hello","tag":"hello","versions":[2],"extensions":[]}""")>]
[<InlineData("""{"tag":"cancel","version":2,"requestId":"01"}""")>]
[<InlineData("""{"tag":"cancel","version":2,"requestId":"18446744073709551616"}""")>]
[<InlineData("""{"tag":"cancel","version":2,"requestId":"\ud800"}""")>]
[<InlineData("""{"tag":"shutdown","version":2e0}""")>]
[<InlineData("""{"tag":"shutdown","version":-0}""")>]
let ``Native grammar rejects noncanonical or ambiguous frames`` (raw: string) =
    Assert.Throws<ProtocolFailure>(fun () -> NativeProtocol.parseInbound (Protocol.utf8.GetBytes raw) |> ignore)
    |> ignore

[<Fact>]
let ``V1 and V2 remain deliberately disjoint`` () =
    let native = NativeProtocol.ready ()
    NativeProtocol.validateOutbound native

    Assert.Throws<ProtocolFailure>(fun () -> Protocol.validateOutbound native)
    |> ignore

    Assert.Throws<ProtocolFailure>(fun () -> NativeProtocol.validateOutbound (Protocol.ready ()))
    |> ignore

    let oversized = Array.create 65537 32uy

    Assert.Throws<ProtocolFailure>(fun () -> NativeProtocol.parseInbound oversized |> ignore)
    |> ignore

    let invalidUtf8 = [| 123uy; 34uy; 255uy; 34uy; 58uy; 48uy; 125uy |]

    Assert.Throws<ProtocolFailure>(fun () -> NativeProtocol.parseInbound invalidUtf8 |> ignore)
    |> ignore

[<Fact>]
let ``Native pipeline pages shared MQL rows scene coordinates details and SVG`` () =
    task {
        use session = new Session(synthetic)
        do! session.Start()
        do! session.Send(load "1")
        let! loaded = session.Terminal()
        Assert.Equal(scope, text "snapshotId" loaded)
        Assert.Equal("40", loaded |> property "result" |> text "objectCount")

        let source =
            "MATCH (o: Object) RETURN o.Address, o.Size, o.Type AS BOX (Label = o.Type)"

        do! session.Send(run "2" source)
        let! result = session.Terminal()
        let result = property "result" result
        Assert.Equal("complete", text "status" result)
        Assert.Equal(40, (property "rowCount" result).GetInt32())
        Assert.Equal("1", text "queryId" result)
        Assert.Equal(40, (result |> property "scene" |> property "elementCount").GetInt32())
        do! session.Send(rows "3" "1" "null")
        let! first = session.Terminal()
        Assert.Equal(32, (first |> property "result" |> property "items").GetArrayLength())
        Assert.Equal("32", first |> property "result" |> text "nextCursor")

        let entity =
            (first |> property "result" |> property "items")[0] |> property "entity"

        Assert.Equal("0xffffffffffffffff", text "methodTable" entity)
        Assert.Equal(JsonValueKind.Null, (property "end" entity).ValueKind)
        Assert.Equal(JsonValueKind.Null, (property "generation" entity).ValueKind)
        do! session.Send(rows "4" "1" "\"32\"")
        let! second = session.Terminal()
        Assert.Equal(8, (second |> property "result" |> property "items").GetArrayLength())
        Assert.Equal(JsonValueKind.Null, (second |> property "result" |> property "nextCursor").ValueKind)
        do! session.Send(request "5" true "scene.page" """{"sceneId":"1","cursor":null,"pageSize":32}""")
        let! elements = session.Terminal()
        let raw = fixture ()

        use store =
            IndexedHeapSnapshot.Create(raw, SnapshotIndexLimits.defaults, CancellationToken.None)
            |> unwrap

        let plan =
            Mql.prepare QueryLimits.defaults source
            |> Result.bind (Mql.bind raw.Metadata.Id)
            |> unwrap

        let expectedResult =
            Mql.execute QueryLimits.defaults plan store (QueryExecutionContext.create CancellationToken.None)

        let expectedScene =
            (Scene.build
                {
                    SceneOptions.defaults with
                        Limits = {
                            SceneLimits.defaults with
                                MaxElements = 1024
                        }
                }
                expectedResult
                (SceneExecutionContext.create CancellationToken.None))
                .Scene.Value

        let actual = (elements |> property "result" |> property "items")[0]
        Assert.Equal(expectedScene.Elements[0].Bounds.X, (actual |> property "bounds" |> property "x").GetDouble())

        Assert.Equal(
            expectedScene.Elements[0].Text.Value.Lines[0].Text,
            ((actual |> property "text" |> property "lines")[0] |> text "text")
        )

        do!
            session.Send(
                request
                    "6"
                    true
                    "details"
                    """{"runtime":0,"address":"0x0000000000001000","cursor":null,"pageSize":32}"""
            )

        let! details = session.Terminal()
        Assert.Equal("24", (details |> property "result" |> property "items")[0] |> text "size")

        let target =
            Path.GetFullPath("native-test-" + Guid.NewGuid().ToString("N") + ".svg")

        try
            do! session.Send(request "7" true "export" (JsonSerializer.Serialize({| sceneId = "1"; path = target |})))
            let! exported = session.Terminal()
            Assert.Equal("export", exported |> property "result" |> text "tag")
            use expected = new MemoryStream()

            Svg.write SvgLimits.defaults expectedScene expected CancellationToken.None
            |> unwrap
            |> ignore

            Assert.Equal<byte>(expected.ToArray(), File.ReadAllBytes(target))
            do! session.Send(request "8" true "export" (JsonSerializer.Serialize({| sceneId = "1"; path = target |})))
            let! noOverwrite = session.Terminal()
            Assert.Equal("InvalidRequest", noOverwrite |> property "error" |> text "code")
            Assert.Equal<byte>(expected.ToArray(), File.ReadAllBytes(target))
        finally
            if File.Exists target then
                File.Delete target

        do! session.Stop()
    }

[<Fact>]
let ``New query invalidates old identifiers and compiler diagnostics remain source spanned`` () =
    task {
        use session = new Session(synthetic)
        do! session.Start()
        do! session.Send(load "1")
        let! _ = session.Terminal()
        do! session.Send(run "2" "MATCH (o:Object) RETURN o")
        let! _ = session.Terminal()
        do! session.Send(run "3" "not valid MQL")
        let! invalid = session.Terminal()
        let result = property "result" invalid
        Assert.Equal("failed", text "status" result)
        Assert.Equal("2", text "queryId" result)
        Assert.True((property "diagnostics" result).GetArrayLength() > 0)
        Assert.True(((property "diagnostics" result)[0] |> property "span" |> property "line").GetInt32() > 0)
        Assert.Equal(JsonValueKind.Null, (property "scene" result).ValueKind)
        do! session.Send(rows "4" "1" "null")
        let! stale = session.Terminal()
        Assert.Equal("QueryNotFound", stale |> property "error" |> text "code")
        do! session.Send(request "5" true "snapshot.dispose" "{}")
        let! disposed = session.Terminal()
        Assert.Equal("disposed", disposed |> property "result" |> text "tag")
        do! session.Send(rows "6" "2" "null")
        let! missing = session.Terminal()
        Assert.Equal("SnapshotNotFound", missing |> property "error" |> text "code")
        do! session.Stop()
    }

[<Fact>]
let ``Native row paging is byte bounded and oversized items fail without empty next pages`` () =
    let raw = fixture ()

    use store =
        IndexedHeapSnapshot.Create(raw, SnapshotIndexLimits.defaults, CancellationToken.None)
        |> unwrap

    let plan =
        Mql.prepare QueryLimits.defaults "MATCH (o:Object) RETURN o"
        |> Result.bind (Mql.bind raw.Metadata.Id)
        |> unwrap

    let result =
        Mql.execute QueryLimits.defaults plan store (QueryExecutionContext.create CancellationToken.None)

    let row = {
        result.Rows[0] with
            Values = [ "large", QueryValue.Text(String('x', 10000)) ]
    }

    let request: NativeRequest = {
        Id = 1UL
        Snapshot = Some scope
        Operation = NativeOperation.Rows(1UL, None, 32)
    }

    let bytes =
        NativeJson.page request "rows" 1UL None 32 (Array.create 32 row) NativeJson.row

    Assert.InRange(bytes.Length, 1, 65536)
    use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes))
    let result = property "result" document.RootElement
    Assert.InRange((property "items" result).GetArrayLength(), 1, 31)
    Assert.NotEqual<string>("0", text "nextCursor" result)

    let huge = {
        row with
            Values = [ "large", QueryValue.Text(String('x', 65536)) ]
    }

    let error =
        Assert.Throws<NativeFailure>(fun () ->
            NativeJson.page request "rows" 1UL None 32 [| huge |] NativeJson.row |> ignore)

    Assert.Equal(NativeFailure "OutputLimit", error)

[<Fact>]
let ``Cancelled replacement preserves previous store query and scene while native work stays off read loop`` () =
    task {
        let entered =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let release =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let mutable imports = 0

        let read _ _ _ _ =
            task {
                imports <- imports + 1

                if imports > 1 then
                    entered.TrySetResult(()) |> ignore
                    do! release.Task

                return Ok(fixture ())
            }

        use session = new Session(read)
        do! session.Start()
        do! session.Send(load "1")
        let! _ = session.Terminal()
        do! session.Send(run "2" "MATCH (o:Object) RETURN o AS BOX")
        let! _ = session.Terminal()
        do! session.Send(load "3")
        do! entered.Task.WaitAsync(TimeSpan.FromSeconds(2.0))
        do! session.SendMany [ load "4"; """{"tag":"cancel","version":2,"requestId":"3"}""" ]
        let! busy = session.Terminal()
        Assert.Equal("Busy", busy |> property "error" |> text "code")
        let nextRead = session.Input.ReadCalls + 1
        do! session.Send """{"tag":"cancel","version":2,"requestId":"3"}"""
        do! session.Input.WaitForRead(nextRead).WaitAsync(TimeSpan.FromSeconds(1.0))
        release.TrySetResult(()) |> ignore
        let! cancelled = session.Terminal()
        Assert.Equal("Cancelled", cancelled |> property "error" |> text "code")
        do! session.Send(rows "5" "1" "null")
        let! retained = session.Terminal()
        Assert.Equal("rows", retained |> property "result" |> text "tag")
        Assert.Equal("1", retained |> property "result" |> text "queryId")
        do! session.Send(request "6" true "scene.page" """{"sceneId":"1","cursor":null,"pageSize":32}""")
        let! scene = session.Terminal()
        Assert.Equal("1", scene |> property "result" |> text "sceneId")
        Assert.Equal(32, (scene |> property "result" |> property "items").GetArrayLength())

        let target =
            Path.GetFullPath("native-retained-" + Guid.NewGuid().ToString("N") + ".svg")

        try
            do! session.Send(request "7" true "export" (JsonSerializer.Serialize({| sceneId = "1"; path = target |})))
            let! exported = session.Terminal()
            Assert.Equal("export", exported |> property "result" |> text "tag")
            Assert.True(File.Exists target)
        finally
            if File.Exists target then
                File.Delete target

        do! session.Send(run "8" "MATCH (o:Object) RETURN o")
        let! usable = session.Terminal()
        Assert.Equal(40, (usable |> property "result" |> property "rowCount").GetInt32())
        do! session.Stop()
    }

[<Fact>]
let ``Truncated query cannot export and network requires explicit trusted cache`` () =
    task {
        use session = new Session(synthetic)
        do! session.Start()

        let network =
            load "1"
            |> fun value -> value.Replace("\"allowNetwork\":false", "\"allowNetwork\":true")

        do! session.Send network
        let! rejected = session.Terminal()
        Assert.Equal("InvalidRequest", rejected |> property "error" |> text "code")
        do! session.Send(load "2")
        let! _ = session.Terminal()

        do!
            session.Send(
                run "3" "MATCH (o:Object) RETURN o AS BOX"
                |> fun value -> value.Replace("\"maxResults\":4096", "\"maxResults\":1")
            )

        let! result = session.Terminal()
        Assert.Equal("truncated", result |> property "result" |> text "status")

        let target =
            Path.GetFullPath("native-unpublished-" + Guid.NewGuid().ToString("N") + ".svg")

        do! session.Send(request "4" true "export" (JsonSerializer.Serialize({| sceneId = "1"; path = target |})))
        let! rejected = session.Terminal()
        Assert.Equal("IncompleteResult", rejected |> property "error" |> text "code")
        Assert.False(File.Exists target)
        do! session.Stop()
    }

[<Fact>]
let ``Queued page cannot publish a previous query epoch after new query admission`` () =
    task {
        use session = new Session(synthetic)
        do! session.Start()
        do! session.Send(load "1")
        let! _ = session.Terminal()
        do! session.Send(run "2" "MATCH (o:Object) RETURN o")
        let! _ = session.Terminal()
        session.Output.PauseAfterWrite(session.Output.WriteCalls + 1)
        do! session.Send(rows "3" "1" "null")
        let! visible = session.Terminal()
        Assert.Equal("rows", visible |> property "result" |> text "tag")
        let nextRead = session.Input.ReadCalls + 1
        do! session.SendMany [ rows "4" "1" "null"; run "5" "MATCH (o:Object) RETURN o.Size" ]
        do! session.Input.WaitForRead(nextRead).WaitAsync(TimeSpan.FromSeconds(1.0))
        session.Output.ReleaseWrite()
        let! one = session.Terminal()
        let! two = session.Terminal()

        let results =
            [ one; two ] |> List.map (fun item -> text "requestId" item, item) |> Map.ofList

        Assert.Equal("Cancelled", results["4"] |> property "error" |> text "code")
        Assert.Equal("2", results["5"] |> property "result" |> text "queryId")
        do! session.Stop()
    }

[<Fact>]
let ``Native terminal admission releases before blocked visible write and shutdown awaits teardown`` () =
    task {
        use session = new Session(synthetic)
        do! session.Start()
        do! session.Send(load "1")
        let! _ = session.Terminal()
        do! session.Send(run "2" "MATCH (o:Object) RETURN o")
        let! _ = session.Terminal()
        session.Output.PauseAfterWrite(session.Output.WriteCalls + 8)
        do! session.SendMany [ for id in 3..10 -> rows (string id) "1" "null" ]

        for _ in 3..10 do
            let! frame = session.Terminal()
            Assert.Equal("success", text "tag" frame)

        let nextRead = session.Input.ReadCalls + 1
        do! session.SendMany [ for id in 11..18 -> rows (string id) "1" "null" ]
        do! session.Input.WaitForRead(nextRead).WaitAsync(TimeSpan.FromSeconds(1.0))
        session.Output.ReleaseWrite()

        for _ in 11..18 do
            let! frame = session.Terminal()
            Assert.Equal("success", text "tag" frame)

        do! session.Stop()
    }

[<Fact>]
let ``Extraction progress is coalesced to newest counter and cancellation remains responsive`` () =
    task {
        let started =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let read _ _ (progress: IProgress<SnapshotProgress>) token =
            task {
                for completed in 1UL .. 10000UL do
                    progress.Report {
                        Stage = ExtractionStage.MemoryMap
                        RuntimeIndex = Some 0
                        Completed = completed
                    }

                started.TrySetResult(()) |> ignore
                do! Task.Delay(Timeout.Infinite, token)
                return Ok(fixture ())
            }

        use session = new Session(read)
        do! session.Start()
        do! session.Send(load "1")
        do! started.Task.WaitAsync(TimeSpan.FromSeconds(2.0))
        let! progress = session.Read()
        Assert.Equal("progress", text "tag" progress)
        Assert.Equal("10000", text "completed" progress)
        Assert.Equal("MemoryMap", text "phase" progress)
        Assert.False(progress.TryGetProperty("total") |> fst)
        do! session.Send """{"tag":"cancel","version":2,"requestId":"1"}"""
        let! cancelled = session.Terminal()
        Assert.Equal("Cancelled", cancelled |> property "error" |> text "code")
        do! session.Stop()
    }

[<Fact>]
let ``Failed reload preserves store and partial metadata is explicit and not exportable`` () =
    task {
        let mutable imports = 0

        let read _ _ _ _ =
            imports <- imports + 1

            Task.FromResult(
                if imports = 1 then
                    let diagnostic = {
                        Code = "SYN001"
                        Message = "Private diagnostic not logged."
                        Stage = ExtractionStage.MemoryMap
                        Runtime = None
                        Address = None
                    }

                    Ok {
                        fixture () with
                            IsPartial = true
                            Diagnostics = [| diagnostic |]
                    }
                else
                    Error(AnalysisError.InvalidDump "Private path must not reach protocol error.")
            )

        use session = new Session(read)
        do! session.Start()
        do! session.Send(load "1")
        let! loaded = session.Terminal()
        Assert.True((loaded |> property "result" |> property "sourcePartial").GetBoolean())
        Assert.Equal(1, (loaded |> property "result" |> property "diagnosticCount").GetInt32())
        do! session.Send(load "2")
        let! failed = session.Terminal()
        Assert.Equal("InvalidDump", failed |> property "error" |> text "code")
        Assert.DoesNotContain("Private", failed.GetRawText())
        do! session.Send(run "3" "MATCH (o:Object) RETURN o AS BOX")
        let! query = session.Terminal()
        Assert.True((query |> property "result" |> property "sourcePartial").GetBoolean())
        Assert.Equal(1, (query |> property "result" |> property "sourceDiagnosticCount").GetInt32())

        let target =
            Path.GetFullPath("native-partial-" + Guid.NewGuid().ToString("N") + ".svg")

        do! session.Send(request "4" true "export" (JsonSerializer.Serialize({| sceneId = "1"; path = target |})))
        let! refused = session.Terminal()
        Assert.Equal("IncompleteResult", refused |> property "error" |> text "code")
        Assert.False(File.Exists target)
        do! session.Stop()
    }

[<Fact>]
let ``Strict native argument shape paging bounds address spelling and JSON depth are enforced`` () =
    let reject raw =
        Assert.Throws<ProtocolFailure>(fun () ->
            NativeProtocol.parseInbound (Protocol.utf8.GetBytes(raw: string)) |> ignore)
        |> ignore

    reject (request "1" true "query.page" """{"queryId":"1","cursor":null,"pageSize":33}""")
    reject (request "1" true "query.page" """{"queryId":"1","cursor":null,"pageSize":1,"extra":0}""")
    reject (request "1" true "details" """{"runtime":0,"address":"0x000000000000ABCD","cursor":null,"pageSize":1}""")

    reject (
        run "1" "MATCH (o:Object) RETURN o"
        |> fun raw -> raw.Replace("\"plotWidth\":1024", "\"plotWidth\":1024.0")
    )

    reject (
        run "1" "MATCH (o:Object) RETURN o"
        |> fun raw -> raw.Replace("\"maxElements\":1024", "\"maxElements\":1025")
    )

    reject ("{\"x\":" + String.replicate 17 "[" + "0" + String.replicate 17 "]" + "}")

type private SceneDeadlineClock() =
    inherit TimeProvider()
    let mutable timestamp = 0L
    member val Expired = true with get, set
    override _.TimestampFrequency = 1000L

    override this.GetTimestamp() =
        if this.Expired then
            timestamp <- timestamp + 5000L

        timestamp

[<Fact>]
let ``Scene deadline without geometry reports explicit failure and publishes no query`` () =
    task {
        let clock = SceneDeadlineClock()
        use session = new Session(synthetic, clock)
        do! session.Start()
        do! session.Send(load "1")
        let! _ = session.Terminal()
        do! session.Send(run "2" "MATCH (o:Object) RETURN o AS BOX")
        let! stopped = session.Terminal()
        Assert.Equal("error", text "tag" stopped)
        Assert.Equal("SceneTruncated", stopped |> property "error" |> text "code")
        do! session.Send(rows "3" "1" "null")
        let! missing = session.Terminal()
        Assert.Equal("QueryNotFound", missing |> property "error" |> text "code")
        clock.Expired <- false
        do! session.Send(run "4" "MATCH (o:Object) RETURN o AS BOX")
        let! complete = session.Terminal()
        Assert.Equal("complete", complete |> property "result" |> text "status")
        Assert.Equal(JsonValueKind.Object, (complete |> property "result" |> property "scene").ValueKind)
        do! session.Stop()
    }

[<Theory>]
[<InlineData(0)>]
[<InlineData(1)>]
[<InlineData(2)>]
[<InlineData(3)>]
let ``Detail projection rejects missing or inconsistent relationships instead of inventing identity`` scenario =
    let raw = fixture ()

    use store =
        IndexedHeapSnapshot.Create(raw, SnapshotIndexLimits.defaults, CancellationToken.None)
        |> unwrap

    let info = store.GetInfo(CancellationToken.None) |> unwrap
    let item = raw.Objects[0]

    let segment, typ =
        match scenario with
        | 0 -> None, Ok(Some raw.Types[0])
        | 1 -> Some info.Segments[0], Error SnapshotIndexError.Disposed
        | 2 -> Some info.Segments[0], Ok None
        | _ ->
            Some {
                info.Segments[0] with
                    Address = 0xdeadUL
            },
            Ok(Some raw.Types[0])

    let error =
        Assert.Throws<NativeFailure>(fun () -> NativeWorker.detailEntity item segment typ |> ignore)

    Assert.Equal(NativeFailure "SnapshotInvariant", error)

[<Fact>]
let ``Failed reload retains query and scene identifiers until a successful replacement commits`` () =
    task {
        let mutable imports = 0

        let read _ _ _ _ =
            imports <- imports + 1

            Task.FromResult(
                if imports = 2 then
                    Error(AnalysisError.InvalidDump "private native error")
                else
                    Ok(fixture ())
            )

        use session = new Session(read)
        do! session.Start()
        do! session.Send(load "1")
        let! _ = session.Terminal()
        do! session.Send(run "2" "MATCH (o:Object) RETURN o AS BOX")
        let! _ = session.Terminal()
        do! session.Send(load "3")
        let! failed = session.Terminal()
        Assert.Equal("InvalidDump", failed |> property "error" |> text "code")
        do! session.Send(rows "4" "1" "null")
        let! rows = session.Terminal()
        Assert.Equal("1", rows |> property "result" |> text "queryId")
        do! session.Send(request "5" true "scene.page" """{"sceneId":"1","cursor":null,"pageSize":32}""")
        let! scene = session.Terminal()
        Assert.Equal(32, (scene |> property "result" |> property "items").GetArrayLength())

        let target =
            Path.GetFullPath("native-failed-reload-" + Guid.NewGuid().ToString("N") + ".svg")

        try
            do! session.Send(request "6" true "export" (JsonSerializer.Serialize({| sceneId = "1"; path = target |})))
            let! exported = session.Terminal()
            Assert.Equal("export", exported |> property "result" |> text "tag")
        finally
            if File.Exists target then
                File.Delete target

        do! session.Send(load "7")
        let! replaced = session.Terminal()
        Assert.Equal("snapshot", replaced |> property "result" |> text "tag")
        do! session.Send(request "8" true "query.page" """{"queryId":"1","cursor":null,"pageSize":32}""")
        let! stale = session.Terminal()
        Assert.Equal("QueryNotFound", stale |> property "error" |> text "code")
        do! session.Send(request "9" true "scene.page" """{"sceneId":"1","cursor":null,"pageSize":32}""")
        let! stale = session.Terminal()
        Assert.Equal("SceneNotFound", stale |> property "error" |> text "code")
        do! session.Stop()
    }

[<Fact>]
let ``Explicit trusted cache permits network DAC policy on Windows only`` () =
    task {
        let cache = Path.GetFullPath("native-trusted-cache")
        let mutable called = false

        let read _ (options: SnapshotReaderOptions) _ _ =
            called <- true
            Assert.True(options.Dac.AllowNetwork)
            Assert.Equal(Some cache, options.Dac.CacheDirectory)
            Assert.Empty(options.Dac.TrustedPaths)
            Assert.False(options.IncludeReferences)
            Assert.False(options.IncludeRoots)
            Assert.False(options.IncludeStringDetails)
            Task.FromResult(Ok(fixture ()))

        use session = new Session(read)
        do! session.Start()

        do!
            session.Send(
                request
                    "1"
                    false
                    "snapshot.load"
                    (JsonSerializer.Serialize(
                        {|
                            path = dumpPath
                            dacPath = (null: string)
                            cachePath = cache
                            allowNetwork = true
                        |}
                    ))
            )

        let! result = session.Terminal()

        if OperatingSystem.IsWindows() then
            Assert.True(called)
            Assert.Equal("snapshot", result |> property "result" |> text "tag")
        else
            Assert.False(called)
            Assert.Equal("UnsupportedTarget", result |> property "error" |> text "code")

        do! session.Stop()
    }

[<Theory>]
[<InlineData("snapshot.load", "snapshot")>]
[<InlineData("snapshot.dispose", "disposed")>]
[<InlineData("export", "export")>]
let ``Cancellation after commit before reply visibility preserves authoritative success`` operation expectedTag =
    task {
        use session = new Session(synthetic)
        do! session.Start()
        do! session.Send(load "1")
        let! _ = session.Terminal()
        do! session.Send(run "2" "MATCH (o:Object) RETURN o AS BOX")
        let! _ = session.Terminal()

        let target =
            Path.GetFullPath("native-commit-boundary-" + Guid.NewGuid().ToString("N") + ".svg")

        try
            let frame =
                match operation with
                | "snapshot.load" -> load "3"
                | "snapshot.dispose" -> request "3" true operation "{}"
                | _ -> request "3" true operation (JsonSerializer.Serialize({| sceneId = "1"; path = target |}))

            let nextWrite = session.Output.WriteCalls + 1
            session.Output.Pause()
            do! session.Send frame
            // Write entry occurs after FrameWriter's guarded commit, but Pause hides every byte.
            do! session.Output.WaitForWrite(nextWrite).WaitAsync(TimeSpan.FromSeconds(1.0))

            if operation = "export" then
                Assert.True(File.Exists target)

            let nextRead = session.Input.ReadCalls + 1
            do! session.Send """{"tag":"cancel","version":2,"requestId":"3"}"""
            do! session.Input.WaitForRead(nextRead).WaitAsync(TimeSpan.FromSeconds(1.0))
            session.Output.Resume()
            let! committed = session.Terminal()
            Assert.Equal("success", text "tag" committed)
            Assert.Equal(expectedTag, committed |> property "result" |> text "tag")
            do! session.Send(rows "4" "1" "null")
            let! following = session.Terminal()

            match operation with
            | "snapshot.load" -> Assert.Equal("QueryNotFound", following |> property "error" |> text "code")
            | "snapshot.dispose" -> Assert.Equal("SnapshotNotFound", following |> property "error" |> text "code")
            | _ ->
                Assert.Equal("rows", following |> property "result" |> text "tag")
                Assert.True(File.Exists target)

            do! session.Stop()
        finally
            if File.Exists target then
                File.Delete target
    }
