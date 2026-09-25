module MemoryVisualizer.Core.Tests.IndexedHeapSnapshotTests

open System
open System.Collections.Generic
open System.Runtime.CompilerServices
open System.Threading
open MemoryVisualizer.Core
open Xunit

let private token = CancellationToken.None

let private unwrap result =
    result |> Result.defaultWith (fun error -> failwithf "%A" error)

let private snapshotId =
    SnapshotId.create (Guid.Parse "fc644f7b-cf63-4c20-82fa-7efea63e09e1") |> unwrap

let private runtime index : RuntimeIdentity = {
    SnapshotId = snapshotId
    Index = index
}

let private typ index table : TypeIdentity = {
    Runtime = runtime index
    MethodTable = table
}

let private identity index address : ObjectIdentity = {
    Runtime = runtime index
    Address = address
}

let private high = 9007199254740993UL

let private fixture () =
    let runtimeInfo index = {
        Identity = runtime index
        Version = "synthetic"
        VersionSource = "fixture"
        Flavor = "Core"
        ModuleAddress = 0UL
        CanWalkHeap = true
        MemoryMap = ExtractionCompleteness.Complete
        References = ExtractionCompleteness.Complete
        Roots = ExtractionCompleteness.Complete
        Handles = ExtractionCompleteness.Complete
    }

    let heap index heapIndex = {
        Runtime = runtime index
        Index = heapIndex
        Address = 0UL
        IsServer = true
        HasRegions = false
        HasPinnedObjectHeap = false
    }

    let segment index heapIndex address kind start finish : HeapSegment = {
        Runtime = runtime index
        HeapIndex = heapIndex
        Address = address
        Kind = kind
        IsPinned = false
        ObjectRange = { Start = start; End = finish }
        CommittedRange = { Start = start; End = finish }
        ReservedRange = { Start = finish; End = finish }
        Generations = [|
            {
                Generation = 0
                Range = { Start = start; End = finish }
            }
        |]
        AllocationContexts = [| { Start = finish; End = finish } |]
    }

    let typeInfo index table free = {
        Identity = typ index table
        Name = Some "Same.Display.Name"
        ModuleAddress = Some table
        MetadataToken = 1
        IsArray = false
        IsString = false
        IsFree = free
        ContainsPointers = true
    }

    let item index address table segment size generation free = {
        Identity = identity index address
        Type = typ index table
        SegmentAddress = segment
        SizeBytes = size
        Generation = generation
        IsFree = free
        StringDetail = None
    }

    let edge source target kind = {
        Source = identity 0 source
        Target = identity 0 target
        Kind = kind
        Offset = Some 8
        FieldName = Some "Next"
    }

    {
        Metadata = {
            Id = snapshotId
            PointerSizeBytes = 8
            RuntimeVersion = "synthetic"
            ObjectCount = 8UL
        }
        Target = {
            OperatingSystem = "synthetic"
            Architecture = "x64"
            PointerSizeBytes = 8
        }
        Runtimes = [| runtimeInfo 1; runtimeInfo 0 |]
        Heaps = [| heap 0 0; heap 0 1; heap 1 0; heap 1 1 |]
        Segments = [|
            segment 0 0 0x100UL HeapKind.Small 0x100UL 0x200UL
            segment 0 1 0x300UL HeapKind.Large 0x300UL 0x400UL
            segment 1 0 0x100UL HeapKind.Small 0x100UL 0x200UL
            segment 1 1 high HeapKind.Frozen high UInt64.MaxValue
        |]
        Types = [|
            typeInfo 0 22UL false
            typeInfo 0 11UL false
            typeInfo 0 33UL true
            typeInfo 1 11UL false
        |]
        Objects = [|
            item 1 high 11UL high 32UL None false
            item 0 0x170UL 11UL 0x100UL 8UL (Some 2) false
            item 1 0x110UL 11UL 0x100UL 16UL (Some 0) false
            item 0 0x140UL 33UL 0x100UL 16UL (Some 2) true
            item 0 0x120UL 22UL 0x100UL 8UL (Some 1) false
            item 1 UInt64.MaxValue 11UL high 1UL None false
            item 0 0x110UL 11UL 0x100UL 48UL (Some 0) false
            item 0 0x310UL 11UL 0x300UL 64UL None false
            item 1 (UInt64.MaxValue - 15UL) 11UL high 16UL None false
        |]
        Edges = [|
            edge 0x110UL 0x120UL ReferenceKind.Field
            edge 0x120UL 0x110UL ReferenceKind.Field
            edge 0x110UL 0x120UL ReferenceKind.Field
            edge 0x110UL 0xdeadUL ReferenceKind.DependentHandle
        |]
        Roots = [|
            {
                Runtime = runtime 0
                SlotAddress = 1UL
                RawTargetAddress = Some 0x111UL
                Object = Some(identity 0 0x110UL)
                Kind = "Stack"
                IsInterior = true
                IsPinned = false
                ThreadId = Some 7u
                StackPointer = Some 0x99UL
            }
        |]
        Handles = [|
            {
                Runtime = runtime 0
                Address = 2UL
                Kind = "Weak"
                TargetAddress = 0x110UL
                DependentTargetAddress = None
                IsStrong = false
                IsPinned = false
                ReferenceCount = 0u
            }
        |]
        StringDetails = ExtractionCompleteness.NotRequested
        Diagnostics = [||]
        IsPartial = false
    }

