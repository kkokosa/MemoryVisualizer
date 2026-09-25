# Native snapshot extraction

The F# adapter uses pinned ClrMD **4.1.745802** on the repository's exact .NET 11
RC1 SDK. It accepts a dump filename, never a PID. `IHeapSnapshotReader` extends
the unchanged `ISnapshotReader` metadata interface without forcing existing
implementations to implement the new method. The CLI `inspect` is the initial
real native entry point. Worker v1 remains explicitly fake; production IPC
integration requires selected-file handles, real capabilities/error mappings,
and a suitable import watchdog, not reuse of its synthetic ten-second deadline.

## Local/offline and native library trust

Default options allow **no network**, **no symbol cache**, **no implicit
dump-adjacent DAC**, and **no sensitive strings**. Supply `Dac.TrustedPaths`,
keyed by zero-based runtime index, with absolute local paths you trust. DACs are
native executable code, not passive data. Do not accept these paths directly
from untrusted recipes or dump metadata. UNC DAC/cache paths are rejected.
There is no unsafe-signature or ignore-mismatch switch.

Use the DAC shipped with the **exact target runtime build and architecture**,
obtained from your trusted deployment/runtime distribution. Windows CoreCLR
uses `mscordaccore.dll`; Unix uses `libmscordaccore.so` or
`libmscordaccore.dylib`; .NET Framework uses `mscordacwks.dll`. Arbitrary
historical DACs are not redistributed. This is the resolution strategy for
historical #1, not a promise that its old Windows dump works everywhere.

Optional `Dac.CacheDirectory` enables a caller-trusted per-user symbol-layout
cache even while offline. No environment symbol paths or shared temporary
symbol cache are used. Keys are derived from ClrMD's advertised runtime build
IDs or PE timestamp/image size, with the native `Self`/`Coreclr` distinction.
Cache entries are native code: protect this directory against other users'
writes. Explicit paths take precedence and never fall back silently.

`Dac.AllowNetwork` / CLI `--allow-network` requires `--cache` and is supported
**only on Windows**. It contacts the fixed official HTTPS endpoint
`https://msdl.microsoft.com/download/symbols/` (and its redirects), disclosing
runtime binary name/build identifiers, not the dump or heap contents. Downloads
are cancellable, have a 60-second deadline covering headers and the entire
response body plus a 64 MiB size cap, and are written
through unique temporary files. ClrMD Windows DAC signature verification stays
enabled; `CreateRuntime(path, false)` retains version matching. A failed
download/cache/library load is visible and actionable.

Unix automated native download/verification is not equivalent to Windows
Authenticode. Obtain a trusted exact DAC out of band and supply its absolute
path or deliberately seed a trusted cache. The adapter does not weaken TLS,
signature checks, or mismatch checks to make a fixture pass.

ClrMD 4.1 can report native runtime version `0.0` for .NET 11 ELF images with
multiple writable LOAD segments. The adapter scans at most 64 MiB **of the
dumped runtime module**, with cancellation and bounded overlapping buffers,
for one distinct four-component `@(#)Version ` marker. It constructs public
ClrMD metadata with that target-derived version and still performs normal DAC
matching. `RuntimeSnapshot.VersionSource` reports this recovery. Missing or
ambiguous markers fail; the version is never copied from the selected DAC.
Single-file runtimes are explicitly rejected because ClrMD skips version
matching for that layout; safe build-ID validation is follow-up work.

## Compatibility evidence

Cross-platform hosts do not imply arbitrary dump portability. OS and worker
architecture must match the target before DAC loading. Initial validation is
for generated, heap-containing, framework-dependent .NET 11 RC1 dumps. `WithHeap`
is the CI default; `Full` has also been exercised separately.

