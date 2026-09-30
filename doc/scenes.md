# Positioned scenes and standalone SVG (M1)

The visual-first slice of #12 turns a supported dump plus bounded MQL into
editable, standalone vector SVG. `MemoryVisualizer.Scene` references Query/Core,
not ClrMD, a browser, or Electron. The [M1 desktop workspace](desktop-workspace.md)
consumes these exact positions through native protocol v3 for interactive
rendering, selection and shared-engine export. Platform acceptance is verified
through #13. Worker protocol v1 and its explicit fake backend are unchanged.
This scene version is not an unversioned addition to that closed IPC protocol.

## Architecture decision: one positioned scene

The native pipeline is
`dump -> HeapSnapshot -> IndexedHeapSnapshot -> MQL QueryResult -> PositionedScene -> SVG`.
Parsing and typed preparation happen before native import. There is one semantic
composition, layout, label-breaking and style-resolution implementation, in F#.
The SVG writer only serializes its positioned primitives and text. Future worker
integration must publish this scene (through a deliberately versioned transport),
and the Electron renderer must consume its positions rather than infer another
layout from addresses. Pan/zoom may transform these coordinates, not reflow them.

`Scene.build options queryResult context` returns a `SceneBuildResult` with
`Status`, optional `Scene`, and bounded `SCN001` invalid-input, `SCN002` stale, or
`SCN003` unusable-query diagnostics. `Scene.validate options` checks configuration
before import. `SceneExecutionContext.create token` supplies defaults;
`IsSnapshotCurrent` and `TimeProvider` are injectable trusted host callbacks.

`PositionedScene` has internal construction and public immutable properties:

| Property        | Contract                                                                                                                                                               |
| --------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `SchemaVersion` | `2`; independent of snapshot, query JSON and worker protocol versions                                                                                                  |
| `SnapshotId`    | In-memory ownership for selection and atomic active-snapshot publication; never serialized in SVG                                                                      |
| `Bounds`        | Finite scene-unit `X,Y,Width,Height`, including text overflow and stroke margin                                                                                        |
| `Lanes`         | Ordinal ID, optional runtime/heap, positioned bounds; deterministic numeric runtime/heap order                                                                         |
| `Elements`      | Stable ordinal ID, lane ID, numeric layer, rectangle or line geometry, geometry bounds, resolved style, optional positioned text and source association, clipping flag |
| `Layout`        | Requested `SceneLayout.Linear` or `SceneLayout.Compact`; address redaction always uses schematic geometry instead                                                      |
| `Gaps`          | Optional `SceneGapMarkers`: shared positioned band/lines/style, bounded x translation offsets, and accessible description in the unchanged Legend field                |
| `Theme`         | Resolved six-digit lowercase hex colors                                                                                                                                |
| `Redaction`     | Explicit addresses/strings/paths/labels policy                                                                                                                         |
| `Completeness`  | Query status/reasons, source available/partial/diagnostic count, scene status/reasons                                                                                  |

Elements use `element-<admission index>` IDs; lanes use `lane-<sorted index>`.
No address, method table, text, path, hash of sensitive data or snapshot GUID is
embedded in these IDs. IDs are stable for equal inputs/settings and new imports
of the **same dump**, not globally unique or stable under a changed selection.
The scene's SnapshotId plus source association identifies an item in the active
snapshot. Hosts must still check SnapshotId atomically when publishing; the
cooperative predicate cannot make a UI state transition atomic.

`SceneSource` carries runtime, heap, statement index, controlled kind, address,
size, and applicable segment address/method table. Addresses are `0x` plus sixteen
lowercase hex digits; sizes are decimal strings. It never copies type names,
captured string contents, dump paths, diagnostic messages or the original query.
These DTOs are renderer-neutral F# values, **not** a promise that default F# union
JSON serialization is the future IPC wire format.

## Exact layout and interval policy

Coordinates use horizontal byte addresses. All intervals are half-open
`[Address, Address + Size)`. Endpoint arithmetic, clipping and subtraction use
`bigint` **before** conversion to floating point; the exclusive end may equal
`2^64`, but may not exceed it. Zero-size directives and empty/intersection-free
intervals produce no element, while still consuming an inspection. MQL DRAW
endpoints remain uint64, so only object intervals or the scene viewport can end
at `2^64`.