let private create snapshot =
    IndexedHeapSnapshot.Create(snapshot, SnapshotIndexLimits.defaults, token)
    |> unwrap

let private page offset limit = { Offset = offset; Limit = limit }

let private select (store: IndexedHeapSnapshot) selection offset limit =
    store.SelectObjects(selection, page offset limit, token) |> unwrap

let private keys (items: seq<HeapObject>) =
    items
    |> Seq.map (fun item -> item.Identity.Runtime.Index, item.Identity.Address)
    |> Seq.toArray

let private assertKeys expected actual =
    Assert.Equal<(int * uint64) array>(expected, keys actual)

let private isInvalid result =
    match result with
    | Error(SnapshotIndexError.InvalidInput _) -> ()
    | _ -> failwithf "Expected InvalidInput, got %A" result

let private isLimit result =
    match result with
    | Error(SnapshotIndexError.LimitExceeded _) -> ()
    | _ -> failwithf "Expected LimitExceeded, got %A" result

[<Fact>]
let ``Exact identities distinguish runtimes snapshots and same-name types`` () =
    use store = create (fixture ())
    let first = store.TryGetObject(identity 0 0x110UL, token) |> unwrap |> Option.get
    let second = store.TryGetObject(identity 1 0x110UL, token) |> unwrap |> Option.get
    Assert.NotEqual(first.Identity, second.Identity)
    Assert.NotEqual(first.SizeBytes, second.SizeBytes)
    Assert.True(store.TryGetObject(identity 0 0x111UL, token) |> unwrap |> Option.isNone)
    Assert.True(store.TryGetObject(identity 7 0x110UL, token) |> unwrap |> Option.isNone)
    Assert.True(store.TryGetType(typ 0 99UL, token) |> unwrap |> Option.isNone)
    Assert.Equal(Some "Same.Display.Name", (store.TryGetType(typ 0 11UL, token) |> unwrap |> Option.get).Name)

    assertKeys
        [| 0, 0x110UL; 0, 0x170UL; 0, 0x310UL |]
        (select
            store
            {
                ObjectSelection.all with
                    Type = Some(typ 0 11UL)
            }
            0
            20)
            .Items

    assertKeys
        [| 0, 0x120UL |]
        (select
            store
            {
                ObjectSelection.all with
                    Type = Some(typ 0 22UL)
            }
            0
            20)
            .Items

    let foreign = {
        runtime 0 with
            SnapshotId = SnapshotId.create (Guid.NewGuid()) |> unwrap
    }

    store.TryGetObject(
        {
            first.Identity with
                Runtime = foreign
        },
        token
    )
    |> isInvalid

    store.TryGetType({ first.Type with Runtime = foreign }, token) |> isInvalid

    store.SelectObjects(
        {
            ObjectSelection.all with
                Runtime = Some foreign
        },
        page 0 1,
        token
    )
    |> isInvalid

