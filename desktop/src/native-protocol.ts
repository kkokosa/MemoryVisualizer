import { isValidUnicode, ProtocolError } from "./protocol.js";
import {
  NATIVE_LIMITS,
  NATIVE_OPERATIONS,
  NATIVE_VERSION,
  type NativeInbound,
  type NativeOutbound,
  type SceneSettings,
  type WorkspaceDocument,
} from "./native-types.js";

type RecordValue = Record<string, unknown>;
const MAX_U64 = "18446744073709551615";
const statuses = ["complete", "truncated", "cancelled", "failed"] as const;

function invalid(): never {
  throw new ProtocolError("ProtocolViolation", "Invalid native protocol message.");
}

function object(value: unknown): RecordValue {
  if (value === null || typeof value !== "object" || Array.isArray(value)) invalid();
  const prototype: unknown = Object.getPrototypeOf(value);
  if (prototype !== Object.prototype && prototype !== null) invalid();
  for (const key of Reflect.ownKeys(value)) {
    const descriptor = Object.getOwnPropertyDescriptor(value, key)!;
    if (typeof key !== "string" || !descriptor.enumerable || !("value" in descriptor)) invalid();
  }
  return value as RecordValue;
}

function fields(value: unknown, names: readonly string[]): RecordValue {
  const record = object(value);
  if (
    Object.keys(record).length !== names.length ||
    !names.every((key) => Object.hasOwn(record, key))
  )
    invalid();
  return record;
}

function equal(value: unknown, expected: unknown): void {
  if (value !== expected) invalid();
}

function text(value: unknown, min = 0, max: number = NATIVE_LIMITS.maxFrameBytes): string {
  if (
    typeof value !== "string" ||
    value.length < min ||
    value.length > max ||
    !isValidUnicode(value)
  )
    invalid();
  return value;
}

function integer(value: unknown, min = 0, max = 2_147_483_647): number {
  if (
    typeof value !== "number" ||
    !Number.isSafeInteger(value) ||
    Object.is(value, -0) ||
    value < min ||
    value > max
  )
    invalid();
  return value;
}

function number(value: unknown, min = -Number.MAX_SAFE_INTEGER): number {
  if (
    typeof value !== "number" ||
    !Number.isFinite(value) ||
    Object.is(value, -0) ||
    value < min ||
    value > Number.MAX_SAFE_INTEGER
  )
    invalid();
  return value;
}

function boolean(value: unknown): void {
  if (typeof value !== "boolean") invalid();
}

function array(value: unknown, max: number): unknown[] {
  if (!Array.isArray(value) || value.length > max) invalid();
  return value as unknown[];
}

function member(value: unknown, choices: readonly string[]): void {
  if (typeof value !== "string" || !choices.includes(value)) invalid();
}

function uint64(value: unknown, nonzero = false): void {
  const decimal = text(value, 1, 20);
  if (
    !/^(?:0|[1-9][0-9]*)(?![\s\S])/.test(decimal) ||
    (nonzero && decimal === "0") ||
    (decimal.length === 20 && decimal > MAX_U64)
  )
    invalid();
}

function id(value: unknown): void {
  const uuid = text(value, 36, 36);
  if (
    !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}(?![\s\S])/.test(uuid) ||
    uuid === "00000000-0000-0000-0000-000000000000"
  )
    invalid();
}

function nullable(value: unknown, validate: (value: unknown) => void): void {
  if (value !== null) validate(value);
}

function address(value: unknown): void {
  if (!/^0x[0-9a-f]{16}(?![\s\S])/.test(text(value, 18, 18))) invalid();
}

function bounded(
  value: unknown,
  maxBytes: number = NATIVE_LIMITS.maxFrameBytes,
  sizeMessage?: string,
): void {
  let encoded: string | undefined;
  try {
    encoded = JSON.stringify(value);
  } catch {
    invalid();
  }
  if (encoded === undefined) invalid();
  if (Buffer.byteLength(encoded, "utf8") > maxBytes) {
    if (sizeMessage) throw new ProtocolError("InvalidRequest", sizeMessage);
    invalid();
  }
}

