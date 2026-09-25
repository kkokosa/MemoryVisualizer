export const VERSION = 1 as const;

export const LIMITS = Object.freeze({
  maxFrameBytes: 65_536,
  maxOutstanding: 8,
  maxPageSize: 128,
  maxSceneItems: 128,
  progressIntervalMs: 100,
});

export const OPERATIONS = [
  "capabilities",
  "snapshot.load",
  "snapshot.dispose",
  "query",
  "scene",
  "details",
  "recipe.validate",
  "export",
] as const;

export type Operation = (typeof OPERATIONS)[number];
export type Capabilities = {
  backend: "fake";
  operations: readonly [
    "capabilities",
    "snapshot.load",
    "snapshot.dispose",
    "query",
    "scene",
    "details",
    "recipe.validate",
    "export",
  ];
  extensions: readonly [];
  limits: typeof LIMITS;
};

export const CAPABILITIES: Capabilities = Object.freeze({
  backend: "fake",
  operations: Object.freeze(OPERATIONS),
  extensions: Object.freeze([]) as readonly [],
  limits: LIMITS,
});

export type Hello = {
  tag: "hello";
  versions: number[];
  extensions: string[];
};

type RequestBase = {
  tag: "request";
  version: typeof VERSION;
  requestId: string;
};

export type Request = RequestBase &
  (
    | {
        operation: "capabilities";
        snapshotId: null;
        args: Record<string, never>;
      }
    | {
        operation: "snapshot.load";
        snapshotId: null;
        args: { source: "fixture:tiny"; delayMs: number };
      }
    | {
        operation: "snapshot.dispose";
        snapshotId: string;
        args: Record<string, never>;
      }
    | {
        operation: "query";
        snapshotId: string;
        args: { text: string; pageSize: number; cursor: string | null };
      }
    | {
        operation: "scene";
        snapshotId: string;
        args: { maxItems: number };
      }
    | {
        operation: "details";
        snapshotId: string;
        args: { objectId: string; pageSize: number; cursor: string | null };
      }
    | {
        operation: "recipe.validate";
        snapshotId: null;
        args: { recipe: { schemaVersion: typeof VERSION; query: string } };
      }
    | {
        operation: "export";
        snapshotId: string;
        args: { format: "svg"; maxBytes: number };
      }
  );

export type Cancel = {
  tag: "cancel";
  version: typeof VERSION;
  requestId: string;
};
export type Shutdown = { tag: "shutdown"; version: typeof VERSION };
export type Inbound = Hello | Request | Cancel | Shutdown;

export type Item = { objectId: string; address: string; size: string };
export type Page = {
  tag: "page";
  items: Item[];
  nextCursor: string | null;
  truncated: boolean;
};
export type Scene = {
  tag: "scene";
  schemaVersion: typeof VERSION;
  items: Item[];
  truncated: boolean;
};
export type Result =
  | { tag: "capabilities"; value: Capabilities }
  | { tag: "snapshot"; objectCount: string }
  | { tag: "disposed" }
  | Page
  | Scene
  | { tag: "recipe"; schemaVersion: typeof VERSION; valid: true }
  | {
      tag: "export";
      format: "svg";
      artifactId: "fixture:svg";
      byteLength: string;
    };

export type Success = {
  tag: "success";
  version: typeof VERSION;
  requestId: string;
  snapshotId: string | null;
  result: Result;
};

export const ERROR_CODES = [
  "Busy",
  "Cancelled",
  "SnapshotNotFound",
  "InvalidRequest",
  "NotImplemented",
  "InternalError",
  "StaleSnapshot",
  "WorkerExited",
  "Timeout",
  "ProtocolError",
] as const;
export type ErrorCode = (typeof ERROR_CODES)[number];
export type ErrorResponse = {
  tag: "error";
  version: typeof VERSION;
  requestId: string;
  snapshotId: string | null;
  error: { code: ErrorCode; message: string; retryable: boolean };
};
export type Progress = {
  tag: "progress";
  version: typeof VERSION;
  requestId: string;
  snapshotId: string | null;
  phase: "working";
  completed: number;
  total: 100;
};
export const FATAL_CODES = [
  "UnsupportedVersion",
  "UnsupportedExtension",
  "InvalidFrame",
  "ProtocolViolation",
  "TransportTimeout",
  "InternalError",
] as const;
export type Fatal = {
  tag: "fatal";
  code: (typeof FATAL_CODES)[number];
  message: string;
};
export type Ready = {
  tag: "ready";
  version: typeof VERSION;
  capabilities: Capabilities;
};
export type Bye = { tag: "bye"; version: typeof VERSION };
export type Outbound = Ready | Fatal | Success | ErrorResponse | Progress | Bye;

export class ProtocolError extends Error {
  constructor(
    public readonly code: string,
    message: string,
  ) {
    super(message);
    this.name = "ProtocolError";
  }
}

function invalid(): never {
  throw new ProtocolError("ProtocolViolation", "Invalid protocol message.");
}

type ObjectValue = Record<string, unknown>;

