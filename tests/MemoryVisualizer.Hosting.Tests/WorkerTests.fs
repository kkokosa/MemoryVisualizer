module MemoryVisualizer.Hosting.Tests.WorkerTests

open System
open System.Collections.Generic
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open MemoryVisualizer.Worker
open Xunit

let private hello = """{"tag":"hello","versions":[1],"extensions":[]}"""
let private shutdown = """{"tag":"shutdown","version":1}"""
let private scope1 = "00000000-0000-0000-0000-000000000001"
let private scope2 = "00000000-0000-0000-0000-000000000002"
let private property (name: string) (value: JsonElement) = value.GetProperty(name)
let private stringProperty name value = (property name value).GetString()
let private tag = stringProperty "tag"

let private request id scope operation args =
    let scopeJson = if scope = "" then "null" else $"\"{scope}\""
    $"{{\"tag\":\"request\",\"version\":1,\"requestId\":\"{id}\",\"snapshotId\":{scopeJson},\"operation\":\"{operation}\",\"args\":{args}}}"

let private load id delay =
    request id "" "snapshot.load" $"{{\"source\":\"fixture:tiny\",\"delayMs\":{delay}}}"

let private cancel id =
    $"{{\"tag\":\"cancel\",\"version\":1,\"requestId\":\"{id}\"}}"

/// A bounded in-memory pipe: runtime tests exercise real incremental reads and backpressure.
type private TestPipe() =
    inherit Stream()
    let channel = Channel.CreateBounded<byte array>(BoundedChannelOptions(8))
    let mutable current = Array.empty<byte>
    let mutable offset = 0
    let mutable pause: TaskCompletionSource<unit> option = None
    let mutable writeCalls = 0

    member _.WriteCalls = Volatile.Read(&writeCalls)

    member _.Complete() = channel.Writer.TryComplete() |> ignore

    member _.Pause() =
        pause <- Some(TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously))

    member _.Resume() =
        pause |> Option.iter (fun waiting -> waiting.TrySetResult(()) |> ignore)
        pause <- None

    override _.CanRead = true
    override _.CanWrite = true
    override _.CanSeek = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength(_) = raise (NotSupportedException())
    override _.Flush() = ()
    override _.FlushAsync(_) = Task.CompletedTask

    override this.Read(buffer, offset, count) =
        this.ReadAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult()

    override this.Write(buffer, offset, count) =
        this.WriteAsync(buffer.AsMemory(offset, count), CancellationToken.None).AsTask().GetAwaiter().GetResult()

    override _.ReadAsync(buffer: Memory<byte>, cancellation: CancellationToken) =
        ValueTask<int>(
            task {
                if offset = current.Length then
                    let! available = channel.Reader.WaitToReadAsync(cancellation).AsTask()

                    if available then
                        let! next = channel.Reader.ReadAsync(cancellation).AsTask()
                        current <- next
                        offset <- 0

                let count = min buffer.Length (current.Length - offset)
                current.AsMemory(offset, count).CopyTo(buffer)
                offset <- offset + count
                return count
            }
        )

    override _.WriteAsync(buffer: ReadOnlyMemory<byte>, cancellation: CancellationToken) =
        Interlocked.Increment(&writeCalls) |> ignore

        ValueTask(
            task {
                match pause with
                | Some waiting -> do! waiting.Task
                | None -> ()

                do! channel.Writer.WriteAsync(buffer.ToArray(), cancellation).AsTask()
            }
        )

    override this.Dispose(disposing) =
        this.Resume()
        this.Complete()
        base.Dispose(disposing)

