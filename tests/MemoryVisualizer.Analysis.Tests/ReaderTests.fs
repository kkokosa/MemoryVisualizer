module MemoryVisualizer.Analysis.Tests.ReaderTests

open System
open System.IO
open System.Threading
open MemoryVisualizer.Analysis.ClrMd
open MemoryVisualizer.Cli
open MemoryVisualizer.Core
open MemoryVisualizer.Core.Analysis
open Xunit

let private read path options =
    ClrMdSnapshotReader().ReadSnapshotAsync(path, options, None, CancellationToken.None)

[<Fact>]
let ``Missing and empty paths are typed failures`` () =
    task {
        let! missing =
            read
                (Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".dmp"))
                SnapshotReaderOptions.defaults

        match missing with
        | Error(AnalysisError.FileNotFound _) -> ()
        | result -> failwithf "Unexpected result %A" result

        let! empty = read "" SnapshotReaderOptions.defaults

        match empty with
        | Error(AnalysisError.InvalidInput _) -> ()
        | result -> failwithf "Unexpected result %A" result
    }

[<Fact>]
let ``Invalid and truncated inputs never succeed`` () =
    task {
        let path = Path.GetTempFileName()

        try
            for bytes in [| [||]; [| 0uy; 1uy; 2uy; 3uy |]; [| 0x4Duy; 0x44uy; 0x4Duy; 0x50uy |] |] do
                File.WriteAllBytes(path, bytes)
                let! result = read path SnapshotReaderOptions.defaults
                Assert.True(Result.isError result, "Malformed dump must not produce a snapshot.")
        finally
            File.Delete path
    }

[<Fact>]
let ``Cancellation is honored before native loading`` () =
    use cancellation = new CancellationTokenSource()
    cancellation.Cancel()

    Assert.Throws<OperationCanceledException>(fun () ->
        ClrMdSnapshotReader()
            .ReadSnapshotAsync("missing.dmp", SnapshotReaderOptions.defaults, None, cancellation.Token)
        |> ignore)
    |> ignore

[<Theory>]
[<InlineData("WINDOWS", "X64", "LINUX", "X64")>]
[<InlineData("WINDOWS", "X64", "WINDOWS", "X86")>]
[<InlineData("LINUX", "Arm64", "LINUX", "Arm64")>]
let ``Unsupported compatibility is actionable`` hostOS hostArch targetOS targetArch =
    match Compatibility.check hostOS hostArch targetOS targetArch with
    | Error(AnalysisError.UnsupportedTarget message) -> Assert.NotEmpty message
    | result -> failwithf "Unexpected compatibility %A" result

[<Fact>]
let ``Ranges and scoped identities preserve uint64 and half open semantics`` () =
    let id = SnapshotId.create (Guid.NewGuid()) |> Result.defaultWith failwith
    let runtime = { SnapshotId = id; Index = 0 }

    let first: ObjectIdentity = {
        Runtime = runtime
        Address = UInt64.MaxValue - 16UL
    }

    let second = {
        first with
            Runtime = { runtime with Index = 1 }
    }

    Assert.NotEqual(first, second)

    let range = {
        Start = first.Address
        End = UInt64.MaxValue
    }

    Assert.Equal(16UL, AddressRange.length range)
    Assert.True(AddressRange.contains first.Address range)
    Assert.False(AddressRange.contains UInt64.MaxValue range)

    Assert.Throws<ArgumentException>(fun () -> AddressRange.length { Start = 2UL; End = 1UL } |> ignore)
    |> ignore

    let typ: TypeIdentity = {
        Runtime = runtime
        MethodTable = UInt64.MaxValue
    }

    Assert.NotEqual(typ, { typ with Runtime = second.Runtime })

[<Fact>]
let ``Defaults prohibit network caches and sensitive details`` () =
    let options = SnapshotReaderOptions.defaults
    Assert.False options.Dac.AllowNetwork
    Assert.True options.Dac.CacheDirectory.IsNone
    Assert.Empty options.Dac.TrustedPaths
    Assert.False options.IncludeStringDetails

[<Fact>]
let ``Native reference size limits do not narrow object sizes or claim coverage`` () =
    Assert.True(ReferenceCoverage.canEnumerate true (uint64 Int32.MaxValue) true)
    Assert.False(ReferenceCoverage.canEnumerate true (uint64 Int32.MaxValue + 1UL) true)
    Assert.False(ReferenceCoverage.canEnumerate true UInt64.MaxValue true)
    Assert.False(ReferenceCoverage.canEnumerate true 24UL false)
    Assert.True(ReferenceCoverage.canEnumerate false UInt64.MaxValue false)

[<Fact>]
let ``Invalid budgets and untrusted path policy fail before IO`` () =
    task {
        let options = SnapshotReaderOptions.defaults

        let cases = [
            {
                options with
                    Limits = { options.Limits with MaxObjects = 0 }
            }
            {
                options with
                    Dac = {
                        options.Dac with
                            TrustedPaths = Map.ofList [ 0, "relative-dac.dll" ]
                    }
            }
            {
                options with
                    Dac = { options.Dac with AllowNetwork = true }
            }
        ]

        for options in cases do
            let! result = read "missing.dmp" options

            match result with
            | Error(AnalysisError.InvalidInput _) -> ()
            | result -> failwithf "Expected policy error, got %A" result
    }

[<Theory>]
[<InlineData("--unknown")>]
[<InlineData("--dac")>]
let ``Inspect rejects invalid arguments without stdout`` argument =
    use stdout = new StringWriter()
    use stderr = new StringWriter()
    Assert.Equal(2, Inspect.run [| "missing.dmp"; argument |] stdout stderr CancellationToken.None)
    Assert.Equal("", stdout.ToString())
    Assert.NotEmpty(stderr.ToString())