function object(value: unknown): ObjectValue {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return invalid();
  }
  const prototype: unknown = Object.getPrototypeOf(value);
  if (prototype !== Object.prototype && prototype !== null) return invalid();
  return value as ObjectValue;
}

function fields(value: unknown, names: readonly string[]): ObjectValue {
  const result = object(value);
  const keys = Object.keys(result);
  if (keys.length !== names.length || !names.every((name) => Object.hasOwn(result, name))) {
    return invalid();
  }
  return result;
}

function equal(value: unknown, expected: unknown): void {
  if (value !== expected) invalid();
}

export function isValidUnicode(value: string): boolean {
  for (let i = 0; i < value.length; i++) {
    const unit = value.charCodeAt(i);
    if (unit >= 0xd800 && unit <= 0xdbff) {
      const next = value.charCodeAt(++i);
      if (!(next >= 0xdc00 && next <= 0xdfff)) return false;
    } else if (unit >= 0xdc00 && unit <= 0xdfff) {
      return false;
    }
  }
  return true;
}

function text(value: unknown, min: number, max: number): string {
  if (typeof value !== "string") return invalid();
  let length = 0;
  for (const character of value) {
    if (++length > max || !isValidUnicode(character)) return invalid();
  }
  if (length < min) return invalid();
  return value;
}

function integer(value: unknown, min: number, max: number): number {
  if (
    typeof value !== "number" ||
    !Number.isSafeInteger(value) ||
    Object.is(value, -0) ||
    value < min ||
    value > max
  ) {
    return invalid();
  }
  return value;
}

function array(value: unknown, min: number, max: number): unknown[] {
  if (!Array.isArray(value) || value.length < min || value.length > max) {
    return invalid();
  }
  return value as unknown[];
}

function boolean(value: unknown): void {
  if (typeof value !== "boolean") invalid();
}

function member(value: unknown, values: readonly string[]): void {
  if (typeof value !== "string" || !values.includes(value)) invalid();
}

function uint64(value: unknown, nonzero = false): void {
  const decimal = text(value, 1, 20);
  // Unlike $, this end assertion cannot match before a final line terminator.
  if (
    !/^(?:0|[1-9][0-9]*)(?![\s\S])/.test(decimal) ||
    (nonzero && decimal === "0") ||
    (decimal.length === 20 && decimal > "18446744073709551615")
  ) {
    invalid();
  }
}

function snapshot(value: unknown): void {
  const uuid = text(value, 36, 36);
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![\s\S])/.test(uuid) ||
    uuid === "00000000-0000-0000-0000-000000000000"
  ) {
    invalid();
  }
}

function nullableSnapshot(value: unknown): void {
  if (value !== null) snapshot(value);
}

function cursor(value: unknown): void {
  if (value !== null) uint64(value);
}

function capabilities(value: unknown): void {
  const caps = fields(value, ["backend", "operations", "extensions", "limits"]);
  equal(caps.backend, "fake");
  const operations = array(caps.operations, OPERATIONS.length, OPERATIONS.length);
  for (let i = 0; i < OPERATIONS.length; i++) {
    equal(operations[i], OPERATIONS[i]);
  }
  array(caps.extensions, 0, 0);
  const limits = fields(caps.limits, Object.keys(LIMITS));
  for (const key of Object.keys(LIMITS) as (keyof typeof LIMITS)[]) {
    equal(limits[key], LIMITS[key]);
  }
}

function item(value: unknown): void {
  const record = fields(value, ["objectId", "address", "size"]);
  uint64(record.objectId, true);
  const address = text(record.address, 18, 18);
  if (!/^0x[0-9a-f]{16}(?![\s\S])/.test(address)) invalid();
  uint64(record.size);
}

function items(value: unknown, max: number): void {
  for (const record of array(value, 0, max)) item(record);
}

function request(value: ObjectValue): void {
  fields(value, ["tag", "version", "requestId", "snapshotId", "operation", "args"]);
  equal(value.version, VERSION);
  uint64(value.requestId, true);
  switch (value.operation) {
    case "capabilities":
      equal(value.snapshotId, null);
      fields(value.args, []);
      break;
    case "snapshot.load": {
      equal(value.snapshotId, null);
      const args = fields(value.args, ["source", "delayMs"]);
      equal(args.source, "fixture:tiny");
      integer(args.delayMs, 0, 5000);
      break;
    }
    case "snapshot.dispose":
      snapshot(value.snapshotId);
      fields(value.args, []);
      break;
    case "query": {
      snapshot(value.snapshotId);
      const args = fields(value.args, ["text", "pageSize", "cursor"]);
      text(args.text, 1, 4096);
      integer(args.pageSize, 1, LIMITS.maxPageSize);
      cursor(args.cursor);
      break;
    }
    case "details": {
      snapshot(value.snapshotId);
      const args = fields(value.args, ["objectId", "pageSize", "cursor"]);
      uint64(args.objectId, true);
      integer(args.pageSize, 1, LIMITS.maxPageSize);
      cursor(args.cursor);
      break;
    }
    case "scene": {
      snapshot(value.snapshotId);
      const args = fields(value.args, ["maxItems"]);
      integer(args.maxItems, 1, LIMITS.maxSceneItems);
      break;
    }
    case "recipe.validate": {
      equal(value.snapshotId, null);
      const args = fields(value.args, ["recipe"]);
      const recipe = fields(args.recipe, ["schemaVersion", "query"]);
      equal(recipe.schemaVersion, VERSION);
      text(recipe.query, 1, 4096);
      break;
    }
    case "export": {
      snapshot(value.snapshotId);
      const args = fields(value.args, ["format", "maxBytes"]);
      equal(args.format, "svg");
      integer(args.maxBytes, 1, 32_768);
      break;
    }
    default:
      invalid();
  }
}

