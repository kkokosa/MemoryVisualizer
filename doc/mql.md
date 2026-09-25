# MQL: bounded M1 query foundation

MQL is data, not Cypher or executable code. This M1 slice of #11 supplies a
headless F# parser, typed planner and executor over `IndexedHeapSnapshot`.
It produces rows and address-based drawing **instructions**, not scenes or SVG.
The CLI uses the shared library. The v1 worker/Electron fake backend is unchanged;
real IPC binding belongs to #13. Full #11 is not complete: graph/root-path M2
and worker/CLI real-protocol equivalence remain deferred.

## Grammar and capability matrix

The following is the implementation contract, written before the parser.
Keywords and property/style names are case-insensitive; binding names and string
values are case-sensitive. Whitespace is insignificant. Statements require `;`
between them; a final `;` is optional. No comments, implicit joins or expressions
outside this vocabulary are accepted.

```text
query       = statement (";" statement)* [";"]
statement   = match | draw
match       = "MATCH" "(" name ":" selector ")"
              ["WHERE" predicate] "RETURN" projection
              [ "AS" ("BOX" | "PIN") ["(" style ("," style)* ")"] ]
selector    = "Object" | "Segment" | "Generation"
predicate   = term ("AND" term)*
term        = property comparison literal
            | property ("STARTS IN" | "OVERLAPS") "[" uint "," uint ")"
            | "(" predicate ")"
comparison  = "=" | "!=" | "<" | "<=" | ">" | ">="
projection  = (name | property) ("," (name | property))*
property    = name "." propertyName
literal     = uint | string | "true" | "false"
style       = "Label" "=" (property | literal)
            | "LabelPosition" "=" ("InnerCenter" | "OuterLeft")
            | "Background" "=" (colorName | string)
            | "Width" "=" uint
draw        = "DRAW Memory" "(" uint "," uint ","
              "Runtime" "=" uint "," "Heap" "=" uint
              ["," "Width" "=" uint] ")"
uint        = decimalDigits | "0x" hexDigits
```

Strings use double quotes, with only `\"`, `\\`, `\n`, `\r`, `\t` escapes.
Unsigned integers are at most `18446744073709551615`; negative numbers, overflow,
fractions, numeric suffixes and malformed hex are errors. Identifiers are ASCII
letters/underscore followed by letters/digits/underscore.

| Capability                                                | M1 contract                                                         |
| --------------------------------------------------------- | ------------------------------------------------------------------- |
| Selection                                                 | One Object, Segment or Generation binding per MATCH                 |
| Common properties                                         | Address, Size, Runtime, Heap, HeapKind                              |
| Object properties                                         | Type (display name), MethodTable, Generation, IsFree                |
| Segment properties                                        | End (object range end)                                              |
| Generation properties                                     | End, Generation                                                     |
| Predicates                                                | AND, grouping, six comparisons; Address STARTS IN / OVERLAPS        |
| Projection                                                | Binding or property, in written order; duplicates allowed           |
| Presentation                                              | One BOX/PIN per matching row, separate from projection              |
| Label                                                     | One literal or typed property; missing captured values stay null    |
| Colors                                                    | Black, White, Grey, Red, Green, Blue, Yellow, or `"#RRGGBB"`        |
| Width                                                     | Integer 1 through 4096; default 1; abstract presentation hint       |
| DRAW Memory                                               | Explicit runtime and heap lane, half-open range                     |
| Composition                                               | Statement order, one shared target-address coordinate system        |
| References, paths, roots, joins, functions, OR, mutations | Unsupported with diagnostic; M2 is not an unrestricted path matcher |

Address, Size, End and MethodTable are uint64. Runtime, Heap and Generation
are nonnegative integer values (query constants must fit int32). Type/HeapKind
are strings; IsFree is boolean. HeapKind strings are `Small`, `Large`, `Pinned`,
`Frozen`, or the captured unknown kind name. Missing Type/Generation is null;
comparisons, including `!=`, with missing values are false. String/boolean
properties support only `=` and `!=`. Type matches **all** matching display
names but never collapses runtime-scoped method-table identities. Free entries
are included unless explicitly filtered with IsFree.

Segment Address/Size/End use ObjectRange; its identity remains the captured
segment address. Generation uses the captured generation interval and parent
segment identity. Object End is intentionally not a property: Address + Size
can equal `2^64`. Drawing instructions preserve address plus uint64 size,
without overflow, clamping or floating-point conversion.

All ranges are half-open `[start,end)`. Equal endpoints are valid empty ranges;
inverted ranges are errors. OVERLAPS means a shared byte, including objects
starting before the query interval. STARTS IN tests the start only. End must
fit uint64, so a range cannot cover the byte at UInt64.MaxValue; equality or
unranged selection can still return it. DRAW requires an existing runtime/heap
lane even when its range is empty; an empty DRAW creates no instruction.
Matching geometries never infer membership in other heaps or runtimes.

