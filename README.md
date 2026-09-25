# MemoryVisualizer

MemoryVisualizer is being rebuilt as a local-first, cross-platform tool for turning .NET memory into reproducible, publication-quality diagrams. The architecture is F#/.NET 11 analysis and scene computation, a headless CLI, and a TypeScript/Electron desktop shell. See the [roadmap (#5)](https://github.com/kkokosa/MemoryVisualizer/issues/5).

**Implemented now:** the foundation from [#6](https://github.com/kkokosa/MemoryVisualizer/issues/6): SDK-style projects, snapshot IDs and DTOs, an isolated ClrMD adapter boundary, executable help/version commands, and build/test tooling. **Not implemented:** dump loading, MQL, graph storage, layout/rendering, exports, a desktop window, or worker IPC. The old WPF/FsXaml application and Neo4j/Java/Paket startup dependencies have been removed rather than ported.

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

No arguments also prints help and exits. Unsupported arguments return exit code `2`, write a diagnostic to stderr, and leave stdout empty. Help/version return `0`. The worker does **not** read stdin or start a protocol loop yet.

`npm run smoke` executes both DLL and native apphost forms of both programs, checking actual exit codes, stdout/stderr, help/version aliases, invalid arguments, and output paths. It defaults to `Release`; set `CONFIGURATION=Debug` to check a Debug build. Set `RUNTIME_IDENTIFIER` to the host RID after a RID-specific build. These are environment variables (use `$env:NAME = "value"` in PowerShell).

### Dependencies and formatting

Ordinary NuGet `PackageReference` plus committed `packages.lock.json` files pin direct and transitive dependencies, including the SDK's FSharp.Core version. `NuGet.Config` clears inherited feeds and uses only nuget.org; no private feed credentials, MyGet, or Paket are required. ClrMD `4.1.745802` is the stable package selected from nuget.org, referenced only by `MemoryVisualizer.Analysis.ClrMd`. The SDK, not an installed Visual Studio workload, supplies the F# compiler.

The RC SDK bundles FSharp.Core packages with different Windows/Linux content hashes despite the same version. `DisableImplicitLibraryPacksFolder` deliberately disables that SDK-local package feed so all platforms restore the canonical nuget.org package and share identical lock hashes. This is an observed prerelease reproducibility gap, not a framework retarget or relaxed lock check.

The root npm workspace owns the single `package-lock.json`; run npm commands from the root, not a separate desktop install. TypeScript `7.0.2`, Electron `44.4.2`, and Prettier `3.9.8` are exact pins. `.npmrc` disables dependency lifecycle scripts: the Electron package/types are installed, but no Electron runtime is downloaded or launched in this foundation. `desktop/src/index.ts` is deliberately only a module marker. Main/preload/renderer entry points, React/Vite, runtime installation, packaging and IPC belong to [#7](https://github.com/kkokosa/MemoryVisualizer/issues/7), [#13](https://github.com/kkokosa/MemoryVisualizer/issues/13), and [#15](https://github.com/kkokosa/MemoryVisualizer/issues/15).

Format F# with `dotnet fantomas src tests`; format JSON, TypeScript, JavaScript, YAML and Markdown with `npm run format`. `.editorconfig` sets LF, UTF-8, four-space F# and two-space metadata/TypeScript indentation. Fantomas is permitted to run on a newer installed runtime, including the pinned .NET 11 prerelease, without changing its pinned tool version.

For an intentional dependency update, edit exact manifest versions, run `dotnet restore MemoryVisualizer.slnx --force-evaluate` and/or `npm install`, review the lockfile changes, then rerun the commands above. Upgrade the SDK explicitly in `global.json` and regenerate NuGet locks. CI reads this same pin; transition to .NET 11 GA is required before a stable release.

## Project and contract boundaries

| Project                                | Responsibility                                                                                                                                                                                                                                                      |
| -------------------------------------- | ------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `src/MemoryVisualizer.Core`            | Platform-neutral snapshot-local IDs, minimal snapshot/object records, DTO conversion, and an analysis interface. No ClrMD, native, UI, JSON framework, or Electron dependency. Query and scene modules can be added here separately without multiplying assemblies. |
| `src/MemoryVisualizer.Analysis.ClrMd`  | The only ClrMD reference. Implements the core interface with an explicit `NotImplemented` error until extraction work lands; respects cancellation and never opens a dump. Only core types cross its public boundary.                                               |
| `src/MemoryVisualizer.Worker`          | Future analysis/query process; help/version scaffold only.                                                                                                                                                                                                          |
| `src/MemoryVisualizer.Cli`             | Future headless query/export host; help/version scaffold only, not an IPC client.                                                                                                                                                                                   |
| `src/Shared/CommandLine.fs`            | Shared hosting-only source linked into the executables and hosting tests; CLI concerns do not enter the core.                                                                                                                                                       |
| `tests/MemoryVisualizer.Core.Tests`    | Contract, identity and lossless serialization tests, with no adapter reference or native loading.                                                                                                                                                                   |
| `tests/MemoryVisualizer.Hosting.Tests` | Argument handling, explicit unimplemented analysis and cancellation, plus managed ClrMD assembly loading.                                                                                                                                                           |
| `desktop`                              | Private npm TypeScript/Electron workspace reserved for the desktop issue.                                                                                                                                                                                           |

`SnapshotId` is a nonempty UUID. `ObjectId`, `TypeId` and `SegmentId` are distinct nonzero unsigned 64-bit identifiers, allocated within one snapshot and entity kind; they are **not** addresses, indices shared across snapshots, or runtime handles. An `ObjectReference` includes both snapshot and object ID, so the same local number in two snapshots refers to different objects. The eventual extractor owns deterministic allocation and persistence; this scaffold allocates no heap IDs and promises no cross-dump identity.

Core addresses and sizes are `uint64`. Boundary DTOs carry a schema version (`1`), lowercase UUID text, decimal strings for IDs/counts/sizes, and `0x` plus sixteen lowercase hexadecimal digits for addresses. This preserves values beyond JavaScript's safe-integer range. Snapshot scope is present on every object DTO; its type ID has that same scope. DTOs contain only primitives, not ClrMD handles or UI objects. They are output contracts, not yet an input validator, scene schema, or stdio envelope; protocol negotiation and inbound validation belong to #7. The core does not serialize itself: the host will own JSON policy and framing.

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

The GitHub Actions matrix covers Windows x64 (`windows-2025`), Linux x64 (`ubuntu-24.04`), macOS arm64 (`macos-15`) and macOS x64 (`macos-15-intel`). Each job restores locked dependencies, checks formatting and TypeScript, builds, runs unit tests, and smokes portable and host-RID executables. This is a managed foundation matrix, not a supported dump-analysis or packaged-desktop matrix. Adding CI does not itself establish that every remote OS run has passed.

The managed ClrMD dependency can be loaded without invoking DAC/native analysis. Native DAC resolution, dump/host OS and architecture compatibility, supported runtime versions, packaging and minimum OS requirements remain unverified and belong to #8/#15. Neither the scaffold nor its tests access symbols, download DACs, inspect historical dumps, or upload heap contents. Historical #1/#2/#4 and foundation #6 are not automatically closed by this change.

## Product examples (not implemented)

All diagrams and MQL snippets below are historical product/design examples, **not screenshots or functionality of this scaffold**. They preserve the intended address-aligned composition and publication-quality output. MQL will be a documented subset inspired by Cypher, not full Cypher compatibility or a Neo4j dependency. Live process attachment is deferred.

![Example results](/doc/visualizer_figures.png)

Often there is a need to understand what is inside .NET memory process - probably because of some kind of memory leak. Nevertheless, .NET memory management is also very interesting piece of software. No matter what is the reason you need to look inside, there is a huge amount of data to be analyzed. And as we all know that a picture is worth a thousand words, this tool is dedicated specifically to visualize .NET memory - both from memory dumps and from attached processes.

The intended emphasis is on **drawing images for articles, presentations and workshops**, rather than replacing general-purpose memory profilers. Queries and drawing instructions will describe what to show, rather than being limited to hard-coded views.

The goal of this tool is to produce figures like in the opening (exemplary) picture. The program itself will be very simple, with the main window containing query window and the results:

![Main window](/doc/visualizer_window.png)

Queries in MemoryVisualizer are written in the custom language MQL (Memory Query Language) which is based on [Cypher](https://neo4j.com/developer/cypher-query-language/) (neo4j query language) with extensions allowing for formatting and drawing information. Let's look at some examples. Note: those examples impose certain simplifications, not to get lost in the complexity of the memory management problem itself. For example, I assume Workstation GC mode to have only one managed heap.

We can ask for memory segments only:

```
MATCH (seg: Segment)
RETURN seg
```

![MQL1](/doc/mql1.png)

The above-mentioned extensions to Cypher allows to specify how results should be drawn, for example:

```
MATCH (seg: Segment)
RETURN seg AS BOX (Label = seg.Address,
                   LabelPosition = OuterLeft)
```

![MQL1](/doc/mql2.png)

We can draw only generations:

```
MATCH (gen Generation)
RETURN gen AS BOX (Label = gen.Name,
                  LabelPosition = InnerCenter,
                  White = Grey)
```

![MQL1](/doc/mql3.png)

But as one command will contain two queries returning both segments and generations, the engine drawing must be wise and put one on the second line of addresses. This is the very important principle of drawing query results - if the command contains several queries, the results of these queries are drawn in the "overlapping" mode in terms of address space:

```
MATCH (seg: Segment)
RETURN seg

MATCH (gen Generation)
RETURN gen AS BOX (Label = gen.Name,
                   LabelPosition = InnerCenter,
                   Background = hash (seg.Generation))
```

![MQL1](/doc/mql4.png)

Going forward, as Cypher (so MQL) is excellent in querying graphs, it allows perfectly to query for object references:

```
MATCH (parent: Object) - [ref] -> (obj: Object)
WHERE obj.Address = 0xDDE51018
RETURN parent, ref, obj
```

![MQL1](/doc/mql5.png)

And with the AS operator we can impose on a further way of drawing:

```
MATCH (parent: Object) - [ref] -> (obj: Object)
WHERE obj.Address = 0xDDE51018
RETURN parent AS CIRCLE (Radius = parent.Size)
       ref
       obj AS CIRCLE (Label = obj.Address + "\r\n" + obj.Type,
                      Radius = obj.Size)
```

![MQL1](/doc/mql6.png)

We want to see what roots keeps a reference to the object? Nothing easier thanks to Cypher capabilities (`relationships`):

```
MATCH p = (root: Object) - [*] -> (obj: Object)
WHERE obj.Address = 0xDDE51018
RETURN root AS DOT (Label = root.Type)
       relationships (p)
       obj AS DOT (Label = obj.Size)
```

In addition, there is a structure of relationships between the various entities representing memory. Eg. the _Segment_ will have a relationship to its _Generations_. Those relations together with overlapping semantics can be easily consumed by MQL. To draw all segments containing generation 2:

```
MATCH (seg: Segment) -> (gen Generation)
WHERE gen.Generation = 2
RETURN seg, gen AS BOX (Label = gen.Name,
                        LabelPosition = InnerCenter,
                        Background = Yellow)
```

![MQL1](/doc/mql7.png)

We can then for example draw additionaly objects of given type:

```
MATCH (seg: Segment) -> (gen Generation) -> (obj: Object)
WHERE gen.Generation = 2 AND obj.Type = "SomeClass"
RETURN seg
       gen AS BOX (Label = gen.Name, LabelPosition = InnerCenter)
       obj AS PIN
```

![MQL1](/doc/mql8.png)

We can also combine this altogether:

```
MATCH (seg: Segment) RETURN seg

MATCH (gen: Generation)
RETURN gen AS BOX (Label = gen.Name, LabelPosition = InnerCenter)

MATCH (obj: Object) - [ref] -> (child: Object)
WHERE child.Address = 0xDDE51018
RETURN obj, ref, child
```

![MQL1](/doc/mql9.png)

For illustrational purposes there is also an addional command `DRAW`. It can take a variety of input functions but at the beginning it be only a `Memory` function, which draws symbolically a given block of memory:

```
DRAW Memory (0xDDE51000, 0xDFE51000, Width = 1M)
```

![MQL1](/doc/mql10.png)

Then you could use it with the rest of other queries thanks to the "overlapping semantics":

```
MATCH (gen: Generation)
RETURN gen AS BOX (Background = hash (gen.Generation), Label = gen.Name, LabelPosition = InnerCenter)

DRAW Memory (0xDDE51000, 0xDFE51000, Width = 1M)
```

![MQL1](/doc/mql11.png)

Thanks to DRAW command and expressiveness of MQL, drawing fragmentation is as easy as:

```
MATCH (obj: Object)
WHERE obj.Type = "Free"
RETURN obj AS BOX

DRAW Memory (0xDDE51000, 0xDFE51000, Width = 1M)
```

![MQL1](/doc/mql12.png)

Implementation milestones and current status are tracked in [the roadmap](https://github.com/kkokosa/MemoryVisualizer/issues/5), not in these historical examples.