function redaction(value: unknown): void {
  const record = fields(value, ["addresses", "strings", "paths", "labels"]);
  for (const entry of Object.values(record)) boolean(entry);
}

export function validateSettings(value: unknown): SceneSettings {
  const record = fields(value, [
    "layout",
    "plotWidth",
    "viewport",
    "redaction",
    "maxResults",
    "maxElements",
  ]);
  member(record.layout, ["linear", "compact"]);
  integer(record.plotWidth, 64, 4096);
  if (record.viewport !== null) {
    const viewport = fields(record.viewport, ["start", "size"]);
    uint64(viewport.start);
    uint64(viewport.size);
    if (BigInt(viewport.start as string) + BigInt(viewport.size as string) > 1n << 64n) invalid();
  }
  redaction(record.redaction);
  integer(record.maxResults, 1, NATIVE_LIMITS.maxResults);
  integer(record.maxElements, 1, NATIVE_LIMITS.maxSceneItems);
  return record as SceneSettings;
}

export function validateWorkspaceDocument(value: unknown): WorkspaceDocument {
  const record = fields(value, ["query", "settings", "annotations", "hiddenLayers"]);
  text(record.query, 0, NATIVE_LIMITS.maxQueryLength);
  validateSettings(record.settings);
  const annotated = new Set<string>();
  for (const value of array(record.annotations, 128)) {
    const annotation = fields(value, ["elementId", "text"]);
    const elementId = text(annotation.elementId, 9, 12);
    if (
      !/^element-(?:0|[1-9][0-9]*)(?![\s\S])/.test(elementId) ||
      Number(elementId.slice(8)) >= NATIVE_LIMITS.maxSceneItems
    )
      invalid();
    if (annotated.has(elementId)) invalid();
    annotated.add(elementId);
    text(annotation.text, 0, 512);
  }
  const layers = array(record.hiddenLayers, 6);
  for (const layer of layers) integer(layer, 0, 5);
  if (new Set(layers).size !== layers.length) invalid();
  bounded(
    record,
    256 * 1024,
    "Workspace document exceeds the 256 KiB JSON limit. Shorten the query or annotations.",
  );
  return record as WorkspaceDocument;
}

function capabilities(value: unknown): void {
  const record = fields(value, [
    "backend",
    "sceneSchemaVersion",
    "layouts",
    "operations",
    "limits",
  ]);
  equal(record.backend, "native");
  equal(record.sceneSchemaVersion, 2);
  const layouts = array(record.layouts, 2);
  equal(layouts.length, 2);
  equal(layouts[0], "linear");
  equal(layouts[1], "compact");
  const operations = array(record.operations, NATIVE_OPERATIONS.length);
  equal(operations.length, NATIVE_OPERATIONS.length);
  NATIVE_OPERATIONS.forEach((operation, index) => equal(operations[index], operation));
  const limits = fields(record.limits, Object.keys(NATIVE_LIMITS));
  for (const key of Object.keys(NATIVE_LIMITS) as (keyof typeof NATIVE_LIMITS)[])
    equal(limits[key], NATIVE_LIMITS[key]);
}

function path(value: unknown): void {
  if (text(value, 1, 4096).includes("\0")) invalid();
}

function request(record: RecordValue): void {
  fields(record, ["tag", "version", "requestId", "snapshotId", "operation", "args"]);
  equal(record.version, NATIVE_VERSION);
  uint64(record.requestId, true);
  if (record.operation === "snapshot.load") {
    equal(record.snapshotId, null);
    const args = fields(record.args, ["path", "dacPath", "cachePath", "allowNetwork"]);
    path(args.path);
    nullable(args.dacPath, path);
    nullable(args.cachePath, path);
    boolean(args.allowNetwork);
    return;
  }
  id(record.snapshotId);
  switch (record.operation) {
    case "snapshot.dispose":
      fields(record.args, []);
      break;
    case "query.run": {
      const args = fields(record.args, ["text", "settings"]);
      text(args.text, 1, NATIVE_LIMITS.maxQueryLength);
      validateSettings(args.settings);
      break;
    }
    case "query.page":
    case "scene.page": {
      const key = record.operation === "query.page" ? "queryId" : "sceneId";
      const args = fields(record.args, [key, "cursor", "pageSize"]);
      uint64(args[key], true);
      nullable(args.cursor, uint64);
      integer(args.pageSize, 1, NATIVE_LIMITS.maxPageSize);
      break;
    }
    case "details": {
      const args = fields(record.args, ["runtime", "address", "cursor", "pageSize"]);
      integer(args.runtime);
      address(args.address);
      nullable(args.cursor, uint64);
      integer(args.pageSize, 1, NATIVE_LIMITS.maxPageSize);
      break;
    }
    case "export": {
      const args = fields(record.args, ["sceneId", "path"]);
      uint64(args.sceneId, true);
      path(args.path);
      break;
    }
    default:
      invalid();
  }
}