type private Session() =
    let input = new TestPipe()
    let output = new TestPipe()
    let diagnostics = new StringWriter()
    let running = Worker.runAsync input output diagnostics
    let reader = FrameReader(output)
    member _.Diagnostics = diagnostics.ToString()
    member _.Running = running
    member _.Output = output
    member _.CloseInput() = input.Complete()

    member _.SendMany(frames: string list) =
        let bytes = Protocol.utf8.GetBytes(String.concat "\n" frames + "\n")
        input.WriteAsync(bytes.AsMemory(), CancellationToken.None).AsTask()

    member this.Send(frame) = this.SendMany [ frame ]

    member _.Read() =
        task {
            let! bytes =
                reader.ReadAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8.0))

            Assert.True(bytes.IsSome, "Expected a protocol frame.")
            Protocol.validateOutbound bytes.Value
            use document = JsonDocument.Parse(ReadOnlyMemory<byte>(bytes.Value))
            return document.RootElement.Clone()
        }

    member this.Terminal() =
        task {
            let mutable result = Unchecked.defaultof<JsonElement>
            let mutable reading = true

            while reading do
                let! frame = this.Read()

                if tag frame <> "progress" then
                    result <- frame
                    reading <- false

            return result
        }

    member this.Start() =
        task {
            do! this.Send hello
            let! ready = this.Read()
            Assert.Equal("ready", tag ready)
        }

    member this.Stop() =
        task {
            do! this.Send shutdown
            let! frame = this.Read()
            Assert.Equal("bye", tag frame)
            let! exitCode = running.WaitAsync(TimeSpan.FromSeconds(3.0))
            Assert.Equal(0, exitCode)
            Assert.Equal("", diagnostics.ToString())
        }

    interface IDisposable with
        member _.Dispose() =
            input.Dispose()
            output.Dispose()
            diagnostics.Dispose()

[<Fact>]
let ``Synthetic operations preserve IDs scopes paging and uint64 precision`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(request "1" "" "capabilities" "{}")
        let! capabilities = session.Terminal()
        Assert.Equal("capabilities", capabilities |> property "result" |> tag)
        do! session.Send(load "2" 0)
        let! loaded = session.Terminal()
        Assert.Equal(scope1, stringProperty "snapshotId" loaded)
        Assert.Equal("3", loaded |> property "result" |> stringProperty "objectCount")

        do! session.Send(request "3" scope1 "query" """{"text":"ignored","pageSize":2,"cursor":null}""")
        let! page = session.Terminal()
        let first = property "result" page
        Assert.Equal(2, (property "items" first).GetArrayLength())
        Assert.Equal("2", stringProperty "nextCursor" first)
        Assert.True((property "truncated" first).GetBoolean())
        do! session.Send(request "4" scope1 "query" """{"text":"ignored","pageSize":2,"cursor":"2"}""")
        let! page2 = session.Terminal()
        let last = (page2 |> property "result" |> property "items")[0]
        Assert.Equal("18446744073709551615", stringProperty "objectId" last)
        Assert.Equal("18446744073709551615", stringProperty "size" last)
        Assert.Equal("0xffffffffffffffff", stringProperty "address" last)

        do! session.Send(request "5" scope1 "details" """{"objectId":"1","pageSize":1,"cursor":null}""")
        let! details = session.Terminal()
        Assert.Equal(1, (details |> property "result" |> property "items").GetArrayLength())
        do! session.Send(request "6" scope1 "scene" """{"maxItems":1}""")
        let! scene = session.Terminal()
        Assert.Equal(1, (scene |> property "result" |> property "items").GetArrayLength())
        Assert.True((scene |> property "result" |> property "truncated").GetBoolean())
        do! session.Send(request "7" "" "recipe.validate" """{"recipe":{"schemaVersion":1,"query":"synthetic"}}""")
        let! recipe = session.Terminal()
        Assert.Equal("recipe", recipe |> property "result" |> tag)
        do! session.Send(request "8" scope1 "export" """{"format":"svg","maxBytes":1}""")
        let! exported = session.Terminal()
        Assert.Equal("0", exported |> property "result" |> stringProperty "byteLength")
        do! session.Stop()
    }

[<Fact>]
let ``Progress is bounded and cancellation idempotently produces one terminal`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(load "1" 5000)
        let! progress = session.Read()
        Assert.Equal("progress", tag progress)
        Assert.Equal("1", stringProperty "requestId" progress)
        Assert.Equal(JsonValueKind.Null, (property "snapshotId" progress).ValueKind)
        do! session.SendMany [ cancel "1"; cancel "1" ]
        let! terminal = session.Terminal()
        Assert.Equal("error", tag terminal)
        Assert.Equal("Cancelled", terminal |> property "error" |> stringProperty "code")
        do! session.SendMany [ cancel "1"; cancel "999" ]
        do! session.Stop()
    }

