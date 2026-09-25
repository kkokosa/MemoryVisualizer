# MemoryVisualizer

MemoryVisualizer is being rebuilt as a local-first, cross-platform tool for turning .NET memory into reproducible, publication-quality diagrams. The architecture is F#/.NET 11 analysis and scene computation, a headless CLI, and a TypeScript/Electron desktop shell. See the [roadmap (#5)](https://github.com/kkokosa/MemoryVisualizer/issues/5).

**Implemented now:** the [#6](https://github.com/kkokosa/MemoryVisualizer/issues/6) foundation, [#7](https://github.com/kkokosa/MemoryVisualizer/issues/7) bounded worker protocol, [#8](https://github.com/kkokosa/MemoryVisualizer/issues/8) native dump snapshot adapter and CLI summary, the **visual-first M1 slice of [#10](https://github.com/kkokosa/MemoryVisualizer/issues/10)** (indexed snapshot store), and the **bounded M1 slice of [#11](https://github.com/kkokosa/MemoryVisualizer/issues/11)** (MQL parser, typed planner/executor and native query CLI). Snapshots include memory maps, reference edges, roots, and handle metadata. The Electron shell and v1 worker protocol still use their explicit **fake backend**, not the new adapter/store/query library. **Not implemented:** MQL reference traversal/root-path algorithms, layout/rendering, SVG/other exports, packaging, or the desktop workspace UX. Full #10 backend comparison, measured ADR and large benchmarks remain deferred; managed indexes are provisional. Full #11 also requires M2 and real worker integration. The old WPF/FsXaml application and Neo4j/Java/Paket startup dependencies have been removed rather than ported.

## Quick start

Install these exact tools:

| Tool     | Version                   | Pin                                                               |
| -------- | ------------------------- | ----------------------------------------------------------------- |
| .NET SDK | `11.0.100-rc.1.26425.128` | `global.json`, with prereleases enabled and roll-forward disabled |
| Node.js  | `24.19.0`                 | `.node-version` and `package.json`                                |
| npm      | `11.19.0`                 | `package.json` (`npm install --global npm@11.19.0` if necessary)  |
| Fantomas | `8.0.5`                   | local `.config/dotnet-tools.json`                                 |

The .NET pin matches the [official .NET 11 release metadata](https://builds.dotnet.microsoft.com/dotnet/release-metadata/11.0/releases.json), checked on 2026-09-25. The Windows x64 SDK archive was downloaded and its SHA-512 verified against that feed. There is no .NET 10 fallback. Install the exact SDK from the official feed using the [.NET install script](https://learn.microsoft.com/dotnet/core/tools/dotnet-install-script), or its published archive. For a private install, put its directory on `PATH` and set `DOTNET_ROOT` to that directory so both `dotnet` and executable apphosts use it. Run `dotnet --version` from the repository root to confirm the pin.

From the repository root, on Windows, Linux, or macOS:

```text
dotnet tool restore
dotnet restore MemoryVisualizer.slnx --locked-mode
dotnet build MemoryVisualizer.slnx -c Release --no-restore
dotnet test MemoryVisualizer.slnx -c Release --no-build --no-restore
npm ci
npm run typecheck
npm run smoke
npm run test:protocol
npm run electron:install
npm run test:electron
dotnet fantomas --check src tests
npm run format:check
```

Run the executables without loading a dump:

```text
dotnet run --project src/MemoryVisualizer.Worker -c Release --no-build --no-restore -- --help
dotnet run --project src/MemoryVisualizer.Worker -c Release --no-build --no-restore -- --version
dotnet run --project src/MemoryVisualizer.Cli -c Release --no-build --no-restore -- --help
dotnet run --project src/MemoryVisualizer.Cli -c Release --no-build --no-restore -- --version
```

No arguments also prints help and exits. Unsupported arguments return exit code `2`, write a diagnostic to stderr, and leave stdout empty. Help/version return `0`. Only the worker's explicit `--protocol --backend=fake` invocation starts the stdio protocol; ordinary help/version commands never wait for stdin.

For an offline native snapshot summary, use an explicit dump and an **absolute trusted DAC path from the exact target runtime build**:

```text
dotnet run --project src/MemoryVisualizer.Cli -c Release --no-build --no-restore -- inspect example.dmp --dac C:\trusted-runtime\mscordaccore.dll --memory-map-only
```

On Linux/macOS substitute the matching `libmscordaccore.so`/`libmscordaccore.dylib` absolute path. Omit `--memory-map-only` to include reference/root/handle counts. Output is bounded JSON without object/string payloads; all counts use decimal strings. Exit codes: `0` complete, `3` usable partial (read diagnostics), `2` failure/invalid arguments, `130` cancellation. The public F# `IHeapSnapshotReader` provides the full materialized data. See [snapshot extraction and compatibility](doc/snapshots.md) for DAC trust, limits, completeness, and support restrictions.

`IndexedHeapSnapshot.Create` consumes that materialized snapshot without native handles. It provides exact scoped object/type lookup and deterministic bounded selection by runtime, heap, segment, generation, heap kind, type, free/allocated entries, and half-open address ranges. See the [provisional indexed-store contract](doc/indexed-snapshots.md) for ownership, interval overlap versus starts-in-range, pagination, partial input, limits, cancellation, and disposal. This API is not yet wired to a scene or desktop import.

For native MQL, use `query` instead of `inspect` and pass `--query "MATCH (o:Object) RETURN o.Address,o.Size"` along with the dump path and trusted DAC options. This returns versioned JSON rows and drawing instructions, **not SVG**. `--max-results`, `--max-directives`, `--max-candidates` and `--max-elapsed-ms` may lower the defaults. Exit codes are `0` complete, `3` source-partial or query-truncated, `2` failure, `130` cancellation. The [MQL specification](doc/mql.md) defines syntax, typed properties, explicit runtime/heap DRAW lanes, source spans, shared budgets, the public F# API and CLI JSON contract.

`npm run smoke` executes both DLL and native apphost forms of both programs, checking actual exit codes, stdout/stderr, help/version aliases, invalid arguments, and output paths. It defaults to `Release`; set `CONFIGURATION=Debug` to check a Debug build. Set `RUNTIME_IDENTIFIER` to the host RID after a RID-specific build. These are environment variables (use `$env:NAME = "value"` in PowerShell).

### Dependencies and formatting

Ordinary NuGet `PackageReference` plus committed `packages.lock.json` files pin direct and transitive dependencies, including the SDK's FSharp.Core version. `NuGet.Config` clears inherited feeds and uses only nuget.org; no private feed credentials, MyGet, or Paket are required. ClrMD `4.1.745802` is the stable package selected from nuget.org, referenced only by `MemoryVisualizer.Analysis.ClrMd`. The SDK, not an installed Visual Studio workload, supplies the F# compiler.

The RC SDK bundles FSharp.Core packages with different Windows/Linux content hashes despite the same version. `DisableImplicitLibraryPacksFolder` deliberately disables that SDK-local package feed so all platforms restore the canonical nuget.org package and share identical lock hashes. This is an observed prerelease reproducibility gap, not a framework retarget or relaxed lock check.

The root npm workspace owns the single `package-lock.json`; run npm commands from the root, not a separate desktop install. TypeScript `7.0.2`, Electron `44.4.2`, and Prettier `3.9.8` are exact pins. `.npmrc` still disables dependency lifecycle scripts. `npm run electron:install` explicitly allowlists only the pinned Electron package's official runtime installer (including its checksum verification); it does not enable arbitrary install scripts. `npm run build:desktop` compiles main, the single-file sandbox-compatible preload, and tests. React/Vite/workspace UX and packaging remain in [#13](https://github.com/kkokosa/MemoryVisualizer/issues/13) and [#15](https://github.com/kkokosa/MemoryVisualizer/issues/15).

### Worker bridge and synthetic test shell

The normative [worker protocol](doc/worker-protocol.md) specifies v1 handshake,
extension/version rejection, request and snapshot IDs, typed results/errors,
cancellation, and shutdown. Shared fixtures in `protocol/fixtures.json` are
validated independently by F# and TypeScript. Exact limits are 65,536 UTF-8 bytes
per frame, 8 outstanding requests, 128 page/scene items, JSON depth 16, and at most
one progress event per request per 100 ms. Writers honor pipe backpressure;
progress is coalesced, and stalled peers have finite deadlines. uint64 values
remain canonical strings, including `18446744073709551615`.

After building the solution and desktop and explicitly installing Electron,
launch `node_modules/.bin/electron desktop` (PowerShell:
`.\node_modules\.bin\electron.cmd desktop`). The test shell can load, cancel,
and dispose a synthetic fixture. It cannot select files, analyze dumps, run MQL,
or produce a real export. Main spawns the explicit native apphost with separate
arguments and `shell: false`; there is no server and no Node utility-process
substitution. `RUNTIME_IDENTIFIER` chooses an already built host-RID output.
Release portable output is the default, and requires the pinned .NET runtime.
Self-contained distribution is deliberately left to packaging work.

The renderer has no Node integration, raw IPC, process, filesystem, or arbitrary
path API. Context isolation and the Chromium sandbox remain enabled. The preload
rejects oversized/invalid arguments before IPC, caps in-flight invocations at 8,
and coalesces repeated cancellation. Main checks
the originating window, its main frame and exact local URL, then validates each
operation again. Snapshot replacement/disposal suppresses stale results. Crashes
and timeouts reject pending work without automatic analysis retries. Only the
owned child is force-terminated after failed shutdown; default logs omit payloads
and dependency stderr. Startup/request/shutdown deadlines are 5/10/2 seconds.

`npm run test:protocol` builds TypeScript and runs shared-fixture, framing, and
real native-process lifecycle tests. `npm run test:electron` runs the real
sandboxed preload, rejects an unauthorized second window, exercises cancellation
and disposal, closes the app with work active, and verifies its worker has exited.
Linux headless hosts need a display (`xvfb-run -a npm run test:electron`), Chromium
native libraries, and a working Chromium sandbox; do not use `--no-sandbox`.

Format F# with `dotnet fantomas src tests`; format JSON, TypeScript, JavaScript, YAML and Markdown with `npm run format`. `.editorconfig` sets LF, UTF-8, four-space F# and two-space metadata/TypeScript indentation. Fantomas is permitted to run on a newer installed runtime, including the pinned .NET 11 prerelease, without changing its pinned tool version.

For an intentional dependency update, edit exact manifest versions, run `dotnet restore MemoryVisualizer.slnx --force-evaluate` and/or `npm install`, review the lockfile changes, then rerun the commands above. Upgrade the SDK explicitly in `global.json` and regenerate NuGet locks. CI reads this same pin; transition to .NET 11 GA is required before a stable release.

## Project and contract boundaries

| Project                                 | Responsibility                                                                                                                                                                                                                     |
| --------------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/MemoryVisualizer.Core`             | Platform-neutral snapshot/runtime-scoped identities, materialized records, provisional read-only M1 indexes, DTO conversion, and analysis interfaces. No ClrMD, native, UI, JSON framework, or Electron dependency.                |
| `src/MemoryVisualizer.Query`            | Bounded M1 MQL lexer/parser, typed selection/projection and separate presentation plans, snapshot-scoped executor. Core-only dependency; suitable for CLI and future real worker binding.                                          |
| `src/MemoryVisualizer.Analysis.ClrMd`   | The only ClrMD reference. Explicit dump extraction, trusted DAC resolution, compatibility checks, bounded materialization, progress/cancellation, and typed errors/partial diagnostics. Only core types cross its public boundary. |
| `src/MemoryVisualizer.Worker`           | Bounded v1 protocol host and deterministic fake backend; real analysis/query operations remain deferred.                                                                                                                           |
| `src/MemoryVisualizer.Cli`              | Offline native `inspect` summary and `query` rows/drawing instructions plus help/version. Not an IPC client or SVG exporter.                                                                                                       |
| `src/Shared/CommandLine.fs`             | Shared hosting-only source linked into the executables and hosting tests; CLI concerns do not enter the core.                                                                                                                      |
| `tests/MemoryVisualizer.Core.Tests`     | Contract, identity, lossless serialization, indexed-store and shared-fixture MQL grammar/type/span/budget/composition tests, with no adapter reference or native loading.                                                          |
| `tests/MemoryVisualizer.Hosting.Tests`  | Argument handling, managed adapter loading/failures, and worker protocol coverage.                                                                                                                                                 |
| `tests/MemoryVisualizer.Analysis.Tests` | Real generated-dump integration, offline DACs, memory maps, aliases/cycles/arrays/handles, bounded details, corruption, cancellation/disposal, and lossless summaries.                                                             |
| `tests/MemoryVisualizer.DumpFixture`    | Controlled synthetic fixture child process; no production attach or historical dump access.                                                                                                                                        |
| `desktop`                               | Typed worker owner, narrow preload API, synthetic sandboxed test shell, and native/Electron lifecycle tests.                                                                                                                       |

`SnapshotId` is a nonempty UUID. The original DTO contracts retain distinct nonzero `ObjectId`, `TypeId`, and `SegmentId` values plus `ObjectReference` snapshot scope; they are not native handles. Extraction adds explicit `RuntimeIdentity` (snapshot UUID and discovered runtime index), `ObjectIdentity` (runtime and address), and `TypeIdentity` (runtime and method table). Equal type names never imply equal types. Identities do not survive a new capture/import; no cross-dump identity is promised.

Core addresses and sizes are `uint64`. Boundary DTOs carry a schema version (`1`), lowercase UUID text, decimal strings for IDs/counts/sizes, and `0x` plus sixteen lowercase hexadecimal digits for addresses. This preserves values beyond JavaScript's safe-integer range. Snapshot scope is present on every object DTO; its type ID has that same scope. DTOs contain only primitives, not ClrMD handles or UI objects. The worker owns strict input/output validation and framing; the core remains transport-independent. The synthetic scene envelope is not the future geometry/layout schema.

## Output layout and CI

The shared output requirement in [#4](https://github.com/kkokosa/MemoryVisualizer/issues/4) is implemented with:

```text
bin/<ProjectName>/<Configuration>/net11.0/[<RID>/]
obj/<ProjectName>/<Configuration>/net11.0/[<RID>/]
```

The SDK appends the configuration, target framework and optional runtime identifier; a portable build omits the RID directory. Build outputs from different projects, configurations and RIDs cannot overwrite one another. Publish output goes in `publish/` beneath the relevant output directory. Project-level NuGet assets/cache files live in `obj/<ProjectName>/`; do not run concurrent restores for different settings in the same checkout. The declared RID set (`win-x64`, `linux-x64`, `osx-arm64`, `osx-x64`) keeps the lockfiles consistent for portable and host-specific restores.

After the locked restore above, build each executable for the host RID (substitute `linux-x64`, `osx-arm64`, or `osx-x64` as appropriate):

```text
dotnet build src/MemoryVisualizer.Worker -c Release --no-restore -r win-x64 --self-contained false
dotnet build src/MemoryVisualizer.Cli -c Release --no-restore -r win-x64 --self-contained false
```

This produces `bin/MemoryVisualizer.Worker/Release/net11.0/win-x64/MemoryVisualizer.Worker.exe` and an independent CLI directory. The SDK rejects solution-wide RID builds, so target the individual projects. All four RID targets are already restored; do not use `dotnet restore -r <RID>`, which replaces the declared RID set and invalidates the shared locks. Generated outputs, local tools, npm installs, test results and new dumps are ignored. Existing historical dump files remain unchanged and are **not** used by tests or uploaded.

The GitHub Actions matrix covers Windows x64 (`windows-2025`), Linux x64 (`ubuntu-24.04`), macOS arm64 (`macos-15`) and macOS x64 (`macos-15-intel`). Each job restores locked dependencies, checks formatting and TypeScript, builds, runs unit/shared-contract tests and **generated native dump integration**, smokes portable and host-RID executables, and runs native-worker and real sandboxed Electron lifecycle tests. Linux uses Xvfb and the official Chromium sandbox helper. This is a source-built matrix, not a packaged-desktop matrix. Adding CI does not establish that every remote OS run has passed; see the [compatibility evidence table](doc/snapshots.md).

Integration tests spawn only their own synthetic fixture child and collect a heap-containing `WithHeap` dump, then use its trusted installed runtime DAC with network disabled. This is not a mini/triage dump; `Full` capture is opt-in for extended coverage because unrelated native maps can produce multi-gigabyte files on hosted macOS. All completeness assertions remain unchanged. Temporary dumps are deleted, never committed or uploaded as CI artifacts. Historical dumps remain untouched. Packaging/minimum OS certification belongs to #15; historical issues are not automatically closed. No dump contents or telemetry are uploaded by the adapter. Optional Windows symbol-service lookup is documented and off by default.

## Executable M1 examples

MQL retains familiar MATCH/WHERE/RETURN vocabulary, not Cypher compatibility.
The following composition is executable and tested against the deterministic
test fixture; on a real dump, use its captured addresses and runtime/heap lanes:

```text
MATCH (seg: Segment) RETURN seg;
MATCH (gen: Generation) RETURN gen.Generation AS BOX (Label = gen.Generation, LabelPosition = InnerCenter, Background = Grey);
MATCH (obj: Object) WHERE obj.Generation = 2 AND obj.Type = "Same.Display.Name" RETURN obj.Address, obj.Size AS PIN;
DRAW Memory(0x100, 0x200, Runtime = 0, Heap = 0, Width = 16)
```

The rows and instructions preserve one target-address coordinate system without
merging equal addresses from different runtimes or heaps. Selection/projection
are separate from presentation. Free entries can be drawn explicitly:

```text
MATCH (obj: Object) WHERE obj.IsFree = true RETURN obj AS BOX (Background = Yellow);
DRAW Memory(0x100, 0x400, Runtime = 0, Heap = 0)
```

The [full bounded grammar and tested examples](doc/mql.md) replace the old
non-executable sketches. Intentional corrections: `(gen Generation)` needed
`(gen: Generation)`; `gen.Name` is replaced by captured `gen.Generation`;
`White = Grey` becomes `Background = Grey`; `seg.Generation` cannot refer to
another statement's binding; arbitrary `hash(...)`/string expressions are not
allowed; statements need semicolons; `Width = 1M` is not an integer width.
Use `IsFree = true`, not the display-name assumption `Type = "Free"`.
Relationship patterns, unrestricted root paths, DOT/CIRCLE and executable
templates remain unsupported. These were design ideas, not legacy grammar.

## Historical visual concepts (not rendered by M1)

The intended emphasis remains **drawing images for articles, presentations and
workshops**, rather than replacing general-purpose profilers. These historical
images illustrate future output and UX, not screenshots of current features.
Live process attachment, reference traversal, shared scenes/SVG (#12), and the
real desktop import/editor/recipes workflow (#13) remain separate work.

![Historical composition concept](/doc/visualizer_figures.png)

![Historical editor concept](/doc/visualizer_window.png)

![Historical address-aligned layers concept](/doc/mql4.png)

![Historical fragmentation concept](/doc/mql12.png)

Implementation milestones are tracked in [the roadmap](https://github.com/kkokosa/MemoryVisualizer/issues/5).
