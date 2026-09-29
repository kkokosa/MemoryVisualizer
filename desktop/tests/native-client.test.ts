import assert from "node:assert/strict";
import childProcess from "node:child_process";
import { EventEmitter } from "node:events";
import { syncBuiltinESMExports } from "node:module";
import { resolve } from "node:path";
import { PassThrough, Writable } from "node:stream";
import test, { type TestContext } from "node:test";
import { FrameDecoder } from "../src/framing.js";
import { NativeClient } from "../src/native-client.js";
import { parseNativeInbound } from "../src/native-protocol.js";
import {
  DEFAULT_SETTINGS,
  NATIVE_LIMITS,
  NATIVE_OPERATIONS,
  type Entity,
  type NativeInbound,
  type NativeRequest,
  type NativeResult,
} from "../src/native-types.js";

const snapshotId = "00000000-0000-0000-0000-000000000001";
const nextSnapshotId = "00000000-0000-0000-0000-000000000002";
const queryId = "3";
const nextQueryId = "4";
const sceneId = "5";
const bounds = { x: 0, y: 0, width: 1024, height: 100 };
const entity: Entity = {
  kind: "Object",
  snapshotId,
  runtime: 0,
  heap: 0,
  segmentAddress: "0x0000000000000000",
  address: "0xffffffffffffffff",
  size: "18446744073709551615",
  heapKind: "Small",
  end: null,
  methodTable: null,
  type: "System.String",
  generation: 0,
  isFree: false,
};
const load = {
  operation: "snapshot.load",
  snapshotId: null,
  args: { path: "C:\\test.dmp", dacPath: null, cachePath: null, allowNetwork: false },
} as const;
const run = {
  operation: "query.run",
  snapshotId,
  args: { text: "MATCH o:Object RETURN o", settings: DEFAULT_SETTINGS },
} as const;
const details = {
  operation: "details",
  snapshotId,
  args: { runtime: 0, address: entity.address, cursor: null, pageSize: 1 },
} as const;

// This is an in-memory native v2 peer, not a reinterpretation of the v1 fixture backend.
class NativePeer extends EventEmitter {
  readonly stdout = new PassThrough();
  readonly stderr = new PassThrough();
  readonly stdin: Writable;
  readonly frames: NativeInbound[] = [];
  readonly active = new Map<string, NativeRequest>();
  readonly kills: string[] = [];
  readonly pid = 123456;
  autoCancel = true;
  autoShutdown = true;
  private closed = false;

  constructor() {
    super();
    const decoder = new FrameDecoder(true);
    this.stdin = new Writable({
      write: (chunk: Buffer, _encoding, done) => {
        for (const value of decoder.push(chunk)) {
          const frame = parseNativeInbound(value);
          this.frames.push(frame);
          if (frame.tag === "request") this.active.set(frame.requestId, frame);
          if (frame.tag === "cancel" && this.autoCancel)
            queueMicrotask(() => {
              const pending = this.active.get(frame.requestId);
              if (pending) this.error(pending, "Cancelled");
            });
          if (frame.tag === "shutdown" && this.autoShutdown)
            queueMicrotask(() => {
              for (const pending of this.active.values()) this.error(pending, "Cancelled");
              this.send({ tag: "bye", version: 2 });
              this.exit(0);
            });
        }
        done();
      },
    });
  }

  send(value: unknown): void {
    this.stdout.write(Buffer.from(`${JSON.stringify(value)}\n`));
  }

  ready(): void {
    this.send({
      tag: "ready",
      version: 2,
      capabilities: { backend: "native", operations: NATIVE_OPERATIONS, limits: NATIVE_LIMITS },
    });
  }

  success(request: NativeRequest | string, result: NativeResult, scope?: string): void {
    const pending = typeof request === "string" ? this.active.get(request)! : request;
    this.active.delete(pending.requestId);
    this.send({
      tag: "success",
      version: 2,
      requestId: pending.requestId,
      snapshotId: scope ?? pending.snapshotId,
      result,
    });
  }

  error(request: NativeRequest | string, code: string): void {
    const pending = typeof request === "string" ? this.active.get(request)! : request;
    this.active.delete(pending.requestId);
    this.send({
      tag: "error",
      version: 2,
      requestId: pending.requestId,
      snapshotId: pending.snapshotId,
      error: { code, message: `Native ${code}.`, retryable: false },
    });
  }