export function parseInbound(value: unknown): Inbound {
  const frame = object(value);
  switch (frame.tag) {
    case "hello": {
      fields(frame, ["tag", "versions", "extensions"]);
      const versions = array(frame.versions, 1, 8);
      for (const version of versions) {
        integer(version, 1, Number.MAX_SAFE_INTEGER);
      }
      if (new Set(versions).size !== versions.length) invalid();
      const extensions = array(frame.extensions, 0, 8);
      for (const extension of extensions) {
        const name = text(extension, 1, 64);
        if (!/^[a-z][a-z0-9]*(?:\.[a-z][a-z0-9]*)+(?![\s\S])/.test(name)) invalid();
      }
      if (new Set(extensions).size !== extensions.length) invalid();
      break;
    }
    case "request":
      request(frame);
      break;
    case "cancel":
      fields(frame, ["tag", "version", "requestId"]);
      equal(frame.version, VERSION);
      uint64(frame.requestId, true);
      break;
    case "shutdown":
      fields(frame, ["tag", "version"]);
      equal(frame.version, VERSION);
      break;
    default:
      invalid();
  }
  return frame as Inbound;
}

function result(value: unknown, snapshotId: unknown): void {
  const record = object(value);
  if (record.tag === "capabilities" || record.tag === "recipe") {
    equal(snapshotId, null);
  } else {
    snapshot(snapshotId);
  }
  switch (record.tag) {
    case "capabilities":
      fields(record, ["tag", "value"]);
      capabilities(record.value);
      break;
    case "snapshot":
      fields(record, ["tag", "objectCount"]);
      uint64(record.objectCount);
      break;
    case "disposed":
      fields(record, ["tag"]);
      break;
    case "page":
      fields(record, ["tag", "items", "nextCursor", "truncated"]);
      items(record.items, LIMITS.maxPageSize);
      cursor(record.nextCursor);
      boolean(record.truncated);
      break;
    case "scene":
      fields(record, ["tag", "schemaVersion", "items", "truncated"]);
      equal(record.schemaVersion, VERSION);
      items(record.items, LIMITS.maxSceneItems);
      boolean(record.truncated);
      break;
    case "recipe":
      fields(record, ["tag", "schemaVersion", "valid"]);
      equal(record.schemaVersion, VERSION);
      equal(record.valid, true);
      break;
    case "export":
      fields(record, ["tag", "format", "artifactId", "byteLength"]);
      equal(record.format, "svg");
      equal(record.artifactId, "fixture:svg");
      uint64(record.byteLength);
      break;
    default:
      invalid();
  }
}

export function parseOutbound(value: unknown): Outbound {
  const frame = object(value);
  switch (frame.tag) {
    case "ready":
      fields(frame, ["tag", "version", "capabilities"]);
      equal(frame.version, VERSION);
      capabilities(frame.capabilities);
      break;
    case "fatal":
      fields(frame, ["tag", "code", "message"]);
      member(frame.code, FATAL_CODES);
      text(frame.message, 1, 256);
      break;
    case "success":
      fields(frame, ["tag", "version", "requestId", "snapshotId", "result"]);
      equal(frame.version, VERSION);
      uint64(frame.requestId, true);
      result(frame.result, frame.snapshotId);
      break;
    case "error": {
      fields(frame, ["tag", "version", "requestId", "snapshotId", "error"]);
      equal(frame.version, VERSION);
      uint64(frame.requestId, true);
      nullableSnapshot(frame.snapshotId);
      const error = fields(frame.error, ["code", "message", "retryable"]);
      member(error.code, ERROR_CODES);
      text(error.message, 1, 256);
      boolean(error.retryable);
      break;
    }
    case "progress":
      fields(frame, ["tag", "version", "requestId", "snapshotId", "phase", "completed", "total"]);
      equal(frame.version, VERSION);
      uint64(frame.requestId, true);
      nullableSnapshot(frame.snapshotId);
      equal(frame.phase, "working");
      integer(frame.completed, 0, 100);
      equal(frame.total, 100);
      break;
    case "bye":
      fields(frame, ["tag", "version"]);
      equal(frame.version, VERSION);
      break;
    default:
      invalid();
  }
  return frame as Outbound;
}
