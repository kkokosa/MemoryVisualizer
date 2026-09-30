# Visual-first desktop workspace (M1)

The desktop uses Electron, React and Vite for local controls and the native F#
worker for import, indexed selection, MQL, positioning and SVG export. The old
synthetic v1 shell remains available only through explicit `--fixture` or the
v1 integration-test launcher. Ordinary desktop startup uses **native protocol
v3**, never a fake fallback.

## Build and launch

Use the repository's pinned .NET SDK, Node and npm to build:

```text
npm ci
npm run build:workspace
```

The build installs only the explicitly allowlisted, checksum-verified Electron
runtime and publishes the worker self-contained for the current supported host:
Windows x64, Linux glibc x64, macOS x64 or macOS arm64. It uses a locked NuGet
restore and the exact `global.json` SDK. Output is
`artifacts/workspace-<RID>`; an existing output folder is never overwritten.
To keep an earlier or running build intact, choose a new destination, for example
`npm run build:workspace -- --output artifacts/workspace-win-x64-compact`.
The optional output folder must not already exist; publication never replaces it.
Runtime symlinks are preserved verbatim and checked to be relative, resolvable
and contained within the assembled application, including macOS frameworks.

Launch `MemoryVisualizer.exe` on Windows, `MemoryVisualizer` on Linux, or
`MemoryVisualizer.app` on macOS. The assembled folder includes Electron,
bundled React assets and the worker's .NET runtime. **Running it does not require
Node, npm, a .NET SDK, an installed .NET runtime, a terminal, or a checkout.**
Keep the entire folder together. A matching trusted DAC for the _dump's_ runtime
is still an external analysis prerequisite; the worker's bundled runtime is not
an excuse to use a mismatched DAC.

This is a runnable development workspace, **not an installer or signed release**.
Installer generation, signing/notarization, update delivery and stable-release
qualification remain #15. Linux requires Chromium's normal sandbox support and
desktop libraries. On systems using the setuid helper, an administrator must
configure the assembled `chrome-sandbox` as root-owned mode 4755, just as for the
development Electron runtime. Never launch with `--no-sandbox`.

For development, build the native solution and run `npm run build:desktop`,
`npm run electron:install`, then `electron desktop` through the local npm binary.
The development launcher uses the native apphost in the repository's normal
`bin` output, with optional `RUNTIME_IDENTIFIER` and `CONFIGURATION` selection.
It does not discover a worker in another checkout.

When building the same worktree from Windows and WSL, keep native intermediates
separate with `-p:MemoryVisualizerBuildRoot=<absolute-build-root>/` on restore,
build, test and publish. The default remains the repository's `bin`/`obj` layout.
This build-only override does not change how a distributed workspace locates its
bundled worker.

## First illustration

1. Choose **Open dump** and a supported heap-containing dump. In the native
   trust dialog, select a matching DAC from a trusted runtime distribution or a
   protected symbol-layout cache. Follow the [native compatibility policy](snapshots.md).
2. Use a template or write bounded [MQL](mql.md), then **Run**. Segment and
   generation templates provide a useful overview before selecting objects.
   Failures have actionable source spans; partial/truncated states are visible.
3. Pan, zoom or fit the positioned SVG. Select an element to inspect its exact
   source association and request bounded object details. Browse result pages;
   the renderer does not receive the full heap.
4. Save a recipe to a **new** filename or export standalone SVG through the
   shared engine. Existing files are never overwritten, even if the native save
   dialog offers an overwrite prompt. Choose a different filename.

New workspaces default to **Compact overview** in **Address layout**. The shared
engine fits occupied memory to the address axis while replacing empty gaps with
marked breaks. Blue segments and grey generations therefore remain visible when
a distant frozen segment would otherwise consume most of the linear address
span. Overlapping ranges stay aligned and occupied byte-length proportions are
preserved; distances across a break are **not** linear address distances.
Choose **Linear (true address spacing)** and Run to inspect the original address
scale. Fit adjusts the camera; it does not change either mapping. Very small
objects may still need a focused viewport. MQL `Width` controls a box's vertical
thickness, not its address extent.