[<Fact>]
let ``Queries distinguish overlap from starts-in with half-open endpoints gaps and nested intervals`` () =
    use store = create (fixture ())

    let query mode start finish =
        select
            store
            {
                ObjectSelection.all with
                    Runtime = Some(runtime 0)
                    Range = Some(mode { Start = start; End = finish })
            }
            0
            20

    assertKeys [| 0, 0x120UL |] (query ObjectRangeSelection.StartsIn 0x120UL 0x140UL).Items
    assertKeys [| 0, 0x110UL; 0, 0x120UL |] (query ObjectRangeSelection.Overlaps 0x120UL 0x140UL).Items
    assertKeys [| 0, 0x110UL |] (query ObjectRangeSelection.Overlaps 0x128UL 0x140UL).Items
    assertKeys [| 0, 0x140UL |] (query ObjectRangeSelection.Overlaps 0x140UL 0x150UL).Items
    Assert.Empty (query ObjectRangeSelection.Overlaps 0x150UL 0x170UL).Items
    Assert.Empty (query ObjectRangeSelection.StartsIn 0x150UL 0x170UL).Items
    Assert.Empty (query ObjectRangeSelection.Overlaps 0x120UL 0x120UL).Items
    Assert.Empty (query ObjectRangeSelection.StartsIn 0x120UL 0x120UL).Items
    Assert.Empty (query ObjectRangeSelection.Overlaps 0UL 1UL).Items
    Assert.Empty (query ObjectRangeSelection.StartsIn UInt64.MaxValue UInt64.MaxValue).Items

[<Fact>]
let ``Unsigned addresses include final-byte intervals without arithmetic wrap`` () =
    use store = create (fixture ())
    Assert.Equal(1UL, (store.TryGetObject(identity 1 UInt64.MaxValue, token) |> unwrap |> Option.get).SizeBytes)

    let query mode start finish =
        select
            store
            {
                ObjectSelection.all with
                    Range = Some(mode { Start = start; End = finish })
            }
            0
            20

    assertKeys [| 1, high |] (query ObjectRangeSelection.StartsIn high (high + 1UL)).Items
    assertKeys [| 1, high |] (query ObjectRangeSelection.Overlaps (high + 31UL) (high + 32UL)).Items
    Assert.Empty (query ObjectRangeSelection.Overlaps (high + 32UL) (high + 33UL)).Items

    assertKeys
        [| 1, UInt64.MaxValue - 15UL |]
        (query ObjectRangeSelection.Overlaps (UInt64.MaxValue - 1UL) UInt64.MaxValue).Items

    Assert.Empty (query ObjectRangeSelection.StartsIn (UInt64.MaxValue - 1UL) UInt64.MaxValue).Items

[<Fact>]
let ``Unsigned sizes retain all bits and overlapping intervals need no byte allocation`` () =
    let source = fixture ()

    source.Objects[0] <-
        {
            source.Objects[0] with
                Identity = identity 1 1UL
                SizeBytes = UInt64.MaxValue
        }

    use store = create source
    let found = store.TryGetObject(identity 1 1UL, token) |> unwrap |> Option.get
    Assert.Equal(UInt64.MaxValue, found.SizeBytes)

    let selection = {
        ObjectSelection.all with
            Type = Some(typ 1 11UL)
            Range =
                Some(
                    ObjectRangeSelection.Overlaps {
                        Start = UInt64.MaxValue - 1UL
                        End = UInt64.MaxValue
                    }
                )
    }

    assertKeys [| 1, 1UL; 1, UInt64.MaxValue - 15UL |] (select store selection 0 20).Items

    source.Objects[0] <-
        {
            source.Objects[0] with
                Identity = identity 1 2UL
        }

    IndexedHeapSnapshot.Create(source, SnapshotIndexLimits.defaults, token)
    |> isInvalid