  kill(signal: string): boolean {
    this.kills.push(signal);
    queueMicrotask(() => this.exit(null));
    return true;
  }

  exit(code: number | null): void {
    if (this.closed) return;
    this.closed = true;
    this.stdout.end();
    this.emit("close", code);
  }
}

function setup(t: TestContext, ready = true): { client: NativeClient; peer: NativePeer } {
  const peer = new NativePeer();
  const spawn = t.mock.method(childProcess, "spawn", () => peer);
  syncBuiltinESMExports();
  const client = new NativeClient(resolve("owned-native-worker.exe"));
  const call = spawn.mock.calls[0]!;
  assert.deepEqual(call.arguments.slice(1), [
    ["--protocol", "--backend=native"],
    { shell: false, windowsHide: true, stdio: ["pipe", "pipe", "pipe"] },
  ]);
  assert.deepEqual(peer.frames, [{ tag: "hello", versions: [2], extensions: [] }]);
  t.after(async () => {
    if (!peer.kills.length) await client.close();
    else await client.exited;
    spawn.mock.restore();
    syncBuiltinESMExports();
  });
  if (ready) peer.ready();
  return { client, peer };
}

async function loaded(client: NativeClient, peer: NativePeer): Promise<void> {
  await client.ready;
  const handle = client.request(load);
  peer.success(
    handle.requestId,
    { tag: "snapshot", objectCount: entity.size, sourcePartial: false, diagnosticCount: 0 },
    snapshotId,
  );
  await handle.result;
  assert.equal(client.activeSnapshot, snapshotId);
}

function queryResult(id = queryId, count = 2): NativeResult {
  return {
    tag: "query",
    queryId: id,
    status: "complete",
    sourceAvailable: true,
    sourcePartial: false,
    sourceDiagnosticCount: 0,
    rowCount: count,
    candidates: "2",
    truncationReasons: [],
    diagnostics: [],
    scene: {
      schemaVersion: 1,
      sceneId,
      snapshotId,
      bounds,
      lanes: [{ id: "lane-0", runtime: 0, heap: 0, bounds }],
      theme: { background: "#fff", stroke: "#000", text: "#000" },
      redaction: DEFAULT_SETTINGS.redaction,
      elementCount: 1,
      status: "complete",
      truncationReasons: [],
    },
  };
}

async function queried(client: NativeClient, peer: NativePeer): Promise<void> {
  await loaded(client, peer);
  const handle = client.request(run);
  peer.success(handle.requestId, queryResult());
  await handle.result;
}

test("native owner negotiates v2, bounds admission before writing and closes exactly once", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const pending = Array.from({ length: 8 }, () => client.request(details));
  const before = peer.frames.length;
  assert.throws(() => client.request(details), { code: "Busy" });
  assert.equal(peer.frames.length, before);
  for (const handle of pending)
    peer.success(handle.requestId, { tag: "details", items: [entity], nextCursor: null });
  assert.equal((await Promise.all(pending.map((handle) => handle.result))).length, 8);
  const closing = client.close();
  assert.equal(client.close(), closing);
  await closing;
  assert.equal(peer.frames.filter((frame) => frame.tag === "shutdown").length, 1);
  assert.equal(peer.kills.length, 0);
  assert.equal(client.activeSnapshot, null);
  assert.throws(() => client.request(load), { code: "WorkerExited" });
});

test("failed replacement preserves the previous snapshot, query and exportable scene", async (t) => {
  const { client, peer } = setup(t);
  await queried(client, peer);
  const replacement = client.request(load);
  const rejected = assert.rejects(replacement.result, { code: "ImportFailed" });
  assert.equal(client.activeSnapshot, snapshotId);
  assert.throws(() => client.request(details), { code: "Busy" });
  peer.error(replacement.requestId, "ImportFailed");
  await rejected;
  assert.equal(client.activeSnapshot, snapshotId);
  const page = client.request({
    operation: "query.page",
    snapshotId,
    args: { queryId, cursor: null, pageSize: 1 },
  });
  peer.success(page.requestId, {
    tag: "rows",
    queryId,
    items: [{ statementIndex: 0, entity, values: [] }],
    nextCursor: "1",
  });
  await page.result;
  const exported = client.request({
    operation: "export",
    snapshotId,
    args: { sceneId, path: "C:\\old-scene.svg" },
  });
  peer.success(exported.requestId, { tag: "export", byteLength: "1024" });
  await exported.result;
  const request = client.request(details);
  peer.success(request.requestId, { tag: "details", items: [entity], nextCursor: null });
  await request.result;
});

