# Worker protocol v1

This is the local stdio boundary for issue #7, not an analysis, MQL, scene-layout,
storage, or packaging implementation. Start the native worker apphost with the
separate arguments `--protocol`, `--backend=fake`. Other invocation forms retain
help/version/error behavior. The fake backend never opens a dump or writes an
export. The desktop test shell labels it as synthetic.

## Framing and limits

Each frame is one UTF-8 JSON object followed by LF (no BOM, CR, blank lines,
duplicate properties, invalid UTF-8, or trailing JSON). Maximum frame size is
65,536 bytes **excluding** LF, maximum JSON depth is 16, and strings must contain
valid Unicode scalar values. EOF between frames is a disconnect; EOF within a
frame is a protocol failure. Stdout contains protocol frames only. Stderr is
diagnostics only; default diagnostics contain fixed error codes/messages, never
request payloads, dump paths, query text, heap labels, or stderr from dependencies.

At most 8 ordinary requests may be in flight. Control frames (cancel/shutdown)
do not consume request slots. Each result page and scene has at most 128 items.
Progress is coalesced to the latest value per active request and emitted at most
once per 100 ms per request. Terminal responses are never dropped. Writers await
pipe backpressure; they must not build an unbounded output queue. A blocked peer,
startup, and requests have finite deadlines. The main process bounds input/output
and pending requests independently, and terminates only its own child on failure.
No operation is automatically retried.

The preload applies primitive type/length and eight-request admission limits
before invoking Electron IPC; repeated cancellation is coalesced. Because a
sandboxed preload cannot require local modules, this small admission guard is
inline and covered by real Electron boundary tests. Main remains authoritative:
it independently validates every complete request with the shared schema and
checks the originating window, main frame and local URL. Thus renderer arguments
cannot enqueue arbitrary objects or oversized text across the IPC boundary.

## Schema rules

All fields shown below are required. Unknown fields/tags are rejected, not ignored.
There are no optional fields in v1. `null` is allowed only where explicitly listed.
JSON integers are safe, nonnegative integers, with tighter bounds below. Their
wire tokens use only `0` or a nonzero digit followed by digits: `-0`, fractional
and exponent spellings are rejected even if their mathematical value is integral.
String length limits count Unicode scalar values, not UTF-16 code units. Values
that can exceed JavaScript's safe integer range use canonical decimal strings
(`0` or a nonzero digit followed by digits), bounded to uint64 max
`18446744073709551615`. IDs use nonzero decimal strings; addresses use exactly
`0x` and 16 lowercase hex digits. Snapshot IDs are lowercase nonempty UUID strings.
Each connection's request IDs strictly increase (including requests that fail).
An ID is never reused; no unbounded completed-request ID set is necessary.

Handshake is mandatory before requests:

```json
{ "tag": "hello", "versions": [1], "extensions": [] }
```

`versions` is a unique array of 1..8 positive safe integers; `extensions` is a
unique array of at most 8 lowercase dotted names (1..64 characters). V1 supports
no extensions. Unknown requested extensions are fatal `UnsupportedExtension`.
Future extensions must be named and mutually negotiated here; additional fields
or operations require a negotiated extension or a new protocol version.

```json
{"tag":"ready","version":1,"capabilities":{"backend":"fake","operations":["capabilities","snapshot.load","snapshot.dispose","query","scene","details","recipe.validate","export"],"extensions":[],"limits":{"maxFrameBytes":65536,"maxOutstanding":8,"maxPageSize":128,"maxSceneItems":128,"progressIntervalMs":100}}}
{"tag":"fatal","code":"UnsupportedVersion","message":"No supported protocol version."}
```

Ready capabilities contain exactly the operations in that order and these exact
limits. Fatal codes are `UnsupportedVersion`, `UnsupportedExtension`,
`InvalidFrame`, `ProtocolViolation`, `TransportTimeout`, and `InternalError`.
Fatal messages are nonempty, at most 256 characters. A fatal frame is best effort
before exit code 2; a peer not reading cannot prevent process exit.

## Requests and results

```json
{"tag":"request","version":1,"requestId":"1","snapshotId":null,"operation":"snapshot.load","args":{"source":"fixture:tiny","delayMs":0}}
{"tag":"success","version":1,"requestId":"1","snapshotId":"00000000-0000-0000-0000-000000000001","result":{"tag":"snapshot","objectCount":"3"}}
{"tag":"error","version":1,"requestId":"2","snapshotId":null,"error":{"code":"Busy","message":"Request limit reached.","retryable":true}}
{"tag":"progress","version":1,"requestId":"1","snapshotId":null,"phase":"working","completed":1,"total":100}
```