[<Fact>]
let ``Progress emissions are coalesced instead of queuing while output is paused`` () =
    task {
        use session = new Session()
        do! session.Start()
        session.Output.Pause()
        do! session.Send(load "1" 500)
        do! Task.Delay(350)
        session.Output.Resume()
        let progress = ResizeArray<JsonElement>()
        let mutable complete = false

        while not complete do
            let! frame = session.Read()

            if tag frame = "progress" then
                progress.Add frame
            else
                Assert.Equal("success", tag frame)
                complete <- true

        Assert.InRange(progress.Count, 1, 3)
        do! session.Stop()
    }

[<Fact>]
let ``Replacement loads cancel earlier loads and cancelled loads leave no snapshot`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.SendMany [ load "1" 5000; load "2" 0 ]
        let! one = session.Terminal()
        let! two = session.Terminal()

        let results =
            [ one; two ]
            |> Seq.map (fun frame -> stringProperty "requestId" frame, frame)
            |> Map.ofSeq

        Assert.Equal("Cancelled", results["1"] |> property "error" |> stringProperty "code")
        Assert.Equal(scope1, stringProperty "snapshotId" results["2"])
        do! session.SendMany [ load "3" 5000; cancel "3" ]
        let! cancelled = session.Terminal()
        Assert.Equal("Cancelled", cancelled |> property "error" |> stringProperty "code")
        do! session.Send(request "4" scope1 "scene" """{"maxItems":128}""")
        let! missing = session.Terminal()
        Assert.Equal("SnapshotNotFound", missing |> property "error" |> stringProperty "code")
        do! session.Send(load "5" 0)
        let! loaded = session.Terminal()
        Assert.Equal(scope2, stringProperty "snapshotId" loaded)
        do! session.Stop()
    }

[<Fact>]
let ``Dispose invalidates scope and subsequent loads receive fresh identifiers`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(load "1" 0)
        let! _ = session.Terminal()
        do! session.Send(request "2" scope1 "snapshot.dispose" "{}")
        let! disposed = session.Terminal()
        Assert.Equal("disposed", disposed |> property "result" |> tag)
        Assert.Equal(scope1, stringProperty "snapshotId" disposed)
        do! session.Send(request "3" scope1 "query" """{"text":"ignored","pageSize":128,"cursor":null}""")
        let! missing = session.Terminal()
        Assert.Equal("SnapshotNotFound", missing |> property "error" |> stringProperty "code")
        do! session.Send(load "4" 0)
        let! loaded = session.Terminal()
        Assert.Equal(scope2, stringProperty "snapshotId" loaded)
        do! session.Stop()
    }

