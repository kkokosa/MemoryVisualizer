# Generated heap fixture

Build this F# executable with the SDK pinned in the repository's `global.json`.
It uses only synthetic objects and never opens existing dumps or attaches to
processes. Do not use the historical dumps under `data/` for these tests.

Start `MemoryVisualizer.DumpFixture.dll` using that SDK's `dotnet` host, with
standard input and output redirected. The executable takes no arguments and
writes exactly one newline-terminated JSON readiness message:

```json
{ "status": "ready", "processId": 1234, "runtimeVersion": "11.0.0", "dacPath": "..." }
```

The version above is illustrative; the real value is the executing runtime's
version. `dacPath` identifies the DAC in
`RuntimeEnvironment.GetRuntimeDirectory()`, not a path from dump metadata.
The external collector should verify `processId` against the child it just
spawned and use `Microsoft.Diagnostics.NETCore.Client` **0.2.661903** to collect
a `DumpType.WithHeap` dump of **only that child**. This includes managed heap
data, unlike mini/triage capture, without requiring unrelated native mappings.
The integration harness defaults to this on every host; set
`MEMORYVISUALIZER_FIXTURE_DUMP_TYPE=Full` for explicit extended coverage.
Both modes must satisfy the same complete snapshot assertions.
Keep input open during collection and continuously drain stdout after the
readiness line, as well as stderr. Send a
line, or close input, to release the fixture and exit with code zero. All
explicit GCHandles are freed in a `finally` block. No dump is written by the
fixture itself; integration tests own collection and cleanup.

## Known shape

All named types below are in `MemoryVisualizer.DumpFixture`. Public fields have
explicit names, not F# auto-property backing names. A normal strong GCHandle
retains the single `FixtureRoots` object throughout collection.

| Root field                          | Expected meaning                                                                                                                                                                                                                                                                         |
| ----------------------------------- | ---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| `CycleFirst`, `CycleSecond`         | Two `CycleNode` objects. Each `Next` points to the other; both `Shared` fields point to the same `SharedTarget`.                                                                                                                                                                         |
| `SharedFirst`, `SharedSecond`       | Two field edges to that same single `SharedTarget`.                                                                                                                                                                                                                                      |
| `DuplicateFirst`, `DuplicateSecond` | Instances of two separately emitted types, both named `MemoryVisualizer.DumpFixture.DuplicatePayload`, in dynamic assemblies `MemoryVisualizer.DumpFixture.DynamicFirst` and `MemoryVisualizer.DumpFixture.DynamicSecond`. Their method tables differ despite identical full type names. |
| `NodeArray`                         | `CycleNode[]`: first, second, first, including repeated-target array edges.                                                                                                                                                                                                              |
| `ObjectArray`                       | `object[]`: shared, first, shared, null.                                                                                                                                                                                                                                                 |
| `ValueArray`                        | `int[]`: 11, 22, 33, 44; no object-reference elements.                                                                                                                                                                                                                                   |
| `PinnedBytes`                       | 4,096-byte SOH array held by a pinned GCHandle, filled with `0x2A`. A separate non-inlined, non-optimized `waitForRelease` method holds an interior managed byref to element 17 before readiness and throughout `Console.ReadLine`; it writes `43uy` through the byref after release.    |
| `PohBytes`                          | 4,096-byte array allocated directly on the POH; no pinned GCHandle for this array.                                                                                                                                                                                                       |
| `LargeBytes`                        | 100,000-byte LOH array filled with `0x5A`.                                                                                                                                                                                                                                               |
| `LohSurvivors`                      | Eight 100,000-byte arrays; interleaved discarded allocations encourage free LOH holes after noncompacting full GC. Hole counts and placement are deliberately not guaranteed.                                                                                                            |
| `SensitiveText`                     | Non-interned copy of `MemoryVisualizer_SYNTHETIC_SENSITIVE_MARKER_DO_NOT_EXPORT`; useful for verifying payloads are not exported.                                                                                                                                                        |
| `WeakShort`, `WeakLong`             | Distinct `WeakShortTarget` and `WeakLongTarget` instances, additionally held by `Weak` and `WeakTrackResurrection` GCHandles respectively. Strong fields intentionally keep both referents alive.                                                                                        |
| `DependentKey`, `DependentTable`    | A live `DependentKey` and `ConditionalWeakTable<object, object>`. Dependent handles form `DependentKey` → `DependentBridge` → `DependentLeaf`. The bridge and leaf have no ordinary strong field/GCHandle roots.                                                                         |

Small nodes reside on the SOH. GC runs before readiness; generations, segment
layout, object addresses, runtime-internal objects/handles, and free holes are
runtime-dependent. Assert fixture-specific identities and relationships rather
than total process object/handle counts.
