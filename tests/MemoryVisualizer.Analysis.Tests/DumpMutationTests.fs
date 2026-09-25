module MemoryVisualizer.Analysis.Tests.DumpMutationTests

open System
open System.IO
open Microsoft.Diagnostics.Runtime
open MemoryVisualizer.Analysis.ClrMd
open Xunit

[<Theory>]
[<InlineData(9us)>]
[<InlineData(12us)>]
let ``Minidump memory list mutations preserve unrelated mappings`` (architecture: uint16) =
    let path =
        Path.Combine(Path.GetTempPath(), "MemoryVisualizer-mini-header-" + Guid.NewGuid().ToString("N") + ".dmp")

    let first = 0x1_0000_0000UL
    let second = 0x2_0000_0000UL
    let firstValue = 0x1111222233334444UL
    let secondValue = 0x5555666677778888UL
    let replacement = 0x9999aaaabbbbccccUL

    try
        do
            use stream = File.Create path
            use writer = new BinaryWriter(stream)
            // SystemInfoStream, WithHeap's MemoryListStream, and an empty ModuleListStream.
            for word in [| 0x504d444du; 0x0000a793u; 3u; 32u; 0u; 0u; 0x200u; 0u |] do
                writer.Write word

            for kind, size, rva in [ 7u, 56u, 68u; 5u, 36u, 124u; 4u, 4u, 160u ] do
                writer.Write kind
                writer.Write size
                writer.Write rva

            writer.Write architecture
            writer.Write(0us)
            writer.Write(0us)
            writer.Write(1uy)
            writer.Write(1uy)

            for word in [| 10u; 0u; 0u; 2u; 0u; 0u; 0u; 0u; 0u; 0u; 0u; 0u |] do
                writer.Write word

            writer.Write(2u)

            for address, fileOffset in [ first, 164u; second, 180u ] do
                writer.Write address
                writer.Write(16u)
                writer.Write fileOffset

            writer.Write(0u)
            writer.Write firstValue
            writer.Write(0UL)
            writer.Write secondValue
            writer.Write(0UL)

        let verify firstReadable expectedSecond =
            use target =
                DataTarget.LoadDump(
                    path,
                    DataTargetOptions(SkipRuntimeEnumeration = true, SymbolPaths = [||], FileLocator = NoFileLocator())
                )

            let mutable pointer = 0UL
            Assert.Equal(firstReadable, target.DataReader.ReadPointer(first, &pointer))

            if firstReadable then
                Assert.Equal(firstValue, pointer)

            Assert.True(target.DataReader.ReadPointer(second, &pointer))
            Assert.Equal(expectedSecond, pointer)

        verify true secondValue
        DumpMutation.rewritePointer path second secondValue replacement
        verify true replacement
        DumpMutation.hideMemoryRange path first
        verify false replacement
        use bytes = new BinaryReader(File.OpenRead path)
        bytes.BaseStream.Position <- 164L
        Assert.Equal(firstValue, bytes.ReadUInt64())
    finally
        File.Delete path

[<Theory>]
[<InlineData(0x01000007u)>]
[<InlineData(0x0100000cu)>]
let ``MachO mutation removes virtual mapping but preserves other segments`` cpuType =
    let path =
        Path.Combine(Path.GetTempPath(), "MemoryVisualizer-mach-header-" + Guid.NewGuid().ToString("N") + ".dmp")

    let first = 0x1_0000_0000UL
    let second = 0x2_0000_0000UL
    let firstValue = 0x1111222233334444UL
    let secondValue = 0x5555666677778888UL

    try
        do
            use stream = File.Create path
            use writer = new BinaryWriter(stream)
            // A synthetic 64-bit core with two LC_SEGMENT_64 commands and known bytes.
            for word in [| 0xfeedfacfu; cpuType; 0u; 4u; 2u; 144u; 0u; 0u |] do
                writer.Write word

            for address, fileOffset in [ first, 176UL; second, 192UL ] do
                writer.Write(0x19u)
                writer.Write(72u)
                writer.Write(Array.zeroCreate<byte> 16)
                writer.Write address
                writer.Write(16UL)
                writer.Write fileOffset
                writer.Write(16UL)

                for _ in 1..4 do
                    writer.Write(0u)

            writer.Write firstValue
            writer.Write(0UL)
            writer.Write secondValue
            writer.Write(0UL)

        let verify firstReadable =
            use target =
                DataTarget.LoadDump(
                    path,
                    DataTargetOptions(SkipRuntimeEnumeration = true, SymbolPaths = [||], FileLocator = NoFileLocator())
                )

            let mutable pointer = 0UL
            Assert.Equal(firstReadable, target.DataReader.ReadPointer(first, &pointer))

            if firstReadable then
                Assert.Equal(firstValue, pointer)

            Assert.True(target.DataReader.ReadPointer(second, &pointer))
            Assert.Equal(secondValue, pointer)

        verify true
        DumpMutation.hideMemoryRange path first
        verify false
        use bytes = new BinaryReader(File.OpenRead path)
        bytes.BaseStream.Position <- 176L
        Assert.Equal(firstValue, bytes.ReadUInt64())
    finally
        File.Delete path
