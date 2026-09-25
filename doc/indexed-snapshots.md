# Indexed snapshots: visual-first M1 slice

`MemoryVisualizer.Core.IndexedHeapSnapshot` adds read-only selection over the
materialized `HeapSnapshot` produced by the [native adapter](snapshots.md).
This is the **provisional visual-first slice of #10**, enabling future real
memory maps in #12 and desktop import in #13. Managed arrays and indexes are
an implementation choice for this slice, **not** a measured backend winner
or a final ADR. There is no new package, database service, or native dependency.
The adapter, `inspect` CLI, and fixture-only worker/Electron protocol are unchanged.

Full #10 acceptance remains deferred: identical-workload backend comparisons,
license/version evaluation of alternatives, peak/retained-memory benchmarks,
the measured ADR, reverse reference indexes, and bounded graph/root-path
workloads. MQL, scene generation, rendering, and protocol integration are not
part of this store.

The separate [MQL M1 library](mql.md) now consumes this store through bounded
visitor primitives; storage still does not interpret query/presentation syntax.

## Public API

All types below live in `MemoryVisualizer.Core`:

```fsharp
let built =
    IndexedHeapSnapshot.Create(
        snapshot,
        SnapshotIndexLimits.defaults,
        cancellationToken
    )

match built with
| Error error -> // Report the typed error; do not replace the active store.
    Error error
| Ok store ->
    use owned = store
    owned.SelectObjects(
        { ObjectSelection.all with
            Runtime = Some runtimeIdentity
            Range = Some(ObjectRangeSelection.Overlaps addressRange)
            Entries = HeapEntrySelection.Allocated },
        { Offset = 0; Limit = 128 },
        cancellationToken
    )
```

`Create`, `GetInfo`, `TryGetObject`, `TryGetType`, and `SelectObjects` return
`Result<_, SnapshotIndexError>`. Errors are `InvalidInput`, `LimitExceeded`,
or `Disposed`. Cooperative cancellation throws `OperationCanceledException`,
matching the reader's cancellation convention; it is not an empty result.
Creation is synchronous: callers should schedule CPU-bound work away from UI
threads. No native reader is retained.

`TryGetObject(ObjectIdentity, token)` is an **exact starting-address** lookup,
not an interior-pointer lookup. `TryGetType(TypeIdentity, token)` resolves a
runtime-scoped method table, never a display name. A different runtime can
have the same address or method table; distinct types can have identical
names. Foreign snapshot IDs and negative runtime indices are invalid;
unknown identities within this snapshot's scope return `None` or an empty page.
No new dense identity scheme or cross-capture identity is introduced.

`GetInfo(token)` exposes snapshot/target metadata and read-only collections
of runtimes, heaps, types, segments, edges, roots, handles, and diagnostics.
`SnapshotSegmentInfo` has the same segment metadata as `HeapSegment`, but
its generation and allocation-context collections are also read-only.
Object rows are obtained through exact lookup or bounded selection.
Heaps are ordered by runtime index, then heap index.

`VisitObjects(visitor, token)` calls `HeapObject -> HeapType ->
SnapshotSegmentInfo -> bool` once per captured candidate in runtime/address
order, including free entries. `VisitSegments(visitor, token)` calls
`SnapshotSegmentInfo -> bool` in runtime/segment-address order; each visited
segment's generation ranges sort by generation/start/end. A visitor returns
false to stop immediately (no lookahead); the operation returns `Ok false`
if stopped or `Ok true` if every callback returned true. Even returning false
on the last row reports a caller stop, not inferred exhaustion. No filtering,
offset rescans or execution-time sorts hide behind these callbacks.

Visitors run synchronously under the existing store lock with cancellation
checks between calls. Callers must check their own deadline/work budget before
processing each candidate, and bound work inside callbacks. The Query library
does so. Callback exceptions propagate; these APIs are for trusted hosts, not
query-supplied executable code. Dispose waits for visitation as for selection.

## Selection, ranges, and pages

`ObjectSelection.all` includes **all captured objects and free entries**.
Optional `Runtime`, `Type`, `HeapIndex`, `SegmentAddress`, `Generation`,
`HeapKind`, and `Range` restrictions combine with AND. `Entries` is `All`,
`Allocated`, or `Free`. Heap and segment filters apply within each object's
runtime; without a runtime restriction, a heap index/segment address can
match several runtimes. Type identity already includes its runtime;
contradictory type/runtime restrictions match nothing.

Generation compares the captured `HeapObject.Generation`, not guessed range
membership. An unknown generation does not match a specified generation.
LOH, POH, and frozen heaps are kinds, not invented generations 3 or 4.
Heap/kind selection uses the object's explicit segment association; overlapping
metadata ranges do not create extra memberships. Captured free entries retain
their identities, sizes, and type metadata; unexplained gaps never become
invented free objects. `Metadata.ObjectCount` excludes free entries.

Ranges are unsigned **half-open `[Start, End)`**:

- `StartsIn` matches `Start <= object.Address < End`.
- `Overlaps` matches any shared byte between the range and the object's
  address/size interval, including objects starting before `Start`.
- Equal endpoints form a valid empty query; inverted endpoints are invalid.
- Overlapping/nested object intervals are supported, without assuming
  non-overlapping allocations or segment ranges.

Sizes are positive `uint64`; the object's inclusive last byte must fit in
`uint64`. Thus an object can end immediately after `UInt64.MaxValue` without
wrapping arithmetic. `AddressRange.End` cannot represent `2^64`: no half-open
range can select the byte at `UInt64.MaxValue`. Exact lookup and selections
without a range can still return an object starting there. Nothing clamps an
object's size or a range endpoint.

