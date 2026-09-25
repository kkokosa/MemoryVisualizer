namespace MemoryVisualizer.Core.Contracts

open System.Globalization
open MemoryVisualizer.Core

[<CLIMutable>]
type SnapshotDto = {
    SchemaVersion: int
    SnapshotId: string
    PointerSizeBytes: int
    RuntimeVersion: string
    ObjectCount: string
}

[<CLIMutable>]
type ObjectDto = {
    SchemaVersion: int
    SnapshotId: string
    ObjectId: string
    TypeId: string
    Address: string
    SizeBytes: string
}

[<RequireQualifiedAccess>]
module SnapshotContract =
    [<Literal>]
    let SchemaVersion = 1

    let snapshot (metadata: SnapshotMetadata) : SnapshotDto = {
        SchemaVersion = SchemaVersion
        SnapshotId = SnapshotId.format metadata.Id
        PointerSizeBytes = metadata.PointerSizeBytes
        RuntimeVersion = metadata.RuntimeVersion
        ObjectCount = metadata.ObjectCount.ToString(CultureInfo.InvariantCulture)
    }

    let objectSummary (summary: ObjectSummary) : ObjectDto = {
        SchemaVersion = SchemaVersion
        SnapshotId = SnapshotId.format summary.Reference.SnapshotId
        ObjectId = ObjectId.format summary.Reference.ObjectId
        TypeId = TypeId.format summary.TypeId
        Address = "0x" + summary.Address.ToString("x16", CultureInfo.InvariantCulture)
        SizeBytes = summary.SizeBytes.ToString(CultureInfo.InvariantCulture)
    }