The panes have keyboard-adjustable splitters. Native menus expose Open Dump
(`Ctrl/Cmd+O`), Open Recipe (`Ctrl/Cmd+Shift+O`), Save Recipe
(`Ctrl/Cmd+S`), Run (`Ctrl/Cmd+Enter`), Cancel (`Escape`) and Export
(`Ctrl/Cmd+Shift+E`). The editor is a controlled textarea with safe highlighting,
not a full language-service IDE.

The renderer constructs controlled SVG elements and text nodes; it never imports
SVG/HTML markup. All geometry, styles, line breaks, label bounds, `textLength`
and source associations come from the [shared positioned scene](scenes.md).
Only view transforms and interaction are local. Absolute uint64 addresses are
strings/BigInt, never JavaScript `Number`. Scene ownership uses snapshot IDs;
ordinal element IDs alone are not global object identities.

Layer visibility and notes are **view/recipe settings**. Shared-engine SVG
exports all scene layers and does not include recipe annotations. Notes must
not transfer silently to ordinal elements after a different query, settings or
dump changes the scene basis. Detached notes remain visible and local-only until
explicitly reapplied or removed; saving the attached notes does not mark those
unsaved detached notes clean. Redaction is applied by the shared scene builder,
including its schematic address-redacted geometry; it is not a browser filter.
Changing query or scene settings requires another Run before export. Changing
redaction also clears the old scene so it cannot be mistaken for redacted output.
Scene export refuses partial/truncated/cancelled/failed output, matching the CLI.

## Native protocol v3

The normative typed wire shapes are in `desktop/src/native-types.ts`, with
independent strict validators in `native-protocol.ts` and the F# worker.
Version 3 explicitly supersedes the unreleased native v2 contract; peers
advertising only v2 are rejected rather than given altered geometry silently.
This is a deliberate new version, not extra fields in the closed
[synthetic v1 contract](worker-protocol.md).

Start with separate arguments `--protocol --backend=native`. Frames are UTF-8
JSON followed by LF, at most **65,536 bytes excluding LF**, depth at most 16.
Duplicate fields, invalid Unicode, unknown fields/tags and noncanonical integer
tokens are rejected. Scene coordinates are finite JSON numbers; other numeric
fields have their declared integer bounds. Snapshot IDs are nonempty lowercase
UUIDs. Request IDs, query IDs, scene IDs, cursors, counts and uint64 values use
canonical decimal strings; addresses use `0x` plus sixteen lowercase hex digits.
No stdout text other than frames is permitted.

```json
{"tag":"hello","versions":[3],"extensions":[]}
{"tag":"request","version":3,"requestId":"1","snapshotId":null,"operation":"snapshot.load","args":{"path":"MAIN_SELECTED_ABSOLUTE_PATH","dacPath":"MAIN_TRUSTED_DAC","cachePath":null,"allowNetwork":false}}
{"tag":"cancel","version":3,"requestId":"1"}
{"tag":"shutdown","version":3}
```

Handshake capabilities identify `backend:"native"` and the ordered operations
`snapshot.load`, `snapshot.dispose`, `query.run`, `query.page`, `scene.page`,
`details`, `export`. Limits are `maxFrameBytes:65536`, `maxOutstanding:8`,
`maxPageSize:32`, `maxSceneItems:1024`, `maxResults:4096`,
`maxQueryLength:16384`, `progressIntervalMs:100`.
Capabilities also declare `sceneSchemaVersion:2` and
`layouts:["linear","compact"]`. Scene headers carry the selected layout and
nullable packed, positioned gap markers. There are at most 1,025 gap offsets,
one reusable two-line glyph and one shared text legend; all remain subject to
the same 65,536-byte frame budget. Decorations have no source associations or
selection targets. Linear and address-redacted scenes contain no gap markers.