`SceneOptions.Layout` defaults to `SceneLayout.Linear` for headless/backward
compatibility. Desktop overview selects `SceneLayout.Compact`, with an explicit
linear option for true address-distance inspection. Both are shared-engine
layouts: neither the browser nor the SVG writer infers positions from addresses.

There is one **global**, not per-lane, origin and scale. Without a viewport,
origin is the minimum start and end is the maximum end of the admitted visible
directives. With `Viewport = Some { Start; Size }`, those explicit endpoints
define the scale even if no directive intersects. Clip every directive to this
viewport before layout. Geometry uses the clipped interval; source association
retains the original interval and `IsClipped` reports the difference. A PIN
whose interval begins before the viewport is placed on the clipped left edge.
Viewport clipping is intentional selection, not execution truncation.

In **linear** mode, for nonempty span `S`, clipped interval `[A,B)` and origin `O`:

```text
plotX = 528
plotWidth = options.PlotWidth             (default 1024, range 64..4096)
x = 528 + float(A - O) / float(S) * plotWidth
rectangle width = float(B - A) / float(S) * plotWidth
```

Linear gaps are not compacted; intervals never wrap. Equal addresses in different
statements/runtimes/heaps have equal x coordinates, but runtime and heap lanes
remain distinct. Layout does not infer cross-heap membership. A very large span
can make a small object subpixel and nearby starts indistinguishable after the
relative conversion. There is **no artificial minimum byte width or claim of
pixel-resolution uint64 addresses**: use the explicit viewport to zoom. PIN is
an editable vertical marker at x, with zero-width geometry bounds; its original
object size remains in the source association.

### Compact overview and explicit address breaks

Compact mode first clips, bounds and admits directives using the same budgets
as linear mode, then unions **all admitted visible intervals across all runtime
and heap lanes**. Overlapping, duplicate and touching intervals merge using
`bigint` endpoints. No heap kind, including frozen segments, is excluded.
Unselected heap ranges are not scanned. Only empty gaps in that global union
are compressed; occupied ranges retain one globally consistent byte scale.
Here "empty" means **no admitted selected range**, not necessarily free or
unmapped memory. An object-only selection omits other objects, segment space
and other unselected ranges. Segment boxes are actual selected GC segment
intervals; anonymous lane grouping is not a segment or an entire heap extent.
Consequently overlapping addresses align across lanes and relative occupied
byte lengths remain proportional. A directive itself cannot span a compressed
gap, because its entire clipped interval contributes to the occupied union.

With `G` nonempty gaps and occupied union size `B` bytes:

```text
gapWidth = min(12, plotWidth * 0.2 / G)
dataWidth = plotWidth - G * gapWidth
x = 528 + float(exact occupied bytes before A) / float(B) * dataWidth
        + gaps before A * gapWidth
rectangle width = float(clipped interval byte size) / float(B) * dataWidth
```

At least 80% of the plot is reserved for data. The exact occupied-byte prefix
and interval size are computed before float conversion; widths are never
obtained by subtracting rounded coordinates. Starts use a bounded binary search
over merged intervals. There is still no artificial minimum byte width.
Explicit viewport gaps before the first and after the last admitted interval
are included. Gaps number at most `admitted.Count + 1`; without an explicit
viewport only internal gaps can exist. Empty scenes and zero-sized viewports
have no markers. Compact scenes with no gaps retain linear geometry exactly.

For compact scenes with gaps only, the first lane starts at y=32, reserving
headroom for two diagonal axis strokes at y=20..28 per gap. A pale amber band
spans all lanes at each break. All painted glyph coordinates, colors and stroke
widths come from the engine and are included in scene bounds. The unchanged
Legend DTO carries this plain ASCII accessibility description, not a visible
caption: `Compressed gaps contain no selected ranges; not necessarily free or
unmapped memory. Horizontal distance is not linear.` Its retained text metrics
are valid but do not enlarge the visible scene bounds.

`SceneGapMarkers` is a packed repeated-glyph DTO:

```text
Offsets : float list
Band    : SceneBounds                   // X=0, global Y
Lines   : (ScenePoint * ScenePoint) list // local X, global Y
Style   : ResolvedStyle
Legend  : SceneText                     // accessible description, never painted
```