test("cancelled replacement preserves the committed scene while rejecting pre-load work", async (t) => {
  const { client, peer } = setup(t);
  await queried(client, peer);
  peer.autoCancel = false;
  const oldPage = client.request({
    operation: "query.page",
    snapshotId,
    args: { queryId, cursor: null, pageSize: 1 },
  });
  const stale = assert.rejects(oldPage.result, { code: "StaleSnapshot" });
  const replacement = client.request(load);
  const cancelled = assert.rejects(replacement.result, { code: "Cancelled" });
  client.cancel(replacement.requestId);
  peer.error(replacement.requestId, "Cancelled");
  await cancelled;
  peer.success(oldPage.requestId, {
    tag: "rows",
    queryId,
    items: [{ statementIndex: 0, entity, values: [] }],
    nextCursor: "1",
  });
  await stale;
  assert.equal(client.activeSnapshot, snapshotId);
  const exported = client.request({
    operation: "export",
    snapshotId,
    args: { sceneId, path: "C:\\old-scene.svg" },
  });
  peer.success(exported.requestId, { tag: "export", byteLength: "1024" });
  await exported.result;
});

test("successful replacement invalidates the old committed query and scene", async (t) => {
  const { client, peer } = setup(t);
  await queried(client, peer);
  const replacement = client.request(load);
  peer.success(
    replacement.requestId,
    { tag: "snapshot", objectCount: "0", sourcePartial: false, diagnosticCount: 0 },
    nextSnapshotId,
  );
  await replacement.result;
  assert.equal(client.activeSnapshot, nextSnapshotId);
  assert.throws(
    () =>
      client.request({
        operation: "export",
        snapshotId: nextSnapshotId,
        args: { sceneId, path: "C:\\old-scene.svg" },
      }),
    { code: "StaleQuery" },
  );
  assert.throws(
    () =>
      client.request({
        operation: "query.page",
        snapshotId: nextSnapshotId,
        args: { queryId, cursor: null, pageSize: 1 },
      }),
    { code: "StaleQuery" },
  );
});

test("overlapping snapshot transitions are locally Busy before a committed load becomes stale", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const first = client.request(load);
  const written = peer.frames.length;
  assert.throws(() => client.request(load), { code: "Busy" });
  assert.throws(() => client.request({ operation: "snapshot.dispose", snapshotId, args: {} }), {
    code: "Busy",
  });
  assert.equal(
    peer.frames.length,
    written,
    "No replacement/cancel may be sent before the first terminal.",
  );
  peer.success(
    first.requestId,
    { tag: "snapshot", objectCount: "0", sourcePartial: false, diagnosticCount: 0 },
    nextSnapshotId,
  );
  await first.result;
  assert.equal(client.activeSnapshot, nextSnapshotId);
  const second = client.request(load);
  const busy = assert.rejects(second.result, { code: "Busy" });
  peer.error(second.requestId, "Busy");
  await busy;
  assert.equal(
    client.activeSnapshot,
    nextSnapshotId,
    "A later rejected load cannot restore disposed snapshot metadata.",
  );
  assert.throws(() => client.request(details), { code: "StaleSnapshot" });
  assert.equal(peer.frames.filter((frame) => frame.tag === "cancel").length, 0);
});

test("pending disposal also prevents a new load until the disposal terminal", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const dispose = client.request({ operation: "snapshot.dispose", snapshotId, args: {} });
  const written = peer.frames.length;
  assert.throws(() => client.request(load), { code: "Busy" });
  assert.equal(peer.frames.length, written);
  peer.success(dispose.requestId, { tag: "disposed" });
  await dispose.result;
  assert.equal(client.activeSnapshot, null);
  const loadAgain = client.request(load);
  peer.success(
    loadAgain.requestId,
    { tag: "snapshot", objectCount: "0", sourcePartial: false, diagnosticCount: 0 },
    nextSnapshotId,
  );
  await loadAgain.result;
  assert.equal(client.activeSnapshot, nextSnapshotId);
});

