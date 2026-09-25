namespace MemoryVisualizer.Analysis.ClrMd

open System
open System.Collections.Generic
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Microsoft.Diagnostics.Runtime
open MemoryVisualizer.Core
open MemoryVisualizer.Core.Analysis

exception private LimitReached of string

module internal Extraction =
    let private text (value: string) =
        if isNull value then
            None
        else
            Some(
                if value.Length > 4096 then
                    value.Substring(0, 4096)
                else
                    value
            )

    let private range (value: MemoryRange) : AddressRange = { Start = value.Start; End = value.End }

    let private kind =
        function
        | GCSegmentKind.Large -> HeapKind.Large
        | GCSegmentKind.Pinned -> HeapKind.Pinned
        | GCSegmentKind.Frozen -> HeapKind.Frozen
        | GCSegmentKind.Generation0
        | GCSegmentKind.Generation1
        | GCSegmentKind.Generation2
        | GCSegmentKind.Ephemeral -> HeapKind.Small
        | value -> HeapKind.Unknown(string value)

    let private nativeFailure (error: exn) =
        match error with
        | :? ClrDiagnosticsException
        | :? IOException
        | :? COMException
        | :? BadImageFormatException -> true
        | _ -> false

    let validate (options: SnapshotReaderOptions) =
        let limits = options.Limits

        let positive = [
            limits.MaxObjects
            limits.MaxTypes
            limits.MaxSegments
            limits.MaxEdges
            limits.MaxRoots
            limits.MaxHandles
            limits.MaxDiagnostics
            limits.MaxStringObjects
            limits.MaxStringCharacters
            limits.MaxTotalStringCharacters
        ]

        if positive |> List.exists (fun value -> value <= 0) then
            raise (
                AnalysisFailure(
                    AnalysisError.InvalidInput
                        "All extraction limits must be positive. Disable a stage through its include option, not a zero limit."
                )
            )

        if limits.MaxStringCharacters > 1_048_576 then
            raise (
                AnalysisFailure(
                    AnalysisError.InvalidInput "Individual string details cannot exceed 1,048,576 characters."
                )
            )

        DacResolver.validate options.Dac

    let run
        (target: DataTarget)
        (options: SnapshotReaderOptions)
        (progress: IProgress<SnapshotProgress> option)
        (token: CancellationToken)
        =
        let limits = options.Limits
        let snapshotId = SnapshotId.create (Guid.NewGuid()) |> Result.defaultWith invalidOp
        let objects = ResizeArray<HeapObject>()
        let types = ResizeArray<HeapType>()
        let segments = ResizeArray<HeapSegment>()
        let heaps = ResizeArray<HeapInfo>()
        let runtimes = ResizeArray<RuntimeSnapshot>()
        let edges = ResizeArray<ObjectEdge>()
        let roots = ResizeArray<HeapRoot>()
        let handles = ResizeArray<HeapHandle>()
        let diagnostics = ResizeArray<SnapshotDiagnostic>()
        let mutable anyPartial = false
        let mutable stringPartial = false
        let mutable strings = 0
        let mutable stringCharacters = 0
        let mutable firstFailure = None
        let memory = target.DataReader
        let scratch = Array.zeroCreate<byte> 65536

        let report stage index count =
            token.ThrowIfCancellationRequested()

            progress
            |> Option.iter (fun sink ->
                sink.Report {
                    Stage = stage
                    RuntimeIndex = index
                    Completed = uint64 count
                })

            token.ThrowIfCancellationRequested()

        let diagnostic stage runtime address code message =
            anyPartial <- true

            if diagnostics.Count < limits.MaxDiagnostics then
                diagnostics.Add {
                    Code = code
                    Message = message
                    Stage = stage
                    Runtime = runtime
                    Address = address
                }
            elif
                diagnostics.Count = limits.MaxDiagnostics
                && diagnostics[diagnostics.Count - 1].Code <> "DiagnosticLimit"
            then
                diagnostics[diagnostics.Count - 1] <- {
                    Code = "DiagnosticLimit"
                    Message = "Further diagnostics were omitted by the configured limit."
                    Stage = stage
                    Runtime = runtime
                    Address = None
                }

        let ensure count maximum description =
            token.ThrowIfCancellationRequested()

            if count >= maximum then
                raise (LimitReached description)

        let readable start length =
            let mutable address = start
            let mutable remaining = length
            let mutable valid = true

            while valid && remaining > 0UL do
                token.ThrowIfCancellationRequested()
                let count = int (min remaining (uint64 scratch.Length))
                let read = memory.Read(address, scratch.AsSpan(0, count))

                if read <= 0 then
                    valid <- false
                else
                    address <- address + uint64 read
                    remaining <- remaining - uint64 read

            valid

        report ExtractionStage.Discovery None 0
        let infos = target.ClrVersions

        if infos.IsEmpty then
            raise (
                AnalysisFailure(
                    AnalysisError.HeapUnavailable
                        "No supported CLR was discovered. Capture a managed process with heap and runtime module data."
                )
            )

        if infos.Length > 32 then
            raise (AnalysisFailure(AnalysisError.InvalidDump "Dump exceeds the 32-runtime safety limit."))

        for index in 0 .. infos.Length - 1 do
            token.ThrowIfCancellationRequested()
            let mutable info = infos[index]
            let mutable versionSource = "ClrMetadata"

            let identity = {
                SnapshotId = snapshotId
                Index = index
            }

            let objectIdentity address : ObjectIdentity = {
                Runtime = identity
                Address = address
            }

            let mutable mapState = ExtractionCompleteness.Complete

            let mutable refsState =
                if options.IncludeReferences then
                    ExtractionCompleteness.Complete
                else
                    ExtractionCompleteness.NotRequested

            let mutable rootsState =
                if options.IncludeRoots then
                    ExtractionCompleteness.Complete
                else
                    ExtractionCompleteness.NotRequested

            let mutable handlesState =
                if options.IncludeReferences || options.IncludeRoots then
                    ExtractionCompleteness.Complete
                else
                    ExtractionCompleteness.NotRequested

            let mutable canWalk = false
            let mutable currentStage = ExtractionStage.Discovery
            let firstEdge = edges.Count

            let mark stage address code message =
                match stage with
                | ExtractionStage.Discovery
                | ExtractionStage.MemoryMap -> mapState <- ExtractionCompleteness.Partial
                | ExtractionStage.References -> refsState <- ExtractionCompleteness.Partial
                | ExtractionStage.Roots -> rootsState <- ExtractionCompleteness.Partial
                | ExtractionStage.Handles -> handlesState <- ExtractionCompleteness.Partial
                | ExtractionStage.StringDetails -> stringPartial <- true

                diagnostic stage (Some identity) address code message

            let stageWork stage action =
                if currentStage <> stage then
                    report stage (Some index) 0

                currentStage <- stage

                try
                    action ()
                with
                | LimitReached message -> mark stage None "LimitReached" message
                | error when nativeFailure error ->
                    mark
                        stage
                        None
                        "NativeReadFailed"
                        $"Native extraction failed ({error.GetType().Name}). Capture a full heap dump with the matching runtime DAC."

            try
                if info.IsSingleFile then
                    raise (
                        AnalysisFailure(
                            AnalysisError.UnsupportedTarget
                                "Single-file runtime dumps are not supported yet: ClrMD skips DAC version comparison for this layout. Use a framework-dependent/non-single-file capture until build-identity verification is available."
                        )
                    )

                let recovered, source = RuntimeVersion.recover target info token
                info <- recovered
                versionSource <- source
                let dacPath = DacResolver.resolve options.Dac index info token

                use runtime =
                    try
                        info.CreateRuntime(dacPath, false)
                    with
                    | :? InvalidOperationException
                    | :? ArgumentException
                    | :? DllNotFoundException
                    | :? BadImageFormatException
                    | :? InvalidDataException
                    | :? IOException
                    | :? ClrDiagnosticsException
                    | :? COMException ->
                        raise (
                            AnalysisFailure(
                                AnalysisError.DacLoadFailed
                                    $"Unable to load the trusted DAC for runtime {index} ({info.Version}). Check exact build/architecture, DAC signature on Windows, and native dependencies. Mismatch checks remain enabled."
                            )
                        )

                let heap = runtime.Heap
                canWalk <- heap.CanWalkHeap

                if not canWalk then
                    mark
                        ExtractionStage.MemoryMap
                        None
                        "HeapNotWalkable"
                        "The GC structures are not walkable (possibly a GC in progress). Capture another full heap dump."

                let typeIds = Dictionary<uint64, TypeIdentity>()
                let runtimeObjects = ResizeArray<HeapObject>()

                let intern (typ: ClrType) =
                    match typeIds.TryGetValue typ.MethodTable with
                    | true, value -> value
                    | _ ->
                        ensure types.Count limits.MaxTypes "Type limit reached; increase MaxTypes to extract more."

                        let id = {
                            Runtime = identity
                            MethodTable = typ.MethodTable
                        }

                        typeIds.Add(typ.MethodTable, id)

                        types.Add {
                            Identity = id
                            Name = text typ.Name
                            ModuleAddress = if isNull typ.Module then None else Some typ.Module.Address
                            MetadataToken = typ.MetadataToken
                            IsArray = typ.IsArray
                            IsString = typ.IsString
                            IsFree = typ.IsFree
                            ContainsPointers = typ.ContainsPointers
                        }

                        id

                let details (obj: ClrObject) =
                    if not options.IncludeStringDetails || not obj.Type.IsString then
                        None
                    elif
                        strings >= limits.MaxStringObjects
                        || stringCharacters >= limits.MaxTotalStringCharacters
                    then
                        if not stringPartial then
                            mark
                                ExtractionStage.StringDetails
                                None
                                "StringLimit"
                                "Optional string detail budget exhausted; remaining values were not copied."

                        None
                    else
                        let maximum =
                            min limits.MaxStringCharacters (limits.MaxTotalStringCharacters - stringCharacters)

                        let mutable length = 0

                        if
                            not (memory.Read<int>(obj.Address + uint64 memory.PointerSize, &length))
                            || length < 0
                        then
                            mark
                                ExtractionStage.StringDetails
                                (Some obj.Address)
                                "StringUnavailable"
                                "String length is unavailable."

                            None
                        else
                            let value = obj.AsString(maximum)

                            if isNull value then
                                mark
                                    ExtractionStage.StringDetails
                                    (Some obj.Address)
                                    "StringUnavailable"
                                    "String bytes are unavailable."

                                None
                            else
                                strings <- strings + 1
                                stringCharacters <- stringCharacters + value.Length

                                Some {
                                    Value = value
                                    Truncated = length > value.Length
                                }

                stageWork ExtractionStage.MemoryMap (fun () ->
                    for subHeap in heap.SubHeaps do
                        ensure heaps.Count limits.MaxSegments "Heap limit reached."

                        heaps.Add {
                            Runtime = identity
                            Index = subHeap.Index
                            Address = subHeap.Address
                            IsServer = heap.IsServer
                            HasRegions = subHeap.HasRegions
                            HasPinnedObjectHeap = subHeap.HasPinnedObjectHeap
                        }

                    let contexts = ResizeArray<AddressRange>()

                    for context in heap.EnumerateAllocationContexts() do
                        ensure contexts.Count limits.MaxSegments "Allocation-context limit reached."
                        contexts.Add(range context)

                    for segment in heap.Segments do
                        ensure segments.Count limits.MaxSegments "Segment limit reached; increase MaxSegments."
                        let objectRange = range segment.ObjectRange

                        let localContexts =
                            contexts
                            |> Seq.filter (fun item -> item.Start >= objectRange.Start && item.Start < objectRange.End)
                            |> Seq.toArray

                        let small = kind segment.Kind = HeapKind.Small

                        let generations =
                            if small then
                                [|
                                    {
                                        Generation = 0
                                        Range = range segment.Generation0
                                    }
                                    {
                                        Generation = 1
                                        Range = range segment.Generation1
                                    }
                                    {
                                        Generation = 2
                                        Range = range segment.Generation2
                                    }
                                |]
                                |> Array.filter (fun item -> item.Range.End <> item.Range.Start)
                            else
                                [||]

                        segments.Add {
                            Runtime = identity
                            HeapIndex = segment.SubHeap.Index
                            Address = segment.Address
                            Kind = kind segment.Kind
                            IsPinned = segment.IsPinned
                            ObjectRange = objectRange
                            CommittedRange = range segment.CommittedMemory
                            ReservedRange = range segment.ReservedMemory
                            Generations = generations
                            AllocationContexts = localContexts
                        }

                        let ranges =
                            Array.concat [|
                                [| objectRange; range segment.CommittedMemory; range segment.ReservedMemory |]
                                generations |> Array.map _.Range
                                localContexts
                            |]

                        if ranges |> Array.exists (fun item -> item.End < item.Start) then
                            mark
                                ExtractionStage.MemoryMap
                                (Some segment.Address)
                                "InvalidRange"
                                "A segment has an inverted address range."
                        elif canWalk && objects.Count < limits.MaxObjects && types.Count < limits.MaxTypes then
                            stageWork ExtractionStage.MemoryMap (fun () ->
                                let alignment =
                                    if segment.Kind = GCSegmentKind.Large || segment.Kind = GCSegmentKind.Pinned then
                                        8UL
                                    else
                                        uint64 memory.PointerSize

                                let align value =
                                    (value + alignment - 1UL) &&& ~~~(alignment - 1UL)

                                let minimum = uint64 memory.PointerSize * 3UL
                                let contextMap = localContexts |> Seq.map (fun item -> item.Start, item.End) |> dict

                                let skipContexts address =
                                    let mutable next = address

                                    let mutable skipping =
                                        segment.Kind <> GCSegmentKind.Large && segment.Kind <> GCSegmentKind.Frozen

                                    let mutable attempts = 0

                                    while skipping && attempts <= localContexts.Length do
                                        attempts <- attempts + 1

                                        match contextMap.TryGetValue next with
                                        | true, limit when limit >= next && limit <= UInt64.MaxValue - align minimum ->
                                            next <- min objectRange.End (limit + align minimum)
                                        | _ -> skipping <- false

                                    next

                                let mutable expected = skipContexts segment.FirstObjectAddress

                                for obj in segment.EnumerateObjects(false) do
                                    ensure
                                        objects.Count
                                        limits.MaxObjects
                                        "Object limit reached; increase MaxObjects."

                                    if obj.Address <> expected then
                                        mark
                                            ExtractionStage.MemoryMap
                                            (Some expected)
                                            "HeapGap"
                                            "Heap traversal skipped an unexplained range; it is not recognized free space."

                                    if not obj.IsValid || isNull obj.Type || obj.Type.MethodTable = 0UL then
                                        mark
                                            ExtractionStage.MemoryMap
                                            (Some obj.Address)
                                            "InvalidObject"
                                            "An object header/type is unavailable."
                                    else
                                        let size = obj.Size

                                        if
                                            size = 0UL
                                            || obj.Address < objectRange.Start
                                            || obj.Address >= objectRange.End
                                            || size > objectRange.End - obj.Address
                                        then
                                            mark
                                                ExtractionStage.MemoryMap
                                                (Some obj.Address)
                                                "InvalidObjectSize"
                                                "An object size lies outside its segment."
                                        else
                                            if not (readable obj.Address size) then
                                                mark
                                                    ExtractionStage.MemoryMap
                                                    (Some obj.Address)
                                                    "MissingHeapPage"
                                                    "Object bytes are absent from the dump. Capture a full heap dump."

                                            let value = {
                                                Identity = objectIdentity obj.Address
                                                Type = intern obj.Type
                                                SegmentAddress = segment.Address
                                                SizeBytes = size
                                                Generation =
                                                    generations
                                                    |> Array.tryFind (fun item ->
                                                        AddressRange.contains obj.Address item.Range)
                                                    |> Option.map _.Generation
                                                IsFree = obj.IsFree
                                                StringDetail = details obj
                                            }

                                            objects.Add value
                                            runtimeObjects.Add value
                                            let step = max minimum (align size)

                                            expected <-
                                                if step > objectRange.End - obj.Address then
                                                    objectRange.End
                                                else
                                                    skipContexts (obj.Address + step)

                                    if objects.Count % 1024 = 0 then
                                        report ExtractionStage.MemoryMap (Some index) objects.Count

                                if expected < objectRange.End then
                                    mark
                                        ExtractionStage.MemoryMap
                                        (Some expected)
                                        "IncompleteSegment"
                                        "Object enumeration ended before the segment's allocated range was covered.")
                        elif canWalk then
                            mark
                                ExtractionStage.MemoryMap
                                (Some segment.Address)
                                "LimitReached"
                                "Object/type budget exhausted; segment metadata remains available.")

                if segments.Count = 0 || runtimeObjects.Count = 0 then
                    mark
                        ExtractionStage.MemoryMap
                        None
                        "NoObjects"
                        "No usable objects were extracted for this runtime. Heap pages or GC metadata may be missing."

                if options.IncludeReferences then
                    stageWork ExtractionStage.References (fun () ->
                        for item in runtimeObjects do
                            token.ThrowIfCancellationRequested()

                            if not item.IsFree then
                                let obj = heap.GetObject(item.Identity.Address)

                                let covered =
                                    ReferenceCoverage.canEnumerate
                                        obj.ContainsPointers
                                        item.SizeBytes
                                        (not obj.Type.GCDesc.IsEmpty)

                                if not covered then
                                    mark
                                        ExtractionStage.References
                                        (Some item.Identity.Address)
                                        "ReferenceLayoutUnavailable"
                                        "ClrMD cannot enumerate this pointer-containing object's references: missing GC descriptor or size above Int32.MaxValue. Object size and memory-map data are preserved."

                                let references =
                                    if covered then
                                        obj.EnumerateReferencesWithFields(false, false)
                                    else
                                        Seq.empty

                                for reference in references do
                                    ensure edges.Count limits.MaxEdges "Reference limit reached; increase MaxEdges."

                                    if reference.Object.Address <> 0UL then
                                        let referenceKind =
                                            if reference.IsDependentHandle then
                                                ReferenceKind.DependentHandle
                                            elif reference.IsArrayElement then
                                                ReferenceKind.ArrayElement
                                            elif reference.IsField then
                                                ReferenceKind.Field
                                            else
                                                ReferenceKind.Runtime

                                        let field =
                                            if reference.IsField && not (isNull reference.Field) then
                                                text reference.Field.Name
                                            else
                                                None

                                        edges.Add {
                                            Source = item.Identity
                                            Target = objectIdentity reference.Object.Address
                                            Kind = referenceKind
                                            Offset =
                                                if reference.IsField || reference.IsArrayElement then
                                                    Some reference.Offset
                                                else
                                                    None
                                            FieldName = field
                                        }

                                    if edges.Count % 1024 = 0 then
                                        report ExtractionStage.References (Some index) edges.Count)

                    if mapState = ExtractionCompleteness.Partial then
                        mark
                            ExtractionStage.References
                            None
                            "PartialMemoryMap"
                            "References cover only the extracted readable object data."

                if options.IncludeReferences || options.IncludeRoots then
                    stageWork ExtractionStage.Handles (fun () ->
                        for handle in runtime.EnumerateHandles() do
                            ensure handles.Count limits.MaxHandles "Handle limit reached; increase MaxHandles."

                            let dependent =
                                if handle.HandleKind = ClrHandleKind.Dependent then
                                    Some handle.Dependent.Address
                                else
                                    None

                            handles.Add {
                                Runtime = identity
                                Address = handle.Address
                                Kind = string handle.HandleKind
                                TargetAddress = handle.Object.Address
                                DependentTargetAddress = dependent
                                IsStrong = handle.IsStrong
                                IsPinned = handle.IsPinned
                                ReferenceCount = handle.ReferenceCount
                            }

                            match dependent with
                            | Some destination when
                                options.IncludeReferences && handle.Object.Address <> 0UL && destination <> 0UL
                                ->
                                if edges.Count < limits.MaxEdges then
                                    edges.Add {
                                        Source = objectIdentity handle.Object.Address
                                        Target = objectIdentity destination
                                        Kind = ReferenceKind.DependentHandle
                                        Offset = None
                                        FieldName = None
                                    }
                                elif refsState <> ExtractionCompleteness.Partial then
                                    mark
                                        ExtractionStage.References
                                        None
                                        "LimitReached"
                                        "Reference budget omitted dependent-handle edges; handle metadata remains available."
                            | _ -> ()

                            if handles.Count % 1024 = 0 then
                                report ExtractionStage.Handles (Some index) handles.Count)

                    if handlesState = ExtractionCompleteness.Partial && options.IncludeReferences then
                        mark
                            ExtractionStage.References
                            None
                            "PartialHandles"
                            "Dependent-handle edges may be incomplete."

                if options.IncludeReferences then
                    let known =
                        HashSet<uint64>(
                            runtimeObjects
                            |> Seq.filter (fun item -> not item.IsFree)
                            |> Seq.map _.Identity.Address
                        )

                    for edgeIndex in firstEdge .. edges.Count - 1 do
                        token.ThrowIfCancellationRequested()
                        let edge = edges[edgeIndex]

                        if
                            not (known.Contains edge.Source.Address)
                            || not (known.Contains edge.Target.Address)
                        then
                            mark
                                ExtractionStage.References
                                (Some edge.Target.Address)
                                "UnresolvedReference"
                                "An edge endpoint is not an extracted non-free object. The raw scoped identity is preserved; do not treat this graph as complete."

                if options.IncludeRoots then
                    let ordered = runtimeObjects.ToArray() |> Array.sortBy _.Identity.Address

                    let resolve address interior =
                        let mutable low = 0
                        let mutable high = ordered.Length - 1
                        let mutable result = None

                        while low <= high do
                            let middle = low + (high - low) / 2
                            let item = ordered[middle]

                            if address < item.Identity.Address then
                                high <- middle - 1
                            elif address - item.Identity.Address < item.SizeBytes then
                                if not item.IsFree && (interior || address = item.Identity.Address) then
                                    result <- Some item.Identity

                                low <- high + 1
                            else
                                low <- middle + 1

                        result

                    stageWork ExtractionStage.Roots (fun () ->
                        for root in heap.EnumerateRoots() do
                            ensure roots.Count limits.MaxRoots "Root limit reached; increase MaxRoots."
                            let mutable pointer = 0UL

                            let raw =
                                if root.IsInterior && memory.ReadPointer(root.Address, &pointer) then
                                    Some pointer
                                elif root.Object.Address <> 0UL then
                                    Some root.Object.Address
                                else
                                    None

                            let resolved =
                                let direct =
                                    if root.Object.Address <> 0UL then
                                        resolve root.Object.Address root.IsInterior
                                    else
                                        None

                                direct
                                |> Option.orElseWith (fun () ->
                                    raw |> Option.bind (fun address -> resolve address root.IsInterior))

                            let thread, stack =
                                match root with
                                | :? ClrStackRoot as stack when not (isNull stack.StackFrame) ->
                                    let frame = stack.StackFrame

                                    (if isNull frame.Thread then
                                         None
                                     else
                                         Some frame.Thread.OSThreadId),
                                    Some frame.StackPointer
                                | _ -> None, None

                            roots.Add {
                                Runtime = identity
                                SlotAddress = root.Address
                                RawTargetAddress = raw
                                Object = resolved
                                Kind = string root.RootKind
                                IsInterior = root.IsInterior
                                IsPinned = root.IsPinned
                                ThreadId = thread
                                StackPointer = stack
                            }

                            if resolved.IsNone then
                                mark
                                    ExtractionStage.Roots
                                    (Some root.Address)
                                    "UnresolvedRoot"
                                    "Root provenance was retained, but its target could not be resolved to an extracted object."

                            if roots.Count % 1024 = 0 then
                                report ExtractionStage.Roots (Some index) roots.Count)

                    if mapState = ExtractionCompleteness.Partial then
                        mark
                            ExtractionStage.Roots
                            None
                            "PartialMemoryMap"
                            "Root resolution is limited by incomplete heap data."
            with
            | AnalysisFailure error ->
                if firstFailure.IsNone then
                    firstFailure <- Some error

                mark currentStage None "RuntimeUnavailable" (string error)

                if options.IncludeReferences then
                    refsState <- ExtractionCompleteness.Partial

                if options.IncludeRoots then
                    rootsState <- ExtractionCompleteness.Partial

                if options.IncludeReferences || options.IncludeRoots then
                    handlesState <- ExtractionCompleteness.Partial
            | error when nativeFailure error ->
                mark
                    currentStage
                    None
                    "RuntimeReadFailed"
                    $"Runtime extraction failed ({error.GetType().Name}); recapture with full heap data."

                if options.IncludeReferences then
                    refsState <- ExtractionCompleteness.Partial

                if options.IncludeRoots then
                    rootsState <- ExtractionCompleteness.Partial

                if options.IncludeReferences || options.IncludeRoots then
                    handlesState <- ExtractionCompleteness.Partial

            if options.IncludeStringDetails && mapState = ExtractionCompleteness.Partial then
                mark
                    ExtractionStage.StringDetails
                    None
                    "PartialMemoryMap"
                    "String details cover only the extracted readable objects."

            runtimes.Add {
                Identity = identity
                Version = string info.Version
                VersionSource = versionSource
                Flavor = string info.Flavor
                ModuleAddress = info.ModuleInfo.ImageBase
                CanWalkHeap = canWalk
                MemoryMap = mapState
                References = refsState
                Roots = rootsState
                Handles = handlesState
            }

        token.ThrowIfCancellationRequested()

        if segments.Count = 0 then
            let error =
                firstFailure
                |> Option.defaultValue (
                    AnalysisError.HeapUnavailable
                        "No heap segments could be extracted. Capture a full heap dump and supply matching trusted DACs."
                )

            raise (AnalysisFailure error)

        {
            Metadata = {
                Id = snapshotId
                PointerSizeBytes = memory.PointerSize
                RuntimeVersion = runtimes |> Seq.map _.Version |> String.concat "; "
                ObjectCount = objects |> Seq.filter (fun item -> not item.IsFree) |> Seq.length |> uint64
            }
            Target = {
                OperatingSystem = string memory.TargetPlatform
                Architecture = string memory.Architecture
                PointerSizeBytes = memory.PointerSize
            }
            Runtimes = runtimes.ToArray()
            Heaps = heaps.ToArray()
            Segments = segments.ToArray()
            Types = types.ToArray()
            Objects = objects.ToArray()
            Edges = edges.ToArray()
            Roots = roots.ToArray()
            Handles = handles.ToArray()
            Diagnostics = diagnostics.ToArray()
            StringDetails =
                if not options.IncludeStringDetails then
                    ExtractionCompleteness.NotRequested
                elif stringPartial then
                    ExtractionCompleteness.Partial
                else
                    ExtractionCompleteness.Complete
            IsPartial = anyPartial
        }
