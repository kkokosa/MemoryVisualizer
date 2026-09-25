namespace MemoryVisualizer.Query

open System
open System.Threading
open MemoryVisualizer.Core

/// Offsets, lengths and one-based columns count UTF-16 code units.
type SourceSpan = {
    Offset: int
    Length: int
    Line: int
    Column: int
}

type Spanned<'T> = { Value: 'T; Span: SourceSpan }

type QueryDiagnostic = {
    Code: string
    Message: string
    Span: SourceSpan
}

type QueryLimits = {
    MaxQueryLength: int
    MaxTokens: int
    MaxNesting: int
    MaxStatements: int
    MaxProjectionColumns: int
    MaxResults: int
    MaxDirectives: int
    MaxCandidates: int
    MaxElapsedMilliseconds: int
}

[<RequireQualifiedAccess>]
module QueryLimits =
    let defaults = {
        MaxQueryLength = 16384
        MaxTokens = 4096
        MaxNesting = 16
        MaxStatements = 32
        MaxProjectionColumns = 64
        MaxResults = 4096
        MaxDirectives = 4096
        MaxCandidates = 1000000
        MaxElapsedMilliseconds = 5000
    }

    let internal origin = {
        Offset = 0
        Length = 0
        Line = 1
        Column = 1
    }

    let validate limits =
        if obj.ReferenceEquals(limits, null) then
            Error [
                {
                    Code = "MQL001"
                    Message = "Query limits must not be null."
                    Span = origin
                }
            ]
        else
            let ceiling = defaults

            [
                "MaxQueryLength", limits.MaxQueryLength, ceiling.MaxQueryLength
                "MaxTokens", limits.MaxTokens, ceiling.MaxTokens
                "MaxNesting", limits.MaxNesting, ceiling.MaxNesting
                "MaxStatements", limits.MaxStatements, ceiling.MaxStatements
                "MaxProjectionColumns", limits.MaxProjectionColumns, ceiling.MaxProjectionColumns
                "MaxResults", limits.MaxResults, ceiling.MaxResults
                "MaxDirectives", limits.MaxDirectives, ceiling.MaxDirectives
                "MaxCandidates", limits.MaxCandidates, ceiling.MaxCandidates
                "MaxElapsedMilliseconds", limits.MaxElapsedMilliseconds, ceiling.MaxElapsedMilliseconds
            ]
            |> List.choose (fun (name, value, maximum) ->
                if value > 0 && value <= maximum then
                    None
                else
                    Some {
                        Code = "MQL001"
                        Message = $"{name} must be between 1 and {maximum}."
                        Span = origin
                    })
            |> function
                | [] -> Ok()
                | errors -> Error errors

[<RequireQualifiedAccess>]
type Literal =
    | Unsigned of uint64
    | Text of string
    | Boolean of bool

[<RequireQualifiedAccess>]
type ExpressionSyntax =
    | Literal of Spanned<Literal>
    | Binding of Spanned<string>
    | Property of binding: Spanned<string> * property: Spanned<string>

[<RequireQualifiedAccess>]
type Comparison =
    | Equal
    | NotEqual
    | Less
    | LessOrEqual
    | Greater
    | GreaterOrEqual

[<RequireQualifiedAccess>]
type PredicateSyntax =
    | Compare of ExpressionSyntax * Spanned<Comparison> * Spanned<Literal>
    | Range of ExpressionSyntax * overlaps: bool * Spanned<uint64> * Spanned<uint64>
    | And of PredicateSyntax list

type StyleSyntax = {
    Name: Spanned<string>
    Expression: ExpressionSyntax
}

type MatchSyntax = {
    Binding: Spanned<string>
    Selector: Spanned<string>
    Predicate: PredicateSyntax option
    Projection: ExpressionSyntax list
    Presentation: (Spanned<string> * StyleSyntax list) option
}

type DrawSyntax = {
    Span: SourceSpan
    Start: Spanned<uint64>
    End: Spanned<uint64>
    Runtime: Spanned<uint64>
    Heap: Spanned<uint64>
    Width: Spanned<uint64> option
}

[<RequireQualifiedAccess>]
type StatementSyntax =
    | Match of MatchSyntax
    | Draw of DrawSyntax