test("query replacement invalidates in-flight pages, progress and old scene/export IDs", async (t) => {
  const { client, peer } = setup(t);
  await queried(client, peer);
  peer.autoCancel = false;
  const page = client.request({
    operation: "query.page",
    snapshotId,
    args: { queryId, cursor: null, pageSize: 1 },
  });
  const stale = assert.rejects(page.result, { code: "StaleQuery" });
  const next = client.request(run);
  const progress: unknown[] = [];
  client.on("progress", (frame) => progress.push(frame));
  peer.send({
    tag: "progress",
    version: 2,
    requestId: page.requestId,
    snapshotId,
    phase: "page",
    completed: "0",
  });
  assert.equal(progress.length, 0);
  assert.throws(
    () => client.request({ operation: "export", snapshotId, args: { sceneId, path: "C:\\x.svg" } }),
    { code: "StaleQuery" },
  );
  peer.success(next.requestId, queryResult(nextQueryId));
  await next.result;
  peer.success(page.requestId, {
    tag: "rows",
    queryId,
    items: [{ statementIndex: 0, entity, values: [] }],
    nextCursor: "1",
  });
  await stale;
  assert.throws(
    () =>
      client.request({
        operation: "query.page",
        snapshotId,
        args: { queryId, cursor: null, pageSize: 1 },
      }),
    { code: "StaleQuery" },
  );
  assert.equal(peer.kills.length, 0);
});

test("replaced query success cannot replace current query metadata", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  peer.autoCancel = false;
  const old = client.request(run);
  const stale = assert.rejects(old.result, { code: "StaleQuery" });
  const current = client.request(run);
  peer.success(current.requestId, queryResult(nextQueryId));
  await current.result;
  peer.success(old.requestId, queryResult(queryId));
  await stale;
  assert.throws(
    () =>
      client.request({
        operation: "query.page",
        snapshotId,
        args: { queryId, cursor: null, pageSize: 1 },
      }),
    { code: "StaleQuery" },
  );
  const page = client.request({
    operation: "query.page",
    snapshotId,
    args: { queryId: nextQueryId, cursor: null, pageSize: 2 },
  });
  peer.success(page.requestId, {
    tag: "rows",
    queryId: nextQueryId,
    items: [
      { statementIndex: 0, entity, values: [] },
      { statementIndex: 0, entity, values: [] },
    ],
    nextCursor: null,
  });
  await page.result;
});

test("native cancellation is coalesced and acknowledged without terminating the child", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const handle = client.request(run);
  const rejected = assert.rejects(handle.result, { code: "Cancelled" });
  for (let i = 0; i < 1000; i++) client.cancel(handle.requestId);
  await rejected;
  client.cancel(handle.requestId);
  assert.equal(peer.frames.filter((frame) => frame.tag === "cancel").length, 1);
  assert.equal(peer.kills.length, 0);
});

for (const operation of ["snapshot.load", "snapshot.dispose", "export"] as const) {
  test(`committed ${operation} success held until after cancellation remains authoritative`, async (t) => {
    t.mock.timers.enable({ apis: ["setTimeout"] });
    const { client, peer } = setup(t);
    await queried(client, peer);
    peer.autoCancel = false;
    const input =
      operation === "snapshot.load"
        ? load
        : operation === "snapshot.dispose"
          ? { operation, snapshotId, args: {} }
          : { operation, snapshotId, args: { sceneId, path: "C:\\committed.svg" } };
    const handle = client.request(input);
    const committedResult: NativeResult =
      operation === "snapshot.load"
        ? { tag: "snapshot", objectCount: "1", sourcePartial: false, diagnosticCount: 0 }
        : operation === "snapshot.dispose"
          ? { tag: "disposed" }
          : { tag: "export", byteLength: "1024" };
    const committedScope = operation === "snapshot.load" ? nextSnapshotId : snapshotId;
    client.cancel(handle.requestId);
    assert.equal(peer.frames.filter((frame) => frame.tag === "cancel").length, 1);
    peer.success(handle.requestId, committedResult, committedScope);
    const result = await handle.result;
    assert.deepEqual(result.result, committedResult);
    assert.equal(
      client.activeSnapshot,
      operation === "snapshot.load"
        ? nextSnapshotId
        : operation === "snapshot.dispose"
          ? null
          : snapshotId,
    );
    t.mock.timers.tick(2000);
    assert.equal(
      peer.kills.length,
      0,
      "The authoritative terminal must clear cancellation escalation.",
    );
    assert.equal(
      peer.frames.filter((frame) => frame.tag === "request").length,
      3,
      "Committed work is not replayed.",
    );
  });
}