[<Fact>]
let ``Runtime heap segment generation kind and free filters compose without invented generations`` () =
    use store = create (fixture ())
    let run selection = (select store selection 0 20).Items

    assertKeys
        [| 0, 0x140UL |]
        (run {
            ObjectSelection.all with
                Entries = HeapEntrySelection.Free
        })

    Assert.Equal(
        8,
        (run {
            ObjectSelection.all with
                Entries = HeapEntrySelection.Allocated
        })
            .Count
    )

    assertKeys
        [| 0, 0x110UL; 1, 0x110UL |]
        (run {
            ObjectSelection.all with
                Generation = Some 0
        })

    assertKeys
        [| 0, 0x310UL |]
        (run {
            ObjectSelection.all with
                HeapIndex = Some 1
                Runtime = Some(runtime 0)
        })

    assertKeys
        [| 0, 0x110UL; 0, 0x120UL; 0, 0x140UL; 0, 0x170UL; 1, 0x110UL |]
        (run {
            ObjectSelection.all with
                SegmentAddress = Some 0x100UL
        })

    assertKeys
        [| 0, 0x310UL |]
        (run {
            ObjectSelection.all with
                HeapKind = Some HeapKind.Large
        })

    assertKeys
        [| 0, 0x170UL |]
        (run {
            ObjectSelection.all with
                Type = Some(typ 0 11UL)
                Generation = Some 2
                HeapIndex = Some 0
        })

    Assert.Empty(
        run {
            ObjectSelection.all with
                HeapKind = Some HeapKind.Large
                Generation = Some 2
        }
    )

    Assert.Empty(
        run {
            ObjectSelection.all with
                Runtime = Some(runtime 1)
                Type = Some(typ 0 11UL)
        }
    )

    Assert.Empty(
        run {
            ObjectSelection.all with
                HeapIndex = Some 99
        }
    )

    Assert.Empty(
        run {
            ObjectSelection.all with
                Type = Some(typ 9 11UL)
        }
    )

[<Fact>]
let ``Paging counts matches has exact cap semantics and stable runtime-address order`` () =
    use store = create (fixture ())
    let all = select store ObjectSelection.all 0 9
    Assert.False all.IsTruncated
    Assert.True all.NextOffset.IsNone
    Assert.False all.IsPartial

    assertKeys
        [|
            0, 0x110UL
            0, 0x120UL
            0, 0x140UL
            0, 0x170UL
            0, 0x310UL
            1, 0x110UL
            1, high
            1, UInt64.MaxValue - 15UL
            1, UInt64.MaxValue
        |]
        all.Items

    let first = select store ObjectSelection.all 0 8
    Assert.True first.IsTruncated
    Assert.Equal(Some 8, first.NextOffset)
    let last = select store ObjectSelection.all 8 8
    Assert.Single last.Items |> ignore
    Assert.False last.IsTruncated
    Assert.True last.NextOffset.IsNone

    for offset in [ 9; 10; Int32.MaxValue ] do
        let result = select store ObjectSelection.all offset 9
        Assert.Empty result.Items
        Assert.False result.IsTruncated
        Assert.True result.NextOffset.IsNone

    let selection = {
        ObjectSelection.all with
            Type = Some(typ 0 11UL)
    }

    let firstType = select store selection 0 2
    Assert.Equal(Some 2, firstType.NextOffset)
    assertKeys [| 0, 0x310UL |] (select store selection 2 2).Items
    Assert.False (select store selection 1 2).IsTruncated

