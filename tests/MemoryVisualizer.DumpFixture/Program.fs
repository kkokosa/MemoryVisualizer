namespace MemoryVisualizer.DumpFixture

open System
open System.Collections.Generic
open System.IO
open System.Reflection
open System.Reflection.Emit
open System.Runtime
open System.Runtime.CompilerServices
open System.Runtime.InteropServices
open System.Text.Json

[<AllowNullLiteral; Sealed>]
type SharedTarget() =
    [<DefaultValue>]
    val mutable Marker: string

[<AllowNullLiteral; Sealed>]
type CycleNode() =
    [<DefaultValue>]
    val mutable Name: string

    [<DefaultValue>]
    val mutable Next: CycleNode

    [<DefaultValue>]
    val mutable Shared: SharedTarget

[<Sealed>]
type WeakShortTarget() = class end

[<Sealed>]
type WeakLongTarget() = class end

[<Sealed>]
type DependentKey() = class end

[<Sealed>]
type DependentBridge() = class end

[<Sealed>]
type DependentLeaf() = class end

[<Sealed>]
type FixtureRoots() =
    [<DefaultValue>]
    val mutable CycleFirst: CycleNode

    [<DefaultValue>]
    val mutable CycleSecond: CycleNode

    [<DefaultValue>]
    val mutable SharedFirst: SharedTarget

    [<DefaultValue>]
    val mutable SharedSecond: SharedTarget

    [<DefaultValue>]
    val mutable DuplicateFirst: obj

    [<DefaultValue>]
    val mutable DuplicateSecond: obj

    [<DefaultValue>]
    val mutable NodeArray: CycleNode array

    [<DefaultValue>]
    val mutable ObjectArray: obj array

    [<DefaultValue>]
    val mutable ValueArray: int array

    [<DefaultValue>]
    val mutable PinnedBytes: byte array

    [<DefaultValue>]
    val mutable PohBytes: byte array

    [<DefaultValue>]
    val mutable LargeBytes: byte array

    [<DefaultValue>]
    val mutable LohSurvivors: byte array array

    [<DefaultValue>]
    val mutable SensitiveText: string

    [<DefaultValue>]
    val mutable WeakShort: WeakShortTarget

    [<DefaultValue>]
    val mutable WeakLong: WeakLongTarget

    [<DefaultValue>]
    val mutable DependentKey: DependentKey

    [<DefaultValue>]
    val mutable DependentTable: ConditionalWeakTable<obj, obj>

module Program =
    let private createDuplicatePayload assemblyName =
        let assembly =
            AssemblyBuilder.DefineDynamicAssembly(AssemblyName assemblyName, AssemblyBuilderAccess.Run)

        let emittedModule = assembly.DefineDynamicModule assemblyName

        let payload =
            emittedModule.DefineType(
                "MemoryVisualizer.DumpFixture.DuplicatePayload",
                TypeAttributes.Public ||| TypeAttributes.Sealed
            )

        payload.DefineDefaultConstructor MethodAttributes.Public |> ignore
        Activator.CreateInstance(payload.CreateType())

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let private createLargeObjectHoles () =
        // The returned array retains alternating LOH blocks; noncompacting GC can expose the holes.
        let allocations = Array.init 16 (fun _ -> Array.zeroCreate<byte> 100_000)
        Array.init 8 (fun index -> allocations[index * 2])

    [<MethodImpl(MethodImplOptions.NoInlining)>]
    let private createGraph () =
        let shared = SharedTarget(Marker = "synthetic-shared-target")
        let first = CycleNode(Name = "synthetic-cycle-first", Shared = shared)
        let second = CycleNode(Name = "synthetic-cycle-second", Shared = shared)
        first.Next <- second
        second.Next <- first

        let key = DependentKey()
        let bridge = DependentBridge()
        let leaf = DependentLeaf()
        let table = ConditionalWeakTable<obj, obj>()
        table.Add(key, bridge)
        table.Add(bridge, leaf)

        // Neither bridge nor leaf has an ordinary strong root once this non-inlined method returns.
        FixtureRoots(
            CycleFirst = first,
            CycleSecond = second,
            SharedFirst = shared,
            SharedSecond = shared,
            DuplicateFirst = createDuplicatePayload "MemoryVisualizer.DumpFixture.DynamicFirst",
            DuplicateSecond = createDuplicatePayload "MemoryVisualizer.DumpFixture.DynamicSecond",
            NodeArray = [| first; second; first |],
            ObjectArray = [| shared; first; shared; null |],
            ValueArray = [| 11; 22; 33; 44 |],
            PinnedBytes = Array.create 4096 0x2Auy,
            PohBytes = GC.AllocateArray<byte>(4096, pinned = true),
            LargeBytes = Array.create 100_000 0x5Auy,
            LohSurvivors = createLargeObjectHoles (),
            SensitiveText = String("MemoryVisualizer_SYNTHETIC_SENSITIVE_MARKER_DO_NOT_EXPORT".ToCharArray()),
            WeakShort = WeakShortTarget(),
            WeakLong = WeakLongTarget(),
            DependentKey = key,
            DependentTable = table
        )

    let private runtimeDacPath () =
        let fileName =
            if OperatingSystem.IsWindows() then "mscordaccore.dll"
            elif OperatingSystem.IsMacOS() then "libmscordaccore.dylib"
            else "libmscordaccore.so"

        let path = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), fileName)

        if not (File.Exists path) then
            failwith "The executing runtime's DAC was not found."

        path

    [<MethodImpl(MethodImplOptions.NoInlining ||| MethodImplOptions.NoOptimization)>]
    let private waitForRelease (buffer: byte array) (readiness: string) =
        // Establish the live interior stack root before advertising readiness, even in Release builds.
        let interior = &buffer[17]
        Console.Out.WriteLine readiness
        Console.Out.Flush()
        Console.ReadLine() |> ignore
        interior <- 43uy

    let private run () =
        let handles = ResizeArray<GCHandle>()

        try
            let graph = createGraph ()
            handles.Add(GCHandle.Alloc(graph, GCHandleType.Normal))
            handles.Add(GCHandle.Alloc(graph.PinnedBytes, GCHandleType.Pinned))
            handles.Add(GCHandle.Alloc(graph.WeakShort, GCHandleType.Weak))
            handles.Add(GCHandle.Alloc(graph.WeakLong, GCHandleType.WeakTrackResurrection))

            GCSettings.LargeObjectHeapCompactionMode <- GCLargeObjectHeapCompactionMode.Default
            GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = false)
            GC.WaitForPendingFinalizers()
            GC.Collect(2, GCCollectionMode.Forced, blocking = true, compacting = false)

            let readiness =
                JsonSerializer.Serialize {|
                    status = "ready"
                    processId = Environment.ProcessId
                    runtimeVersion = Environment.Version.ToString()
                    dacPath = runtimeDacPath ()
                |}

            waitForRelease graph.PinnedBytes readiness
            GC.KeepAlive graph
            0
        finally
            for allocated in handles do
                let mutable handle = allocated

                if handle.IsAllocated then
                    handle.Free()

    [<EntryPoint>]
    let main arguments =
        if arguments.Length <> 0 then
            Console.Error.WriteLine "Usage: MemoryVisualizer.DumpFixture (no arguments)"
            2
        else
            try
                run ()
            with error ->
                Console.Error.WriteLine("Fixture failed: {0}", error.Message)
                1