export function parseNativeInbound(value: unknown): NativeInbound {
  const record = object(value);
  switch (record.tag) {
    case "hello": {
      fields(record, ["tag", "versions", "extensions"]);
      const versions = array(record.versions, 1);
      equal(versions.length, 1);
      equal(versions[0], NATIVE_VERSION);
      equal(array(record.extensions, 0).length, 0);
      break;
    }
    case "request":
      request(record);
      break;
    case "cancel":
      fields(record, ["tag", "version", "requestId"]);
      equal(record.version, NATIVE_VERSION);
      uint64(record.requestId, true);
      break;
    case "shutdown":
      fields(record, ["tag", "version"]);
      equal(record.version, NATIVE_VERSION);
      break;
    default:
      invalid();
  }
  bounded(record);
  return record as NativeInbound;
}

function bounds(value: unknown): void {
  const record = fields(value, ["x", "y", "width", "height"]);
  number(record.x);
  number(record.y);
  number(record.width, 0);
  number(record.height, 0);
}

function point(value: unknown): void {
  const record = fields(value, ["x", "y"]);
  number(record.x);
  number(record.y);
}

function line(value: unknown): void {
  const record = fields(value, ["start", "finish"]);
  point(record.start);
  point(record.finish);
}

function style(value: unknown): void {
  const record = fields(value, ["fill", "stroke", "strokeWidth"]);
  text(record.fill, 1, 128);
  text(record.stroke, 1, 128);
  number(record.strokeWidth, 0);
}

function sceneText(
  value: unknown,
  maxLines = 1024,
  maxText: number = NATIVE_LIMITS.maxFrameBytes,
): void {
  const layout = fields(value, [
    "bounds",
    "lines",
    "cellWidth",
    "fontSize",
    "lineHeight",
    "fill",
    "replacedCodeUnits",
    "isTruncated",
  ]);
  bounds(layout.bounds);
  for (const value of array(layout.lines, maxLines)) {
    const line = fields(value, ["text", "x", "baseline", "width"]);
    text(line.text, 0, maxText);
    number(line.x);
    number(line.baseline);
    number(line.width, 0);
  }
  number(layout.cellWidth, 0);
  number(layout.fontSize, 0);
  number(layout.lineHeight, 0);
  text(layout.fill, 1, 128);
  integer(layout.replacedCodeUnits);
  boolean(layout.isTruncated);
}

function gaps(value: unknown): void {
  const record = fields(value, ["offsets", "band", "lines", "style", "legend"]);
  for (const offset of array(record.offsets, NATIVE_LIMITS.maxSceneItems + 1)) number(offset);
  bounds(record.band);
  for (const value of array(record.lines, 2)) line(value);
  style(record.style);
  sceneText(record.legend, 1, 128);
}

function strings(value: unknown, max = 64): void {
  for (const entry of array(value, max)) text(entry, 1, 256);
}

function entity(value: unknown, snapshotId: unknown): void {
  const record = fields(value, [
    "kind",
    "snapshotId",
    "runtime",
    "heap",
    "segmentAddress",
    "address",
    "size",
    "heapKind",
    "end",
    "methodTable",
    "type",
    "generation",
    "isFree",
  ]);
  text(record.kind, 1, 64);
  id(record.snapshotId);
  equal(record.snapshotId, snapshotId);
  integer(record.runtime);
  integer(record.heap);
  address(record.segmentAddress);
  address(record.address);
  uint64(record.size);
  text(record.heapKind, 1);
  nullable(record.end, address);
  nullable(record.methodTable, address);
  nullable(record.type, text);
  nullable(record.generation, integer);
  nullable(record.isFree, boolean);
}

