module MemoryVisualizer.Core.Tests.ContractTests

open System
open System.Text.Json
open MemoryVisualizer.Core
open MemoryVisualizer.Core.Contracts
open Xunit

let private unwrap =
    function
    | Ok value -> value
    | Error error -> failwith error

let private snapshotId =
    SnapshotId.create (Guid.Parse "5c00da50-ec2f-4c97-9f36-aa7957adce31") |> unwrap

[<Fact>]
let ``Empty snapshot and zero local IDs are rejected explicitly`` () =
    Assert.True(SnapshotId.create Guid.Empty |> Result.isError)
    Assert.True(ObjectId.create 0UL |> Result.isError)
    Assert.True(TypeId.create 0UL |> Result.isError)
    Assert.True(SegmentId.create 0UL |> Result.isError)

[<Fact>]
let ``Local IDs retain all 64 bits and remain independent of addresses`` () =
    Assert.Equal("18446744073709551615", ObjectId.create UInt64.MaxValue |> unwrap |> ObjectId.format)
    Assert.Equal("18446744073709551615", TypeId.create UInt64.MaxValue |> unwrap |> TypeId.format)
    Assert.Equal("18446744073709551615", SegmentId.create UInt64.MaxValue |> unwrap |> SegmentId.format)

[<Fact>]
let ``An object reference includes its snapshot scope`` () =
    let objectId = ObjectId.create 1UL |> unwrap

    let first: ObjectReference = {
        SnapshotId = snapshotId
        ObjectId = objectId
    }

    let second = {
        first with
            SnapshotId = SnapshotId.create (Guid.Parse "82a57f2d-b564-4f63-b4a5-0723c965e325") |> unwrap
    }

    Assert.NotEqual(first, second)

    Assert.Equal(
        first,
        {
            first with
                ObjectId = ObjectId.create 1UL |> unwrap
        }
    )

[<Fact>]
let ``Snapshot DTO contains canonical IDs and string counts`` () =
    let dto =
        SnapshotContract.snapshot {
            Id = snapshotId
            PointerSizeBytes = 8
            RuntimeVersion = "synthetic"
            ObjectCount = UInt64.MaxValue
        }

    use json = JsonDocument.Parse(JsonSerializer.Serialize dto)
    Assert.Equal(1, json.RootElement.GetProperty("SchemaVersion").GetInt32())
    Assert.Equal("5c00da50-ec2f-4c97-9f36-aa7957adce31", json.RootElement.GetProperty("SnapshotId").GetString())
    Assert.Equal(JsonValueKind.String, json.RootElement.GetProperty("ObjectCount").ValueKind)
    Assert.Equal("18446744073709551615", json.RootElement.GetProperty("ObjectCount").GetString())
    Assert.Equal(dto, JsonSerializer.Deserialize<SnapshotDto>(json.RootElement.GetRawText()))

[<Theory>]
[<InlineData(0UL, "0x0000000000000000")>]
[<InlineData(9007199254740993UL, "0x0020000000000001")>]
[<InlineData(UInt64.MaxValue, "0xffffffffffffffff")>]
let ``Object DTO is lossless beyond JavaScript safe integer range`` address expectedAddress =
    let dto =
        SnapshotContract.objectSummary {
            Reference = {
                SnapshotId = snapshotId
                ObjectId = ObjectId.create UInt64.MaxValue |> unwrap
            }
            TypeId = TypeId.create 9007199254740993UL |> unwrap
            Address = address
            SizeBytes = UInt64.MaxValue
        }

    use json = JsonDocument.Parse(JsonSerializer.Serialize dto)
    Assert.Equal(expectedAddress, json.RootElement.GetProperty("Address").GetString())
    Assert.Equal("18446744073709551615", json.RootElement.GetProperty("ObjectId").GetString())
    Assert.Equal("9007199254740993", json.RootElement.GetProperty("TypeId").GetString())
    Assert.Equal("18446744073709551615", json.RootElement.GetProperty("SizeBytes").GetString())
    Assert.Equal(dto, JsonSerializer.Deserialize<ObjectDto>(json.RootElement.GetRawText()))

[<Fact>]
let ``Core has no ClrMD or desktop assembly dependency`` () =
    let references = typeof<SnapshotMetadata>.Assembly.GetReferencedAssemblies()

    for reference in references do
        Assert.DoesNotContain("Diagnostics.Runtime", reference.Name)
        Assert.DoesNotContain("Presentation", reference.Name)
        Assert.DoesNotContain("Electron", reference.Name)