[<Fact>]
let ``Every indexed range path agrees with independent linear selection and paging`` () =
    let source = fixture ()
    use store = create source

    let points = [|
        0UL
        0x110UL
        0x111UL
        0x120UL
        0x128UL
        0x140UL
        0x150UL
        0x170UL
        0x310UL
        high
        high + 32UL
        UInt64.MaxValue
    |]

    for start in points do
        for finish in points |> Array.filter (fun finish -> finish >= start) do
            for overlap in [ false; true ] do
                for typeFilter in [ None; Some(typ 0 11UL); Some(typ 1 11UL) ] do
                    let selection = {
                        ObjectSelection.all with
                            Type = typeFilter
                            Entries = HeapEntrySelection.Allocated
                            Range =
                                Some(
                                    (if overlap then
                                         ObjectRangeSelection.Overlaps
                                     else
                                         ObjectRangeSelection.StartsIn)
                                        { Start = start; End = finish }
                                )
                    }

                    let expected =
                        source.Objects
                        |> Array.filter (fun item ->
                            not item.IsFree
                            && (typeFilter |> Option.forall ((=) item.Type))
                            && (if overlap then
                                    start < finish
                                    && bigint item.Identity.Address < bigint finish
                                    && bigint item.Identity.Address + bigint item.SizeBytes > bigint start
                                else
                                    start <= item.Identity.Address && item.Identity.Address < finish))
                        |> Array.sortBy (fun item -> item.Identity.Runtime.Index, item.Identity.Address)

                    for offset in [ 0; 1; 3; 10 ] do
                        let result = select store selection offset 2
                        let remaining = expected |> Array.skip (min offset expected.Length)
                        assertKeys (remaining |> Array.truncate 2 |> keys) result.Items
                        Assert.Equal(remaining.Length > 2, result.IsTruncated)
                        Assert.Equal((if remaining.Length > 2 then Some(offset + 2) else None), result.NextOffset)

[<Fact>]
let ``Empty snapshots build and invalid inputs fail explicitly`` () =
    let source = fixture ()

    let empty = {
        source with
            Metadata = {
                source.Metadata with
                    ObjectCount = 0UL
            }
            Objects = [||]
            Types = [||]
            Runtimes = [||]
            Segments = [||]
            Heaps = [||]
            Edges = [||]
            Roots = [||]
            Handles = [||]
    }

    use store = create empty
    Assert.Empty (select store ObjectSelection.all 0 1).Items
    Assert.True(store.TryGetObject(identity 0 1UL, token) |> unwrap |> Option.isNone)

    for selection in
        [
            {
                ObjectSelection.all with
                    Range = Some(ObjectRangeSelection.StartsIn { Start = 2UL; End = 1UL })
            }
            {
                ObjectSelection.all with
                    Range = Some(ObjectRangeSelection.Overlaps { Start = 2UL; End = 1UL })
            }
            {
                ObjectSelection.all with
                    HeapIndex = Some -1
            }
            {
                ObjectSelection.all with
                    Generation = Some -1
            }
            {
                ObjectSelection.all with
                    Runtime = Some(runtime -1)
            }
        ] do
        store.SelectObjects(selection, page 0 1, token) |> isInvalid

    for request in [ page -1 1; page 0 0; page 0 -1; page 0 4097 ] do
        store.SelectObjects(ObjectSelection.all, request, token) |> isInvalid

    IndexedHeapSnapshot.Create(Unchecked.defaultof<HeapSnapshot>, SnapshotIndexLimits.defaults, token)
    |> isInvalid

    IndexedHeapSnapshot.Create({ source with Objects = null }, SnapshotIndexLimits.defaults, token)
    |> isInvalid

    IndexedHeapSnapshot.Create(
        source,
        {
            SnapshotIndexLimits.defaults with
                MaxObjects = 0
        },
        token
    )
    |> isInvalid

    IndexedHeapSnapshot.Create(
        source,
        {
            SnapshotIndexLimits.defaults with
                MaxPageSize = 4097
        },
        token
    )
    |> isInvalid

