module MemoryVisualizer.Analysis.Tests.ExportCommandTests

open System
open System.IO
open System.Threading
open MemoryVisualizer.Cli
open Xunit

let private invoke arguments =
    use stdout = new StringWriter()
    use stderr = new StringWriter()
    let code = ExportCommand.run arguments stdout stderr CancellationToken.None
    code, stdout.ToString(), stderr.ToString()

[<Theory>]
[<InlineData("--unknown", "Unsupported export option")>]
[<InlineData("--output", "Duplicate option")>]
[<InlineData("--max-elements", "Missing value")>]
let ``Export rejects malformed options before import`` flag expected =
    let code, output, error =
        invoke [|
            "missing.dmp"
            "--query"
            "MATCH(o:Object) RETURN o"
            "--output"
            "out.svg"
            flag
        |]

    Assert.Equal(2, code)
    Assert.Equal("", output)
    Assert.Contains(expected, error)

[<Fact>]
let ``Option values that look like flags are consumed atomically rather than stolen`` () =
    let code, _, error =
        invoke [| "missing.dmp"; "--query"; "--output"; "--output"; "valid.svg" |]

    Assert.Equal(2, code)
    Assert.Contains("MQL", error)
    Assert.DoesNotContain("Duplicate", error)

    let code, _, error =
        invoke [| "missing.dmp"; "--dac"; "--query"; "--output"; "valid.svg" |]

    Assert.Equal(2, code)
    Assert.Contains("--query is required", error)

[<Theory>]
[<InlineData("NUL.svg", "reserved device")>]
[<InlineData("invalid?.svg", "portable file name")>]
[<InlineData("", "must not be empty")>]
let ``Invalid destination fails before nonexistent dump import`` name expected =
    let code, _, error =
        invoke [| "missing.dmp"; "--query"; "MATCH(o:Object) RETURN o"; "--output"; name |]

    Assert.Equal(2, code)
    Assert.Contains(expected, error)

[<Fact>]
let ``Compile and all limit validation precede native import and filesystem creation`` () =
    for query, extra, expected in
        [
            "MATCH(o:Object) RETURN o.Unknown", [||], "MQL203"
            "MATCH(o:Object) RETURN o", [| "--max-elements"; "0" |], "SCN001"
            "MATCH(o:Object) RETURN o", [| "--max-svg-bytes"; "0" |], "MaxBytes"
            "MATCH(o:Object) RETURN o", [| "--view-start"; "0" |], "specified together"
        ] do
        let args =
            Array.append [| "missing.dmp"; "--query"; query; "--output"; "unused.svg" |] extra

        let code, output, error = invoke args
        Assert.Equal(2, code)
        Assert.Equal("", output)
        Assert.Contains(expected, error)

[<Fact>]
let ``Existing output input collision missing directories and failed import never alter files`` () =
    let directory =
        Path.Combine(Path.GetTempPath(), "MemoryVisualizer-export-test-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory directory |> ignore
    let output = Path.Combine(directory, "existing.svg")

    try
        File.WriteAllText(output, "sentinel")
        let args dump output = [| dump; "--query"; "MATCH(o:Object) RETURN o"; "--output"; output |]
        let code, _, error = invoke (args "missing.dmp" output)
        Assert.Equal(2, code)
        Assert.Contains("already exists", error)
        Assert.Equal("sentinel", File.ReadAllText output)
        let code, _, error = invoke (args output output)
        Assert.Equal(2, code)
        Assert.Contains("input dump", error)

        let code, _, error =
            invoke (args "missing.dmp" (Path.Combine(directory, "absent", "file.svg")))

        Assert.Equal(2, code)
        Assert.Contains("directory does not exist", error)

        let code, stdout, stderr =
            invoke (args "missing.dmp" (Path.Combine(directory, "new.svg")))

        Assert.Equal(2, code)
        Assert.Equal("", stdout)
        Assert.NotEmpty stderr
        Assert.Equal<string array>([| output |], Directory.GetFiles directory)
    finally
        File.Delete output
        Directory.Delete directory

[<Theory>]
[<InlineData("race", 2)>]
[<InlineData("cancel", 130)>]
[<InlineData("partial", 3)>]
[<InlineData("failure", 2)>]
let ``Atomic publisher discards incomplete writes and honors cancellation and no overwrite races`` action expected =
    let directory =
        Path.Combine(Path.GetTempPath(), "MemoryVisualizer-export-atomic-" + Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory directory |> ignore
    let output = Path.Combine(directory, "result.svg")
    use cancellation = new CancellationTokenSource()
    use stdout = new StringWriter()
    use stderr = new StringWriter()

    let exporter _ _ _ _ _ _ (stream: Stream) (_: CancellationToken) =
        stream.Write([| 1uy; 2uy; 3uy |])

        match action with
        | "race" ->
            File.WriteAllText(output, "competitor")
            0, ""
        | "cancel" ->
            cancellation.Cancel()
            0, ""
        | "partial" -> 3, "Source partial; no SVG published."
        | _ -> raise (IOException "injected failure")

    try
        let code =
            ExportCommand.runWith
                exporter
                [| "unused.dmp"; "--query"; "MATCH(o:Object) RETURN o"; "--output"; output |]
                stdout
                stderr
                cancellation.Token

        Assert.Equal(expected, code)
        Assert.Equal("", stdout.ToString())
        Assert.NotEmpty(stderr.ToString())
        Assert.Empty(Directory.GetFiles(directory, ".memoryvisualizer-*.tmp"))

        if action = "race" then
            Assert.Equal("competitor", File.ReadAllText output)
        else
            Assert.False(File.Exists output)
    finally
        File.Delete output
        Directory.Delete directory
