namespace MemoryVisualizer.Analysis.ClrMd

open System
open System.Collections.Generic
open System.Runtime.InteropServices
open System.Text
open System.Threading
open Microsoft.Diagnostics.Runtime
open MemoryVisualizer.Core.Analysis

module internal RuntimeVersion =
    // ClrMD 4.1 inspects only the first writable ELF LOAD for this marker.
    // .NET 11 can put it in a second LOAD. Recover from the dump, never from the DAC.
    let recover (target: DataTarget) (original: ClrInfo) (token: CancellationToken) =
        if
            original.Version.Major <> 0
            || target.DataReader.TargetPlatform = OSPlatform.Windows
        then
            original, "ClrMetadata"
        else
            let moduleInfo = original.ModuleInfo
            let length = moduleInfo.ImageSize

            if
                length <= 0L
                || length > 64L * 1024L * 1024L
                || moduleInfo.ImageBase > UInt64.MaxValue - uint64 length
            then
                raise (
                    AnalysisFailure(
                        AnalysisError.DacLoadFailed
                            "The runtime version is unknown and its dumped module is outside the bounded 64 MiB version-recovery limit. Capture full runtime module data."
                    )
                )

            let marker = Encoding.ASCII.GetBytes "@(#)Version "
            let versions = HashSet<Version>()
            let buffer = Array.zeroCreate<byte>(4096 + 64)
            let mutable offset = 0L

            while offset < length do
                token.ThrowIfCancellationRequested()
                let count = int (min (int64 buffer.Length) (length - offset))

                let read =
                    target.DataReader.Read(moduleInfo.ImageBase + uint64 offset, buffer.AsSpan(0, count))

                let mutable start = 0

                while start + marker.Length < read do
                    let matched = buffer.AsSpan(start, read - start).IndexOf(marker.AsSpan())

                    if matched < 0 then
                        start <- read
                    else
                        let tokenStart = start + matched + marker.Length
                        let mutable ending = tokenStart

                        while ending < read && ending - tokenStart < 64 && buffer[ending] <> byte ' ' do
                            ending <- ending + 1

                        if ending < read && ending - tokenStart < 64 then
                            let value = Encoding.ASCII.GetString(buffer, tokenStart, ending - tokenStart)

                            match Version.TryParse value with
                            | true, version when version.Major > 0 && version.Revision >= 0 ->
                                versions.Add version |> ignore
                            | _ -> ()

                        start <- tokenStart

                offset <- offset + 4096L

            if versions.Count <> 1 then
                raise (
                    AnalysisFailure(
                        AnalysisError.DacLoadFailed
                            "Runtime version metadata is unavailable or ambiguous in the dumped module. Capture a full dump; DAC mismatch checks cannot be bypassed."
                    )
                )

            let recovered =
                ClrInfo(
                    target,
                    moduleInfo,
                    Seq.exactlyOne versions,
                    Flavor = original.Flavor,
                    IsSingleFile = original.IsSingleFile,
                    DebuggingLibraries = original.DebuggingLibraries,
                    ContractDescriptorAddress = original.ContractDescriptorAddress,
                    IndexTimeStamp = original.IndexTimeStamp,
                    IndexFileSize = original.IndexFileSize,
                    BuildId = original.BuildId
                )

            recovered, "DumpedModuleVersionMarker"
