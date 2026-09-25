import assert from "node:assert/strict";
import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { resolve } from "node:path";
import test from "node:test";
import { FrameDecoder } from "../src/framing.js";
import { LIMITS, parseOutbound, type Outbound, type Success } from "../src/protocol.js";
import { WorkerClient } from "../src/worker-client.js";
import { workerPath } from "../src/worker-path.js";

const hello = { tag: "hello", versions: [1], extensions: [] };
function request(
  requestId: string,
  operation: string,
  args: unknown,
  snapshotId: string | null = null,
): object {
  return { tag: "request", version: 1, requestId, snapshotId, operation, args };
}

class Peer {
  child: ChildProcessWithoutNullStreams;
  private frames: Outbound[] = [];
  private waiters: ((value: Outbound) => void)[] = [];
  readonly exit: Promise<number | null>;
  constructor() {
    this.child = spawn(workerPath(), ["--protocol", "--backend=fake"], { shell: false });
    const decoder = new FrameDecoder();
    this.child.stdout.on("data", (chunk: Buffer) => {
      for (const item of decoder.push(chunk)) {
        const frame = parseOutbound(item);
        const waiter = this.waiters.shift();
        if (waiter) waiter(frame);
        else {
          assert.ok(this.frames.length < 128, "test peer received unbounded messages");
          this.frames.push(frame);
        }
      }
    });
    this.child.stderr.resume();
    this.child.stdin.on("error", () => {});
    this.exit = new Promise((done) => this.child.on("close", done));
  }
  send(value: object): void {
    this.child.stdin.write(`${JSON.stringify(value)}\n`);
  }
  next(): Promise<Outbound> {
    const frame = this.frames.shift();
    if (frame) return Promise.resolve(frame);
    return new Promise((done, reject) => {
      const timer = setTimeout(() => reject(new Error("Frame deadline elapsed.")), 5000);
      this.waiters.push((value) => {
        clearTimeout(timer);
        done(value);
      });
    });
  }
  async terminal(): Promise<Outbound> {
    let frame = await this.next();
    while (frame.tag === "progress") frame = await this.next();
    return frame;
  }
  async close(): Promise<void> {
    this.child.kill("SIGKILL");
    await this.exit;
  }
}

test(
  "native worker negotiates fragmented UTF8 input and exact frame size",
  { timeout: 10_000 },
  async () => {
    const peer = new Peer();
    try {
      const text = JSON.stringify(hello);
      const frame = Buffer.from(
        text + " ".repeat(LIMITS.maxFrameBytes - Buffer.byteLength(text)) + "\n",
      );
      for (let offset = 0; offset < frame.length; offset += 137) {
        peer.child.stdin.write(frame.subarray(offset, offset + 137));
      }
      const ready = await peer.next();
      assert.equal(ready.tag, "ready");
      if (ready.tag === "ready") assert.deepEqual(ready.capabilities.limits, LIMITS);
      peer.send(request("1", "recipe.validate", { recipe: { schemaVersion: 1, query: "é" } }));
      assert.equal((await peer.terminal()).tag, "success");
      peer.send({ tag: "shutdown", version: 1 });
      assert.equal((await peer.next()).tag, "bye");
      assert.equal(await peer.exit, 0);
    } finally {
      await peer.close();
    }
  },
);

for (const [name, bytes] of [
  ["malformed JSON", Buffer.from("{]\n")],
  ["invalid UTF8", Buffer.from([0xc0, 0xaf, 0x0a])],
  ["oversized frame", Buffer.alloc(LIMITS.maxFrameBytes + 1, 0x20)],
  ["duplicate key", Buffer.from('{"tag":"hello","tag":"hello","versions":[1],"extensions":[]}\n')],
  ["blank frame", Buffer.from("\n")],
  ["CRLF", Buffer.from(JSON.stringify(hello) + "\r\n")],
  ["mid-frame EOF", Buffer.from('{"tag":"hello"')],
] as const) {
  test(`native worker rejects ${name} without hanging`, { timeout: 10_000 }, async () => {
    const peer = new Peer();
    try {
      peer.child.stdin.end(bytes);
      assert.equal((await peer.next()).tag, "fatal");
      assert.equal(await peer.exit, 2);
    } finally {
      await peer.close();
    }
  });
}

test(
  "unsupported version and extension fail explicitly before requests",
  { timeout: 15_000 },
  async () => {
    for (const [versions, extensions, code] of [
      [[2], [], "UnsupportedVersion"],
      [[1], ["test.extension"], "UnsupportedExtension"],
    ] as const) {
      const peer = new Peer();
      try {
        peer.send({ tag: "hello", versions, extensions });
        const fatal = await peer.next();
        assert.equal(fatal.tag, "fatal");
        if (fatal.tag === "fatal") assert.equal(fatal.code, code);
        assert.equal(await peer.exit, 2);
      } finally {
        await peer.close();
      }
    }
  },
);

