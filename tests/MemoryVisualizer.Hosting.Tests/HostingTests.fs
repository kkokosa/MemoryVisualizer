module MemoryVisualizer.Hosting.Tests.HostingTests

open System
open System.IO
open System.Threading
open MemoryVisualizer.Analysis.ClrMd
open MemoryVisualizer.Core.Analysis
open MemoryVisualizer.Hosting
open Xunit

[<Theory>]
[<InlineData("--help")>]
[<InlineData("-h")>]
[<InlineData("help")>]
let ``Help goes only to stdout and describes scaffold limits`` argument =
    use stdout = new StringWriter()
    use stderr = new StringWriter()
    Assert.Equal(0, CommandLine.run "test-host" [| argument |] stdout stderr)
    Assert.Contains("Usage: test-host", stdout.ToString())
    Assert.Contains("not implemented", stdout.ToString())
    Assert.Equal("", stderr.ToString())

[<Fact>]
let ``No arguments displays help and exits instead of starting a protocol loop`` () =
    use stdout = new StringWriter()
    use stderr = new StringWriter()
    Assert.Equal(0, CommandLine.run "test-host" [||] stdout stderr)
    Assert.Contains("Usage:", stdout.ToString())
    Assert.Equal("", stderr.ToString())

[<Theory>]
[<InlineData("--version")>]
[<InlineData("version")>]
let ``Version identifies the host and assembly version`` argument =
    use stdout = new StringWriter()
    use stderr = new StringWriter()
    Assert.Equal(0, CommandLine.run "test-host" [| argument |] stdout stderr)
    Assert.StartsWith("test-host 0.1.0-dev", stdout.ToString())
    Assert.Equal("", stderr.ToString())

[<Theory>]
[<InlineData("--dump")>]
[<InlineData("query")>]
[<InlineData("--unknown")>]
let ``Unsupported commands fail explicitly without stdout`` argument =
    use stdout = new StringWriter()
    use stderr = new StringWriter()
    Assert.Equal(2, CommandLine.run "test-host" [| argument |] stdout stderr)
    Assert.Equal("", stdout.ToString())
    Assert.Contains("Unsupported arguments", stderr.ToString())

[<Fact>]
let ``Help cannot mask unsupported trailing arguments`` () =
    use stdout = new StringWriter()
    use stderr = new StringWriter()
    Assert.Equal(2, CommandLine.run "test-host" [| "--help"; "dump.dmp" |] stdout stderr)
    Assert.Equal("", stdout.ToString())
    Assert.Contains("Unsupported arguments", stderr.ToString())

[<Fact>]
let ``Adapter reports missing dumps without fabricating metadata`` () =
    task {
        Assert.False(String.IsNullOrWhiteSpace ClrMdSnapshotReader.DependencyVersion)
        let reader = ClrMdSnapshotReader() :> ISnapshotReader
        let! result = reader.ReadMetadataAsync("does-not-exist.dmp", CancellationToken.None)

        match result with
        | Error(AnalysisError.FileNotFound message) -> Assert.Contains("does not exist", message)
        | result -> failwithf "Expected actionable missing-file error, got %A" result
    }

[<Fact>]
let ``Adapter honors already requested cancellation without native loading`` () =
    use cancellation = new CancellationTokenSource()
    cancellation.Cancel()
    let reader = ClrMdSnapshotReader() :> ISnapshotReader

    Assert.Throws<OperationCanceledException>(fun () ->
        reader.ReadMetadataAsync("does-not-exist.dmp", cancellation.Token) |> ignore)
    |> ignore
