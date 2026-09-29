namespace MemoryVisualizer.Analysis.Tests

open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.Diagnostics.NETCore.Client
open Microsoft.Diagnostics.Runtime
open MemoryVisualizer.Analysis.ClrMd
open MemoryVisualizer.Cli
open MemoryVisualizer.Core
open MemoryVisualizer.Core.Analysis
open MemoryVisualizer.Query
open MemoryVisualizer.Scene
open Xunit

type GeneratedDump() =
    let dumpType =
        match Environment.GetEnvironmentVariable("MEMORYVISUALIZER_FIXTURE_DUMP_TYPE") with
        | null
        | ""
        | "WithHeap" -> DumpType.WithHeap
        | "Full" -> DumpType.Full
        | _ ->
            invalidArg
                "MEMORYVISUALIZER_FIXTURE_DUMP_TYPE"
                "Use WithHeap (the CI default) or Full (explicit extended native-memory coverage)."

    let directory =
        Path.Combine(Path.GetTempPath(), "MemoryVisualizer-fixture-" + Guid.NewGuid().ToString("N"))

    let dumpPath = Path.Combine(directory, "synthetic.dmp")
    let mutable dacPath = ""

    do
        Directory.CreateDirectory directory |> ignore
        let targetFrameworkDirectory = DirectoryInfo(AppContext.BaseDirectory)
        let configuration = targetFrameworkDirectory.Parent.Name
        let bin = targetFrameworkDirectory.Parent.Parent.Parent.FullName

        let fixture =
            Path.Combine(
                bin,
                "MemoryVisualizer.DumpFixture",
                configuration,
                "net11.0",
                "MemoryVisualizer.DumpFixture.dll"
            )

        let host = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH")
        let host = if String.IsNullOrWhiteSpace host then "dotnet" else host

        let start =
            ProcessStartInfo(
                host,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            )

        start.ArgumentList.Add fixture
        use child = new Process(StartInfo = start)

        if not (child.Start()) then
            failwith "Synthetic fixture process failed to start."

        let stdout = ProcessOutput(4096)
        let stderr = ProcessOutput(4096)
        let stderrDrain = stderr.DrainAsync child.StandardError
        let mutable stdoutDrain = Task.CompletedTask
        let mutable captured = false

        try
            let line =
                child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds 30.0).GetAwaiter().GetResult()

            if isNull line then
                failwithf "Fixture exited before readiness. stderr tail: %s" stderr.Tail

            stdoutDrain <- stdout.DrainAsync(child.StandardOutput)

            use ready = JsonDocument.Parse line
            Assert.Equal("ready", ready.RootElement.GetProperty("status").GetString())
            Assert.Equal(child.Id, ready.RootElement.GetProperty("processId").GetInt32())
            dacPath <- ready.RootElement.GetProperty("dacPath").GetString()
            Assert.True(Path.IsPathFullyQualified dacPath && File.Exists dacPath)
            let captureLimit = TimeSpan.FromMinutes(3.0)
            use timeout = new CancellationTokenSource(captureLimit)

            try
                DiagnosticsClient(child.Id)
                    .WriteDumpAsync(dumpType, dumpPath, false, timeout.Token)
                    .GetAwaiter()
                    .GetResult()
            with :? OperationCanceledException when timeout.IsCancellationRequested ->
                let size =
                    if File.Exists dumpPath then
                        FileInfo(dumpPath).Length
                    else
                        0L

                let status =
                    if child.HasExited then
                        $"exited ({child.ExitCode})"
                    else
                        "running"

                failwithf
                    "Synthetic %O dump capture exceeded %.0f seconds; child %d is %s; dump bytes=%d. stdout tail: %s stderr tail: %s"
                    dumpType
                    captureLimit.TotalSeconds
                    child.Id
                    status
                    size
                    stdout.Tail
                    stderr.Tail

            child.StandardInput.WriteLine("release")
            child.StandardInput.Close()
            child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds 15.0).GetAwaiter().GetResult()
            Assert.Equal(0, child.ExitCode)
            captured <- true
        finally
            let mutable outputClosed = false

            try
                if not child.HasExited then
                    child.Kill(true)

                    if not (child.WaitForExit(10_000)) then
                        failwith "Synthetic fixture did not exit within ten seconds after termination."

                Task.WhenAll(stdoutDrain, stderrDrain).WaitAsync(TimeSpan.FromSeconds(10.0)).GetAwaiter().GetResult()
                outputClosed <- true
            finally
                if not captured || not outputClosed then
                    File.Delete dumpPath
                    Directory.Delete directory

    member _.Path = dumpPath
    member _.Directory = directory

    member _.Options = {
        SnapshotReaderOptions.defaults with
            Dac = {
                SnapshotReaderOptions.defaults.Dac with
                    TrustedPaths = Map.ofList [ 0, dacPath ]
            }
    }

    interface IDisposable with
        member _.Dispose() =
            File.Delete dumpPath
            Directory.Delete directory

