namespace MemoryVisualizer.Scene

open System
open System.Threading
open MemoryVisualizer.Core
open MemoryVisualizer.Query

type SceneBounds = {
    X: float
    Y: float
    Width: float
    Height: float
}

type ScenePoint = { X: float; Y: float }

[<RequireQualifiedAccess>]
type SceneGeometry =
    | Rectangle of SceneBounds
    | Line of start: ScenePoint * finish: ScenePoint

type ResolvedStyle = {
    Fill: string
    Stroke: string
    StrokeWidth: float
}

type SceneTextLine = {
    Text: string
    X: float
    Baseline: float
    Width: float
}

type SceneText = {
    Bounds: SceneBounds
    Lines: SceneTextLine list
    CellWidth: float
    FontSize: float
    LineHeight: float
    Fill: string
    ReplacedCodeUnits: int
    IsTruncated: bool
}

/// Strings are canonical decimal/hex, never JavaScript numbers or snapshot UUIDs.
type SceneSource = {
    Runtime: int
    Heap: int
    StatementIndex: int
    Kind: string
    Address: string
    Size: string
    SegmentAddress: string option
    MethodTable: string option
}

type SceneElement = {
    Id: string
    LaneId: string
    Layer: int
    Geometry: SceneGeometry
    Bounds: SceneBounds
    Style: ResolvedStyle
    Text: SceneText option
    Source: SceneSource option
    IsClipped: bool
}

type SceneLane = {
    Id: string
    Runtime: int option
    Heap: int option
    Bounds: SceneBounds
}

[<RequireQualifiedAccess>]
type SceneTruncation =
    | Directives
    | Elements
    | Lanes
    | LabelCharacters
    | TotalLabelCharacters
    | ElapsedTime

[<RequireQualifiedAccess>]
type SceneStatus =
    | Complete
    | Truncated of SceneTruncation list
    | Cancelled
    | Failed

type SceneDiagnostic = { Code: string; Message: string }

type SceneCompleteness = {
    QueryStatus: string
    QueryTruncation: string list
    SourceAvailable: bool
    SourcePartial: bool
    SourceDiagnosticCount: int
    SceneStatus: SceneStatus
}

type RedactionPolicy = {
    Addresses: bool
    Strings: bool
    Paths: bool
    Labels: bool
}

[<RequireQualifiedAccess>]
module RedactionPolicy =
    let none = {
        Addresses = false
        Strings = false
        Paths = false
        Labels = false
    }

    let all = {
        Addresses = true
        Strings = true
        Paths = true
        Labels = true
    }

type SceneTheme = {
    Background: string
    Stroke: string
    Text: string
}

[<RequireQualifiedAccess>]
module SceneTheme =
    let defaults = {
        Background = "#ffffff"
        Stroke = "#202020"
        Text = "#202020"
    }

type SceneLimits = {
    MaxDirectives: int
    MaxElements: int
    MaxLanes: int
    MaxLabelCharacters: int
    MaxTotalLabelCharacters: int
    MaxElapsedMilliseconds: int
}

[<RequireQualifiedAccess>]
module SceneLimits =
    let defaults = {
        MaxDirectives = 4096
        MaxElements = 4096
        MaxLanes = 64
        MaxLabelCharacters = 128
        MaxTotalLabelCharacters = 65536
        MaxElapsedMilliseconds = 5000
    }

/// Start + Size may equal 2^64; Size=0 is an explicitly empty viewport.
type SceneViewport = { Start: uint64; Size: uint64 }

type SceneOptions = {
    Limits: SceneLimits
    Theme: SceneTheme
    Redaction: RedactionPolicy
    Viewport: SceneViewport option
    PlotWidth: int
}

[<RequireQualifiedAccess>]
module SceneOptions =
    let defaults = {
        Limits = SceneLimits.defaults
        Theme = SceneTheme.defaults
        Redaction = RedactionPolicy.none
        Viewport = None
        PlotWidth = 1024
    }

type SceneExecutionContext = {
    CancellationToken: CancellationToken
    IsSnapshotCurrent: SnapshotId -> bool
    TimeProvider: TimeProvider
}

[<RequireQualifiedAccess>]
module SceneExecutionContext =
    let create token = {
        CancellationToken = token
        IsSnapshotCurrent = fun _ -> true
        TimeProvider = TimeProvider.System
    }

/// Only the shared builder constructs scenes. Renderers consume positioned values.
type PositionedScene = internal {
    Version: int
    OwningSnapshot: SnapshotId
    SceneBounds: SceneBounds
    SceneLanes: SceneLane list
    SceneElements: SceneElement list
    SceneTheme: SceneTheme
    SceneRedaction: RedactionPolicy
    SceneCompleteness: SceneCompleteness
} with

    member this.SchemaVersion = this.Version
    member this.SnapshotId = this.OwningSnapshot
    member this.Bounds = this.SceneBounds
    member this.Lanes = this.SceneLanes
    member this.Elements = this.SceneElements
    member this.Theme = this.SceneTheme
    member this.Redaction = this.SceneRedaction
    member this.Completeness = this.SceneCompleteness

type SceneBuildResult = {
    Status: SceneStatus
    Scene: PositionedScene option
    Diagnostics: SceneDiagnostic list
}

type SvgLimits = { MaxBytes: int }

[<RequireQualifiedAccess>]
module SvgLimits =
    let defaults = { MaxBytes = 8 * 1024 * 1024 }

[<RequireQualifiedAccess>]
type SvgError =
    | InvalidInput of string
    | OutputLimitExceeded
    | Cancelled
    | WriteFailed of string
