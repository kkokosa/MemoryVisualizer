import { spawn, type ChildProcessWithoutNullStreams } from "node:child_process";
import { isAbsolute } from "node:path";
import { EventEmitter } from "node:events";
import {
  LIMITS,
  ProtocolError,
  parseInbound,
  parseOutbound,
  type Capabilities,
  type Inbound,
  type Outbound,
  type Progress,
  type Request,
  type Success,
} from "./protocol.js";
import { FrameDecoder, encodeFrame } from "./framing.js";

export type OperationInput = Request extends infer R
  ? R extends Request
    ? Pick<R, "snapshotId" | "operation" | "args">
    : never
  : never;

export type RequestHandle = { requestId: string; result: Promise<Success> };
type Pending = {
  request: Request;
  epoch: number;
  resolve: (result: Success) => void;
  reject: (error: Error) => void;
  timer: NodeJS.Timeout;
};

const STARTUP_MS = 5_000;
const REQUEST_MS = 10_000;
const SHUTDOWN_MS = 2_000;

// This owner never discovers or kills a process by name, and never retries work.
export class WorkerClient extends EventEmitter {
  readonly child: ChildProcessWithoutNullStreams;
  readonly ready: Promise<Capabilities>;
  readonly exited: Promise<void>;
  private readyResolve!: (capabilities: Capabilities) => void;
  private readyReject!: (error: Error) => void;
  private state: "starting" | "ready" | "closing" | "closed" = "starting";
  private pending = new Map<string, Pending>();
  private decoder = new FrameDecoder();
  private counter = 0n;
  private epoch = 0;
  private snapshotId: string | null = null;
  private startup: NodeJS.Timeout;
  private closing: Promise<void> | undefined;
  private receivedBye = false;
  private failure: ProtocolError | undefined;
  private progress = new Map<string, Progress>();
  private progressClock = new Map<string, number>();

  constructor(executable: string) {
    super();
    if (!isAbsolute(executable)) {
      throw new ProtocolError("InvalidRequest", "Worker executable path must be absolute.");
    }
    this.ready = new Promise((resolve, reject) => {
      this.readyResolve = resolve;
      this.readyReject = reject;
    });
    this.child = spawn(executable, ["--protocol", "--backend=fake"], {
      shell: false,
      windowsHide: true,
      stdio: ["pipe", "pipe", "pipe"],
    });
    this.startup = setTimeout(
      () =>
        this.fail("Timeout", "Worker startup timed out. Check the native runtime installation."),
      STARTUP_MS,
    );
    // Drain, but never relay dependency diagnostics which may include sensitive data.
    this.child.stderr.on("data", () => {});
    this.child.on("error", () =>
      this.fail("WorkerExited", "Worker could not start. Check its executable and runtime."),
    );
    this.child.stdin.on("error", () =>
      this.fail("WorkerExited", "Worker input pipe closed unexpectedly."),
    );
    this.child.stdout.on("data", (chunk: Buffer) => {
      try {
        for (const frame of this.decoder.push(chunk)) this.receive(parseOutbound(frame));
      } catch {
        this.fail("ProtocolError", "Worker sent an invalid protocol frame.");
      }
    });
    this.child.stdout.on("end", () => {
      try {
        this.decoder.end();
      } catch {
        this.fail("ProtocolError", "Worker exited within a protocol frame.");
      }
    });
    this.exited = new Promise((resolve) => {
      this.child.on("close", (code) => {
        clearTimeout(this.startup);
        if (this.state !== "closing" || !this.receivedBye || code !== 0) {
          this.fail("WorkerExited", "Worker exited unexpectedly; no work was retried.");
        } else if (this.pending.size > 0) {
          this.fail("ProtocolError", "Worker exited with unfinished requests.");
        }
        this.state = "closed";
        this.emit("closed", this.failure);
        resolve();
      });
    });
    void this.write({ tag: "hello", versions: [1], extensions: [] }).catch(() => {});
  }

  get activeSnapshot(): string | null {
    return this.snapshotId;
  }

  private async write(message: Inbound): Promise<void> {
    if (this.state === "closed" || this.failure) {
      throw this.failure ?? new ProtocolError("WorkerExited", "Worker is closed.");
    }
    let bytes: Uint8Array;
    try {
      bytes = encodeFrame(parseInbound(message));
    } catch {
      this.fail("ProtocolError", "Outgoing worker message failed validation.");
      throw this.failure;
    }
    // No application write queue: request slots bound all concurrent writers.
    // Repeated cancel frames are coalesced separately.
    await new Promise<void>((resolve, reject) => {
      this.child.stdin.write(bytes, (error) => {
        if (error) {
          this.fail("WorkerExited", "Writing to worker failed.");
          reject(new ProtocolError("WorkerExited", "Writing to worker failed."));
        } else resolve();
      });
    });
  }

  request(input: OperationInput): RequestHandle {
    if (this.state !== "ready" || this.failure) {
      throw this.failure ?? new ProtocolError("WorkerExited", "Worker is not ready.");
    }
    if (this.pending.size >= LIMITS.maxOutstanding) {
      throw new ProtocolError("Busy", "Request limit reached.");
    }
    const requestId = (++this.counter).toString();
    const checked = parseInbound({
      tag: "request",
      version: 1,
      requestId,
      operation: input.operation,
      snapshotId: input.snapshotId,
      args: input.args,
    });
    if (checked.tag !== "request") throw new ProtocolError("InvalidRequest", "Invalid operation.");
    const request = checked;
    if (request.snapshotId !== null && request.snapshotId !== this.snapshotId) {
      throw new ProtocolError("StaleSnapshot", "The snapshot is no longer active.");
    }
    if (request.operation === "snapshot.load" || request.operation === "snapshot.dispose") {
      this.epoch++;
      this.snapshotId = null;
      for (const [id, pending] of this.pending) {
        if (pending.request.snapshotId !== null || pending.request.operation === "snapshot.load") {
          this.cancel(id);
        }
      }
    }
    const result = new Promise<Success>((resolve, reject) => {
      const timer = setTimeout(
        () => this.fail("Timeout", "Worker request timed out; no work was retried."),
        REQUEST_MS,
      );
      this.pending.set(requestId, { request, epoch: this.epoch, resolve, reject, timer });
    });
    void this.write(request).catch(() => {});
    return { requestId, result };
  }