| Host / worker                             | Target                             | Status                                                                                           |
| ----------------------------------------- | ---------------------------------- | ------------------------------------------------------------------------------------------------ |
| Windows x64                               | Windows x64, .NET 11 RC1           | Generated heap-containing dump integration verified locally; Full also exercised                 |
| Ubuntu 24.04 glibc x64                    | Linux x64, .NET 11 RC1             | Generated dump integration verified in WSL; target-version recovery required with this ClrMD pin |
| macOS arm64                               | macOS arm64, .NET 11 RC1           | Automated generated-dump gate; not locally certified                                             |
| macOS x64                                 | macOS x64, .NET 11 RC1             | Automated generated-dump gate; not locally certified                                             |
| Windows x64                               | .NET Framework x64 / older CoreCLR | Matching DAC mechanism exists; not in the verified fixture matrix                                |
| Any cross-OS or cross-architecture pair   | Any                                | Rejected with a matching-worker recommendation                                                   |
| x86, ARM32, Linux arm64/musl, single-file | Any                                | Not supported in the initial policy                                                              |

This is not a minimum-OS/package certification table. Mac and hosted CI results
must be observed before claiming those platforms certified. Missing runtime
modules, mini/triage dumps, native-only dumps, GC-in-progress dumps, truncated
files, absent pages, or incompatible DACs can produce errors or partial results.

## Materialization and meaning

All returned arrays/records are managed values: no ClrMD objects, lazy native
enumerators, streams, or callbacks are retained. Native runtimes and the owned
dump reader/stream are disposed before completion, including cancellation and
failure. Callers may keep snapshots indefinitely, subject to ordinary managed
memory use. Arrays are caller-owned; avoid mutation when sharing snapshots.

Runtime identity includes a fresh snapshot UUID and runtime discovery index.
Object identity adds a target `uint64` address; type identity adds a method
table, not a display name. Address/size math remains unsigned. Address ranges
are **half-open**. Segment data includes the owning heap, object range,
committed and reserved ranges exactly as ClrMD reports them, and allocation
contexts. Reserved ranges may be the reserved tail, not a synthetic union with
committed bytes. Region-based runtimes expose `HasRegions`. LOH, POH, and
frozen heaps are kinds, not generations 3/4; generation ranges apply to SOH.

Recognized free entries remain in `Objects` with `IsFree=true` and a free type.
`Metadata.ObjectCount` excludes them. They are not reference sources.
Unexplained gaps are diagnostics, not invented free objects. Traversal is
strict (not `carefully=true`); coverage accounts for pointer-size alignment
and the GC allocation-context minimum-object tail. Object bytes are read
through a fixed 64 KiB scratch buffer to detect missing pages, without retaining
their contents.

`Edges` preserve aliases/cycles and repeated array/field references, source
and target scope, field name and offset where available. `Offset` is ClrMD's
byte offset from **object data after the method-table pointer**, for fields
and array elements alike: slot address = source address + pointer size +
offset. It is not an array index. Consumers may build
incoming/outgoing indexes over this one array. Unknown/non-extracted endpoints
remain raw scoped identities and cause partial-reference diagnostics.
Pointer-containing objects above `Int32.MaxValue` bytes, or without a readable
GC descriptor, are explicitly partial in M2 because ClrMD cannot walk their
ordinary references. Their unsigned sizes and M1 data remain intact.
`DependentHandle` edges are conditional key-to-value edges, **not unconditional
retention**. No structural containment or segment-to-object relationship is
presented as a retaining edge. No root-path/dominator algorithm is included.

`Roots` come from ClrMD's full `EnumerateRoots` API (including finalizer and
additional static/thread-static sources), with original slot address, kind,
pinned/interior flags, raw target when readable, and stack provenance where
available. Interior targets are resolved against extracted object intervals;
unresolved roots are retained and diagnosed rather than discarded. Distinct
root slots/provenance are not deduplicated merely because they share a target.
`Handles` independently records strong, weak-short/long/WinRT, pinned,
reference-counted, and dependent handles. Weak/dependent handles are not added
to the unconditional root set.

## Completeness, budgets, and cancellation

`Result.Error AnalysisError` means no usable snapshot. `Ok snapshot` can be
partial: inspect `IsPartial`, per-runtime `MemoryMap`/`References`/`Roots`/
`Handles`, `StringDetails`, and bounded diagnostics. `NotRequested` is not
`Complete`. The metadata-only API returns `PartialMetadata` instead of hiding
an incomplete object count; use the full API to inspect partial data.
`Complete` means the requested ClrMD extraction and observable coverage checks
finished without detected gaps, not proof that every native GC structure is
uncorrupted or that ClrMD identifies every theoretical root.