[<Fact>]
let ``Unknown cursor returns a scoped error instead of overflowing`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(load "1" 0)
        let! _ = session.Terminal()

        do!
            session.Send(
                request "2" scope1 "query" """{"text":"ignored","pageSize":128,"cursor":"18446744073709551615"}"""
            )

        let! error = session.Terminal()
        Assert.Equal("InvalidRequest", error |> property "error" |> stringProperty "code")
        Assert.Equal(scope1, stringProperty "snapshotId" error)
        do! session.Stop()
    }

[<Theory>]
[<InlineData("snapshot.dispose")>]
[<InlineData("snapshot.load")>]
let ``Snapshot invalidation cancels previously accepted scoped work`` operation =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(load "1" 0)
        let! _ = session.Terminal()

        let invalidate =
            if operation = "snapshot.load" then
                load "3" 0
            else
                request "3" scope1 operation "{}"

        do!
            session.SendMany [
                request "2" scope1 "query" """{"text":"ignored","pageSize":128,"cursor":null}"""
                invalidate
            ]

        let! one = session.Terminal()
        let! two = session.Terminal()

        let results =
            [ one; two ]
            |> Seq.map (fun frame -> stringProperty "requestId" frame, frame)
            |> Map.ofSeq

        Assert.Equal("Cancelled", results["2"] |> property "error" |> stringProperty "code")
        Assert.Equal(scope1, stringProperty "snapshotId" results["2"])
        Assert.Equal("success", tag results["3"])
        do! session.Stop()
    }

[<Fact>]
let ``Outstanding slots remain occupied until terminal writes complete`` () =
    task {
        use session = new Session()
        do! session.Start()
        session.Output.Pause()
        do! session.SendMany [ for id in 1..8 -> request (string id) "" "capabilities" "{}" ]
        do! Task.Delay(100)
        do! session.Send(request "9" "" "capabilities" "{}")
        do! Task.Delay(50)
        session.Output.Resume()
        let mutable busy = false
        let ids = HashSet<string>()

        for _ in 1..9 do
            let! frame = session.Terminal()
            Assert.True(ids.Add(stringProperty "requestId" frame))

            if stringProperty "requestId" frame = "9" then
                Assert.Equal("Busy", frame |> property "error" |> stringProperty "code")
                busy <- true

        Assert.True(busy)
        do! session.Stop()
    }

[<Theory>]
[<InlineData("query", """{"text":"ignored","pageSize":128,"cursor":null}""")>]
[<InlineData("scene", """{"maxItems":128}""")>]
let ``Nine delayed synthetic requests admit exactly eight and return Busy for the ninth`` operation args =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(load "1" 0)
        let! _ = session.Terminal()
        do! session.SendMany [ for id in 2..10 -> request (string id) scope1 operation args ]
        let ids = HashSet<string>()
        let mutable successes = 0
        let mutable busy = 0

        for _ in 1..9 do
            let! frame = session.Terminal()
            Assert.True(ids.Add(stringProperty "requestId" frame))

            if tag frame = "success" then
                successes <- successes + 1
            else
                Assert.Equal("10", stringProperty "requestId" frame)
                Assert.Equal("Busy", frame |> property "error" |> stringProperty "code")
                busy <- busy + 1

        Assert.Equal(8, successes)
        Assert.Equal(1, busy)
        do! session.Stop()
    }

[<Theory>]
[<InlineData("query", """{"text":"ignored","pageSize":128,"cursor":null}""")>]
[<InlineData("scene", """{"maxItems":128}""")>]
let ``Synthetic scoped delay remains cancellable without waiting for completion`` operation args =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(load "1" 0)
        let! _ = session.Terminal()
        do! session.SendMany [ request "2" scope1 operation args; cancel "2" ]
        let! cancelled = session.Terminal()
        Assert.Equal("Cancelled", cancelled |> property "error" |> stringProperty "code")
        Assert.Equal(scope1, stringProperty "snapshotId" cancelled)
        do! session.Stop()
    }

[<Fact>]
let ``Failed request IDs still advance the strictly increasing watermark`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(request "7" scope1 "scene" """{"maxItems":1}""")
        let! error = session.Terminal()
        Assert.Equal("SnapshotNotFound", error |> property "error" |> stringProperty "code")
        do! session.Send(request "7" "" "capabilities" "{}")
        let! fatal = session.Read()
        Assert.Equal("fatal", tag fatal)
        Assert.Equal("ProtocolViolation", stringProperty "code" fatal)
        let! exitCode = session.Running.WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.Equal(2, exitCode)
    }

[<Theory>]
[<InlineData("""{"tag":"hello","versions":[2],"extensions":[]}""", "UnsupportedVersion")>]
[<InlineData("""{"tag":"hello","versions":[1],"extensions":["test.extension"]}""", "UnsupportedExtension")>]
[<InlineData("""{"tag":"shutdown","version":1}""", "ProtocolViolation")>]
[<InlineData("""{"tag":"hello","versions":[1],"extensions":[],"private":"secret-path.dmp"}""", "InvalidFrame")>]
let ``Handshake failures are fatal and diagnostics never echo payloads`` frame code =
    task {
        use session = new Session()
        do! session.Send frame
        let! fatal = session.Read()
        Assert.Equal("fatal", tag fatal)
        Assert.Equal(code, stringProperty "code" fatal)
        let! exitCode = session.Running.WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.Equal(2, exitCode)
        Assert.Equal($"Worker failure: {code}.{Environment.NewLine}", session.Diagnostics)
        Assert.DoesNotContain("secret-path", session.Diagnostics)
    }

[<Fact>]
let ``A repeated handshake fails instead of renegotiating a live connection`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send hello
        let! fatal = session.Read()
        Assert.Equal("ProtocolViolation", stringProperty "code" fatal)
        let! exitCode = session.Running.WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.Equal(2, exitCode)
    }

[<Fact>]
let ``Shutdown cancels work emits terminal responses and then bye`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.SendMany [ load "1" 5000; shutdown ]
        let! cancelled = session.Terminal()
        Assert.Equal("Cancelled", cancelled |> property "error" |> stringProperty "code")
        let! bye = session.Read()
        Assert.Equal("bye", tag bye)
        let! exitCode = session.Running.WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.Equal(0, exitCode)
    }