There are no addresses, gap byte counts or source associations in this geometry
DTO. Consumers instantiate Band/Lines with `translate(offset,0)` for each
offset, and expose Legend's lines joined with spaces once as a description. Keeping one glyph,
rather than a per-lane/per-gap geometry expansion, bounds transport metadata.
SVG and desktop render noninteractive, non-source groups `address-gap-N` with
`data-kind=address-gap`, using those same primitives and translations after the
canvas background and before lane metadata/elements. Bands use `Style.Fill`
with no stroke; lines use no fill and `Style.Stroke`/`Style.StrokeWidth`.
The description is safely serialized as `<desc id="address-layout-description">`,
referenced by root `aria-describedby` only when compact gaps are present.
It produces neither visible text nor clipping geometry.
`data-address-layout` is `compact`, `relative` for linear mode,
or `schematic` for address-redacted scenes.

Within each lane, the maximum of `max(16, directive.Width)` determines content
height `H`. Width is the cross-axis thickness hint, in scene units, with a
16-unit visibility floor and a 4096-unit ceiling. It never changes byte scale.
The first lane starts at y=16 (y=32 only for compact-with-gaps); the initial lane
height is `H+80`, followed by a 16-unit gap.
An element of height `h` starts at `lane.Y+40+(H-h)/2`. This aligns intervals
across generation/segment/object layers in the same lane.

Paint order is layer, statement index, then admission index. Layers are
Memory=0, Segment=1, Generation=2, Free=3, Object/other BOX=4, PIN=5. Later
elements paint on top; box/InnerCenter label overlap is not avoided or made
exclusive. OuterLeft labeled pins use the deterministic row policy below. Query result
ordering is already deterministic; only bounded admitted elements are sorted.
Scene truncation can change automatic fit bounds because missing elements are
not scanned to discover an unbounded global extent.

Lanes retain their explicit runtime/heap associations and positioned bounds
for selection and layout. SVG/desktop emit inert `<g data-kind="lane">` metadata
with ordinal ID and optional runtime/heap fields, not outlined rectangles or
captions: those bounds are layout allocations, **not actual heap extents**.
The root starts at (0,0), reserves 528
units to the left for labels, and includes at least 16 right/bottom margin units.
Long labels may enlarge root width, but do not rescale lanes. An empty scene is
valid, has no lanes/elements, and is 32 units high.

### Readable OuterLeft PIN rows

Only admitted `PIN` directives with `LabelPosition=OuterLeft` and an actual
resolved `SceneText` participate. After label creation, each lane packs the
horizontal interval covering its label and pin stroke into collision-free
rows. Intervals sort by their positioned left edge, then admission index;
this matters because different label lengths can reverse pin-address order.
A priority queue keyed by `(occupied row end X, row index)` reuses the earliest
finishing row only if there is at least eight units of horizontal separation.
Otherwise it creates a new row. All inputs and memory are bounded by admission;
sorting/packing is `O(n log n)`, not a whole-heap scan or pairwise solver.

Rows share a content height equal to the largest pin or label height in that
lane, with eight units between rows. Each short standalone tick and its entire
label translate together to the row center, without a long leader through
other labels. X, interval mapping, IDs, source associations, styles, layers and
clipping flags never change. OuterLeft text still ends eight units before its
own tick. Multiline labels keep all baselines and clipping bounds attached.

If a lane also contains boxes, unlabeled pins or explicit InnerCenter pins,
those retain their original content-band Y; eligible pins start below that
band and its text/stroke extents. Otherwise the first row remains centered on
the original band (a single pin retains its original Y). Lanes expand as needed;
later lanes and all their elements translate by the accumulated expansion.
The scene and shared gap band include the resulting tall layout. Renderers
consume these positions without browser measurement or reflow.

No extra label/element elision or source grouping occurs. Existing label and
admission limits still apply, with explicit truncation. No-text pins, including
redacted labels, do not participate; address redaction bypasses packing entirely.
All existing coordinate/output limits remain: even the core maximum 4096
coincident, 4096-unit-tall pins fit below 17 million scene units, well within
the transport's finite safe-number bound. Hosts may pan/zoom the tall scene
but must not lay it out again.

## Colors and deterministic typography

