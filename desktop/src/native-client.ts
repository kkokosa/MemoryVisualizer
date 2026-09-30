import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { EventEmitter } from "node:events";
import { isAbsolute } from "node:path";
import { FrameDecoder, encodeFrame } from "./framing.js";
import { parseNativeInbound, parseNativeOutbound } from "./native-protocol.js";
import {
  NATIVE_LIMITS,
  NATIVE_VERSION,
  type NativeCapabilities,
  type NativeInbound,
  type NativeOperation,
  type NativeOutbound,
  type NativeRequest,
  type NativeSuccess,
} from "./native-types.js";
import { ProtocolError } from "./protocol.js";

export type NativeRequestHandle = { requestId: string; result: Promise<NativeSuccess> };
type Pending = {
  request: NativeRequest;
  epoch: number;
  queryEpoch: number;
  resolve: (value: NativeSuccess) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
  cancellation?: NodeJS.Timeout;
  progressAt: number;
};
type CurrentQuery = {
  id: string;
  rowCount: number;
  sceneId: string | null;
  elementCount: number;
  lanes: Set<string>;
};

const DEADLINES = {
  "snapshot.load": 120_000,
  "snapshot.dispose": 10_000,
  "query.run": 15_000,
  "query.page": 10_000,
  "scene.page": 10_000,
  details: 10_000,
  export: 15_000,
} as const;
const STARTUP_MS = 10_000;
const SHUTDOWN_MS = 2_000;
const CANCEL_MS = 2_000;

function queryScoped(operation: NativeOperation["operation"]): boolean {
  return (
    operation === "query.run" ||
    operation === "query.page" ||
    operation === "scene.page" ||
    operation === "export"
  );
}

// One owner, one connection, no process discovery and no automatic retries.
export class NativeClient extends EventEmitter {
  readonly child: ChildProcessWithoutNullStreams;
  readonly ready: Promise<NativeCapabilities>;
  readonly exited: Promise<void>;
  private readyResolve!: (value: NativeCapabilities) => void;
  private readyReject!: (error: Error) => void;
  private state: "starting" | "ready" | "closing" | "closed" = "starting";
  private readonly pending = new Map<string, Pending>();
  private readonly decoder = new FrameDecoder(true);
  private counter = 0n;
  private epoch = 0;
  private queryEpoch = 0;
  private snapshotId: string | null = null;
  private query: CurrentQuery | null = null;
  private readonly startup: NodeJS.Timeout;
  private closing: Promise<void> | undefined;
  private failure: ProtocolError | undefined;
  private receivedBye = false;

  constructor(executable: string) {
    super();
    if (!isAbsolute(executable))
      throw new ProtocolError("InvalidRequest", "Worker executable path must be absolute.");
    this.ready = new Promise((resolve, reject) => {
      this.readyResolve = resolve;
      this.readyReject = reject;
    });
    void this.ready.catch(() => {});
    this.child = spawn(executable, ["--protocol", "--backend=native"], {
      shell: false,
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });
    this.startup = setTimeout(
      () =>
        this.fail(
          "Timeout",
          "Native worker startup timed out. Check the executable and runtime installation.",
        ),
      STARTUP_MS,
    );
    this.child.stderr.on("data", () => {});
    this.child.on("error", () =>
      this.fail("WorkerExited", "Native worker could not start. Check its executable and runtime."),
    );
    this.child.stdin.on("error", () =>
      this.fail("WorkerExited", "Native worker input pipe closed unexpectedly."),
    );
    this.child.stdout.on("error", () =>
      this.fail("WorkerExited", "Native worker output pipe closed unexpectedly."),
    );
    this.child.stdout.on("data", (chunk: Buffer) => {
      try {
        for (const frame of this.decoder.push(chunk)) this.receive(parseNativeOutbound(frame));
      } catch {
        this.fail("ProtocolError", "Native worker sent an invalid protocol frame.");
      }
    });
    this.child.stdout.on("end", () => {
      try {
        this.decoder.end();
      } catch {
        this.fail("ProtocolError", "Native worker exited within a protocol frame.");
      }
    });
    this.exited = new Promise((resolve) => {
      this.child.on("close", (code) => {
        clearTimeout(this.startup);
        if (this.state !== "closing" || !this.receivedBye || code !== 0) {
          this.fail("WorkerExited", "Native worker exited unexpectedly; no work was retried.");
        } else if (this.pending.size !== 0) {
          this.fail("ProtocolError", "Native worker exited with unfinished requests.");
        }
        this.state = "closed";
        this.snapshotId = null;
        this.query = null;
        this.emit("closed", this.failure);
        resolve();
      });
    });
    this.write({ tag: "hello", versions: [NATIVE_VERSION], extensions: [] });
  }

  get activeSnapshot(): string | null {
    return this.snapshotId;
  }

  private write(message: NativeInbound, encoded?: Uint8Array): void {
    if (this.state === "closed" || this.failure) return;
    try {
      const bytes = encoded ?? encodeFrame(message, parseNativeInbound);
      this.child.stdin.write(bytes, (error) => {
        if (error) this.fail("WorkerExited", "Writing to native worker failed.");
      });
    } catch {
      this.fail("WorkerExited", "Writing to native worker failed.");
    }
  }