[<Fact>]
let ``Duplicate identities missing ownership zero sizes and overflowing final bytes never publish`` () =
    let source = fixture ()

    let changeObject change =
        let objects = Array.copy source.Objects
        objects[0] <- change objects[0]
        { source with Objects = objects }

    for invalid in
        [
            {
                source with
                    Runtimes = Array.append source.Runtimes [| source.Runtimes[0] |]
            }
            {
                source with
                    Heaps = Array.append source.Heaps [| source.Heaps[0] |]
            }
            {
                source with
                    Types = Array.append source.Types [| source.Types[0] |]
            }
            {
                source with
                    Segments = Array.append source.Segments [| source.Segments[0] |]
            }
            {
                source with
                    Objects = Array.append source.Objects [| source.Objects[0] |]
                    Metadata = {
                        source.Metadata with
                            ObjectCount = 9UL
                    }
            }
            { source with Types = [||] }
            { source with Segments = [||] }
            { source with Heaps = [||] }
            { source with Runtimes = [||] }
            {
                source with
                    Metadata = {
                        source.Metadata with
                            Id = Unchecked.defaultof<SnapshotId>
                    }
            }
            {
                source with
                    Metadata = {
                        source.Metadata with
                            ObjectCount = 99UL
                    }
            }
            changeObject (fun item -> { item with SizeBytes = 0UL })
            changeObject (fun item -> {
                item with
                    Identity = identity 1 UInt64.MaxValue
                    SizeBytes = 2UL
            })
            changeObject (fun item -> { item with Type = typ 0 11UL })
            changeObject (fun item -> { item with Generation = Some -1 })
        ] do
        IndexedHeapSnapshot.Create(invalid, SnapshotIndexLimits.defaults, token)
        |> isInvalid

[<Fact>]
let ``Resource caps include free objects and all retained auxiliary containers with exact boundaries`` () =
    let source = fixture ()

    let auxiliary =
        source.Runtimes.Length
        + source.Heaps.Length
        + source.Edges.Length
        + source.Roots.Length
        + source.Handles.Length
        + source.Diagnostics.Length
        + (source.Segments
           |> Array.sumBy (fun segment -> segment.Generations.Length + segment.AllocationContexts.Length))

    let limits = {
        MaxObjects = source.Objects.Length
        MaxTypes = source.Types.Length
        MaxSegments = source.Segments.Length
        MaxAuxiliaryEntries = auxiliary
        MaxPageSize = 2
    }

    use store = IndexedHeapSnapshot.Create(source, limits, token) |> unwrap
    Assert.Equal(2, (select store ObjectSelection.all 0 2).Items.Count)
    store.SelectObjects(ObjectSelection.all, page 0 3, token) |> isInvalid

    for smaller in
        [
            {
                limits with
                    MaxObjects = limits.MaxObjects - 1
            }
            {
                limits with
                    MaxTypes = limits.MaxTypes - 1
            }
            {
                limits with
                    MaxSegments = limits.MaxSegments - 1
            }
            {
                limits with
                    MaxAuxiliaryEntries = limits.MaxAuxiliaryEntries - 1
            }
        ] do
        IndexedHeapSnapshot.Create(source, smaller, token) |> isLimit

    let tooManyRuntimes = {
        source with
            Runtimes =
                Array.init 33 (fun index -> {
                    source.Runtimes[0] with
                        Identity = runtime index
                })
    }

    IndexedHeapSnapshot.Create(tooManyRuntimes, SnapshotIndexLimits.defaults, token)
    |> isLimit

    use runtimeLimitStore =
        create {
            tooManyRuntimes with
                Runtimes = tooManyRuntimes.Runtimes[..31]
        }

    Assert.Equal(32, (runtimeLimitStore.GetInfo token |> unwrap).Runtimes.Count)