type SnapshotIntegrationTests(fixture: GeneratedDump) =
    let read options progress token =
        ClrMdSnapshotReader().ReadSnapshotAsync(fixture.Path, options, progress, token)

    let snapshot result =
        match result with
        | Ok value -> value
        | Error error -> failwithf "Generated dump could not be read: %A" error

    let named name (value: HeapSnapshot) =
        let types =
            value.Types
            |> Array.filter (fun typ -> typ.Name = Some("MemoryVisualizer.DumpFixture." + name))
            |> Array.map _.Identity
            |> Set.ofArray

        value.Objects |> Array.filter (fun item -> types.Contains item.Type)

    interface IClassFixture<GeneratedDump>

    [<Fact>]
    member _.``Offline exact trusted DAC materializes complete map and scoped objects``() =
        task {
            let! result =
                read
                    {
                        fixture.Options with
                            IncludeReferences = false
                            IncludeRoots = false
                    }
                    None
                    CancellationToken.None

            let value = snapshot result
            Assert.False(value.IsPartial, sprintf "%A" value.Diagnostics)
            Assert.NotEmpty value.Segments
            Assert.NotEmpty value.Heaps
            Assert.Equal(2, (named "CycleNode" value).Length)
            Assert.Equal(1, (named "SharedTarget" value).Length)
            let duplicates = named "DuplicatePayload" value
            Assert.Equal(2, duplicates.Length)
            Assert.NotEqual(duplicates[0].Type, duplicates[1].Type)

            Assert.Equal(
                uint64 value.Objects.Length,
                value.Metadata.ObjectCount
                + uint64 (value.Objects |> Array.filter _.IsFree |> Array.length)
            )

            Assert.Contains(value.Segments, fun segment -> segment.Kind = HeapKind.Large)
            Assert.Contains(value.Segments, fun segment -> segment.Kind = HeapKind.Pinned)
            Assert.Contains(value.Segments, fun segment -> segment.Kind = HeapKind.Small)

            Assert.All(
                value.Objects,
                fun item ->
                    Assert.True(item.SizeBytes > 0UL)
                    Assert.Equal(value.Metadata.Id, item.Identity.Runtime.SnapshotId)
                    Assert.Equal(item.Identity.Runtime, item.Type.Runtime)
                    Assert.True(item.StringDetail.IsNone)

                    let segment =
                        value.Segments
                        |> Array.find (fun segment ->
                            segment.Runtime = item.Identity.Runtime && segment.Address = item.SegmentAddress)

                    Assert.True(AddressRange.contains item.Identity.Address segment.ObjectRange)
                    Assert.True(item.SizeBytes <= segment.ObjectRange.End - item.Identity.Address)

                    if segment.Kind <> HeapKind.Small then
                        Assert.True(item.Generation.IsNone)
            )

            Assert.All(
                value.Runtimes,
                fun runtime -> Assert.Equal(ExtractionCompleteness.NotRequested, runtime.References)
            )

            use stream =
                new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.None)

            Assert.True(stream.Length > 0L)
            // No native handles or deferred enumerators are needed to use the materialized graph.
            Assert.NotEmpty(value.Types |> Array.choose _.Name)

            use store =
                IndexedHeapSnapshot.Create(value, SnapshotIndexLimits.defaults, CancellationToken.None)
                |> Result.defaultWith (fun error -> failwithf "Generated snapshot could not be indexed: %A" error)

            let selectedType = duplicates[0].Type

            let selected =
                store.SelectObjects(
                    {
                        ObjectSelection.all with
                            Type = Some selectedType
                    },
                    { Offset = 0; Limit = 128 },
                    CancellationToken.None
                )
                |> Result.defaultWith (fun error -> failwithf "Indexed selection failed: %A" error)

            let expected =
                value.Objects
                |> Array.filter (fun item -> item.Type = selectedType)
                |> Array.sortBy _.Identity.Address

            Assert.Equal<HeapObject array>(expected, Seq.toArray selected.Items)
            Assert.False selected.IsTruncated
            Assert.False selected.IsPartial

            for item in selected.Items do
                Assert.Equal(Ok(Some item), store.TryGetObject(item.Identity, CancellationToken.None))

            let query =
                "MATCH (o:Object) WHERE o.Type = \"MemoryVisualizer.DumpFixture.DuplicatePayload\" RETURN o.Address,o.Size,o.MethodTable AS PIN (Label=o.Type)"

            let plan =
                Mql.compile QueryLimits.defaults value.Metadata.Id query
                |> Result.defaultWith (failwithf "%A")

            let queryResult =
                Mql.execute QueryLimits.defaults plan store (QueryExecutionContext.create CancellationToken.None)

            Assert.Equal(QueryStatus.Complete, queryResult.Status)
            Assert.False queryResult.SourcePartial
            Assert.Equal(2, queryResult.Rows.Length)
            Assert.Equal(2, queryResult.Directives.Length)

            Assert.Equal<Set<TypeIdentity>>(
                duplicates |> Array.map _.Type |> Set.ofArray,
                queryResult.Rows |> List.choose _.Entity.TypeIdentity |> Set.ofList
            )

            Assert.Equal<uint64 array>(
                duplicates |> Array.map _.Identity.Address |> Array.sort,
                queryResult.Rows |> List.map _.Entity.Address |> List.toArray
            )

            for row in queryResult.Rows do
                Assert.Equal(value.Metadata.Id, row.Entity.Runtime.SnapshotId)
                Assert.Equal(QueryValue.Unsigned row.Entity.Address, snd row.Values[0])
                Assert.Equal(QueryValue.Unsigned row.Entity.Size, snd row.Values[1])
        }

    [<Fact>]
    member _.``Native CLI query uses shared typed pipeline and explicit lossless JSON``() =
        let dac = fixture.Options.Dac.TrustedPaths[0]

        let source =
            "MATCH (o:Object) WHERE o.Type = \"MemoryVisualizer.DumpFixture.DuplicatePayload\" RETURN o.Address,o.Size,o.MethodTable"

        let invoke query extras =
            use stdout = new StringWriter()
            use stderr = new StringWriter()

            let arguments =
                Array.concat [ [| fixture.Path; "--dac"; dac; "--query"; query |]; extras ]

            let exit = QueryCommand.run arguments stdout stderr CancellationToken.None
            exit, stdout.ToString(), stderr.ToString()

        let exit, output, errors = invoke source [||]
        Assert.Equal(0, exit)
        Assert.Equal("", errors)
        use json = JsonDocument.Parse output
        let root = json.RootElement
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32())
        Assert.Equal("complete", root.GetProperty("status").GetString())
        Assert.False(root.GetProperty("sourcePartial").GetBoolean())
        let rows = root.GetProperty("rows").EnumerateArray() |> Seq.toArray
        Assert.Equal(2, rows.Length)

        Assert.NotEqual<string>(
            rows[0].GetProperty("entity").GetProperty("methodTable").GetString(),
            rows[1].GetProperty("entity").GetProperty("methodTable").GetString()
        )

        for row in rows do
            let entity = row.GetProperty("entity")
            Assert.Equal(root.GetProperty("snapshotId").GetString(), entity.GetProperty("snapshotId").GetString())
            let hex = entity.GetProperty("address").GetString()
            let number = UInt64.Parse(hex.Substring(2), Globalization.NumberStyles.HexNumber)
            let projected = (row.GetProperty("values")[0]).GetProperty("value")
            Assert.Equal("uint64", projected.GetProperty("kind").GetString())

            Assert.Equal(
                number.ToString(Globalization.CultureInfo.InvariantCulture),
                projected.GetProperty("value").GetString()
            )

            Assert.Equal(JsonValueKind.String, entity.GetProperty("size").ValueKind)

        Assert.DoesNotContain("StringDetail", output)
        Assert.DoesNotContain(fixture.Path, output)
        let exit, output, _ = invoke source [| "--max-results"; "1" |]
        Assert.Equal(3, exit)
        use json = JsonDocument.Parse output
        Assert.Equal("truncated", json.RootElement.GetProperty("status").GetString())
        Assert.Equal("Results", (json.RootElement.GetProperty("truncationReasons")[0]).GetString())
        Assert.Equal(1, json.RootElement.GetProperty("rows").GetArrayLength())
        let exit, output, _ = invoke "DRAW Memory(1,2,Runtime=2147483647,Heap=0)" [||]
        Assert.Equal(2, exit)
        use json = JsonDocument.Parse output
        Assert.Equal("failed", json.RootElement.GetProperty("status").GetString())
        use stdout = new StringWriter()
        use stderr = new StringWriter()

        let exit =
            QueryCommand.run
                [| "does-not-exist.dmp"; "--query"; "MATCH (o:Object) RETURN o.Unknown" |]
                stdout
                stderr
                CancellationToken.None

        Assert.Equal(2, exit)
        Assert.Contains("MQL203", stdout.ToString())
        use cancellation = new CancellationTokenSource()
        cancellation.Cancel()

        let exit =
            QueryCommand.run [| fixture.Path; "--dac"; dac; "--query"; source |] stdout stderr cancellation.Token

        Assert.Equal(130, exit)

    [<Fact>]
    member _.``Real WithHeap offline DAC MQL positioned scene SVG is stable across fresh imports``() =
        let options = {
            fixture.Options with
                IncludeReferences = false
                IncludeRoots = false
        }

        let source =
            "MATCH(s:Segment) RETURN s AS BOX(Background=Grey,Width=32);"
            + "MATCH(g:Generation) RETURN g AS BOX(Background=Blue,Width=24,Label=g.Generation);"
            + "MATCH(o:Object) WHERE o.Type=\"MemoryVisualizer.DumpFixture.DuplicatePayload\" RETURN o AS PIN(Background=Red,Label=o.Type,LabelPosition=OuterLeft)"

        let render () =
            let value =
                read options None CancellationToken.None
                |> fun work -> work.GetAwaiter().GetResult() |> snapshot

            let plan =
                Mql.compile QueryLimits.defaults value.Metadata.Id source
                |> Result.defaultWith (fun errors -> failwithf "%A" errors)

            use store =
                IndexedHeapSnapshot.Create(value, SnapshotIndexLimits.defaults, CancellationToken.None)
                |> Result.defaultWith (fun error -> failwithf "%A" error)

            let result =
                Mql.execute QueryLimits.defaults plan store (QueryExecutionContext.create CancellationToken.None)

            Assert.Equal(QueryStatus.Complete, result.Status)
            Assert.False result.SourcePartial

            let built =
                Scene.build SceneOptions.defaults result (SceneExecutionContext.create CancellationToken.None)

            Assert.Equal(SceneStatus.Complete, built.Status)
            let scene = built.Scene.Value
            Assert.Equal(result.Directives.Length, scene.Elements.Length)
            Assert.Contains(scene.Elements, fun element -> element.Layer = 1)
            Assert.Contains(scene.Elements, fun element -> element.Layer = 2)
            Assert.Equal(2, scene.Elements |> List.filter (fun element -> element.Layer = 5) |> List.length)
            use stream = new MemoryStream()

            Svg.write SvgLimits.defaults scene stream CancellationToken.None
            |> Result.defaultWith (fun error -> failwithf "%A" error)
            |> ignore

            scene.SnapshotId, stream.ToArray()

        let firstId, first = render ()
        let secondId, second = render ()
        Assert.NotEqual(firstId, secondId)
        Assert.Equal<byte array>(first, second)
        let xml = System.Xml.Linq.XDocument.Parse(Text.Encoding.UTF8.GetString first)
        Assert.Equal("svg", xml.Root.Name.LocalName)

    [<Fact>]
    member _.``Native CLI export exact caps repeat imports and budget failures have atomic file semantics``() =
        let source =
            "MATCH(o:Object) WHERE o.Type=\"MemoryVisualizer.DumpFixture.DuplicatePayload\" RETURN o AS BOX(Label=\"DUPLICATE!\",Background=Blue,Width=24)"

        let dac = fixture.Options.Dac.TrustedPaths[0]
        let output = Path.Combine(fixture.Directory, "native-export.svg")

        let invoke extras cancellationToken =
            use stdout = new StringWriter()
            use stderr = new StringWriter()

            let arguments =
                Array.append [| fixture.Path; "--dac"; dac; "--query"; source; "--output"; output |] extras

            let code = ExportCommand.run arguments stdout stderr cancellationToken
            code, stdout.ToString(), stderr.ToString()

        try
            let exact = [|
                "--max-results"
                "2"
                "--max-directives"
                "2"
                "--max-scene-directives"
                "2"
                "--max-elements"
                "2"
                "--max-lanes"
                "1"
                "--max-label-chars"
                "10"
                "--max-total-label-chars"
                "20"
            |]

            let code, stdout, stderr = invoke exact CancellationToken.None
            Assert.Equal(0, code)
            Assert.Equal("", stderr)
            Assert.Contains("SVG exported", stdout)
            let first = File.ReadAllBytes output
            File.Delete output

            let code, _, error =
                invoke
                    (Array.append exact [|
                        "--max-svg-bytes"
                        first.Length.ToString(Globalization.CultureInfo.InvariantCulture)
                    |])
                    CancellationToken.None

            Assert.Equal(0, code)
            Assert.Equal("", error)
            Assert.Equal<byte array>(first, File.ReadAllBytes output)
            File.Delete output

            for extras in
                [
                    [| "--max-results"; "1" |]
                    [| "--max-directives"; "1" |]
                    [| "--max-scene-directives"; "1" |]
                    [| "--max-elements"; "1" |]
                    [| "--max-label-chars"; "9" |]
                    [| "--max-total-label-chars"; "19" |]
                    [|
                        "--max-svg-bytes"
                        (first.Length - 1).ToString(Globalization.CultureInfo.InvariantCulture)
                    |]
                ] do
                let code, stdout, stderr = invoke extras CancellationToken.None
                Assert.Equal(3, code)
                Assert.Equal("", stdout)
                Assert.NotEmpty stderr
                Assert.False(File.Exists output)
                Assert.Empty(Directory.GetFiles(fixture.Directory, ".memoryvisualizer-*.tmp"))

            use cancellation = new CancellationTokenSource()
            cancellation.Cancel()
            let code, _, _ = invoke [||] cancellation.Token
            Assert.Equal(130, code)
            Assert.False(File.Exists output)
        finally
            File.Delete output

    [<Fact>]
    member _.``Cycles shared fields array edges weak handles and dependent chains retain semantics``() =
        task {
            let! result = read fixture.Options None CancellationToken.None
            let value = snapshot result
            Assert.False(value.IsPartial, sprintf "%A" value.Diagnostics)
            let cycles = named "CycleNode" value
            let shared = Assert.Single(named "SharedTarget" value)
            Assert.Equal(2, cycles.Length)

            for node in cycles do
                Assert.Contains(
                    value.Edges,
                    fun edge ->
                        edge.Source = node.Identity
                        && edge.Target <> node.Identity
                        && cycles |> Array.exists (fun other -> other.Identity = edge.Target)
                        && edge.FieldName = Some "Next"
                )

                Assert.Contains(
                    value.Edges,
                    fun edge ->
                        edge.Source = node.Identity
                        && edge.Target = shared.Identity
                        && edge.FieldName = Some "Shared"
                )

            let root = Assert.Single(named "FixtureRoots" value)

            let aliases =
                value.Edges
                |> Array.filter (fun edge -> edge.Source = root.Identity && edge.Target = shared.Identity)

            Assert.Equal(2, aliases.Length)
            Assert.Equal(2, aliases |> Array.choose _.Offset |> Array.distinct |> Array.length)

            Assert.Contains(
                value.Edges,
                fun edge ->
                    edge.Kind = ReferenceKind.ArrayElement
                    && edge.Target = shared.Identity
                    && edge.Offset.IsSome
            )

            for kind, typeName in [ "WeakShort", "WeakShortTarget"; "WeakLong", "WeakLongTarget" ] do
                let target = Assert.Single(named typeName value)

                let handle =
                    value.Handles
                    |> Array.find (fun handle -> handle.Kind = kind && handle.TargetAddress = target.Identity.Address)

                Assert.False handle.IsStrong
                Assert.DoesNotContain(value.Roots, fun root -> root.SlotAddress = handle.Address)

            for source, target in [ "DependentKey", "DependentBridge"; "DependentBridge", "DependentLeaf" ] do
                let source = Assert.Single(named source value)
                let target = Assert.Single(named target value)

                Assert.Contains(
                    value.Edges,
                    fun edge ->
                        edge.Source = source.Identity
                        && edge.Target = target.Identity
                        && edge.Kind = ReferenceKind.DependentHandle
                )

                Assert.Contains(
                    value.Handles,
                    fun handle ->
                        handle.Kind = "Dependent"
                        && not handle.IsStrong
                        && handle.TargetAddress = source.Identity.Address
                        && handle.DependentTargetAddress = Some target.Identity.Address
                )

            Assert.Contains(value.Roots, fun root -> root.IsPinned && root.Object.IsSome)

            let pinned =
                value.Edges
                |> Array.find (fun edge -> edge.Source = root.Identity && edge.FieldName = Some "PinnedBytes")

            Assert.Contains(
                value.Roots,
                fun root ->
                    root.IsInterior
                    && root.Object = Some pinned.Target
                    && root.RawTargetAddress
                       |> Option.exists (fun address -> address > pinned.Target.Address)
            )

            Assert.All(
                value.Objects |> Array.filter _.IsFree,
                fun item -> Assert.DoesNotContain(value.Edges, fun edge -> edge.Source = item.Identity)
            )
        }

    [<Fact>]
    member _.``Default offline policy never discovers a dump adjacent DAC``() =
        task {
            let trusted = fixture.Options.Dac.TrustedPaths[0]
            let adjacent = Path.Combine(fixture.Directory, Path.GetFileName trusted)

            try
                File.Copy(trusted, adjacent)
                let! result = read SnapshotReaderOptions.defaults None CancellationToken.None

                match result with
                | Error(AnalysisError.DacNotFound _) -> ()
                | result -> failwithf "Expected missing DAC, got %A" result
            finally
                File.Delete adjacent
        }

    [<Fact>]
    member _.``Explicit trusted symbol cache works with network disabled``() =
        task {
            let cache = Path.Combine(fixture.Directory, "trusted-cache")

            try
                let parts =
                    use target =
                        DataTarget.LoadDump(fixture.Path, DataTargetOptions(SymbolPaths = [||]))

                    let library =
                        target.ClrVersions[0].DebuggingLibraries
                        |> Seq.find (fun library ->
                            library.Kind = DebugLibraryKind.Dac
                            && library.Platform = target.DataReader.TargetPlatform
                            && library.TargetArchitecture = RuntimeInformation.ProcessArchitecture)

                    let name = Path.GetFileName(library.FileName).ToLowerInvariant()

                    let key =
                        if not library.IndexBuildId.IsDefaultOrEmpty then
                            let platform =
                                if library.Platform = OSPlatform.OSX then
                                    "mach-uuid"
                                else
                                    "elf-buildid"

                            let prefix =
                                if library.ArchivedUnder = SymbolProperties.Coreclr then
                                    "coreclr-"
                                else
                                    ""

                            platform
                            + "-"
                            + prefix
                            + Convert.ToHexString(library.IndexBuildId.AsSpan()).ToLowerInvariant()
                        else
                            $"{uint32 library.IndexTimeStamp:x8}{uint32 library.IndexFileSize:x}"

                    [| name; key; name |]

                let cachedDac = Path.Combine(Array.append [| cache |] parts)
                Directory.CreateDirectory(Path.GetDirectoryName cachedDac) |> ignore
                File.Copy(fixture.Options.Dac.TrustedPaths[0], cachedDac)

                let options = {
                    fixture.Options with
                        IncludeReferences = false
                        IncludeRoots = false
                        Dac = {
                            SnapshotReaderOptions.defaults.Dac with
                                CacheDirectory = Some cache
                        }
                }

                let! result = read options None CancellationToken.None
                let value = snapshot result
                Assert.False(value.IsPartial, sprintf "%A" value.Diagnostics)
                Assert.Equal(2, (named "CycleNode" value).Length)
            finally
                if Directory.Exists cache then
                    Directory.Delete(cache, true)
        }

    [<Fact>]
    member _.``Missing and corrupt explicit DACs produce actionable failures``() =
        task {
            let path = Path.Combine(fixture.Directory, "invalid-dac.bin")

            let options = {
                fixture.Options with
                    Dac = {
                        fixture.Options.Dac with
                            TrustedPaths = Map.ofList [ 0, path ]
                    }
            }

            let! missing = read options None CancellationToken.None

            match missing with
            | Error(AnalysisError.DacNotFound _) -> ()
            | result -> failwithf "Expected DAC missing, got %A" result

            try
                File.WriteAllBytes(path, [| 0uy; 1uy; 2uy |])
                let! corrupt = read options None CancellationToken.None

                match corrupt with
                | Error(AnalysisError.DacLoadFailed _) -> ()
                | result -> failwithf "Expected DAC invalid, got %A" result
            finally
                File.Delete path
        }

    [<Fact>]
    member _.``Budgets produce usable explicitly partial data``() =
        task {
            let options = {
                fixture.Options with
                    Limits = {
                        fixture.Options.Limits with
                            MaxObjects = 2
                            MaxEdges = 1
                            MaxRoots = 1
                            MaxHandles = 1
                    }
            }

            let! result = read options None CancellationToken.None
            let value = snapshot result
            Assert.True value.IsPartial
            Assert.Equal(2, value.Objects.Length)
            Assert.True(value.Edges.Length <= 1)
            Assert.True(value.Roots.Length <= 1)
            Assert.True(value.Handles.Length <= 1)
            Assert.Contains(value.Diagnostics, fun item -> item.Code = "LimitReached")
            Assert.NotEmpty value.Segments
            let reader = ClrMdSnapshotReader(options) :> ISnapshotReader
            let! metadata = reader.ReadMetadataAsync(fixture.Path, CancellationToken.None)

            match metadata with
            | Error(AnalysisError.PartialMetadata _) -> ()
            | result -> failwithf "Metadata-only API must not hide partial status: %A" result
        }

    [<Fact>]
    member _.``Optional string details obey per string and aggregate limits``() =
        task {
            let options = {
                fixture.Options with
                    IncludeReferences = false
                    IncludeRoots = false
                    IncludeStringDetails = true
                    Limits = {
                        fixture.Options.Limits with
                            MaxStringObjects = 3
                            MaxStringCharacters = 8
                            MaxTotalStringCharacters = 17
                    }
            }

            let! result = read options None CancellationToken.None
            let value = snapshot result
            let details = value.Objects |> Array.choose _.StringDetail
            Assert.NotEmpty details
            Assert.True(details.Length <= 3)
            Assert.True((details |> Array.sumBy (fun detail -> detail.Value.Length)) <= 17)
            Assert.All(details, fun detail -> Assert.True(detail.Value.Length <= 8))
            Assert.Contains(details, fun detail -> detail.Truncated)
            Assert.Equal(ExtractionCompleteness.Partial, value.StringDetails)
        }

    [<Fact>]
    member _.``Progress cancellation releases dump resources``() =
        task {
            use cancellation = new CancellationTokenSource()

            let progress = {
                new IProgress<SnapshotProgress> with
                    member _.Report value =
                        if value.Stage = ExtractionStage.MemoryMap then
                            cancellation.Cancel()
            }

            let! _ =
                Assert.ThrowsAnyAsync<OperationCanceledException>(fun () ->
                    read fixture.Options (Some progress) cancellation.Token :> Task)

            use stream =
                new FileStream(fixture.Path, FileMode.Open, FileAccess.Read, FileShare.None)

            Assert.True(stream.Length > 0L)
        }

    [<Fact>]
    member _.``Missing synthetic heap page never produces complete empty success``() =
        task {
            let options = {
                fixture.Options with
                    IncludeReferences = false
                    IncludeRoots = false
            }

            let! result = read options None CancellationToken.None
            let original = snapshot result
            let target = (named "CycleNode" original)[0]
            let damaged = Path.Combine(fixture.Directory, "missing-heap-page.dmp")

            try
                File.Copy(fixture.Path, damaged)
                DumpMutation.hideMemoryRange damaged target.Identity.Address

                let missing =
                    use probe =
                        DataTarget.LoadDump(
                            damaged,
                            DataTargetOptions(
                                SkipRuntimeEnumeration = true,
                                SymbolPaths = [||],
                                FileLocator = NoFileLocator()
                            )
                        )

                    let mutable pointer = 0UL
                    not (probe.DataReader.ReadPointer(target.Identity.Address, &pointer))

                Assert.True(
                    missing,
                    "The mutation must actually remove the fixture object's memory from the native reader."
                )

                let! result =
                    ClrMdSnapshotReader().ReadSnapshotAsync(damaged, options, None, CancellationToken.None)

                match result with
                | Error _ -> ()
                | Ok value ->
                    Assert.True(value.IsPartial, "Missing heap pages must not report complete traversal.")
                    Assert.NotEmpty value.Diagnostics
                    Assert.NotEmpty value.Segments
            finally
                File.Delete damaged
        }

    [<Fact>]
    member _.``Inspect emits only bounded lossless summary not heap strings``() =
        use stdout = new StringWriter()
        use stderr = new StringWriter()
        let dac = fixture.Options.Dac.TrustedPaths[0]

        let code =
            Inspect.run [| fixture.Path; "--dac"; dac; "--memory-map-only" |] stdout stderr CancellationToken.None

        Assert.Equal(0, code)
        Assert.Equal("", stderr.ToString())
        use json = JsonDocument.Parse(stdout.ToString())
        Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("objectCount").ValueKind)
        Assert.Equal(1, json.RootElement.GetProperty("schemaVersion").GetInt32())
        Assert.False(json.RootElement.GetProperty("partial").GetBoolean())
        Assert.DoesNotContain("SYNTHETIC_SENSITIVE", stdout.ToString())

    [<Fact>]
    member _.``Dangling reference preserves raw edge and marks only M2 partial``() =
        task {
            let! result = read fixture.Options None CancellationToken.None
            let original = snapshot result
            let node = (named "CycleNode" original)[0]

            let reference =
                original.Edges
                |> Array.find (fun edge -> edge.Source = node.Identity && edge.FieldName = Some "Next")

            let damaged = Path.Combine(fixture.Directory, "dangling-reference.dmp")
            let bogus = 0x1111222233334444UL

            try
                File.Copy(fixture.Path, damaged)

                DumpMutation.rewritePointer
                    damaged
                    (reference.Source.Address
                     + uint64 original.Target.PointerSizeBytes
                     + uint64 reference.Offset.Value)
                    reference.Target.Address
                    bogus

                let! result =
                    ClrMdSnapshotReader().ReadSnapshotAsync(damaged, fixture.Options, None, CancellationToken.None)

                let value = snapshot result
                Assert.True value.IsPartial
                Assert.Equal(ExtractionCompleteness.Complete, value.Runtimes[0].MemoryMap)
                Assert.Equal(ExtractionCompleteness.Partial, value.Runtimes[0].References)

                Assert.Contains(
                    value.Edges,
                    fun edge -> edge.Source.Address = reference.Source.Address && edge.Target.Address = bogus
                )

                Assert.Contains(value.Diagnostics, fun item -> item.Code = "UnresolvedReference")
            finally
                File.Delete damaged
        }
