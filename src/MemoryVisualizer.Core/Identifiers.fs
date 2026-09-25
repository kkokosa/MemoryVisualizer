namespace MemoryVisualizer.Core

open System
open System.Globalization

[<Struct>]
type SnapshotId = private SnapshotId of Guid

[<RequireQualifiedAccess>]
module SnapshotId =
    let create value =
        if value = Guid.Empty then
            Error "A snapshot ID must not be empty."
        else
            Ok(SnapshotId value)

    let format (SnapshotId value) = value.ToString("D")

[<Struct>]
type ObjectId = private ObjectId of uint64

[<RequireQualifiedAccess>]
module ObjectId =
    let create value =
        if value = 0UL then
            Error "An object ID must be nonzero."
        else
            Ok(ObjectId value)

    let format (ObjectId value) =
        value.ToString(CultureInfo.InvariantCulture)

[<Struct>]
type TypeId = private TypeId of uint64

[<RequireQualifiedAccess>]
module TypeId =
    let create value =
        if value = 0UL then
            Error "A type ID must be nonzero."
        else
            Ok(TypeId value)

    let format (TypeId value) =
        value.ToString(CultureInfo.InvariantCulture)

[<Struct>]
type SegmentId = private SegmentId of uint64

[<RequireQualifiedAccess>]
module SegmentId =
    let create value =
        if value = 0UL then
            Error "A segment ID must be nonzero."
        else
            Ok(SegmentId value)

    let format (SegmentId value) =
        value.ToString(CultureInfo.InvariantCulture)

[<Struct>]
type ObjectReference = {
    SnapshotId: SnapshotId
    ObjectId: ObjectId
}