[<Fact>]
let ``Partial completeness diagnostics and duplicate cycle and unresolved edge metadata survive unchanged`` () =
    let source = fixture ()

    let diagnostic = {
        Code = "Missing"
        Message = "Synthetic partial"
        Stage = ExtractionStage.References
        Runtime = Some(runtime 0)
        Address = Some 0xdeadUL
    }

    let source = {
        source with
            IsPartial = true
            Runtimes =
                source.Runtimes
                |> Array.map (fun runtime -> {
                    runtime with
                        References = ExtractionCompleteness.Partial
                        MemoryMap = ExtractionCompleteness.Partial
                })
            Diagnostics = [| diagnostic |]
            StringDetails = ExtractionCompleteness.Partial
    }

    use store = create source
    let info = store.GetInfo token |> unwrap
    Assert.True info.IsPartial
    Assert.Equal<RuntimeSnapshot array>(source.Runtimes |> Array.sortBy _.Identity.Index, Seq.toArray info.Runtimes)
    Assert.Equal<ObjectEdge array>(source.Edges, Seq.toArray info.Edges)
    Assert.Equal<HeapRoot array>(source.Roots, Seq.toArray info.Roots)
    Assert.Equal<HeapHandle array>(source.Handles, Seq.toArray info.Handles)
    Assert.Equal<SnapshotDiagnostic array>(source.Diagnostics, Seq.toArray info.Diagnostics)
    Assert.Equal(ExtractionCompleteness.Partial, info.StringDetails)
    Assert.Equal(source.Segments[0].ReservedRange, info.Segments[0].ReservedRange)
    let result = select store ObjectSelection.all 0 9
    Assert.Equal(9, result.Items.Count)
    Assert.True result.IsPartial
    Assert.False result.IsTruncated
    source.Diagnostics[0] <- { diagnostic with Message = "Changed" }
    Assert.Same(diagnostic, info.Diagnostics[0])

[<Fact>]
let ``Inverted segment and nested intervals fail without silently dropping partial data`` () =
    for change in
        [
            (fun (segment: HeapSegment) -> {
                segment with
                    ObjectRange = { Start = 2UL; End = 1UL }
            })
            (fun segment -> {
                segment with
                    CommittedRange = { Start = 2UL; End = 1UL }
            })
            (fun segment -> {
                segment with
                    ReservedRange = { Start = 2UL; End = 1UL }
            })
            (fun segment -> {
                segment with
                    Generations = [|
                        {
                            Generation = 0
                            Range = { Start = 2UL; End = 1UL }
                        }
                    |]
            })
            (fun segment -> {
                segment with
                    AllocationContexts = [| { Start = 2UL; End = 1UL } |]
            })
        ] do
        for partial in [ false; true ] do
            let source = { fixture () with IsPartial = partial }
            source.Segments[0] <- change source.Segments[0]

            IndexedHeapSnapshot.Create(source, SnapshotIndexLimits.defaults, token)
            |> isInvalid

[<Fact>]
let ``Owned containers and read-only outputs isolate all source mutations including nested ranges`` () =
    let source = fixture ()
    use store = create source
    let info = store.GetInfo token |> unwrap
    let objects = select store ObjectSelection.all 0 9
    let originalGeneration = info.Segments[0].Generations[0]
    let originalContext = info.Segments[0].AllocationContexts[0]
    let originalType = info.Types[0]
    let originalEdge = info.Edges[0]

    source.Segments[0].Generations[ 0 ] <-
        {
            originalGeneration with
                Generation = 88
        }

    source.Segments[0].AllocationContexts[ 0 ] <- { Start = 99UL; End = 100UL }
    Array.Fill(source.Segments, source.Segments[1])
    Array.Fill(source.Objects, source.Objects[0])
    Array.Fill(source.Types, source.Types[1])
    Array.Fill(source.Runtimes, source.Runtimes[0])
    Array.Fill(source.Heaps, source.Heaps[0])
    Array.Fill(source.Edges, source.Edges[1])

    Array.Fill(
        source.Roots,
        {
            source.Roots[0] with
                Kind = "Changed"
        }
    )

    Array.Fill(
        source.Handles,
        {
            source.Handles[0] with
                Kind = "Changed"
        }
    )

    Assert.Equal(originalGeneration, info.Segments[0].Generations[0])
    Assert.Equal(originalContext, info.Segments[0].AllocationContexts[0])
    Assert.Same(originalType, info.Types[0])
    Assert.Same(originalEdge, info.Edges[0])
    Assert.Equal("Stack", info.Roots[0].Kind)
    Assert.Equal("Weak", info.Handles[0].Kind)
    Assert.Equal(2, info.Runtimes.Count)
    Assert.Equal(4, info.Heaps.Count)
    assertKeys (keys objects.Items) (select store ObjectSelection.all 0 9).Items
    Assert.False(box objects.Items :? HeapObject array)

    Assert.Throws<NotSupportedException>(fun () -> (objects.Items :?> IList<HeapObject>)[0] <- objects.Items[1])
    |> ignore

    Assert.Throws<NotSupportedException>(fun () ->
        (info.Segments[0].Generations :?> IList<GenerationRange>)[0] <- originalGeneration)
    |> ignore

    Assert.Throws<NotSupportedException>(fun () -> (info.Types :?> IList<HeapType>)[0] <- originalType)
    |> ignore

