namespace MemoryVisualizer.Core

type SnapshotMetadata = {
    Id: SnapshotId
    PointerSizeBytes: int
    RuntimeVersion: string
    ObjectCount: uint64
}

type ObjectSummary = {
    Reference: ObjectReference
    TypeId: TypeId
    Address: uint64
    SizeBytes: uint64
}

[<Struct>]
type RuntimeIdentity = { SnapshotId: SnapshotId; Index: int }

[<Struct>]
type ObjectIdentity = {
    Runtime: RuntimeIdentity
    Address: uint64
}

[<Struct>]
type TypeIdentity = {
    Runtime: RuntimeIdentity
    MethodTable: uint64
}

/// Half-open target address interval; addresses and sizes never pass through signed integers.
[<Struct>]
type AddressRange = { Start: uint64; End: uint64 }

[<RequireQualifiedAccess>]
module AddressRange =
    let contains address range =
        range.Start <= address && address < range.End

    let length range =
        if range.End < range.Start then
            invalidArg (nameof range) "Address range must be half-open and non-inverted."

        range.End - range.Start

[<RequireQualifiedAccess>]
type ExtractionStage =
    | Discovery
    | MemoryMap
    | References
    | Roots
    | Handles
    | StringDetails

[<RequireQualifiedAccess>]
type ExtractionCompleteness =
    | NotRequested
    | Complete
    | Partial

type SnapshotDiagnostic = {
    Code: string
    Message: string
    Stage: ExtractionStage
    Runtime: RuntimeIdentity option
    Address: uint64 option
}

type TargetInfo = {
    OperatingSystem: string
    Architecture: string
    PointerSizeBytes: int
}

type RuntimeSnapshot = {
    Identity: RuntimeIdentity
    Version: string
    VersionSource: string
    Flavor: string
    ModuleAddress: uint64
    CanWalkHeap: bool
    MemoryMap: ExtractionCompleteness
    References: ExtractionCompleteness
    Roots: ExtractionCompleteness
    Handles: ExtractionCompleteness
}

[<RequireQualifiedAccess>]
type HeapKind =
    | Small
    | Large
    | Pinned
    | Frozen
    | Unknown of string

type HeapInfo = {
    Runtime: RuntimeIdentity
    Index: int
    Address: uint64
    IsServer: bool
    HasRegions: bool
    HasPinnedObjectHeap: bool
}

type GenerationRange = { Generation: int; Range: AddressRange }

type HeapSegment = {
    Runtime: RuntimeIdentity
    HeapIndex: int
    Address: uint64
    Kind: HeapKind
    IsPinned: bool
    ObjectRange: AddressRange
    CommittedRange: AddressRange
    ReservedRange: AddressRange
    Generations: GenerationRange array
    AllocationContexts: AddressRange array
}

type HeapType = {
    Identity: TypeIdentity
    Name: string option
    ModuleAddress: uint64 option
    MetadataToken: int
    IsArray: bool
    IsString: bool
    IsFree: bool
    ContainsPointers: bool
}

type StringDetail = { Value: string; Truncated: bool }

type HeapObject = {
    Identity: ObjectIdentity
    Type: TypeIdentity
    SegmentAddress: uint64
    SizeBytes: uint64
    Generation: int option
    IsFree: bool
    StringDetail: StringDetail option
}

[<RequireQualifiedAccess>]
type ReferenceKind =
    | Field
    | ArrayElement
    | DependentHandle
    | Runtime

/// The same edge list supports incoming and outgoing indexes without duplicating edges.
type ObjectEdge = {
    Source: ObjectIdentity
    Target: ObjectIdentity
    Kind: ReferenceKind
    /// Byte offset from object data (after the method-table pointer), as reported by ClrMD.
    Offset: int option
    FieldName: string option
}

type HeapRoot = {
    Runtime: RuntimeIdentity
    SlotAddress: uint64
    RawTargetAddress: uint64 option
    Object: ObjectIdentity option
    Kind: string
    IsInterior: bool
    IsPinned: bool
    ThreadId: uint32 option
    StackPointer: uint64 option
}

/// Weak and dependent handles are metadata, not unconditional GC roots.
type HeapHandle = {
    Runtime: RuntimeIdentity
    Address: uint64
    Kind: string
    TargetAddress: uint64
    DependentTargetAddress: uint64 option
    IsStrong: bool
    IsPinned: bool
    ReferenceCount: uint32
}

/// Fully materialized managed values, valid after native readers/runtimes are disposed.
type HeapSnapshot = {
    Metadata: SnapshotMetadata
    Target: TargetInfo
    Runtimes: RuntimeSnapshot array
    Heaps: HeapInfo array
    Segments: HeapSegment array
    Types: HeapType array
    Objects: HeapObject array
    Edges: ObjectEdge array
    Roots: HeapRoot array
    Handles: HeapHandle array
    StringDetails: ExtractionCompleteness
    Diagnostics: SnapshotDiagnostic array
    IsPartial: bool
}
