namespace MemoryVisualizer.Core.Analysis

open System.Threading
open System.Threading.Tasks
open MemoryVisualizer.Core

[<RequireQualifiedAccess>]
type AnalysisError = NotImplemented of message: string

type ISnapshotReader =
    abstract ReadMetadataAsync:
        dumpPath: string * cancellationToken: CancellationToken -> Task<Result<SnapshotMetadata, AnalysisError>>