test("native worker bounds admission, pages and IDs", { timeout: 15_000 }, async () => {
  const peer = new Peer();
  try {
    peer.send(hello);
    await peer.next();
    peer.send(request("1", "snapshot.load", { source: "fixture:tiny", delayMs: 0 }));
    const loaded = await peer.terminal();
    assert.equal(loaded.tag, "success");
    const snapshot = (loaded as Success).snapshotId;
    const requests = Array.from({ length: 9 }, (_, i) =>
      request(String(i + 2), "query", { text: "fixture", pageSize: 128, cursor: null }, snapshot),
    );
    peer.child.stdin.write(requests.map((item) => JSON.stringify(item)).join("\n") + "\n");
    let busy = 0;
    let success = 0;
    for (let i = 0; i < 9; i++) {
      const response = await peer.terminal();
      if (response.tag === "error" && response.error.code === "Busy") busy++;
      if (response.tag === "success") {
        assert.equal(response.result.tag, "page");
        if (response.result.tag === "page") assert.ok(response.result.items.length <= 128);
        success++;
      }
    }
    assert.equal(busy, 1);
    assert.equal(success, 8);
    peer.send(request("10", "capabilities", {}));
    const duplicate = await peer.terminal();
    assert.equal(duplicate.tag, "fatal");
    assert.equal(await peer.exit, 2);
  } finally {
    await peer.close();
  }
});

test(
  "client lifecycle, lossless pages, coalesced progress and disposal",
  { timeout: 20_000 },
  async () => {
    const client = new WorkerClient(workerPath());
    try {
      await client.ready;
      const times: number[] = [];
      client.on("progress", () => times.push(performance.now()));
      const slow = client.request({
        operation: "snapshot.load",
        snapshotId: null,
        args: { source: "fixture:tiny", delayMs: 1000 },
      });
      const rejected = assert.rejects(slow.result, { code: "Cancelled" });
      await new Promise((done) => setTimeout(done, 350));
      for (let i = 0; i < 1000; i++) client.cancel(slow.requestId);
      await rejected;
      assert.ok(times.length >= 1 && times.length <= 4);
      for (let i = 1; i < times.length; i++) assert.ok(times[i]! - times[i - 1]! >= 99);
      const loaded = await client.request({
        operation: "snapshot.load",
        snapshotId: null,
        args: { source: "fixture:tiny", delayMs: 0 },
      }).result;
      const snapshotId = loaded.snapshotId!;
      const first = await client.request({
        operation: "query",
        snapshotId,
        args: { text: "fixture", pageSize: 1, cursor: null },
      }).result;
      assert.equal(first.result.tag, "page");
      if (first.result.tag !== "page") throw new Error("Expected page");
      assert.equal(first.result.items.length, 1);
      assert.notEqual(first.result.nextCursor, null);
      const full = await client.request({
        operation: "query",
        snapshotId,
        args: { text: "fixture", pageSize: 128, cursor: null },
      }).result;
      if (full.result.tag !== "page") throw new Error("Expected page");
      assert.ok(full.result.items.some((item) => item.address === "0xffffffffffffffff"));
      assert.ok(full.result.items.some((item) => item.size === "18446744073709551615"));
      const oldQuery = client.request({
        operation: "query",
        snapshotId,
        args: { text: "fixture", pageSize: 128, cursor: null },
      });
      const stale = assert.rejects(oldQuery.result, { code: "StaleSnapshot" });
      await client.request({ operation: "snapshot.dispose", snapshotId, args: {} }).result;
      await stale;
      assert.equal(client.activeSnapshot, null);
      assert.throws(
        () => client.request({ operation: "scene", snapshotId, args: { maxItems: 1 } }),
        { code: "StaleSnapshot" },
      );
    } finally {
      const pid = client.child.pid;
      await client.close();
      if (pid) assert.throws(() => process.kill(pid, 0), /ESRCH/);
    }
  },
);

test(
  "replacement suppresses stale load and cancellation leaves no snapshot",
  { timeout: 10_000 },
  async () => {
    const client = new WorkerClient(workerPath());
    try {
      await client.ready;
      const first = client.request({
        operation: "snapshot.load",
        snapshotId: null,
        args: { source: "fixture:tiny", delayMs: 1000 },
      });
      const stale = assert.rejects(first.result, { code: "StaleSnapshot" });
      const second = client.request({
        operation: "snapshot.load",
        snapshotId: null,
        args: { source: "fixture:tiny", delayMs: 0 },
      });
      await second.result;
      await stale;
      assert.ok(client.activeSnapshot);
    } finally {
      await client.close();
    }
  },
);

