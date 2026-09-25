module MemoryVisualizer.Analysis.Tests.DacDownloadTests

open System
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Threading
open System.Threading.Tasks
open MemoryVisualizer.Analysis.ClrMd
open Xunit

type private StalledBody() =
    inherit Stream()
    let mutable disposed = false
    member _.Disposed = disposed
    override _.CanRead = true
    override _.CanSeek = false
    override _.CanWrite = false
    override _.Length = raise (NotSupportedException())

    override _.Position
        with get () = raise (NotSupportedException())
        and set _ = raise (NotSupportedException())

    override _.Flush() = ()
    override _.Seek(_, _) = raise (NotSupportedException())
    override _.SetLength _ = raise (NotSupportedException())
    override _.Write(_, _, _) = raise (NotSupportedException())
    override _.Read(_, _, _) = raise (NotSupportedException())

    override _.ReadAsync(_: Memory<byte>, cancellation: CancellationToken) =
        ValueTask<int>(
            task {
                do! Task.Delay(Timeout.Infinite, cancellation)
                return 0
            }
        )

    override this.Dispose(disposing) =
        disposed <- true
        base.Dispose disposing

type private ImmediateHeaders(body: Stream) =
    inherit HttpMessageHandler()

    override _.Send(request, _) =
        Assert.Equal("msdl.microsoft.com", request.RequestUri.Host)
        new HttpResponseMessage(HttpStatusCode.OK, Content = new StreamContent(body))

    override _.SendAsync(_, _) = raise (NotSupportedException())

[<Theory>]
[<InlineData(false)>]
[<InlineData(true)>]
let ``DAC deadline and caller cancellation cover a stalled response body`` cancelCaller =
    let directory =
        Path.Combine(Path.GetTempPath(), "MemoryVisualizer-dac-timeout-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory directory |> ignore
    let destination = Path.Combine(directory, "mscordaccore.dll")
    let body = new StalledBody()

    use client =
        new HttpClient(new ImmediateHeaders(body), Timeout = Timeout.InfiniteTimeSpan)

    use caller = new CancellationTokenSource()

    let deadline =
        if cancelCaller then
            TimeSpan.FromSeconds(30.0)
        else
            TimeSpan.FromMilliseconds(100.0)

    if cancelCaller then
        caller.CancelAfter(TimeSpan.FromMilliseconds(100.0))

    let elapsed = Stopwatch.StartNew()

    try
        Assert.ThrowsAny<OperationCanceledException>(fun () ->
            DacResolver.downloadWithClient
                client
                deadline
                [| "mscordaccore.dll"; "test-key"; "mscordaccore.dll" |]
                destination
                caller.Token
            |> ignore)
        |> ignore

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(5.0),
            "Headers-only timeout must not leave the body unbounded."
        )

        Assert.Equal(cancelCaller, caller.IsCancellationRequested)
        Assert.True body.Disposed
        Assert.Empty(Directory.GetFiles directory)
    finally
        Directory.Delete(directory, true)
