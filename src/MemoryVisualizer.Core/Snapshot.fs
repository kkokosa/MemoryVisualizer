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