## Examples

These examples are exercised on the deterministic shared test fixture:

```text
MATCH (seg: Segment) RETURN seg;
MATCH (gen: Generation) RETURN gen.Generation AS BOX (Label = gen.Generation, LabelPosition = InnerCenter, Background = Grey);
MATCH (obj: Object) WHERE obj.Generation = 2 AND obj.Type = "Same.Display.Name" RETURN obj.Address, obj.Size AS PIN;
DRAW Memory(0x100, 0x200, Runtime = 0, Heap = 0, Width = 16)
```

```text
MATCH (obj: Object) WHERE obj.IsFree = true RETURN obj AS BOX (Background = Yellow);
DRAW Memory(0x100, 0x400, Runtime = 0, Heap = 0)
```

```text
MATCH (obj: Object) WHERE obj.Address OVERLAPS [0x125, 0x130) RETURN obj.Address, obj.MethodTable
```

Unlike the old README sketches, Generation needs a colon, aliases do not leak
between statements, `White = Grey` is not a style, and `hash(...)` is not an
expression. Use `Background = Grey` or a fixed color. Relationships, DOT,
`relationships(p)`, arbitrary hash/templates, and `Width = 1M` are not M1.

## Bounds, status and identity

All configured limits must be positive and no greater than these hard ceilings:

| Limit                  | Default / ceiling | Accounting                                             |
| ---------------------- | ----------------: | ------------------------------------------------------ |
| MaxQueryLength         |             16384 | UTF-16 code units                                      |
| MaxTokens              |              4096 | Non-whitespace tokens, excludes EOF                    |
| MaxNesting             |                16 | Open grammar delimiters, including predicate groups    |
| MaxStatements          |                32 | MATCH and DRAW combined                                |
| MaxProjectionColumns   |                64 | RETURN columns per MATCH, including repeats            |
| MaxResults             |              4096 | Rows, shared across statements                         |
| MaxDirectives          |              4096 | BOX/PIN rows and nonempty DRAW combined                |
| MaxCandidates          |           1000000 | Actual candidate inspections, shared across statements |
| MaxElapsedMilliseconds |              5000 | Cooperative elapsed execution time                     |

There is one composition and no scenes, traversal, visited-node or path output
in M1, so those bounds are not fabricated as inactive configuration knobs.
Input limits fail parsing. Configuration validation is a diagnostic, never a
silent fallback. Row and directive bounds use one matching-row lookahead:
exactly reaching the cap with no further match is complete, not truncated.
If both would overflow on the next match, both reasons are reported. A work/time
cutoff during lookahead reports that cutoff, not unproven result truncation.
Statements share all execution budgets; no budget reset between statements.

Each inspected Object or Segment costs one candidate, whether it matches or not.
Generation selection charges one per parent segment plus one per generation
range, including empty ranges. A DRAW lane lookup charges one per inspected heap.
Projection/predicate/style work is additionally bounded by source/token limits.
The column cap prevents a rows-times-tokens explosion in output cells (at most
262144 projected values with default caps). These are cardinality/work bounds,
not byte or memory guarantees: captured type/unknown-kind/diagnostic strings
can vary in length, and JSON repeats requested values.
No hidden SelectObjects paging, full result materialization or execution-time
sort precedes the budget checks. Objects visit runtime/address order; segments
visit runtime/segment-address order; generations visit parent order then
generation/start/end order. Query order determines composition order.
Heap lane lookup visits runtime/index order, independent of source array order.

Time and cancellation are checked before each inspection, while evaluating
predicates/projections, and before publication. The clock is injectable through
TimeProvider for exact deadline tests. Time is an exclusive deadline: elapsed
time equal to the configured limit stops execution. Store lock waiting and a
single managed allocation are not interruptible; checks resume immediately
after acquisition. Parsing is bounded but synchronous, not token-cancellable.

Execution status is Complete, Truncated (all applicable reasons at the stop),
Cancelled, or Failed (diagnostics). SourcePartial is orthogonal and preserves
the source's extraction completeness/diagnostics. Earlier bounded rows can
survive cancellation/truncation, but failed/stale execution publishes no rows.
SourceAvailable is false if execution stopped before obtaining source metadata;
SourcePartial=false then does not claim a complete source. SourceDiagnostics
is the store's owned read-only metadata, not an eagerly copied unbounded list.
Plans bind a SnapshotId. A different store or an active-snapshot predicate that
returns false rejects stale execution. Hosts must still atomically validate
identity when publishing into their own active workspace.