test("blocked native cancellation kills only the owned child after a two-second grace", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const { client, peer } = setup(t);
  await loaded(client, peer);
  peer.autoCancel = false;
  const handle = client.request(run);
  const rejected = assert.rejects(handle.result, (error: unknown) => {
    assert.match((error as Error).message, /native call may be blocked/);
    assert.equal((error as { code: string }).code, "Timeout");
    return true;
  });
  client.cancel(handle.requestId);
  t.mock.timers.tick(1999);
  assert.equal(peer.kills.length, 0);
  client.cancel(handle.requestId);
  t.mock.timers.tick(1);
  await rejected;
  await client.exited;
  assert.deepEqual(peer.kills, ["SIGKILL"]);
  assert.equal(client.activeSnapshot, null);
  assert.throws(() => client.request(run), { code: "Timeout" });
});

test("native progress flooding is bounded and cancelled progress cannot leak", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const handle = client.request(run);
  let progress = 0;
  client.on("progress", () => progress++);
  for (let i = 0; i < 100; i++)
    peer.send({
      tag: "progress",
      version: 2,
      requestId: handle.requestId,
      snapshotId,
      phase: "query",
      completed: String(i),
    });
  assert.equal(progress, 1);
  const rejected = assert.rejects(handle.result, { code: "Cancelled" });
  client.cancel(handle.requestId);
  peer.send({
    tag: "progress",
    version: 2,
    requestId: handle.requestId,
    snapshotId,
    phase: "query",
    completed: "101",
  });
  assert.equal(progress, 1);
  await rejected;
});

test("valid paginated rows preserve uint64, requested limits and terminal cursor", async (t) => {
  const { client, peer } = setup(t);
  await queried(client, peer);
  for (const cursor of [null, "1"] as const) {
    const handle = client.request({
      operation: "query.page",
      snapshotId,
      args: { queryId, cursor, pageSize: 1 },
    });
    peer.success(handle.requestId, {
      tag: "rows",
      queryId,
      items: [
        {
          statementIndex: 0,
          entity,
          values: [{ name: "size", value: { kind: "uint64", value: entity.size } }],
        },
      ],
      nextCursor: cursor === null ? "1" : null,
    });
    const result = await handle.result;
    assert.equal(result.result.tag, "rows");
    if (result.result.tag === "rows")
      assert.equal(result.result.items[0]!.entity.address, "0xffffffffffffffff");
  }
  assert.throws(
    () =>
      client.request({
        operation: "query.page",
        snapshotId,
        args: { queryId, cursor: "3", pageSize: 1 },
      }),
    { code: "InvalidRequest" },
  );
});

test("scene pages retain their current scene, lane and fractional geometry", async (t) => {
  const { client, peer } = setup(t);
  await queried(client, peer);
  const handle = client.request({
    operation: "scene.page",
    snapshotId,
    args: { sceneId, cursor: null, pageSize: 1 },
  });
  const geometry = { x: 0.25, y: 1.5, width: 100.5, height: 20 };
  peer.success(handle.requestId, {
    tag: "elements",
    sceneId,
    items: [
      {
        id: "element-0",
        laneId: "lane-0",
        layer: 0,
        bounds: geometry,
        geometry: { kind: "rectangle", bounds: geometry },
        style: { fill: "#fff", stroke: "#000", strokeWidth: 0.5 },
        text: null,
        source: null,
        isClipped: false,
      },
    ],
    nextCursor: null,
  });
  const result = await handle.result;
  assert.equal(result.result.tag, "elements");
  if (result.result.tag === "elements") assert.deepEqual(result.result.items[0]!.bounds, geometry);
});