Progress phase is `working`, completed is 0..100, total is exactly 100. Progress
and errors echo the input snapshot scope; a successful load returns its new
snapshot scope. Other successes echo input scope. Errors use the codes
`Busy`, `Cancelled`, `SnapshotNotFound`, `InvalidRequest`, `NotImplemented`,
`InternalError`, `StaleSnapshot`, `WorkerExited`, `Timeout`, `ProtocolError`.
Messages are nonempty, at most 256 characters; retryable is a boolean, a hint
only, never an instruction to automatically retry.

| Operation        | Input scope | Args                                               | Result                                                                        |
| ---------------- | ----------- | -------------------------------------------------- | ----------------------------------------------------------------------------- |
| capabilities     | null        | `{}`                                               | `{"tag":"capabilities","value":CAPABILITIES}`                                 |
| snapshot.load    | null        | `{"source":"fixture:tiny","delayMs":N}`; N=0..5000 | `{"tag":"snapshot","objectCount":"3"}`                                        |
| snapshot.dispose | UUID        | `{}`                                               | `{"tag":"disposed"}`                                                          |
| query            | UUID        | `{"text":TEXT,"pageSize":N,"cursor":CURSOR}`       | PAGE                                                                          |
| details          | UUID        | `{"objectId":ID,"pageSize":N,"cursor":CURSOR}`     | PAGE                                                                          |
| scene            | UUID        | `{"maxItems":N}`                                   | `{"tag":"scene","schemaVersion":1,"items":[ITEM],"truncated":false}`          |
| recipe.validate  | null        | `{"recipe":{"schemaVersion":1,"query":TEXT}}`      | `{"tag":"recipe","schemaVersion":1,"valid":true}`                             |
| export           | UUID        | `{"format":"svg","maxBytes":N}`; N=1..32768        | `{"tag":"export","format":"svg","artifactId":"fixture:svg","byteLength":"0"}` |

TEXT is 1..4096 characters. Page size and scene maximum are 1..128.
CURSOR is null or a canonical uint64 decimal string; it is opaque to callers.
PAGE is `{"tag":"page","items":[ITEM],"nextCursor":CURSOR,"truncated":BOOL}`.
ITEM is `{"objectId":ID,"address":ADDRESS,"size":UINT64}`. No object contents or
arbitrary metadata cross this boundary. Arrays are bounded to 128, regardless
of frame size; the client also verifies results against the requested limit.
Query text is not executed by the fake backend. Scene items are synthetic
records, not a competing geometric layout contract. Recipe and export are
explicit synthetic acknowledgements, not files or real validation/export.
Fake query and scene calls wait a fixed, cancellable 250 ms so admission limits,
cancellation and stale-result races can be reproduced without expensive work.

Only one snapshot is active. A load invalidates the old snapshot when accepted,
cancels older snapshot-scoped requests and any earlier load, releases the old
data, and allocates a deterministic monotonically numbered UUID on success.
Dispose releases data and cancels other work for that snapshot before responding.
The client maintains a generation epoch: a response from an earlier generation
is rejected as `StaleSnapshot` and never published to the renderer. Failed or
cancelled loads leave no active snapshot. A request using an inactive snapshot
receives `SnapshotNotFound`.

## Cancellation and shutdown

```json
{"tag":"cancel","version":1,"requestId":"1"}
{"tag":"shutdown","version":1}
{"tag":"bye","version":1}
```

Cancel is idempotent, emits no independent acknowledgement, and an active target
eventually receives one terminal error `Cancelled` (a previously completed
request keeps its terminal result). Shutdown stops accepting requests, cancels
active work, emits their terminal responses, releases snapshot state, writes
`bye`, flushes, and exits 0. EOF also cancels/releases/exits without an orphan.
The Electron owner closes stdin and, if graceful shutdown does not finish within
2 seconds, kills only the `ChildProcess` it spawned. Startup timeout is 5 seconds,
ordinary request timeout is 10 seconds; timeout fails the connection and explicitly
rejects pending work rather than retrying it. App exit waits for cleanup.

## Shared evidence

`protocol/fixtures.json` holds accepted/rejected incoming and outgoing frames,
including uint64 max, invalid numeric/enum/null shapes, progress, and errors.
Both F# and TypeScript validate it. Executable tests additionally exercise
the shared parameterized `textBoundaryCases` (materializing 4096 and 4097 astral
characters without duplicating kilobytes of fixture text) and raw `invalidJson`
lexical cases. Process tests exercise
fragmentation, byte limits, invalid UTF-8, mid-frame EOF, cancellation, disposal,
bounded pages, backpressure, and process death. Electron tests use the real
sandboxed preload and native worker, not a localhost service.