M1 does not depend on M2: disable references and roots to obtain only the
memory map. Failures/budgets in M2 preserve M1. A runtime missing its DAC may be
reported alongside other usable runtimes; an entirely unavailable analysis is
an error, never an all-null/empty success. Corrupt/unwalkable heaps are marked.

Default materialization limits: 1,000,000 objects, 100,000 types/segments,
4,000,000 edges, 250,000 roots/handles, and 1,000 diagnostics. Counts bound
retained collections; this is not a fixed process-RSS guarantee. Temporary
indexes and final array copies, ClrMD/native internals, and OS mappings add
overhead. Native type/field/method/stack caches are disabled, dump cache is
64 MiB, and ClrMD's parser safety limits remain enabled. The caller can lower
budgets; reaching them preserves data and records truncation. Resource-budget
benchmarking belongs to #9.

Sensitive string details require `IncludeStringDetails=true`; defaults cap
them at 128 strings, 512 characters each, and 65,536 total characters. Values
are eager and independently marked truncated, not lazy reads after disposal.
Global detail budget exhaustion or incomplete M1 is explicit. CLI summaries
never opt in or serialize object values. Type/field metadata can itself be
sensitive, so protect snapshots even when strings are disabled.

Extraction executes off the calling thread, reports phase/counters (no dump
values), and checks cancellation while iterating and reading bytes. A blocked
native DAC call cannot be interrupted by a managed cancellation token. A
production host must run imports in an owned worker and enforce an external
deadline/termination policy; the CLI Ctrl+C path is cooperative.

## Reproducing native integration

```text
dotnet restore MemoryVisualizer.slnx --locked-mode
dotnet test tests/MemoryVisualizer.Analysis.Tests -c Release --no-restore
```

The tests spawn only `MemoryVisualizer.DumpFixture`, confirm the reported child
PID, and capture that child's `DumpType.WithHeap` dump via the pinned transitive
diagnostics client. This includes managed heap data and is not a mini/triage
dump. CI avoids `Full` because unrelated native mappings can produce
multi-gigabyte files: a hosted macOS x64 Full capture exceeded 3.7 GB and
three minutes. The same complete map, reference, root, type, and offline-DAC
assertions remain mandatory; reducing capture scope does not relax them.
Set `MEMORYVISUALIZER_FIXTURE_DUMP_TYPE=Full` for explicit extended native-memory
coverage, or `WithHeap` for the default. No automatic fallback or retry is used.
Both child output pipes are continuously drained after readiness, with
only a 4,096-character tail retained per pipe. Capture has a finite
three-minute limit inside the ten-minute test-job gate; a timeout reports
child status, generated file size, and bounded diagnostics without retries.
The fixture uses synthetic cycles, shared references, arrays, LOH/POH,
pinned/weak handles, dependent-handle chains, an interior stack byref, and
same-named types from different dynamic assemblies. Tests cover offline explicit
DAC success, missing/corrupt inputs/DACs, budgets, strings, cancellation, and
post-disposal data/file access. A copy of the synthetic dump is deliberately
modified to hide a heap range; it must not become complete success.
Before extraction, the native reader must independently confirm the modified
address is unreadable. Synthetic x64/arm64 Mach-O header tests exercise this
mutation on every host, including preservation of unrelated segment bytes.
HTTP tests use an in-process fake handler to prove stalled DAC response bodies
honor both the whole-download deadline and caller cancellation without network
access or leftover cache files.

The four-host CI matrix runs the same test suite with a process-level timeout.
Tests delete their temporary files; CI never uploads raw dumps or heap
snapshots. Do not substitute the historical `data/` dumps: they are neither
portable nor known-safe fixtures. A forcibly terminated test runner may leave
its specifically named `MemoryVisualizer-fixture-*` temporary directory for
manual local cleanup; it is not an artifact to publish.