for (const mismatch of ["scene ID", "lane", "runtime", "address", "query limit"] as const) {
  test(`native owner validates the returned ${mismatch}`, async (t) => {
    const { client, peer } = setup(t);
    await queried(client, peer);
    if (mismatch === "scene ID" || mismatch === "lane") {
      const handle = client.request({
        operation: "scene.page",
        snapshotId,
        args: { sceneId, cursor: null, pageSize: 1 },
      });
      const rejected = assert.rejects(handle.result, { code: "ProtocolError" });
      peer.success(handle.requestId, {
        tag: "elements",
        sceneId: mismatch === "scene ID" ? "999" : sceneId,
        items: [
          {
            id: "element-0",
            laneId: mismatch === "lane" ? "lane-1" : "lane-0",
            layer: 0,
            bounds,
            geometry: { kind: "rectangle", bounds },
            style: { fill: "#fff", stroke: "#000", strokeWidth: 1 },
            text: null,
            source: null,
            isClipped: false,
          },
        ],
        nextCursor: null,
      });
      await rejected;
    } else if (mismatch === "query limit") {
      const handle = client.request({
        ...run,
        args: { ...run.args, settings: { ...DEFAULT_SETTINGS, maxResults: 1 } },
      });
      const rejected = assert.rejects(handle.result, { code: "ProtocolError" });
      peer.success(handle.requestId, queryResult(nextQueryId, 2));
      await rejected;
    } else {
      const handle = client.request(details);
      const rejected = assert.rejects(handle.result, { code: "ProtocolError" });
      peer.success(handle.requestId, {
        tag: "details",
        items: [
          {
            ...entity,
            runtime: mismatch === "runtime" ? 1 : 0,
            address: mismatch === "address" ? "0x0000000000000000" : entity.address,
          },
        ],
        nextCursor: null,
      });
      await rejected;
    }
  });
}

for (const [name, result] of [
  ["wrong query", { tag: "rows", queryId: nextQueryId, items: [], nextCursor: null }],
  [
    "nonadvancing cursor",
    { tag: "rows", queryId, items: [{ statementIndex: 0, entity, values: [] }], nextCursor: "0" },
  ],
  [
    "skipped cursor",
    { tag: "rows", queryId, items: [{ statementIndex: 0, entity, values: [] }], nextCursor: "2" },
  ],
  ["empty continuation", { tag: "rows", queryId, items: [], nextCursor: "1" }],
  ["premature final page", { tag: "rows", queryId, items: [], nextCursor: null }],
  [
    "page exceeds request",
    {
      tag: "rows",
      queryId,
      items: [
        { statementIndex: 0, entity, values: [] },
        { statementIndex: 0, entity, values: [] },
      ],
      nextCursor: null,
    },
  ],
] as const) {
  test(`native owner rejects ${name}`, async (t) => {
    const { client, peer } = setup(t);
    await queried(client, peer);
    const handle = client.request({
      operation: "query.page",
      snapshotId,
      args: { queryId, cursor: null, pageSize: 1 },
    });
    const rejected = assert.rejects(handle.result, { code: "ProtocolError" });
    peer.send({ tag: "success", version: 2, requestId: handle.requestId, snapshotId, result });
    await rejected;
    await client.exited;
    assert.deepEqual(peer.kills, ["SIGKILL"]);
  });
}

test("native owner rejects oversized peer frames and unknown IDs without retry", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const handle = client.request(details);
  const rejected = assert.rejects(handle.result, { code: "ProtocolError" });
  peer.stdout.write(Buffer.alloc(65537, 0x20));
  await rejected;
  await client.exited;
  assert.deepEqual(peer.kills, ["SIGKILL"]);
  assert.equal(peer.frames.filter((frame) => frame.tag === "request").length, 2);
});

for (const name of ["unknown ID", "wrong scope", "wrong tag", "nested wrong scope"] as const) {
  test(`native owner fails closed on ${name}`, async (t) => {
    const { client, peer } = setup(t);
    await loaded(client, peer);
    const handle = client.request(details);
    const rejected = assert.rejects(handle.result, { code: "ProtocolError" });
    peer.send({
      tag: "success",
      version: 2,
      requestId: name === "unknown ID" ? "999" : handle.requestId,
      snapshotId: name === "wrong scope" ? nextSnapshotId : snapshotId,
      result:
        name === "wrong tag"
          ? { tag: "disposed" }
          : {
              tag: "details",
              items: [
                {
                  ...entity,
                  snapshotId: name === "nested wrong scope" ? nextSnapshotId : snapshotId,
                },
              ],
              nextCursor: null,
            },
    });
    await rejected;
  });
}

test("native request validation precedes writes and caller mutation cannot change pending scope", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const before = peer.frames.length;
  assert.throws(() => client.request({ ...run, args: { ...run.args, text: "x".repeat(16385) } }), {
    code: "ProtocolViolation",
  });
  assert.equal(peer.frames.length, before);
  const input = structuredClone(details);
  const handle = client.request(input);
  Object.assign(input.args, { address: "0x0000000000000000" });
  peer.success(handle.requestId, { tag: "details", items: [entity], nextCursor: null });
  await handle.result;
});