Diagnostics have code/message and source spans. Offset/Length and one-based
Line/Column use **UTF-16 code units**, including surrogate pairs. CRLF counts
as one newline; bare CR and LF each count as newlines. EOF is a zero-length
span at source.Length. Parse/type/semantic errors highlight the actual token,
not the start of the statement. Unsupported Cypher is rejected, never executed.

## Parser choice and integration

FParsec is a capable F# parser-combinator library, but this grammar needs only
bounded lexing and single-token-lookahead recursive descent. A handwritten
parser avoids adding a package while allowing explicit token/nesting accounting,
UTF-16 spans, deterministic diagnostics and early unsupported-feature rejection.
There is no grammar-compatibility promise with the historical non-executable
README sketches. Expand the safe vocabulary deliberately rather than accepting
and ignoring unknown syntax.

The public Query library is suitable for both hosts. The native CLI extracts
an offline memory map, indexes it, compiles MQL and executes this same API.
Its JSON uses strings for addresses/sizes, includes snapshot identity and
statuses, and is not the closed worker v1 protocol or a future scene schema.
CLI extraction has its own existing limits; MQL execution limits begin after
index creation. This milestone does not claim dump-to-SVG functionality.

### Shared API

`MemoryVisualizer.Query` references only Core. `Mql.parse limits source`
returns `Result<QuerySyntax, QueryDiagnostic list>`; `Mql.plan snapshotId syntax`
returns `Result<QueryPlan, QueryDiagnostic list>`. `Mql.compile limits snapshotId
source` combines these. Syntax/plan expose immutable `ParsedStatements` /
`PlannedStatements` lists, with internal root construction.

For import hosts, `Mql.prepare limits source` parses and type-checks into a
`PreparedQuery` **before** expensive native work. `Mql.bind snapshotId prepared`
then creates the same typed QueryPlan. Parse/planning limits apply at compilation,
not again when executing an already compiled plan with different execution caps.
Invalid source/limits/identity return diagnostics; null internal-root objects
(QuerySyntax, PreparedQuery, QueryPlan) are programmer errors and throw
ArgumentNullException rather than inventing a snapshot identity.

`Mql.execute limits plan store (QueryExecutionContext.create cancellationToken)`
returns a `QueryResult`, never a lazy query. Hosts can set context
`IsSnapshotCurrent` and `TimeProvider`; callbacks are trusted host code, not
query expressions. Host callback exceptions propagate as programmer errors.
Results preserve entity identity independently of projected columns. Indices
in results are int32, geometry is uint64, and missing properties are QueryValue.Missing.
Only the executor interprets the typed presentation plan; the store never sees MQL.

### Native CLI JSON

The command is `query <dump-path> --query <text>` with the same `--dac`, `--cache`
and explicit opt-in `--allow-network` extraction settings as inspect. M1 query
imports request memory maps only. Execution limits may be lowered with
`--max-results`, `--max-directives`, `--max-candidates`, `--max-elapsed-ms`.
The parser caps remain fixed CLI defaults. There is no query-file loader or
implicit network/DAC trust relaxation.

Successful preparation followed by execution writes one JSON object:
`schemaVersion:1`, `snapshotId`, `status` (`complete`, `truncated`, `cancelled`,
`failed`), `sourceAvailable`, `sourcePartial`, decimal-string `candidates`,
`truncationReasons`, `diagnostics`, `sourceDiagnosticCount`,
`sourceDiagnosticsTruncated`, `sourceDiagnostics`, `rows`, and `directives`.
Source diagnostics are explicitly sampled to 64 entries with the total count;
the shared library retains access to all original diagnostics.

Rows include zero-based `statementIndex`, a snapshot-scoped `entity`, and ordered
`values:[{name,value:{kind,value}}]`. Value kinds are `uint64` (decimal string),
`text`, `boolean`, `entity`, and `missing` (null). Entities contain kind, snapshotId,
runtime, heap, segmentAddress, address, size, heapKind, and applicable optional
end/methodTable/type/generation/isFree fields. Entity/directive addresses and
method tables are `0x` plus sixteen lowercase hex digits; sizes are decimal
strings. Directives include statementIndex, kind, runtime, heap, address, size,
width, background, labelPosition and optional label/entity. Their snapshot ID is
the envelope's ID. No heap StringDetail payload or dump path is included.

Preparation errors write the smaller object
`{schemaVersion:1,status:"failed",snapshotId:null,diagnostics:[...]}`: no rows or
directives exist yet. Invalid CLI arguments or native import/index failures write
stderr and no JSON. Cancellation during import writes stderr and exits 130;
cancellation during query execution writes the `cancelled` result.

Exit codes: 0 complete/nonpartial, 3 complete/source-partial or query-truncated,
2 invalid/failure, 130 cancelled. A complete query over a partial source remains
`status:"complete",sourcePartial:true`, rather than conflating missing source
data with an execution cutoff. SVG and worker protocol v1 are not this schema.
