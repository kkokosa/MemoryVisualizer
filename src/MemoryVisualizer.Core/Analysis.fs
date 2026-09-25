namespace MemoryVisualizer.Core.Analysis

open System
open System.Threading
open System.Threading.Tasks
open MemoryVisualizer.Core

[<RequireQualifiedAccess>]
type AnalysisError =
    | NotImplemented of message: string
    | InvalidInput of message: string
    | FileNotFound of message: string
    | AccessDenied of message: string
    | UnsupportedTarget of message: string
    | InvalidDump of message: string
    | DacNotFound of message: string
    | DacLoadFailed of message: string
    | HeapUnavailable of message: string
    | PartialMetadata of message: string
    | ReadFailed of message: string

type DacPolicy = {
    /// Caller-authorized native libraries, keyed by zero-based discovered runtime index.
    TrustedPaths: Map<int, string>
    /// Trusted symbol-layout cache. None disables all cache lookup.
    CacheDirectory: string option
    AllowNetwork: bool
}

type ExtractionLimits = {
    MaxObjects: int
    MaxTypes: int
    MaxSegments: int
    MaxEdges: int
    MaxRoots: int
    MaxHandles: int
    MaxDiagnostics: int
    MaxStringObjects: int
    MaxStringCharacters: int
    MaxTotalStringCharacters: int
}

type SnapshotReaderOptions = {
    Dac: DacPolicy
    IncludeReferences: bool
    IncludeRoots: bool
    IncludeStringDetails: bool
    Limits: ExtractionLimits
}

[<RequireQualifiedAccess>]
module SnapshotReaderOptions =
    let defaults = {
        Dac = {
            TrustedPaths = Map.empty
            CacheDirectory = None
            AllowNetwork = false
        }
        IncludeReferences = true
        IncludeRoots = true
        IncludeStringDetails = false
        Limits = {
            MaxObjects = 1_000_000
            MaxTypes = 100_000
            MaxSegments = 100_000
            MaxEdges = 4_000_000
            MaxRoots = 250_000
            MaxHandles = 250_000
            MaxDiagnostics = 1_000
            MaxStringObjects = 128
            MaxStringCharacters = 512
            MaxTotalStringCharacters = 65_536
        }
    }

type SnapshotProgress = {
    Stage: ExtractionStage
    RuntimeIndex: int option
    Completed: uint64
}

type ISnapshotReader =
    abstract ReadMetadataAsync:
        dumpPath: string * cancellationToken: CancellationToken -> Task<Result<SnapshotMetadata, AnalysisError>>

/// Additive interface: existing metadata-only implementations remain source compatible.
type IHeapSnapshotReader =
    inherit ISnapshotReader

    abstract ReadSnapshotAsync:
        dumpPath: string *
        options: SnapshotReaderOptions *
        progress: IProgress<SnapshotProgress> option *
        cancellationToken: CancellationToken ->
            Task<Result<HeapSnapshot, AnalysisError>>