MQL named colors and `#RRGGBB` are resolved to lowercase hex; CSS expressions,
URLs, alpha, arbitrary attributes and user CSS are rejected. `SceneTheme` is
reusable across hosts. Defaults are white canvas, `#202020` outline/text.
BOX fill is Background. PIN stroke is Background (a line has no visible fill).
For InnerCenter rectangle labels, black or white is chosen by the larger
contrast ratio from sRGB relative luminance; this overrides theme Text to keep
Black/Blue/Green and light fills readable. OuterLeft and PIN labels use theme
Text. Custom themes should keep that text readable against the canvas.

**Typography decision:** deterministic mono-cell allocation, not host measurement.
No font is bundled, downloaded or redistributed; no third-party font license or
font embedding dependency is introduced. SVG requests generic `monospace`, uses
12-unit editable text, and forces each line's declared width with `textLength`
and `lengthAdjust="spacingAndGlyphs"`. Cells are 8 units wide; line height is 14,
baseline is 11 units below the line top. Labels wrap at 64 cells with at most two
lines. There is no browser canvas/font measurement or platform-dependent reflow.

InnerCenter centers the text bounds on the rectangle or PIN marker; OuterLeft
places the right edge eight units left of the target. Both are vertically
centered on the target. Text may overhang a small rectangle; its separate bounds
are included in scene bounds. A per-label local clip guarantees glyph overhang
does not escape the label allocation. Small/overlapping boxes and explicit
InnerCenter pins can still produce overlapping labels; OuterLeft labeled pins
instead use the shared rows described above.

Printable ASCII is retained. CRLF is one explicit line break; bare CR/LF break
lines. A break at the 64-cell boundary does not insert a second break; a trailing
break terminates the current line without allocating another empty line.
Tab becomes one space. Every other control, DEL, non-ASCII UTF-16 unit and
unpaired surrogate becomes visible `?`; a surrogate pair becomes `??`.
`ReplacedCodeUnits` is recorded per label and serialized, so unsupported text is
**not silently dropped**. This fallback does not mark the scene incomplete.
Line/character elision does: the text has `IsTruncated` and the scene reports
`LabelCharacters` or `TotalLabelCharacters`.

This policy bounds real Unicode type labels without embedding fonts or assuming
identical installed fonts. **Glyph shapes, rasterization, hinting and fallback
font choice can differ across offline viewers/operating systems.** Geometry,
cell widths, line breaks, styles and SVG bytes are deterministic; pixel-identical
typography is not promised. A future licensed font/outlines policy must preserve
editable vector requirements and version this deliberate ASCII limitation.

## Redaction, including non-visible surfaces

The default is no redaction. Options apply while constructing the scene, before
text is copied or metadata serialized. They are not a find/replace operation on
SVG. Address labels are not reliably distinguishable from unsigned size labels
or opaque numeric literals in a QueryResult, so the address policy is deliberately
conservative.

| Setting   | Visible text                                                           | Source, IDs and geometry                                                                  |
| --------- | ---------------------------------------------------------------------- | ----------------------------------------------------------------------------------------- |
| Addresses | Suppress **all** labels, numeric/boolean/text included                 | Remove all source associations and runtime/heap fields; use schematic fixed-size geometry |
| Strings   | Suppress **all** text-valued labels, including type names and literals | Numeric/boolean labels and address associations remain                                    |
| Paths     | Same conservative suppression of all text-valued labels                | No paths are copied elsewhere in the first place; numeric/address data remain             |
| Labels    | Suppress all labels                                                    | Geometry and address associations remain unless Addresses is also set                     |

Address redaction replaces intervals by 16-unit shapes in 24-unit ordinal cells
within each lane, with fixed 16-unit content height. It removes byte widths,
absolute/relative address gaps, declared thickness, original clipping flags and
numeric labels rather than leaking them through geometry or label length.
Lanes expand horizontally as necessary; no address-derived wrap/scale is used.
Compaction is bypassed entirely and `Gaps=None` regardless of requested layout;
neither marker count, description, pin rows nor compact headroom leaks the original gaps.
PIN remains a zero-width, 16-unit marker. Original runtime/heap values are
replaced by anonymous ordinal lanes. IDs remain ordinal, not hashes of removed
data. SnapshotId remains in memory for host safety, but **never in SVG**.