function row(value: unknown, snapshotId: unknown): void {
  const record = fields(value, ["statementIndex", "entity", "values"]);
  integer(record.statementIndex);
  entity(record.entity, snapshotId);
  for (const entry of array(record.values, 64)) {
    const column = fields(entry, ["name", "value"]);
    text(column.name, 1, NATIVE_LIMITS.maxQueryLength);
    const scalar = fields(column.value, ["kind", "value"]);
    switch (scalar.kind) {
      case "uint64":
        uint64(scalar.value);
        break;
      case "text":
        text(scalar.value);
        break;
      case "boolean":
        boolean(scalar.value);
        break;
      case "entity":
        entity(scalar.value, snapshotId);
        break;
      case "missing":
        equal(scalar.value, null);
        break;
      default:
        invalid();
    }
  }
}

function source(value: unknown): void {
  const record = fields(value, [
    "runtime",
    "heap",
    "statementIndex",
    "kind",
    "address",
    "size",
    "segmentAddress",
    "methodTable",
  ]);
  integer(record.runtime);
  integer(record.heap);
  integer(record.statementIndex);
  text(record.kind, 1, 64);
  address(record.address);
  uint64(record.size);
  nullable(record.segmentAddress, address);
  nullable(record.methodTable, address);
}

function element(value: unknown): void {
  const record = fields(value, [
    "id",
    "laneId",
    "layer",
    "geometry",
    "bounds",
    "style",
    "text",
    "source",
    "isClipped",
  ]);
  text(record.id, 1, 128);
  text(record.laneId, 1, 128);
  integer(record.layer);
  const geometry = object(record.geometry);
  if (geometry.kind === "rectangle") {
    fields(geometry, ["kind", "bounds"]);
    bounds(geometry.bounds);
  } else if (geometry.kind === "line") {
    fields(geometry, ["kind", "start", "finish"]);
    point(geometry.start);
    point(geometry.finish);
  } else invalid();
  bounds(record.bounds);
  style(record.style);
  nullable(record.text, sceneText);
  nullable(record.source, source);
  boolean(record.isClipped);
}

function scene(value: unknown, snapshotId: unknown): void {
  const record = fields(value, [
    "schemaVersion",
    "layout",
    "gaps",
    "sceneId",
    "snapshotId",
    "bounds",
    "lanes",
    "theme",
    "redaction",
    "elementCount",
    "status",
    "truncationReasons",
  ]);
  equal(record.schemaVersion, 2);
  member(record.layout, ["linear", "compact"]);
  uint64(record.sceneId, true);
  id(record.snapshotId);
  equal(record.snapshotId, snapshotId);
  bounds(record.bounds);
  const ids = new Set<string>();
  for (const value of array(record.lanes, 64)) {
    const lane = fields(value, ["id", "runtime", "heap", "bounds"]);
    const laneId = text(lane.id, 1, 128);
    if (ids.has(laneId)) invalid();
    ids.add(laneId);
    nullable(lane.runtime, integer);
    nullable(lane.heap, integer);
    bounds(lane.bounds);
  }
  const theme = fields(record.theme, ["background", "stroke", "text"]);
  for (const value of Object.values(theme)) text(value, 1, 128);
  redaction(record.redaction);
  if (record.layout === "linear" || (record.redaction as RecordValue).addresses)
    equal(record.gaps, null);
  else nullable(record.gaps, gaps);
  integer(record.elementCount, 0, NATIVE_LIMITS.maxSceneItems);
  member(record.status, statuses);
  strings(record.truncationReasons);
}