  request(input: NativeOperation): NativeRequestHandle {
    if (this.state !== "ready" || this.failure)
      throw this.failure ?? new ProtocolError("WorkerExited", "Native worker is not ready.");
    if (this.pending.size >= NATIVE_LIMITS.maxOutstanding)
      throw new ProtocolError("Busy", "Request limit reached.");
    const requestId = (this.counter + 1n).toString();
    // Validate and detach caller-owned objects before changing any state or writing.
    const checked = parseNativeInbound({
      tag: "request",
      version: NATIVE_VERSION,
      requestId,
      operation: input.operation,
      snapshotId: input.snapshotId,
      args: input.args,
    });
    if (checked.tag !== "request")
      throw new ProtocolError("InvalidRequest", "Invalid native operation.");
    const request = structuredClone(checked);
    const encoded = encodeFrame(request, parseNativeInbound);
    if (request.snapshotId !== null && request.snapshotId !== this.snapshotId)
      throw new ProtocolError("StaleSnapshot", "The snapshot is no longer active.");
    const transition =
      request.operation === "snapshot.load" || request.operation === "snapshot.dispose";
    if (
      [...this.pending.values()].some(
        (value) =>
          value.request.operation === "snapshot.load" ||
          value.request.operation === "snapshot.dispose",
      )
    ) {
      throw new ProtocolError("Busy", "A snapshot change is in progress.");
    }
    if (request.operation === "query.page" && request.args.queryId !== this.query?.id)
      throw new ProtocolError("StaleQuery", "The query is no longer current.");
    if (
      (request.operation === "scene.page" || request.operation === "export") &&
      request.args.sceneId !== this.query?.sceneId
    )
      throw new ProtocolError("StaleQuery", "The scene is no longer current.");
    if (request.operation === "query.page" || request.operation === "scene.page") {
      const total =
        request.operation === "query.page" ? this.query!.rowCount : this.query!.elementCount;
      if (BigInt(request.args.cursor ?? "0") > BigInt(total))
        throw new ProtocolError("InvalidRequest", "Page cursor is outside the current result.");
    }
    this.counter++;
    if (transition) {
      this.epoch++;
      this.queryEpoch++;
      // Imports invalidate in-flight work, but retain committed IDs until a successful load.
      if (request.operation === "snapshot.dispose") this.query = null;
      for (const id of this.pending.keys()) this.cancel(id);
    } else if (request.operation === "query.run") {
      this.queryEpoch++;
      this.query = null;
      for (const [id, pending] of this.pending)
        if (queryScoped(pending.request.operation)) this.cancel(id);
    }
    const result = new Promise<NativeSuccess>((resolve, reject) => {
      const timer = setTimeout(
        () =>
          this.fail(
            "Timeout",
            "Native worker request timed out; its owned process was terminated and no work was retried.",
          ),
        DEADLINES[request.operation],
      );
      this.pending.set(requestId, {
        request,
        epoch: this.epoch,
        queryEpoch: this.queryEpoch,
        resolve,
        reject,
        timer,
        progressAt: -Infinity,
      });
    });
    this.write(request, encoded);
    return { requestId, result };
  }

  cancel(requestId: string): void {
    const pending = this.pending.get(requestId);
    if (!pending || pending.cancellation || this.failure || this.state !== "ready") return;
    pending.cancellation = setTimeout(() => {
      this.fail(
        "Timeout",
        "Native cancellation did not complete within 2 seconds. The owned worker was terminated because a native call may be blocked; no work was retried.",
      );
    }, CANCEL_MS);
    this.write({ tag: "cancel", version: NATIVE_VERSION, requestId });
  }

  private stale(pending: Pending): string | null {
    if (pending.epoch !== this.epoch) return "StaleSnapshot";
    if (queryScoped(pending.request.operation) && pending.queryEpoch !== this.queryEpoch)
      return "StaleQuery";
    return null;
  }

