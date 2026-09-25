namespace MemoryVisualizer.Analysis.ClrMd

open System
open System.IO
open System.Net.Http
open System.Runtime.InteropServices
open System.Threading
open System.Threading.Tasks
open Microsoft.Diagnostics.Runtime
open MemoryVisualizer.Core
open MemoryVisualizer.Core.Analysis

type ClrMdSnapshotReader(?defaultOptions: SnapshotReaderOptions) =
    let defaults = defaultArg defaultOptions SnapshotReaderOptions.defaults

    static member DependencyVersion = typeof<DataTarget>.Assembly.GetName().Version.ToString()

    member _.ReadSnapshotAsync(dumpPath: string, options, progress, cancellationToken: CancellationToken) =
        cancellationToken.ThrowIfCancellationRequested()

        Task.Run<Result<HeapSnapshot, AnalysisError>>(
            (fun () ->
                try
                    if String.IsNullOrWhiteSpace dumpPath then
                        raise (
                            AnalysisFailure(
                                AnalysisError.InvalidInput
                                    "Specify an explicit dump file path. Live process attachment is not supported."
                            )
                        )

                    Extraction.validate options

                    let path =
                        try
                            Path.GetFullPath dumpPath
                        with
                        | :? ArgumentException
                        | :? NotSupportedException ->
                            raise (
                                AnalysisFailure(
                                    AnalysisError.InvalidInput "The dump path is invalid. Specify a local file path."
                                )
                            )

                    use input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)

                    let cache =
                        CacheOptions(
                            CacheTypes = false,
                            CacheFields = false,
                            CacheMethods = false,
                            CacheStackTraces = false,
                            CacheStackRoots = false,
                            MaxDumpCacheSize = 64L * 1024L * 1024L
                        )

                    let settings =
                        DataTargetOptions(
                            CacheOptions = cache,
                            FileLocator = NoFileLocator(),
                            SymbolPaths = [||],
                            ForceCompleteRuntimeEnumeration = true,
                            VerifyDacOnWindows = true
                        )

                    use target =
                        try
                            DataTarget.LoadDump(path, input, false, settings)
                        with
                        | :? ArgumentException ->
                            raise (
                                AnalysisFailure(
                                    AnalysisError.InvalidDump
                                        "The input is not a recognized memory dump. Capture a full heap dump on a supported host."
                                )
                            )
                        | :? NotSupportedException ->
                            raise (
                                AnalysisFailure(
                                    AnalysisError.UnsupportedTarget
                                        "This dump format or architecture is not supported. Use a supported matching-host full heap dump."
                                )
                            )

                    match
                        Compatibility.check
                            Compatibility.hostOS
                            (string RuntimeInformation.ProcessArchitecture)
                            (string target.DataReader.TargetPlatform)
                            (string target.DataReader.Architecture)
                    with
                    | Error error -> Error error
                    | Ok() -> Ok(Extraction.run target options progress cancellationToken)
                with
                | AnalysisFailure error -> Error error
                | :? FileNotFoundException
                | :? DirectoryNotFoundException ->
                    Error(AnalysisError.FileNotFound "Dump file does not exist. Supply an existing local dump path.")
                | :? UnauthorizedAccessException ->
                    Error(
                        AnalysisError.AccessDenied
                            "Access to the dump, trusted DAC, or cache was denied. Check file permissions."
                    )
                | :? InvalidDataException
                | :? BadImageFormatException
                | :? ClrDiagnosticsException
                | :? EndOfStreamException ->
                    Error(
                        AnalysisError.InvalidDump
                            "Dump data is invalid, truncated, or unsupported. Recapture a full heap dump on the matching host."
                    )
                | :? HttpRequestException ->
                    Error(
                        AnalysisError.DacNotFound
                            "The official symbol service could not resolve the DAC. Use an explicit trusted matching DAC for offline analysis."
                    )
                | :? OperationCanceledException when not cancellationToken.IsCancellationRequested ->
                    Error(
                        AnalysisError.DacNotFound "DAC network lookup timed out. Use an explicit trusted matching DAC."
                    )
                | :? IOException ->
                    Error(
                        AnalysisError.ReadFailed
                            "Could not read the dump/DAC or write the configured cache. Check permissions, storage, and file availability."
                    )),
            cancellationToken
        )

    interface ISnapshotReader with
        member this.ReadMetadataAsync(dumpPath, cancellationToken) =
            cancellationToken.ThrowIfCancellationRequested()

            task {
                let options = {
                    defaults with
                        IncludeReferences = false
                        IncludeRoots = false
                        IncludeStringDetails = false
                }

                let! result = this.ReadSnapshotAsync(dumpPath, options, None, cancellationToken)

                return
                    match result with
                    | Error error -> Error error
                    | Ok snapshot when snapshot.IsPartial ->
                        Error(
                            AnalysisError.PartialMetadata
                                "Memory-map extraction is partial. Use IHeapSnapshotReader.ReadSnapshotAsync to inspect the usable data and diagnostics."
                        )
                    | Ok snapshot -> Ok snapshot.Metadata
            }

    interface IHeapSnapshotReader with
        member this.ReadSnapshotAsync(dumpPath, options, progress, cancellationToken) =
            this.ReadSnapshotAsync(dumpPath, options, progress, cancellationToken)