Counts, selection/admission order, layer/kind through appearance, colors, lane
grouping and completeness are not anonymized. A recipe can encode sensitive
information through its selections or colors; these flags cannot promise
anonymous data or detect arbitrary opaque labels. The Strings/Paths options
therefore mask all textual labels, not a guessed subset of recognizable secrets.
Captured string-detail payloads, query text, paths and raw source diagnostics are
never exported even with no redaction. Only the source diagnostic **count** is
retained. Review intentional residual structure before sharing an export.

## Processing and output bounds

All limits are positive, may only be lowered, and are shared across statements.

| Limit                   | Default / ceiling | Exact accounting                                                                  |
| ----------------------- | ----------------: | --------------------------------------------------------------------------------- |
| MaxDirectives           |              4096 | Input directive inspections, including empty or clipped-out intervals             |
| MaxElements             |              4096 | Admitted visible directive composites                                             |
| MaxLanes                |                64 | Distinct admitted runtime/heap lanes                                              |
| MaxLabelCharacters      |               128 | UTF-16 input units inspected per unredacted label; also at most two 64-cell lines |
| MaxTotalLabelCharacters |             65536 | Sum of inspected label units after the per-label cap; redacted labels cost zero   |
| MaxElapsedMilliseconds  |              5000 | Exclusive cooperative scene deadline                                              |
| SvgLimits.MaxBytes      |           8388608 | Actual UTF-8 bytes accepted by the output stream                                  |

The builder does not call `List.length`, sort, copy labels or walk the entire
input before admission. Even a manually constructed huge QueryResult has only
bounded directives examined; query rows and diagnostic text are never enumerated.
Bounds discovery/sorting operate on at most MaxElements already-admitted items.
Compact union construction and exact prefix calculations are similarly bounded;
guards run before/after sorting and throughout merging, pin row partitioning,
lane translation and positioning.
Each element contains one rectangle/line plus at most one two-line label; SVG
adds fixed-size groups/clips and at most MaxLanes inert metadata groups. This also bounds
primitive/DOM overhead and text work, not just final array length. The writer
streams through a byte-counting wrapper rather than building a giant XML string.
Its fixed XML buffer is additional bounded memory.

Exactly filling a cap with no additional required item is complete. Overflow
reports all applicable element/lane reasons on that admission. Directive caps
stop at the next input node without inspecting it. Per-label limits take priority
before charging the shared text pool. Redacted labels are not scanned to classify
their contents. Empty scenes/ranges are valid. No hidden aggregation or bitmap
fallback replaces selected vectors.

`Complete` and `Truncated reasons` can carry positioned scenes for future preview.
Elapsed cutoff, cancellation, staleness and invalid input discard the whole scene,
rather than publishing unfinished geometry. SourcePartial is independent of query
or scene truncation. Failed/cancelled queries and unavailable source metadata
cannot create scenes. Cancellation/current-snapshot/time are checked through
admission, bounded label scanning, before and after bounded sorting, and before
return. Individual allocations, sorting and native/store critical sections are
not asynchronously interrupted; checks resume afterward. Trusted callback
exceptions propagate, not become fabricated successes.

`Svg.write limits scene stream token` returns `Result<int,SvgError>` where success
is the exact byte count. It keeps the caller's stream open. On invalid limits,
I/O failure, cancellation or overflow, **discard the entire stream**, which can
contain a prefix but never exceeds MaxBytes. The CLI handles this transactionally.
The low-level writer may serialize an explicitly truncated preview scene; root
status/reason/source/redaction attributes preserve that fact.

## Standalone SVG safety and determinism

The writer emits only SVG root/groups, rectangles, lines, local clip definitions,
editable label text and accessible descriptions. There are no scripts, foreignObject, arbitrary HTML, external
CSS, images, downloaded fonts or external assets. `url(#element-N-text-clip)` is a
generated local reference, not user input. XML text and attributes are written
through `XmlWriter`; invalid text units have already been visibly substituted.
Colors/IDs and element vocabulary are controlled, not escaped arbitrary SVG.

All numbers use invariant culture and finite bounded coordinates; strings keep
lossless integer representations. SVG is UTF-8 without BOM or XML declaration,
with stable attribute/paint order and no timestamp, snapshot GUID, temporary path
or platform newline dependency. Equal dump/query/settings yield identical bytes
even after a new import generates another SnapshotId, provided execution
completes under the budgets. No pixel-identical browser screenshot is implied.