  private receive(message: NativeOutbound): void {
    if (this.failure) return;
    if (message.tag === "fatal") {
      this.fail("ProtocolError", `Native worker protocol failure: ${message.code}.`);
      return;
    }
    if (message.tag === "ready" && this.state === "starting") {
      clearTimeout(this.startup);
      this.state = "ready";
      this.readyResolve(message.capabilities);
      return;
    }
    if (
      message.tag === "bye" &&
      this.state === "closing" &&
      !this.receivedBye &&
      this.pending.size === 0
    ) {
      this.receivedBye = true;
      this.child.stdin.end();
      return;
    }
    if (
      this.state === "starting" ||
      this.receivedBye ||
      (message.tag !== "success" && message.tag !== "error" && message.tag !== "progress")
    ) {
      this.fail("ProtocolError", "Unexpected native worker message.");
      return;
    }
    const pending = this.pending.get(message.requestId);
    if (!pending) {
      this.fail("ProtocolError", "Native worker used an unknown request ID.");
      return;
    }
    if (
      !(message.tag === "success" && pending.request.operation === "snapshot.load") &&
      message.snapshotId !== pending.request.snapshotId
    ) {
      this.fail("ProtocolError", "Native worker returned an invalid snapshot scope.");
      return;
    }
    const stale = this.stale(pending);
    if (message.tag === "progress") {
      const now = performance.now();
      if (
        !stale &&
        !pending.cancellation &&
        now - pending.progressAt >= NATIVE_LIMITS.progressIntervalMs
      ) {
        pending.progressAt = now;
        this.emit("progress", message);
      }
      return;
    }
    if (message.tag === "success" && !this.matches(pending.request, message, stale !== null)) {
      this.fail("ProtocolError", "Native worker result did not match its request or page bounds.");
      return;
    }
    clearTimeout(pending.timer);
    clearTimeout(pending.cancellation);
    this.pending.delete(message.requestId);
    if (stale) {
      pending.reject(
        new ProtocolError(stale, "The snapshot or query changed before the result arrived."),
      );
      return;
    }
    if (message.tag === "error") {
      pending.reject(new ProtocolError(message.error.code, message.error.message));
      return;
    }
    // Cancellation can arrive after native commit; terminal success remains authoritative.
    if (pending.request.operation === "snapshot.load") {
      this.snapshotId = message.snapshotId;
      this.query = null;
    }
    if (pending.request.operation === "snapshot.dispose") this.snapshotId = null;
    if (message.result.tag === "query") {
      const result = message.result;
      this.query = {
        id: result.queryId,
        rowCount: result.rowCount,
        sceneId: result.scene?.sceneId ?? null,
        elementCount: result.scene?.elementCount ?? 0,
        lanes: new Set(result.scene?.lanes.map((lane) => lane.id) ?? []),
      };
    }
    pending.resolve(message);
  }

  private matches(request: NativeRequest, response: NativeSuccess, stale: boolean): boolean {
    const result = response.result;
    switch (request.operation) {
      case "snapshot.load":
        return result.tag === "snapshot" && response.snapshotId !== null;
      case "snapshot.dispose":
        return result.tag === "disposed";
      case "query.run":
        return (
          result.tag === "query" &&
          result.rowCount <= request.args.settings.maxResults &&
          (result.scene === null ||
            (result.scene.elementCount <= request.args.settings.maxElements &&
              result.scene.layout === request.args.settings.layout &&
              (
                Object.keys(
                  request.args.settings.redaction,
                ) as (keyof typeof request.args.settings.redaction)[]
              ).every(
                (key) => result.scene!.redaction[key] === request.args.settings.redaction[key],
              )))
        );
      case "query.page":
        return (
          result.tag === "rows" &&
          result.queryId === request.args.queryId &&
          this.page(request.args, result, stale ? undefined : this.query?.rowCount)
        );
      case "scene.page":
        return (
          result.tag === "elements" &&
          result.sceneId === request.args.sceneId &&
          this.page(request.args, result, stale ? undefined : this.query?.elementCount) &&
          (stale || result.items.every((item) => this.query?.lanes.has(item.laneId)))
        );
      case "details":
        return (
          result.tag === "details" &&
          this.page(request.args, result) &&
          result.items.every(
            (item) =>
              item.runtime === request.args.runtime && item.address === request.args.address,
          )
        );
      case "export":
        return result.tag === "export";
    }
  }

  private page(
    args: { cursor: string | null; pageSize: number },
    result: { items: unknown[]; nextCursor: string | null },
    total?: number,
  ): boolean {
    if (result.items.length > args.pageSize) return false;
    const start = BigInt(args.cursor ?? "0");
    const end = start + BigInt(result.items.length);
    if (end > 18446744073709551615n) return false;
    if (total !== undefined && end > BigInt(total)) return false;
    if (result.nextCursor === null) return total === undefined || end === BigInt(total);
    return (
      result.items.length > 0 &&
      BigInt(result.nextCursor) === end &&
      (total === undefined || end < BigInt(total))
    );
  }

  private fail(code: string, message: string): void {
    if (this.failure) return;
    this.failure = new ProtocolError(code, message);
    clearTimeout(this.startup);
    this.readyReject(this.failure);
    this.epoch++;
    this.queryEpoch++;
    this.snapshotId = null;
    this.query = null;
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      clearTimeout(pending.cancellation);
      pending.reject(this.failure);
    }
    this.pending.clear();
    this.emit("failure", this.failure);
    this.child.kill("SIGKILL");
  }

  close(): Promise<void> {
    if (!this.closing) this.closing = this.stop();
    return this.closing;
  }

  private async stop(): Promise<void> {
    if (this.state === "closed") return;
    if (this.state === "starting")
      this.fail("WorkerExited", "Native worker closed during startup.");
    if (this.failure) {
      await this.exited;
      return;
    }
    this.state = "closing";
    const deadline = setTimeout(
      () =>
        this.fail("Timeout", "Native worker shutdown timed out; its owned process was terminated."),
      SHUTDOWN_MS,
    );
    this.write({ tag: "shutdown", version: NATIVE_VERSION });
    await this.exited;
    clearTimeout(deadline);
  }
}