type QuerySyntax = internal {
    Statements: StatementSyntax list
} with

    member this.ParsedStatements = this.Statements

[<RequireQualifiedAccess>]
type Selector =
    | Object
    | Segment
    | Generation

[<RequireQualifiedAccess>]
type Property =
    | Address
    | Size
    | Runtime
    | Heap
    | HeapKind
    | Type
    | MethodTable
    | Generation
    | IsFree
    | End

[<RequireQualifiedAccess>]
type BoundExpression =
    | Entity
    | Property of Property
    | Constant of Literal

[<RequireQualifiedAccess>]
type BoundPredicate =
    | Compare of Property * Comparison * Literal
    | Range of overlaps: bool * AddressRange
    | And of BoundPredicate list

[<RequireQualifiedAccess>]
type DrawingKind =
    | Box
    | Pin
    | Memory

[<RequireQualifiedAccess>]
type LabelPosition =
    | InnerCenter
    | OuterLeft

type PresentationPlan = {
    Kind: DrawingKind
    Label: BoundExpression option
    LabelPosition: LabelPosition
    Background: string
    Width: int
}

type SelectionPlan = {
    Selector: Selector
    Predicate: BoundPredicate option
    Projection: (string * BoundExpression) list
}

[<RequireQualifiedAccess>]
type PlannedStatement =
    | Select of SelectionPlan * PresentationPlan option
    | Draw of DrawSyntax * width: int

type PreparedQuery = internal {
    UnboundStatements: PlannedStatement list
} with

    member this.PlannedStatements = this.UnboundStatements

type QueryPlan = internal {
    Snapshot: SnapshotId
    Statements: PlannedStatement list
} with

    member this.SnapshotId = this.Snapshot
    member this.PlannedStatements = this.Statements

type SelectedEntity = {
    Kind: Selector
    Runtime: RuntimeIdentity
    Heap: int
    SegmentAddress: uint64
    Address: uint64
    Size: uint64
    End: uint64 option
    TypeIdentity: TypeIdentity option
    TypeName: string option
    Generation: int option
    IsFree: bool option
    HeapKind: HeapKind
}

[<RequireQualifiedAccess>]
type QueryValue =
    | Unsigned of uint64
    | Text of string
    | Boolean of bool
    | Entity of SelectedEntity
    | Missing

type QueryRow = {
    StatementIndex: int
    Entity: SelectedEntity
    Values: (string * QueryValue) list
}

type DrawingDirective = {
    StatementIndex: int
    Kind: DrawingKind
    Runtime: RuntimeIdentity
    Heap: int
    Address: uint64
    Size: uint64
    Entity: SelectedEntity option
    Label: QueryValue option
    LabelPosition: LabelPosition
    Background: string
    Width: int
}

[<RequireQualifiedAccess>]
type TruncationReason =
    | Results
    | Directives
    | Candidates
    | ElapsedTime

[<RequireQualifiedAccess>]
type QueryStatus =
    | Complete
    | Truncated of TruncationReason list
    | Cancelled
    | Failed of QueryDiagnostic list

type QueryResult = {
    SnapshotId: SnapshotId
    Status: QueryStatus
    SourcePartial: bool
    SourceAvailable: bool
    SourceDiagnostics: Collections.Generic.IReadOnlyList<SnapshotDiagnostic>
    Rows: QueryRow list
    Directives: DrawingDirective list
    Candidates: int
}

type QueryExecutionContext = {
    CancellationToken: CancellationToken
    IsSnapshotCurrent: SnapshotId -> bool
    TimeProvider: TimeProvider
}

[<RequireQualifiedAccess>]
module QueryExecutionContext =
    let create token = {
        CancellationToken = token
        IsSnapshotCurrent = fun _ -> true
        TimeProvider = TimeProvider.System
    }

exception internal QueryFailure of QueryDiagnostic

module internal Diagnostics =
    let fail code span message =
        raise (
            QueryFailure {
                Code = code
                Span = span
                Message = message
            }
        )

    let attempt action =
        try
            Ok(action ())
        with QueryFailure diagnostic ->
            Error [ diagnostic ]