| Operation          | Input                                                | Bounded result                                                                     |
| ------------------ | ---------------------------------------------------- | ---------------------------------------------------------------------------------- |
| `snapshot.load`    | Main-owned path, explicit DAC/cache policy           | New snapshot ID, object count, source-partial flag and diagnostic count            |
| `snapshot.dispose` | Active snapshot ID                                   | Disposal acknowledgement                                                           |
| `query.run`        | Active snapshot, text, scene settings                | Query ID/status/diagnostics/counts and positioned scene header or explicit failure |
| `query.page`       | Active snapshot/query ID, cursor, page size          | At most 32 rows and opaque next cursor                                             |
| `scene.page`       | Active snapshot/scene ID, cursor, page size          | At most 32 positioned elements and opaque next cursor                              |
| `details`          | Active snapshot, runtime and exact object address    | At most 32 entities and opaque next cursor                                         |
| `export`           | Active snapshot/scene ID and main-chosen destination | Atomic shared-engine file publication and byte length, never SVG bytes in IPC      |

`query.run` uses the same `Mql.prepare`, `Mql.bind`, `Mql.execute` and
`Scene.build` APIs as the CLI. Settings require `layout:"linear"|"compact"`,
plot width (64..4096), nullable
uint64 start/size viewport (empty viewports are valid), redaction flags, result
limit (1..4096) and scene-element limit (1..1024). Query source is limited to
**16,384 UTF-16 code units**, matching MQL spans, not Unicode scalar count.
Other shared parser, execution, extraction and scene limits remain in force.
The desktop's explicit settings must be supplied to CLI comparison commands
(for example `--layout compact --max-elements 1024`). CLI exports retain their
linear default for compatibility; only new desktop workspaces default to compact.

Only **one retained query and scene** belong to the active snapshot. A new
query invalidates their previous IDs. Successful snapshot replacement or
disposal invalidates query/scene ownership. A failed/cancelled transactional
import preserves the earlier usable snapshot and result rather than pairing
old UI with invalid IDs. Concurrent mutation is bounded and explicitly rejected
or cancelled, never queued for automatic expensive replay. Each host also checks
request/snapshot epochs before publishing results.

Paging is byte-aware as well as count-aware. A page ends before the frame
budget; an individual item that cannot fit reports an explicit output-limit
error. It does not produce empty pages with a repeating cursor. The native
serializer is bounded while writing, not after materializing a huge JSON
string. Source metadata strings can vary in size: cardinality caps do not
claim a fixed worker RSS ceiling.

Progress is coalesced to at most one update per 100 ms per request. At most eight
ordinary requests are admitted; cancel/shutdown are control messages. Admission
is released **before a terminal frame can be observed**, while teardown is
still tracked for shutdown. This removes the v1 post-load burst race without
increasing limits or adding sleeps. Writers await pipe backpressure and have
finite deadlines.

Electron owns the worker process and enforces external deadlines:
startup 10 seconds, import 120 seconds, query/export 15 seconds, pages 10 seconds,
cancel grace 2 seconds and shutdown 2 seconds. A stuck native DAC cannot be
interrupted safely inside the process; after grace expiry the owner kills only
its own child. Failure invalidates live analysis, preserves editor changes and
requires **Restart worker**, then deliberate reopen/run. Nothing is replayed.
Cancellation is a request, not proof that an operation did not commit. Stateful
operations stay pending until their authoritative terminal reply: a completed
snapshot replacement, recipe save or SVG export remains successful if Cancel
arrives after its commit point. The UI reconciles that result instead of keeping
an old snapshot or claiming that an already-written file was cancelled.
Default diagnostics never include dump paths, queries, labels or dependency
stderr.

## Recipes and trust boundary

A recipe is bounded UTF-8 JSON (256 KiB, depth 16) with exactly these fields:

```json
{
  "schemaVersion": 2,
  "query": "MATCH (s:Segment) RETURN s AS BOX",
  "settings": {
    "layout": "compact",
    "plotWidth": 1024,
    "viewport": null,
    "redaction": { "addresses": false, "strings": false, "paths": false, "labels": false },
    "maxResults": 4096,
    "maxElements": 1024
  },
  "annotations": [],
  "hiddenLayers": [],
  "snapshot": {
    "locator": "example.dmp",
    "size": "123456",
    "modifiedUtc": "2026-09-29T12:00:00.000Z"
  }
}
```