function result(value: unknown, snapshotId: unknown): void {
  id(snapshotId);
  const record = object(value);
  switch (record.tag) {
    case "snapshot":
      fields(record, ["tag", "objectCount", "sourcePartial", "diagnosticCount"]);
      uint64(record.objectCount);
      boolean(record.sourcePartial);
      integer(record.diagnosticCount);
      break;
    case "disposed":
      fields(record, ["tag"]);
      break;
    case "query":
      fields(record, [
        "tag",
        "queryId",
        "status",
        "sourceAvailable",
        "sourcePartial",
        "sourceDiagnosticCount",
        "rowCount",
        "candidates",
        "truncationReasons",
        "diagnostics",
        "scene",
      ]);
      uint64(record.queryId, true);
      member(record.status, statuses);
      boolean(record.sourceAvailable);
      boolean(record.sourcePartial);
      integer(record.sourceDiagnosticCount);
      integer(record.rowCount, 0, NATIVE_LIMITS.maxResults);
      uint64(record.candidates);
      strings(record.truncationReasons);
      for (const value of array(record.diagnostics, 128)) {
        const diagnostic = fields(value, ["code", "message", "span"]);
        text(diagnostic.code, 1, 64);
        text(diagnostic.message, 1, 4096);
        const span = fields(diagnostic.span, ["offset", "length", "line", "column"]);
        integer(span.offset);
        integer(span.length);
        integer(span.line, 1);
        integer(span.column, 1);
      }
      if (record.scene !== null) scene(record.scene, snapshotId);
      break;
    case "rows":
      fields(record, ["tag", "queryId", "items", "nextCursor"]);
      uint64(record.queryId, true);
      for (const item of array(record.items, NATIVE_LIMITS.maxPageSize)) row(item, snapshotId);
      nullable(record.nextCursor, uint64);
      break;
    case "elements": {
      fields(record, ["tag", "sceneId", "items", "nextCursor"]);
      uint64(record.sceneId, true);
      const ids = new Set<string>();
      for (const item of array(record.items, NATIVE_LIMITS.maxPageSize)) {
        element(item);
        const elementId = (item as RecordValue).id as string;
        if (ids.has(elementId)) invalid();
        ids.add(elementId);
      }
      nullable(record.nextCursor, uint64);
      break;
    }
    case "details":
      fields(record, ["tag", "items", "nextCursor"]);
      for (const item of array(record.items, NATIVE_LIMITS.maxPageSize)) entity(item, snapshotId);
      nullable(record.nextCursor, uint64);
      break;
    case "export":
      fields(record, ["tag", "byteLength"]);
      uint64(record.byteLength);
      break;
    default:
      invalid();
  }
}

export function parseNativeOutbound(value: unknown): NativeOutbound {
  const record = object(value);
  switch (record.tag) {
    case "ready":
      fields(record, ["tag", "version", "capabilities"]);
      equal(record.version, NATIVE_VERSION);
      capabilities(record.capabilities);
      break;
    case "fatal":
      fields(record, ["tag", "code", "message"]);
      text(record.code, 1, 64);
      text(record.message, 1, 4096);
      break;
    case "success":
      fields(record, ["tag", "version", "requestId", "snapshotId", "result"]);
      equal(record.version, NATIVE_VERSION);
      uint64(record.requestId, true);
      result(record.result, record.snapshotId);
      break;
    case "error": {
      fields(record, ["tag", "version", "requestId", "snapshotId", "error"]);
      equal(record.version, NATIVE_VERSION);
      uint64(record.requestId, true);
      nullable(record.snapshotId, id);
      const error = fields(record.error, ["code", "message", "retryable"]);
      text(error.code, 1, 64);
      text(error.message, 1, 4096);
      boolean(error.retryable);
      break;
    }
    case "progress":
      fields(record, ["tag", "version", "requestId", "snapshotId", "phase", "completed"]);
      equal(record.version, NATIVE_VERSION);
      uint64(record.requestId, true);
      nullable(record.snapshotId, id);
      text(record.phase, 1, 64);
      uint64(record.completed);
      break;
    case "bye":
      fields(record, ["tag", "version"]);
      equal(record.version, NATIVE_VERSION);
      break;
    default:
      invalid();
  }
  bounded(record);
  return record as NativeOutbound;
}
