module MemoryVisualizer.Analysis.Tests.DumpMutationTests

open System
open System.IO
open Microsoft.Diagnostics.Runtime
open MemoryVisualizer.Analysis.ClrMd
open Xunit

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