[<Fact>]
let ``Cancelled builds and every cancelled query throw without invalidating the active store`` () =
    let source = fixture ()
    use store = create source
    use cancelled = new CancellationTokenSource()
    cancelled.Cancel()

    Assert.Throws<OperationCanceledException>(fun () ->
        IndexedHeapSnapshot.Create(source, SnapshotIndexLimits.defaults, cancelled.Token)
        |> ignore)
    |> ignore

    Assert.Throws<OperationCanceledException>(fun () -> store.GetInfo cancelled.Token |> ignore)
    |> ignore

    Assert.Throws<OperationCanceledException>(fun () ->
        store.TryGetObject(identity 0 0x110UL, cancelled.Token) |> ignore)
    |> ignore

    Assert.Throws<OperationCanceledException>(fun () -> store.TryGetType(typ 0 11UL, cancelled.Token) |> ignore)
    |> ignore

    Assert.Throws<OperationCanceledException>(fun () ->
        store.SelectObjects(ObjectSelection.all, page 0 1, cancelled.Token) |> ignore)
    |> ignore

    Assert.Equal(9, (select store ObjectSelection.all 0 9).Items.Count)

[<Fact>]
let ``Disposal is idempotent rejects new access and leaves detached immutable results valid`` () =
    let source = fixture ()
    use store = create source
    let info = store.GetInfo token |> unwrap
    let result = select store ObjectSelection.all 0 9
    store.Dispose()
    store.Dispose()
    Assert.Equal(Error SnapshotIndexError.Disposed, store.GetInfo token)
    Assert.Equal(Error SnapshotIndexError.Disposed, store.TryGetObject(identity 0 0x110UL, token))
    Assert.Equal(Error SnapshotIndexError.Disposed, store.TryGetType(typ 0 11UL, token))
    Assert.Equal(Error SnapshotIndexError.Disposed, store.SelectObjects(ObjectSelection.all, page 0 1, token))
    Assert.Equal(9, result.Items.Count)
    Assert.Equal(4, info.Edges.Count)

[<Fact>]
let ``Owner can build replacement before disposing old store and retain it on build failure`` () =
    let source = fixture ()
    use oldStore = create source

    let replacement =
        IndexedHeapSnapshot.Create({ source with Types = [||] }, SnapshotIndexLimits.defaults, token)

    replacement |> isInvalid
    Assert.Equal(9, (select oldStore ObjectSelection.all 0 9).Items.Count)
    use newStore = create source
    oldStore.Dispose()
    Assert.Equal(Error SnapshotIndexError.Disposed, oldStore.TryGetObject(identity 0 0x110UL, token))
    Assert.Equal(9, (select newStore ObjectSelection.all 0 9).Items.Count)

[<MethodImpl(MethodImplOptions.NoInlining)>]
let private storeWithWeakData () =
    let source = fixture ()
    let store = create source

    store,
    [|
        WeakReference(source.Objects[0])
        WeakReference(source.Edges[0])
        WeakReference(source.Segments[0].Generations[0])
    |]

[<Fact>]
let ``Disposed store releases owned object edge and nested metadata references`` () =
    let store, references = storeWithWeakData ()
    use owned = store
    Assert.All(references, fun reference -> Assert.True reference.IsAlive)
    store.Dispose()
    GC.Collect()
    GC.WaitForPendingFinalizers()
    GC.Collect()
    Assert.All(references, fun reference -> Assert.False reference.IsAlive)
    GC.KeepAlive store