test("unexpected exit rejects all work, clears snapshot and never respawns", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const handles = Array.from({ length: 3 }, () => client.request(details));
  const rejections = handles.map((handle) =>
    assert.rejects(handle.result, { code: "WorkerExited" }),
  );
  peer.exit(2);
  await Promise.all(rejections);
  assert.equal(client.activeSnapshot, null);
  assert.throws(() => client.request(load), { code: "WorkerExited" });
});

test("native startup and shutdown have finite deadlines", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const { client, peer } = setup(t, false);
  const rejected = assert.rejects(client.ready, { code: "Timeout" });
  t.mock.timers.tick(9999);
  assert.equal(peer.kills.length, 0);
  t.mock.timers.tick(1);
  await rejected;
  await client.close();
});

test("native shutdown kills an unresponsive peer after two seconds", async (t) => {
  t.mock.timers.enable({ apis: ["setTimeout"] });
  const { client, peer } = setup(t);
  await client.ready;
  peer.autoShutdown = false;
  const closing = client.close();
  t.mock.timers.tick(1999);
  assert.equal(peer.kills.length, 0);
  t.mock.timers.tick(1);
  await closing;
  assert.deepEqual(peer.kills, ["SIGKILL"]);
});

test("relative native executable paths are rejected before spawn", () => {
  assert.throws(() => new NativeClient("worker.exe"), { code: "InvalidRequest" });
});

test("native spawn failure rejects startup with an actionable error", async (t) => {
  const { client, peer } = setup(t, false);
  const rejected = assert.rejects(client.ready, (error: unknown) => {
    assert.equal((error as { code: string }).code, "WorkerExited");
    assert.match((error as Error).message, /executable and runtime/);
    return true;
  });
  peer.emit("error", new Error("ENOENT"));
  await rejected;
  await client.close();
});

test("a fake backend cannot negotiate the native connection", async (t) => {
  const { client, peer } = setup(t, false);
  const rejected = assert.rejects(client.ready, { code: "ProtocolError" });
  peer.send({
    tag: "ready",
    version: 2,
    capabilities: { backend: "fake", operations: NATIVE_OPERATIONS, limits: NATIVE_LIMITS },
  });
  await rejected;
  assert.deepEqual(peer.kills, ["SIGKILL"]);
});

test("closing during native startup rejects ready and terminates only its child", async (t) => {
  const { client, peer } = setup(t, false);
  const rejected = assert.rejects(client.ready, { code: "WorkerExited" });
  await client.close();
  await rejected;
  assert.deepEqual(peer.kills, ["SIGKILL"]);
});

test("native disposal invalidates scoped work and releases the snapshot", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const pending = client.request(details);
  const stale = assert.rejects(pending.result, { code: "StaleSnapshot" });
  const dispose = client.request({ operation: "snapshot.dispose", snapshotId, args: {} });
  peer.success(dispose.requestId, { tag: "disposed" });
  await dispose.result;
  await stale;
  assert.equal(client.activeSnapshot, null);
  assert.throws(() => client.request(details), { code: "StaleSnapshot" });
});

test("a truncated output frame fails the native connection before exit", async (t) => {
  const { client, peer } = setup(t);
  await loaded(client, peer);
  const pending = client.request(details);
  const rejected = assert.rejects(pending.result, { code: "ProtocolError" });
  peer.stdout.end('{"tag":');
  await rejected;
  await client.exited;
  assert.deepEqual(peer.kills, ["SIGKILL"]);
});

for (const [operation, deadline] of [
  ["import", 120_000],
  ["query", 15_000],
  ["page", 10_000],
  ["export", 15_000],
] as const) {
  test(`native ${operation} deadline is ${deadline}ms`, async (t) => {
    t.mock.timers.enable({ apis: ["setTimeout"] });
    const { client, peer } = setup(t);
    await queried(client, peer);
    const input =
      operation === "import"
        ? load
        : operation === "query"
          ? run
          : operation === "page"
            ? details
            : { operation: "export" as const, snapshotId, args: { sceneId, path: "C:\\x.svg" } };
    const handle = client.request(input);
    const rejected = assert.rejects(handle.result, { code: "Timeout" });
    t.mock.timers.tick(deadline - 1);
    assert.equal(peer.kills.length, 0);
    t.mock.timers.tick(1);
    await rejected;
    await client.exited;
    assert.deepEqual(peer.kills, ["SIGKILL"]);
  });
}