[<Fact>]
let ``EOF cancels active work and completes without requiring shutdown`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(load "1" 5000)
        session.CloseInput()
        let! exitCode = session.Running.WaitAsync(TimeSpan.FromSeconds(3.0))
        Assert.Equal(0, exitCode)
    }

[<Fact>]
let ``Shutdown with eight active requests and blocked output exits with bounded writes`` () =
    task {
        use session = new Session()
        do! session.Start()
        do! session.Send(load "1" 0)
        let! _ = session.Terminal()
        let writesBefore = session.Output.WriteCalls
        session.Output.Pause()

        do!
            session.SendMany [
                for id in 2..9 do
                    request (string id) scope1 "query" """{"text":"ignored","pageSize":128,"cursor":null}"""
                shutdown
            ]

        let! exitCode = session.Running.WaitAsync(TimeSpan.FromSeconds(4.0))
        Assert.Equal(2, exitCode)
        Assert.Contains("TransportTimeout", session.Diagnostics)
        Assert.InRange(session.Output.WriteCalls - writesBefore, 1, Protocol.MaxOutstanding + 1)
        session.Output.Resume()
    }

[<Fact>]
let ``A peer that never reads cannot prevent worker exit`` () =
    task {
        use input = new MemoryStream(Protocol.utf8.GetBytes(hello + "\n"))
        use output = new TestPipe()
        use diagnostics = new StringWriter()
        output.Pause()
        let elapsed = Stopwatch.StartNew()

        let! exitCode =
            (Worker.runAsync input output diagnostics).WaitAsync(TimeSpan.FromSeconds(4.0))

        Assert.Equal(2, exitCode)
        Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(4.0))
        Assert.Contains("TransportTimeout", diagnostics.ToString())
        output.Resume()
    }

[<Fact>]
let ``Startup has a finite deadline even if no bytes arrive`` () =
    task {
        use session = new Session()
        let! fatal = session.Read()
        Assert.Equal("TransportTimeout", stringProperty "code" fatal)
        let! exitCode = session.Running.WaitAsync(TimeSpan.FromSeconds(2.0))
        Assert.Equal(2, exitCode)
    }

[<Fact>]
let ``A partial frame cannot hold the reader indefinitely`` () =
    task {
        use input = new TestPipe()
        do! input.WriteAsync(Protocol.utf8.GetBytes("{").AsMemory(), CancellationToken.None)
        let reader = FrameReader(input, frameTimeout = TimeSpan.FromMilliseconds(100.0))

        let! failure =
            Assert.ThrowsAsync<ProtocolFailure>(fun () -> reader.ReadAsync(CancellationToken.None))

        match failure :> exn with
        | ProtocolFailure code -> Assert.Equal("TransportTimeout", code)
        | _ -> failwith "Expected a bounded frame timeout."
    }

type private SynchronouslyBlockedWriter(release: ManualResetEventSlim) =
    inherit MemoryStream()

    override _.WriteAsync(_: ReadOnlyMemory<byte>, _: CancellationToken) =
        release.Wait()
        ValueTask.CompletedTask

[<Fact>]
let ``Even synchronously blocked standard output cannot prevent a finite exit`` () =
    task {
        use release = new ManualResetEventSlim()
        use input = new MemoryStream(Protocol.utf8.GetBytes(hello + "\n"))
        use output = new SynchronouslyBlockedWriter(release)
        use diagnostics = new StringWriter()

        try
            let! exitCode =
                (Worker.runAsync input output diagnostics).WaitAsync(TimeSpan.FromSeconds(4.0))

            Assert.Equal(2, exitCode)
            Assert.Contains("TransportTimeout", diagnostics.ToString())
        finally
            release.Set()
    }

type private SynchronouslyBlockedDiagnostics(release: ManualResetEventSlim) =
    inherit StringWriter()

    override _.WriteLineAsync(_: string) =
        release.Wait()
        Task.CompletedTask

[<Fact>]
let ``Diagnostic backpressure cannot prevent fatal cleanup`` () =
    task {
        use release = new ManualResetEventSlim()
        use input = new MemoryStream(Protocol.utf8.GetBytes("{}\n"))
        use output = new MemoryStream()
        use diagnostics = new SynchronouslyBlockedDiagnostics(release)

        try
            let! exitCode =
                (Worker.runAsync input output diagnostics).WaitAsync(TimeSpan.FromSeconds(2.0))

            Assert.Equal(2, exitCode)
        finally
            release.Set()
    }