  private cancelled = new Set<string>();

  cancel(requestId: string): void {
    if (!this.pending.has(requestId) || this.cancelled.has(requestId)) return;
    this.cancelled.add(requestId);
    void this.write({ tag: "cancel", version: 1, requestId }).catch(() => {});
  }

  private receive(message: Outbound): void {
    if (this.failure) return;
    if (message.tag === "fatal") {
      this.fail("ProtocolError", `Worker protocol failure: ${message.code}.`);
      return;
    }
    if (this.state === "starting" && message.tag === "ready") {
      clearTimeout(this.startup);
      this.state = "ready";
      this.readyResolve(message.capabilities);
      return;
    }
    if (message.tag === "bye" && this.state === "closing" && this.pending.size === 0) {
      this.receivedBye = true;
      this.child.stdin.end();
      return;
    }
    if (message.tag !== "success" && message.tag !== "error" && message.tag !== "progress") {
      this.fail("ProtocolError", "Unexpected worker message.");
      return;
    }
    const pending = this.pending.get(message.requestId);
    if (!pending) {
      this.fail("ProtocolError", "Worker used an unknown request ID.");
      return;
    }
    const expectedScope = pending.request.snapshotId;
    if (
      !(message.tag === "success" && pending.request.operation === "snapshot.load") &&
      message.snapshotId !== expectedScope
    ) {
      this.fail("ProtocolError", "Worker returned an invalid snapshot scope.");
      return;
    }
    const scoped = expectedScope !== null || pending.request.operation === "snapshot.load";
    if (message.tag === "progress") {
      if (scoped && pending.epoch !== this.epoch) return;
      // Drop intermediate notifications even if a faulty peer floods them.
      this.progress.set(message.requestId, message);
      const now = performance.now();
      if (
        now - (this.progressClock.get(message.requestId) ?? -Infinity) >=
        LIMITS.progressIntervalMs
      ) {
        this.progressClock.set(message.requestId, now);
        this.emit("progress", this.progress.get(message.requestId));
        this.progress.delete(message.requestId);
      }
      return;
    }
    clearTimeout(pending.timer);
    this.pending.delete(message.requestId);
    this.cancelled.delete(message.requestId);
    this.progress.delete(message.requestId);
    this.progressClock.delete(message.requestId);
    if (scoped && pending.epoch !== this.epoch) {
      pending.reject(
        new ProtocolError("StaleSnapshot", "Snapshot changed before the result arrived."),
      );
      return;
    }
    if (message.tag === "error") {
      pending.reject(new ProtocolError(message.error.code, message.error.message));
      return;
    }
    if (!this.matches(pending.request, message)) {
      pending.reject(
        new ProtocolError("ProtocolError", "Worker result did not match its request."),
      );
      this.fail("ProtocolError", "Worker result did not match its request.");
      return;
    }
    if (pending.request.operation === "snapshot.load") this.snapshotId = message.snapshotId;
    pending.resolve(message);
  }

  private matches(request: Request, response: Success): boolean {
    const result = response.result;
    switch (request.operation) {
      case "snapshot.load":
        return result.tag === "snapshot" && response.snapshotId !== null;
      case "snapshot.dispose":
        return result.tag === "disposed";
      case "capabilities":
        return result.tag === "capabilities";
      case "query":
      case "details":
        return result.tag === "page" && result.items.length <= request.args.pageSize;
      case "scene":
        return result.tag === "scene" && result.items.length <= request.args.maxItems;
      case "recipe.validate":
        return result.tag === "recipe";
      case "export":
        return (
          result.tag === "export" && BigInt(result.byteLength) <= BigInt(request.args.maxBytes)
        );
    }
  }

  private fail(code: string, message: string): void {
    if (this.failure) return;
    this.failure = new ProtocolError(code, message);
    clearTimeout(this.startup);
    this.readyReject(this.failure);
    this.snapshotId = null;
    for (const pending of this.pending.values()) {
      clearTimeout(pending.timer);
      pending.reject(this.failure);
    }
    this.pending.clear();
    this.cancelled.clear();
    this.progress.clear();
    this.progressClock.clear();
    this.emit("failure", this.failure);
    this.child.kill("SIGKILL");
  }

  close(): Promise<void> {
    if (this.closing) return this.closing;
    this.closing = this.stop();
    return this.closing;
  }

  private async stop(): Promise<void> {
    if (this.state === "closed") return;
    if (this.state === "starting") {
      this.fail("WorkerExited", "Worker closed during startup.");
      await this.exited;
      return;
    }
    this.state = "closing";
    const deadline = setTimeout(() => {
      this.fail("Timeout", "Worker shutdown timed out; its owned process was terminated.");
    }, SHUTDOWN_MS);
    void this.write({ tag: "shutdown", version: 1 }).catch(() => {});
    await this.exited;
    clearTimeout(deadline);
  }
}