Segment object/committed/reserved/generation/allocation-context ranges must
also be non-inverted; empty ranges remain valid.
Generation indices in nested ranges must be nonnegative, just like captured
object generations; invalid metadata fails indexing rather than wrapping
into unsigned query values. Geometric containment is not
revalidated: these independently captured ranges, especially reserved tails,
are not required to nest, and an object's explicit segment link determines
ownership. An index is not a new claim of physical heap consistency.

Results always sort by **runtime index, then unsigned starting address**,
independent of extraction order or chosen index. `SnapshotPageRequest.Offset`
counts matching entries; `Limit` is the maximum returned entries. Offsets
beyond the end produce an empty final page. No count of all matches is computed.
The store looks ahead for one more match: `IsTruncated=true` and `NextOffset`
are present only when more matching entries exist. An exactly full final page
is not truncated. Request the same selection with `NextOffset` to continue.
Paging offsets belong to the same immutable store and selection, not a new import.

`SnapshotObjectPage.IsPartial` describes **source extraction**, independently
of page truncation. `SnapshotIndexInfo` preserves `IsPartial`, every stage's
completeness, and diagnostics. A usable partial snapshot is indexed without
discarding entries or upgrading its completeness. Structural failures
(duplicates, missing runtime/heap/segment/type associations, invalid object
intervals or metadata ranges, inconsistent captured object count) fail the
entire build, even for partial input. They never silently filter the input.
Captured edges, including duplicates, cycles and unresolved endpoints, remain
one read-only array-backed collection; no adjacency graph is constructed.
Root/handle provenance and conditional/weak-reference meaning are unchanged.

## Ownership, publication, and lifetime

The caller must not mutate the source while `Create` is running. Creation
copies array **containers**, including both nested segment arrays. Immutable
records and strings are shared, not deeply copied or re-interned. After a
successful build the caller may mutate or release every source array without
affecting the store. Public collections are read-only wrappers over private
arrays, not an array disguised as `IReadOnlyList`.

A store is published only after all validation, copying, sorting and index
building succeed. Cancellation or failure yields no store. There is no new
global manager: an owner builds a replacement first, keeps the existing store
on failure, and disposes the old store after a successful swap. The owner must
also suppress stale UI results when switching snapshots.

Operations and disposal are serialized. `Dispose` is idempotent, waits for
an active operation, and clears the store's references to snapshot storage
and indexes. Subsequent operations return `Disposed` (a cancelled token
still throws first). Waiting to enter the operation lock is not interruptible;
active operations themselves check cancellation. Previously returned records,
metadata and pages remain valid immutable managed values; retaining these
values retains their referenced data. Disposal does not force GC, erase
strings, or invalidate externally held results.

## Provisional resource bounds and costs

`SnapshotIndexLimits.defaults` are hard ceilings for this implementation.
Callers may lower each positive limit, but not raise it:

| Bound                                               | Default / ceiling |
| --------------------------------------------------- | ----------------: |
| Captured object entries, **including free entries** |         1,000,000 |
| Types                                               |           100,000 |
| Segments                                            |           100,000 |
| Runtimes                                            |        32 (fixed) |
| Combined auxiliary entries                          |         5,000,000 |
| Returned entries per page                           |             4,096 |

Auxiliary entries count runtimes + heaps + edges + roots + handles +
diagnostics + every nested generation/allocation-context range. All limits
are inclusive: exceeding one fails with `LimitExceeded` before container
copies/index construction. Invalid limit settings and oversized requested
pages fail with `InvalidInput`. Neither build nor queries truncate silently
to fit a resource limit.

There are two `int32` row-index arrays (runtime/address and type/address),
two `uint64` inclusive-last-byte prefix-max arrays for interval selection,
and one reusable temporary `int32` merge-sort buffer. This is **24 bytes of
index payload per object retained plus 4 bytes per object temporary**, not
including array headers, the owned object-reference array, metadata wrappers,
dictionaries, input snapshot, records, strings, or GC overhead. Temporary
validation sets and sort storage are not retained by the published store.

MQL visitation additionally retains an ordered segment-reference array, one
ordered SnapshotSegmentInfo record per segment, and an ordered reference array
plus read-only wrapper for each segment's generation ranges. Original GetInfo
segment/generation collection ordering is preserved independently. On a 64-bit
host the extra array payload is 8 bytes per segment plus 8 bytes per generation
range, **excluding** the added records, array headers and wrappers. Build-time
heap/segment/generation ordering uses two temporary int32 index arrays per sort;
generation sorting is per segment. Heap ordering temporarily retains a second
heap reference array but publishes only the sorted one. These costs are
provisional and not a measured memory budget.

Build cost is `O(N log N)` for sorting, plus linear copying/validation.
The ordered visitors add `O(H log H + S log S + sum(Gs log Gs))` build work for
heaps, segments and per-segment generation ranges, with cancellation in sorting
and copying.
Exact address lookup is binary search; type lookup uses a scoped dictionary.
Address/type range candidates are located by binary search within their
scope; prefix maxima handle overlapping intervals. Heap/kind/generation/free
filters scan candidates. With long overlapping objects, sparse filters, or
large offsets, a query can scan all captured objects, but never more than
the configured object count. Later offset pages rescan earlier matches;
there is no unbounded result allocation, eager total-match materialization,
or hidden scan cutoff claiming complete results.

Counts and index payload sizes are **not measured process-memory or latency
budgets**. Large metadata strings and the already-materialized input add
memory; allocation failure still propagates from the runtime. Cancellation
is checked through copying, validation, merge sorting, prefix building, query
scanning and before publication; individual allocations are not interruptible.
Representative peak-memory/scalability measurements and richer traversal are
deliberately deferred, not implied by these ceilings.
