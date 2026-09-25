module MemoryVisualizer.Analysis.Tests.DumpMutation

open System
open System.IO

/// Test-only changes to dumps of the child fixture, never historical/user dumps.
let private mutate path address replacement =
    use stream =
        new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)

    use reader = new BinaryReader(stream)
    use writer = new BinaryWriter(stream)
    let at offset = stream.Position <- offset
    let mutable patched = false

    let visit start size fileOffset hide =
        if start <= address && address - start < size then
            match replacement with
            | None -> hide ()
            | Some(expected, replacement) ->
                at (int64 fileOffset + int64 (address - start))
                let actual = reader.ReadUInt64()

                if actual <> expected then
                    failwith "Synthetic reference slot did not contain the expected target."

                at (int64 fileOffset + int64 (address - start))
                writer.Write(replacement: uint64)

            patched <- true

    let magic = reader.ReadUInt32()

    if magic = 0x504d444du then
        at 8L
        let count = reader.ReadUInt32()
        let directory = reader.ReadUInt32()

        for index in 0u .. count - 1u do
            at (int64 directory + int64 index * 12L)
            let kind = reader.ReadUInt32()
            reader.ReadUInt32() |> ignore
            let rva = reader.ReadUInt32()

            if kind = 5u then
                at (int64 rva)
                let ranges = reader.ReadUInt32()

                for item in 0u .. ranges - 1u do
                    let entry = int64 rva + 4L + int64 item * 16L
                    at entry
                    let start = reader.ReadUInt64()
                    let size = reader.ReadUInt32()
                    let fileOffset = reader.ReadUInt32()

                    visit start (uint64 size) (uint64 fileOffset) (fun () ->
                        at entry
                        writer.Write(0UL))
            elif kind = 9u then
                at (int64 rva)
                let ranges = reader.ReadUInt64()
                let mutable fileOffset = reader.ReadUInt64()

                for item in 0UL .. ranges - 1UL do
                    let entry = int64 rva + 16L + int64 item * 16L
                    at entry
                    let start = reader.ReadUInt64()
                    let size = reader.ReadUInt64()

                    visit start size fileOffset (fun () ->
                        at entry
                        writer.Write(0UL))

                    fileOffset <- fileOffset + size
    elif magic = 0x464c457fu then
        at 4L

        if reader.ReadByte() <> 2uy then
            failwith "Fixture must be ELF64."

        at 32L
        let headers = reader.ReadUInt64()
        at 54L
        let entrySize = reader.ReadUInt16()
        let count = reader.ReadUInt16()

        for index in 0 .. int count - 1 do
            let entry = int64 headers + int64 index * int64 entrySize
            at entry
            let kind = reader.ReadUInt32()
            at (entry + 8L)
            let fileOffset = reader.ReadUInt64()
            at (entry + 16L)
            let start = reader.ReadUInt64()
            at (entry + 32L)
            let size = reader.ReadUInt64()

            if kind = 1u then
                visit start size fileOffset (fun () ->
                    at (entry + 32L)
                    writer.Write(0UL))
    elif magic = 0xfeedfacfu then
        at 16L
        let count = reader.ReadUInt32()
        let mutable command = 32L

        for _ in 1u .. count do
            at command
            let kind = reader.ReadUInt32()
            let size = reader.ReadUInt32()

            if kind = 0x19u then
                at (command + 24L)
                let start = reader.ReadUInt64()
                let length = reader.ReadUInt64()
                let fileOffset = reader.ReadUInt64()

                visit start length fileOffset (fun () ->
                    // The stream-based ClrMD Mach-O reader maps VMSize, not FileSize.
                    at (command + 32L)
                    writer.Write(0UL)
                    at (command + 48L)
                    writer.Write(0UL))

            command <- command + int64 size
    else
        failwith "Unsupported generated fixture format."

    if not patched then
        failwith "Fixture target did not map to dump memory."

let hideMemoryRange path address = mutate path address None

let rewritePointer path address expected replacement =
    mutate path address (Some(expected, replacement))