test(
  "unexpected exit rejects active work and spawn failure is actionable",
  { timeout: 10_000 },
  async () => {
    const client = new WorkerClient(workerPath());
    await client.ready;
    const load = client.request({
      operation: "snapshot.load",
      snapshotId: null,
      args: { source: "fixture:tiny", delayMs: 5000 },
    });
    const failed = assert.rejects(load.result, { code: "WorkerExited" });
    client.child.kill("SIGKILL");
    await failed;
    await client.close();
    const missing = new WorkerClient(resolve("missing-worker-executable"));
    await assert.rejects(missing.ready, { code: "WorkerExited" });
    await missing.close();
  },
);

test("EOF cancels active work and owned workers exit", { timeout: 10_000 }, async () => {
  const peer = new Peer();
  try {
    peer.send(hello);
    await peer.next();
    peer.send(request("1", "snapshot.load", { source: "fixture:tiny", delayMs: 5000 }));
    peer.child.stdin.end();
    assert.equal(await peer.exit, 0);
  } finally {
    await peer.close();
  }
});

test(
  "client's ninth outstanding request is rejected before writing",
  { timeout: 10_000 },
  async () => {
    const client = new WorkerClient(workerPath());
    try {
      await client.ready;
      const loaded = await client.request({
        operation: "snapshot.load",
        snapshotId: null,
        args: { source: "fixture:tiny", delayMs: 0 },
      }).result;
      const input = {
        operation: "query" as const,
        snapshotId: loaded.snapshotId!,
        args: { text: "synthetic", pageSize: 128, cursor: null },
      };
      const pending = Array.from({ length: 8 }, () => client.request(input).result);
      assert.throws(() => client.request(input), { code: "Busy" });
      await Promise.all(pending);
    } finally {
      await client.close();
    }
  },
);

test("orderly close cancels active work without an orphan", { timeout: 10_000 }, async () => {
  const client = new WorkerClient(workerPath());
  await client.ready;
  const handle = client.request({
    operation: "snapshot.load",
    snapshotId: null,
    args: { source: "fixture:tiny", delayMs: 5000 },
  });
  const cancelled = assert.rejects(handle.result, { code: "Cancelled" });
  await client.close();
  await cancelled;
  const pid = client.child.pid!;
  assert.throws(() => process.kill(pid, 0), /ESRCH/);
});

test("raw disposal releases state and terminates scoped work", { timeout: 10_000 }, async () => {
  const peer = new Peer();
  try {
    peer.send(hello);
    await peer.next();
    peer.send(request("1", "snapshot.load", { source: "fixture:tiny", delayMs: 0 }));
    const loaded = await peer.terminal();
    if (loaded.tag !== "success") throw new Error("Expected snapshot");
    const id = loaded.snapshotId;
    peer.send(request("2", "query", { text: "fixture", pageSize: 1, cursor: null }, id));
    peer.send(request("3", "snapshot.dispose", {}, id));
    const responses = [await peer.terminal(), await peer.terminal()];
    assert.ok(
      responses.some((response) => response.tag === "error" && response.error.code === "Cancelled"),
    );
    assert.ok(
      responses.some(
        (response) => response.tag === "success" && response.result.tag === "disposed",
      ),
    );
    peer.send(request("4", "scene", { maxItems: 1 }, id));
    const gone = await peer.terminal();
    assert.equal(gone.tag, "error");
    if (gone.tag === "error") assert.equal(gone.error.code, "SnapshotNotFound");
    peer.send(request("18446744073709551615", "capabilities", {}));
    const max = await peer.terminal();
    assert.equal(max.tag, "success");
    if (max.tag === "success") assert.equal(max.requestId, "18446744073709551615");
    peer.send({ tag: "shutdown", version: 1 });
    assert.equal((await peer.next()).tag, "bye");
    assert.equal(await peer.exit, 0);
  } finally {
    await peer.close();
  }
});

test("nonreading peer triggers finite backpressure failure", { timeout: 15_000 }, async () => {
  const child = spawn(workerPath(), ["--protocol", "--backend=fake"], { shell: false });
  child.stderr.resume();
  child.stdout.pause();
  child.stdin.on("error", () => {});
  const exit = new Promise<number | null>((done) => child.on("close", done));
  const timer = setTimeout(() => child.kill("SIGKILL"), 10_000);
  try {
    // Bounded test payload exceeds pipe capacity; the worker must not queue it all.
    const batch = Array.from({ length: 10_000 }, (_, index) =>
      JSON.stringify(request(String(index + 1), "capabilities", {})),
    );
    child.stdin.end([JSON.stringify(hello), ...batch, ""].join("\n"));
    // close waits for stdout drainage; exit observes process death independently.
    const code = await new Promise<number | null>((done) => child.on("exit", done));
    assert.equal(
      code,
      2,
      "Worker should fail its stalled write rather than be killed by the test.",
    );
    child.stdout.resume();
    await exit;
  } finally {
    clearTimeout(timer);
    child.kill("SIGKILL");
    child.stdout.resume();
    await exit;
  }
});
