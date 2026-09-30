# Native worker

Run the built worker with `--protocol --backend=native`. The native handshake
requires exactly `{"tag":"hello","versions":[3],"extensions":[]}`. Its wire
contract is `desktop/src/native-types.ts`; it does not extend the closed,
explicitly synthetic `--backend=fake` v1 protocol. Native v2 is rejected, not
reinterpreted. Ready capabilities declare `sceneSchemaVersion: 2` and
`layouts: ["linear", "compact"]`; all versioned native messages use v3.

## Trust and completeness

`snapshot.load` accepts an absolute dump path, an optional caller-trusted
absolute DAC path for runtime zero, and an optional caller-trusted cache.
Network access is off by default. Explicit `allowNetwork: true` requires Windows
and an explicit trusted cache, and uses the existing DAC resolver's fixed official
HTTPS service, verification, size limits, and timeout policy. Other hosts reject
network lookup. DAC/cache paths must originate from the
trusted host, never recipes or dump metadata. The existing ClrMD adapter
retains DAC signature/version checks, disables implicit symbol lookup, and
extracts only the memory map: no references, roots, or sensitive string
details. Snapshot partial status and diagnostic counts are explicit.

Queries use `Mql.prepare`, `Mql.bind`, `Mql.execute`, and `Scene.build`.
Scene pages carry the shared engine's resolved geometry, text, style, and
source association. The browser must not recompute layout. Optional DTO fields
are explicit JSON `null`; target addresses and sizes remain strings.
Current query settings require an exact `layout: "linear" | "compact"`:
the desktop defaults to compact, while shared/headless scene defaults remain
linear. Missing or unknown layouts are errors (legacy recipe migration belongs
to the desktop host, not this protocol).

Scene schema 2 includes the selected layout and nullable `gaps`. Compact gap
markers carry at most 1,025 precomputed x-translation offsets for one positioned
band/two-line glyph template and one short global legend. The browser repeats
that template at those offsets without recomputing the address mapping; it
never receives duplicated geometry per break or source associations for
decorations. Linear scenes and address-redacted scenes have `gaps: null`.
Fractional scene coordinates and offset-array numbers are native-only; the
synthetic v1 frame grammar remains strict integer-only.
Scene failures without publishable geometry return `SceneFailed`,
`SceneTruncated`, or `Cancelled`; only diagnosed query failures may carry a
null scene in a query result. Detail lookup rejects inconsistent segment/type
relationships with `SnapshotInvariant` rather than inventing a heap or type.

## Lifetime and resource bounds

Only the current bounded query and positioned scene are retained. Query and
scene IDs are increasing decimal strings; snapshot IDs are the core snapshot
UUIDs. Accepted query/dispose operations invalidate old query pages.
Accepted loads cancel old in-flight requests, but failed or cancelled replacement
imports preserve the previous snapshot store, query, and scene identifiers.
A successful import swaps stores and invalidates the previous query/scene only
when its terminal result is ready to publish.

Expensive work runs off the pipe read loop. While load, query, export, or
dispose is active, competing requests receive `Busy`. Other requests have at
most eight outstanding admission slots. Publication checks the request epoch
and cancellation under the state lock, after acquiring the output writer.
Slots are released before a terminal frame can be observed; teardown remains
tracked through write completion and shutdown.
Successful state/file commits and admission removal share the publication lock.
A cancel arriving after that boundary cannot rewrite success to `Cancelled`,
even if pipe backpressure has not yet let the client observe the reply.

Frames are strict LF-delimited UTF-8 JSON, at most 64 KiB and depth 16.
Unknown/duplicate request fields, noncanonical integers, and invalid Unicode
are rejected. Pages contain at most 32 items and are additionally byte bounded.
Each candidate item is serialized into a bounded buffer. An individually
oversized item returns `OutputLimit`, never an empty page with the same cursor.
Queries retain at most 4,096 rows and 64 columns; scenes contain at most 1,024
elements, at most 64 lanes, and keep the shared scene label budgets. Compact
header offsets (including all 1,025 breaks plus 64 lanes) fit in one 64 KiB
frame; aggregate serialization still rejects any over-budget header.

Import progress retains only the latest phase/counter and emits at most once
per 100 ms. Cancellation is cooperative; the worker's finite request timer
cannot interrupt a blocked native DAC call. The owning host must enforce its
external import deadline and terminate an unresponsive worker after cancel.

`export` uses the shared bounded SVG writer, not IPC file bytes. It requires
complete query, source, and scene status; writes at most 8 MiB into an exclusive
staging file beside the destination; and atomically publishes a new path without
overwriting. Failed/cancelled staging files are removed. Protocol errors and
worker diagnostics use fixed messages, never native exception text or paths.