The checked-in `tests/MemoryVisualizer.Core.Tests/fixtures/scene-v2.json` is a
readable positioned-scene golden projection, not an IPC schema. `scene-v2.svg`
is its exact writer output. The fixtures are named for scene schema 2. The
linear BOX baseline geometry is unchanged; SVG intentionally replaces lane
outlines with inert metadata groups. This presentation/row-layout update keeps
scene schema 2, native protocol 3 and all DTO shapes unchanged.
Tests also cover large addresses, gaps, overlaps,
same addresses across runtimes/heaps, zero-size ranges, clipping, all caps,
redaction, cancellation/staleness, unsafe text and culture changes. Compact
unit cases additionally cover massive gaps with wide Blue segments
and Grey generations, cross-lane unions and byte ratios, touching/duplicate
ranges, viewport boundary gaps, `2^64` ends, bounded marker counts and shared
SVG glyph positioning. Native tests
generate their own WithHeap dump, use its exact offline trusted DAC, compose
segments/generations/selected objects, and compare SVG across fresh imports.

## Native CLI and atomic publication

```text
MemoryVisualizer.Cli export example.dmp --dac <trusted-absolute-DAC> --query "MATCH(g:Generation) RETURN g AS BOX(Label=g.Generation,Background=Blue,Width=24)" --output new.svg
```

Use `--cache` and opt-in `--allow-network` exactly as native query/inspect.
Export requests memory maps only. Output must be a new file; there is no force or
overwrite option. `--query`, `--output`, DAC/cache and numeric option values are
consumed atomically, even if a value looks like another flag. Duplicate/unknown
options fail. Native query and inspect retain their existing syntax/behavior.
Start with low-cardinality segment/generation diagrams or an explicit object
type/address filter: unrestricted `MATCH(o:Object) RETURN o AS BOX` commonly
exceeds the 4096-result limit on real dumps and therefore publishes no SVG.

`--view-start <decimal-or-0x-uint64>` and `--view-size <decimal-or-0x-uint64>` must
be supplied together. `--plot-width` lowers/raises width within 64..4096.
Redaction flags are `--redact-addresses`, `--redact-strings`, `--redact-paths`,
`--redact-labels` and may be combined.

Query caps are `--max-results`, `--max-directives`, `--max-candidates`,
`--max-elapsed-ms`. Scene/output caps are `--max-scene-directives`,
`--max-elements`, `--max-lanes`, `--max-label-chars`, `--max-total-label-chars`,
`--max-scene-elapsed-ms`, `--max-svg-bytes`.

Preparation, limits and output path validation happen before native work.
Preflight rejects input/output path collision, existing files/directories,
missing parent directories, empty filenames, reserved device names, controls,
nonportable filename characters/trailing dots/spaces and names exceeding 255
UTF-8 bytes. It opens a random **sibling** CreateNew temporary file to establish
write access, never truncating the final path. A successful complete/nonpartial
pipeline flushes and closes it, checks cancellation, then renames with
`overwrite=false`. A file created by another actor in the meantime wins; export
fails and preserves it. Failed/partial/cancelled paths delete the owned temporary
file; cleanup failures are explicitly reported.

This is atomic namespace publication on a local filesystem supporting sibling
rename, not a directory-fsync durability guarantee or protection against hostile
concurrent directory replacement. Once rename succeeds, a later cancellation
cannot undo an already committed file. No raw dump is uploaded or published.

| Exit | Output policy                                                                                                  |
| ---- | -------------------------------------------------------------------------------------------------------------- |
| 0    | Complete query/scene and complete available source; final SVG committed, one success line on stdout            |
| 3    | Source partial, query/scene truncation, or SVG byte budget exceeded; stderr explains, **no final SVG**         |
| 2    | Invalid input, native/index/query/scene/write failure, or output collision; stderr explains, no successful SVG |
| 130  | Cancelled before commit; stderr explains, no final SVG                                                         |

No-match queries and MATCH without AS can validly export an empty SVG; they do
not implicitly draw selected rows. Unicode/control fallback is explicit metadata,
not failure. Truncated labels are incomplete, so the CLI refuses to publish
them rather than silently claiming a full illustration. There is no
`--allow-partial` in this slice.