Version 1 recipes are accepted only with their exact original schema (no layout
field), then migrated in memory to **linear**. Their query, settings, notes and
dependency locator are preserved. Opening does not modify the original file;
Save writes version 2 to a new filename. Version 2 requires an explicit layout;
unknown versions, fields and layout values are rejected. Changing layout
invalidates export and detaches old scene-bound notes until a deliberate rerun
and, for notes, explicit reapplication.

`snapshot` may be null. There are at most 128 distinct element notes, each with
canonical `elementId` (`element-0` through `element-1023`) and at most 512 UTF-16
code units of plain text. Layers are distinct integers 0..5. The 256 KiB limit
applies to the encoded recipe file and desktop document, independently of the
64 KiB native-worker frame limit. Multibyte text and JSON escaping count toward
that byte budget. There are no executable variables, HTML, arbitrary extension
fields, DAC/cache paths, embedded heap data or credentials added by the host.
Queries and notes are user-authored data and can themselves contain sensitive
information; review them before sharing.

Locators use portable relative slash-separated components without `..`, `.`,
empty components, drive/URI prefixes, backslashes or control characters. A
dependency resolved through a link outside the recipe folder is rejected.
Saving a dump outside the chosen recipe folder stores only its filename, not
an external directory hierarchy, and displays a relocation notice.
Missing/unloaded dependencies survive editing and save-as; the user can choose
to reopen just the recipe or explicitly relocate a dump.

Identity uses actual file size and UTC modification timestamp. **It is not a
cryptographic content fingerprint** and does not prove that two files are
identical. A mismatch is visible and loading it requires explicit confirmation;
its old annotations must not attach to new objects. Even matching files require
a native open/trust decision, and opening a recipe never runs its query.
Unsupported versions, invalid fields, duplicate JSON properties, malformed
Unicode and oversize files fail before replacing the current document.

Recipe writes use a same-directory, owner-readable temporary file, flush it,
then atomically publish without overwriting. Failures/cancelled dialogs preserve
edits and existing files. The renderer is authoritative for dirty state, so
edits made while a save dialog is open cannot be marked clean by an older
completed save. Native close/replacement prompts protect unsaved changes.

The renderer has Node disabled, sandbox/context isolation enabled, a restrictive
CSP, no remote assets and no filesystem/process API. Preload exposes only
`invoke`, `cancel`, `setDirty` and `onEvent`, bounds data before IPC and caps
outstanding requests. Main independently validates commands, the exact owning
window, main frame and local URL. Paths come only from native dialogs or validated
recipe dependencies. Navigation, popups, permissions, webviews and network
requests are denied; no external-link opener is exposed.

## Regression gates

`npm run test:protocol` includes strict v1/v2 contracts, bounded framing,
request/cancellation lifecycle, recipe bounds/atomic persistence and pure
renderer utilities. `npm run test:electron` retains the explicit synthetic
sandbox lifecycle gate with event-based progress/cancellation synchronization.
The deterministic blocked-flush admission regression demonstrates terminal
visibility before cleanup without relying on host timing.

For the real workflow, first build the solution and desktop and install
Electron. Set `MEMORYVISUALIZER_DESKTOP_E2E=1`, then run the analysis tests filtered
to `DesktopIntegrationTests`. On headless Linux wrap the test command in
`xvfb-run -a`. Repository discovery walks upward from the test assembly's runtime
directory, independently of deterministic compiler/source-link path mappings.
If build outputs live outside the checkout, set
`MEMORYVISUALIZER_REPOSITORY_ROOT` to the absolute checkout root; invalid overrides
fail explicitly. The fixture creates only its owned child's `WithHeap` dump, supplies
that child's exact trusted offline DAC, drives the actual production main,
sandboxed preload and React DOM, and removes generated files. Native dialog
stubs exist only in the test launcher; production has no arbitrary-path test API.
Actual Electron key events select a focused scene element with Return and resize
the focused snapshot-pane separator by 20 pixels with Right, on every CI host.
Test-only reply barriers also issue Cancel after real load/save/export commits,
then verify that the committed outcomes remain authoritative.
Generated dumps are never committed or uploaded.

Four-host CI remains the platform acceptance gate. Graph/reference actions,
advanced language services, infinite canvases, backend comparisons and release
packaging are not part of this M1 workspace.
