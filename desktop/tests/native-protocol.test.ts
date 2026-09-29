import assert from "node:assert/strict";
import test from "node:test";
import { FrameDecoder, encodeFrame } from "../src/framing.js";
import {
  parseNativeInbound,
  parseNativeOutbound,
  validateSettings,
  validateWorkspaceDocument,
} from "../src/native-protocol.js";
import {
  DEFAULT_SETTINGS,
  NATIVE_LIMITS,
  NATIVE_OPERATIONS,
  type Entity,
  type NativeOperation,
  type NativeResult,
  type SceneElement,
  type SceneInfo,
} from "../src/native-types.js";
import { parseInbound, parseOutbound } from "../src/protocol.js";

const snapshotId = "00000000-0000-0000-0000-000000000001";
const queryId = "2";
const sceneId = "3";
const capabilities = { backend: "native", operations: NATIVE_OPERATIONS, limits: NATIVE_LIMITS };
const bounds = { x: -0.5, y: 0, width: 1024.25, height: 80 };
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
  type: "System.String 🧠",
  generation: 0,
  isFree: false,
};
const scene: SceneInfo = {
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
};
const element: SceneElement = {
  id: "element-0",
  laneId: "lane-0",
  layer: 0,
  geometry: { kind: "line", start: { x: -1.5, y: 0 }, finish: { x: 1e-7, y: 20.25 } },
  bounds,
  style: { fill: "none", stroke: "#fff", strokeWidth: 0.5 },
  text: {
    bounds,
    lines: [{ text: "Native 🧠", x: 0.5, baseline: 12.5, width: 16.25 }],
    cellWidth: 8.5,
    fontSize: 12,
    lineHeight: 16,
    fill: "#fff",
    replacedCodeUnits: 0,
    isTruncated: false,
  },
  source: {
    runtime: 0,
    heap: 0,
    statementIndex: 0,
    kind: "Object",
    address: entity.address,
    size: entity.size,
    segmentAddress: entity.segmentAddress,
    methodTable: null,
  },
  isClipped: false,
};
const operations: NativeOperation[] = [
  {
    operation: "snapshot.load",
    snapshotId: null,
    args: { path: "C:\\dumps\\🧠.dmp", dacPath: null, cachePath: null, allowNetwork: false },
  },
  { operation: "snapshot.dispose", snapshotId, args: {} },
  {
    operation: "query.run",
    snapshotId,
    args: { text: "MATCH o:Object RETURN o", settings: DEFAULT_SETTINGS },
  },
  { operation: "query.page", snapshotId, args: { queryId, cursor: null, pageSize: 32 } },
  { operation: "scene.page", snapshotId, args: { sceneId, cursor: "0", pageSize: 32 } },
  {
    operation: "details",
    snapshotId,
    args: { runtime: 0, address: entity.address, cursor: null, pageSize: 32 },
  },
  { operation: "export", snapshotId, args: { sceneId, path: "C:\\exports\\scene.svg" } },
];
const results: NativeResult[] = [
  { tag: "snapshot", objectCount: entity.size, sourcePartial: true, diagnosticCount: 1 },
  { tag: "disposed" },
  {
    tag: "query",
    queryId,
    status: "complete",
    sourceAvailable: true,
    sourcePartial: false,
    sourceDiagnosticCount: 0,
    rowCount: 1,
    candidates: entity.size,
    truncationReasons: [],
    diagnostics: [
      { code: "MQL001", message: "Message", span: { offset: 0, length: 1, line: 1, column: 1 } },
    ],
    scene,
  },
  {
    tag: "rows",
    queryId,
    items: [
      {
        statementIndex: 0,
        entity,
        values: [
          { name: "uint64", value: { kind: "uint64", value: entity.size } },
          { name: "text", value: { kind: "text", value: "🧠" } },
          { name: "boolean", value: { kind: "boolean", value: true } },
          { name: "entity", value: { kind: "entity", value: entity } },
          { name: "missing", value: { kind: "missing", value: null } },
        ],
      },
    ],
    nextCursor: null,
  },
  { tag: "elements", sceneId, items: [element], nextCursor: null },
  { tag: "details", items: [entity], nextCursor: null },
  { tag: "export", byteLength: entity.size },
];
const success = (result: unknown) => ({
  tag: "success",
  version: 2,
  requestId: "1",
  snapshotId,
  result,
});
const inbound = (operation: unknown): Record<string, unknown> => ({
  tag: "request",
  version: 2,
  requestId: "1",
  ...(operation as object),
});
const invalid = (action: () => unknown) => assert.throws(action, { code: "ProtocolViolation" });

test("native v2 operations, result variants and control frames round-trip independently of v1", () => {
  const inputs = [
    { tag: "hello", versions: [2], extensions: [] },
    ...operations.map(inbound),
    { tag: "cancel", version: 2, requestId: "18446744073709551615" },
    { tag: "shutdown", version: 2 },
  ];
  const outputs = [
    { tag: "ready", version: 2, capabilities },
    ...results.map(success),
    {
      tag: "progress",
      version: 2,
      requestId: "1",
      snapshotId: null,
      phase: "import",
      completed: entity.size,
    },
    {
      tag: "error",
      version: 2,
      requestId: "1",
      snapshotId,
      error: { code: "ImportFailed", message: "Could not import.", retryable: false },
    },
    { tag: "fatal", code: "InvalidFrame", message: "Invalid frame." },
    { tag: "bye", version: 2 },
  ];
  for (const input of inputs) assert.deepEqual(parseNativeInbound(input), input);
  for (const output of outputs) {
    assert.deepEqual(parseNativeOutbound(output), output);
    const decoder = new FrameDecoder(true);
    const bytes = encodeFrame(parseNativeOutbound(output), parseNativeOutbound);
    const frames: unknown[] = [];
    for (const byte of bytes) frames.push(...decoder.push(Uint8Array.of(byte)));
    decoder.end();
    assert.deepEqual(frames, [output]);
  }
  assert.throws(() => parseInbound(inbound(operations[0])));
  assert.throws(() => parseOutbound(success(results[0])));
  invalid(() => parseNativeInbound({ tag: "hello", versions: [1, 2], extensions: [] }));
  invalid(() => parseNativeInbound({ tag: "hello", versions: [2], extensions: ["native.extra"] }));
});

test("native fields, discriminants, capability order and limits are exact", () => {
  for (const input of operations.map(inbound)) {
    invalid(() => parseNativeInbound({ ...input, extra: true }));
    invalid(() => parseNativeInbound({ ...input, version: 1 }));
    invalid(() =>
      parseNativeInbound({ ...input, args: { ...(input.args as object), extra: true } }),
    );
  }
  for (const output of results.map(success)) {
    invalid(() => parseNativeOutbound({ ...output, extra: null }));
    invalid(() =>
      parseNativeOutbound({ ...output, result: { ...(output.result as object), extra: true } }),
    );
  }
  for (const caps of [
    { ...capabilities, backend: "fake" },
    { ...capabilities, extensions: [] },
    { ...capabilities, operations: [...NATIVE_OPERATIONS].reverse() },
    { ...capabilities, limits: { ...NATIVE_LIMITS, maxPageSize: 128 } },
    { ...capabilities, limits: { ...NATIVE_LIMITS, extra: 1 } },
  ])
    invalid(() => parseNativeOutbound({ tag: "ready", version: 2, capabilities: caps }));
  invalid(() => parseNativeOutbound(success({ tag: "page", items: [], nextCursor: null })));
  invalid(() => parseNativeInbound(inbound({ operation: "query", snapshotId, args: {} })));
});

test("request IDs, scopes, cursors and uint64 values never lose precision", () => {
  for (const requestId of ["0", "01", "-1", "+1", "1\n", "18446744073709551616", 1, 1n])
    invalid(() => parseNativeInbound({ ...inbound(operations[0]), requestId }));
  for (const invalidId of [
    null,
    "0",
    snapshotId.toUpperCase().replace("00000000", "ABCDEF00"),
    "00000000-0000-0000-0000-000000000000",
    `${snapshotId}\n`,
  ])
    invalid(() => parseNativeInbound({ ...inbound(operations[1]), snapshotId: invalidId }));
  for (const cursor of ["-1", "01", 0, "18446744073709551616"])
    invalid(() =>
      parseNativeInbound(
        inbound({ operation: "query.page", snapshotId, args: { queryId, cursor, pageSize: 1 } }),
      ),
    );
  for (const address of [
    "0x1",
    "0xFFFFFFFFFFFFFFFF",
    "ffffffffffffffff",
    `${entity.address}\n`,
    "0x10000000000000000",
    0,
  ])
    invalid(() =>
      parseNativeOutbound(
        success({ tag: "details", items: [{ ...entity, address }], nextCursor: null }),
      ),
    );
  for (const size of ["-1", "00", "1.5", "1e3", "18446744073709551616", 9007199254740992])
    invalid(() =>
      parseNativeOutbound(
        success({ tag: "details", items: [{ ...entity, size }], nextCursor: null }),
      ),
    );
});

test("query and scene IDs are nonzero uint64 decimals, while snapshots remain UUIDs", () => {
  for (const ownerId of ["1", "18446744073709551615"]) {
    assert.equal(
      parseNativeInbound(
        inbound({
          operation: "query.page",
          snapshotId,
          args: { queryId: ownerId, cursor: null, pageSize: 1 },
        }),
      ).tag,
      "request",
    );
    assert.equal(
      parseNativeInbound(
        inbound({
          operation: "scene.page",
          snapshotId,
          args: { sceneId: ownerId, cursor: null, pageSize: 1 },
        }),
      ).tag,
      "request",
    );
    assert.equal(
      parseNativeOutbound(
        success({
          tag: "rows",
          queryId: ownerId,
          items: [],
          nextCursor: null,
        }),
      ).tag,
      "success",
    );
    assert.equal(
      parseNativeOutbound(
        success({
          tag: "elements",
          sceneId: ownerId,
          items: [],
          nextCursor: null,
        }),
      ).tag,
      "success",
    );
  }
  for (const ownerId of ["0", "01", snapshotId, "18446744073709551616", 1]) {
    invalid(() =>
      parseNativeInbound(
        inbound({
          operation: "query.page",
          snapshotId,
          args: { queryId: ownerId, cursor: null, pageSize: 1 },
        }),
      ),
    );
    invalid(() =>
      parseNativeInbound(
        inbound({
          operation: "scene.page",
          snapshotId,
          args: { sceneId: ownerId, cursor: null, pageSize: 1 },
        }),
      ),
    );
    invalid(() =>
      parseNativeOutbound(
        success({
          tag: "rows",
          queryId: ownerId,
          items: [],
          nextCursor: null,
        }),
      ),
    );
    invalid(() =>
      parseNativeOutbound(
        success({
          tag: "elements",
          sceneId: ownerId,
          items: [],
          nextCursor: null,
        }),
      ),
    );
  }
  invalid(() =>
    parseNativeInbound(
      inbound({
        operation: "snapshot.dispose",
        snapshotId: "1",
        args: {},
      }),
    ),
  );
});

test("native query length counts UTF-16 units, including non-BMP characters", () => {
  const query = (text: string) =>
    inbound({
      operation: "query.run",
      snapshotId,
      args: { text, settings: DEFAULT_SETTINGS },
    });
  assert.equal(parseNativeInbound(query("🧠".repeat(8192))).tag, "request");
  invalid(() => parseNativeInbound(query("🧠".repeat(8193))));
  invalid(() => parseNativeInbound(query("🧠".repeat(9000))));
  invalid(() =>
    validateWorkspaceDocument({
      query: "🧠".repeat(9000),
      settings: DEFAULT_SETTINGS,
      annotations: [],
      hiddenLayers: [],
    }),
  );
});

test("settings and workspace documents reject undeclared fields and bound persisted data", () => {
  const document = {
    query: "",
    settings: DEFAULT_SETTINGS,
    annotations: [{ elementId: "element-0", text: "🧠 note" }],
    hiddenLayers: [0, 5],
  };
  assert.deepEqual(validateWorkspaceDocument(document), document);
  const maximal = {
    ...document,
    query: "🧠".repeat(8192),
    annotations: Array.from({ length: 128 }, (_, i) => ({
      elementId: `element-${i}`,
      text: "x".repeat(512),
    })),
    hiddenLayers: [0, 1, 2, 3, 4, 5],
  };
  assert.deepEqual(validateWorkspaceDocument(maximal), maximal);
  assert.deepEqual(
    validateSettings({
      ...DEFAULT_SETTINGS,
      viewport: { start: "18446744073709551615", size: "1" },
    }).viewport,
    { start: "18446744073709551615", size: "1" },
  );
  assert.deepEqual(
    validateSettings({ ...DEFAULT_SETTINGS, viewport: { start: "0", size: "0" } }).viewport,
    { start: "0", size: "0" },
  );
  for (const settings of [
    { ...DEFAULT_SETTINGS, plotWidth: 63 },
    { ...DEFAULT_SETTINGS, plotWidth: 4097 },
    { ...DEFAULT_SETTINGS, maxResults: 4097 },
    { ...DEFAULT_SETTINGS, maxElements: 1025 },
    { ...DEFAULT_SETTINGS, maxResults: 0 },
    { ...DEFAULT_SETTINGS, plotWidth: 1024.5 },
    { ...DEFAULT_SETTINGS, viewport: { start: "18446744073709551615", size: "2" } },
    { ...DEFAULT_SETTINGS, redaction: { ...DEFAULT_SETTINGS.redaction, paths: "false" } },
    { ...DEFAULT_SETTINGS, redaction: { ...DEFAULT_SETTINGS.redaction, extra: false } },
  ])
    invalid(() => validateSettings(settings));
  for (const bad of [
    { ...document, extra: true },
    { ...document, query: "x".repeat(16385) },
    { ...document, hiddenLayers: [0, 0] },
    { ...document, hiddenLayers: [-1] },
    { ...document, hiddenLayers: [6] },
    { ...document, query: "🧠".repeat(8193) },
    { ...document, annotations: [document.annotations[0], document.annotations[0]] },
    { ...document, annotations: [{ elementId: "element-0", text: "x".repeat(513) }] },
    {
      ...document,
      annotations: Array.from({ length: 129 }, (_, i) => ({
        elementId: `element-${i}`,
        text: "",
      })),
    },
  ])
    invalid(() => validateWorkspaceDocument(bad));
});

test("workspace annotation IDs, UTF-16 notes and hidden layers enforce exact ceilings", () => {
  const document = {
    query: "",
    settings: DEFAULT_SETTINGS,
    annotations: [{ elementId: "element-1023", text: "x".repeat(512) }],
    hiddenLayers: [0, 1, 2, 3, 4, 5],
  };
  assert.deepEqual(validateWorkspaceDocument(document), document);
  assert.equal(
    validateWorkspaceDocument({
      ...document,
      annotations: [{ elementId: "element-0", text: "🧠".repeat(256) }],
    }).annotations[0]!.text.length,
    512,
  );
  for (const elementId of [
    "element-1024",
    "element-01",
    "element-00",
    "element--1",
    "element-+1",
    "element-1\n",
    "element-1 ",
    "element-1.0",
    "element-1e0",
    "element-",
    "Element-1",
    "arbitrary-id",
  ])
    invalid(() =>
      validateWorkspaceDocument({
        ...document,
        annotations: [{ elementId, text: "" }],
      }),
    );
  for (const annotations of [
    [{ elementId: "element-0", text: "x".repeat(513) }],
    [{ elementId: "element-0", text: "🧠".repeat(257) }],
    [{ elementId: "element-0", text: "", extra: true }],
    [
      { elementId: "element-0", text: "" },
      { elementId: "element-0", text: "duplicate" },
    ],
    Array.from({ length: 129 }, (_, i) => ({ elementId: `element-${i}`, text: "" })),
  ])
    invalid(() => validateWorkspaceDocument({ ...document, annotations }));
  assert.equal(
    validateWorkspaceDocument({
      ...document,
      annotations: Array.from({ length: 128 }, (_, i) => ({ elementId: `element-${i}`, text: "" })),
    }).annotations.length,
    128,
  );
  for (const hiddenLayers of [[6], [-1], [-0], [0.5], [0, 0], [0, 1, 2, 3, 4, 5, 6]])
    invalid(() => validateWorkspaceDocument({ ...document, hiddenLayers }));
});

test("workspace JSON has its own 256 KiB byte limit, not the native 64 KiB frame limit", () => {
  const document = {
    query: "",
    settings: DEFAULT_SETTINGS,
    annotations: Array.from({ length: 128 }, (_, i) => ({
      elementId: `element-${i}`,
      text: "x".repeat(512),
    })),
    hiddenLayers: [0, 1, 2, 3, 4, 5],
  };
  const bytes = (value: unknown) => Buffer.byteLength(JSON.stringify(value), "utf8");
  assert.ok(bytes(document) > NATIVE_LIMITS.maxFrameBytes);
  assert.deepEqual(validateWorkspaceDocument(document), document);
  const multibyte = {
    ...document,
    query: "界".repeat(16384),
    annotations: document.annotations.map((annotation) => ({
      ...annotation,
      text: "界".repeat(512),
    })),
  };
  assert.ok(bytes(multibyte) <= 256 * 1024);
  assert.deepEqual(validateWorkspaceDocument(multibyte), multibyte);
  const tooLarge = (value: unknown) =>
    assert.throws(
      () => validateWorkspaceDocument(value),
      (error: unknown) => {
        assert.equal((error as { code: string }).code, "InvalidRequest");
        assert.match((error as Error).message, /256 KiB.*Shorten the query or annotations/);
        return true;
      },
    );
  for (const text of ["\0".repeat(512), "界".repeat(256) + "\0".repeat(256)]) {
    const oversized = {
      ...document,
      annotations: document.annotations.map((annotation) => ({ ...annotation, text })),
    };
    assert.ok(bytes(oversized) > 256 * 1024);
    tooLarge(oversized);
  }
  const exact = structuredClone(document);
  exact.query = "x".repeat(5000);
  const missing = 256 * 1024 - bytes(exact);
  let escapes = Math.floor(missing / 5);
  for (const annotation of exact.annotations) {
    const count = Math.min(512, escapes);
    annotation.text = "\0".repeat(count) + "x".repeat(512 - count);
    escapes -= count;
  }
  assert.equal(escapes, 0);
  exact.query += "x".repeat(missing % 5);
  assert.equal(bytes(exact), 256 * 1024);
  assert.deepEqual(validateWorkspaceDocument(exact), exact);
  exact.query += "x";
  assert.equal(bytes(exact), 256 * 1024 + 1);
  tooLarge(exact);
});

test("Unicode strings are validated and aggregate wire bytes are independently bounded", () => {
  for (const bad of ["\ud800", "\udfff", "a\ud800b"])
    invalid(() =>
      parseNativeInbound(
        inbound({
          operation: "query.run",
          snapshotId,
          args: { text: bad, settings: DEFAULT_SETTINGS },
        }),
      ),
    );
  const columns = Array.from({ length: 64 }, (_, i) => ({
    name: `column${i}`,
    value: { kind: "text", value: "🧠".repeat(256) },
  }));
  invalid(() =>
    parseNativeOutbound(
      success({
        tag: "rows",
        queryId,
        items: [{ statementIndex: 0, entity, values: columns }],
        nextCursor: null,
      }),
    ),
  );
  const row = {
    statementIndex: 0,
    entity,
    values: [{ name: "large", value: { kind: "text", value: "x".repeat(40_000) } }],
  };
  assert.equal(
    parseNativeOutbound(success({ tag: "rows", queryId, items: [row], nextCursor: null })).tag,
    "success",
  );
  invalid(() =>
    parseNativeOutbound(success({ tag: "rows", queryId, items: [row, row], nextCursor: null })),
  );
  invalid(() =>
    parseNativeInbound(
      inbound({
        operation: "snapshot.load",
        snapshotId: null,
        args: { path: "C:\\x\0y", dacPath: null, cachePath: null, allowNetwork: false },
      }),
    ),
  );
});

test("all page, row-column, scene and query counts are bounded", () => {
  for (const pageSize of [0, 33, 1.5, -0])
    invalid(() =>
      parseNativeInbound(
        inbound({
          operation: "details",
          snapshotId,
          args: { runtime: 0, address: entity.address, cursor: null, pageSize },
        }),
      ),
    );
  invalid(() =>
    parseNativeOutbound(
      success({
        tag: "details",
        items: Array.from({ length: 33 }, () => entity),
        nextCursor: null,
      }),
    ),
  );
  const row = {
    statementIndex: 0,
    entity,
    values: Array.from({ length: 65 }, () => ({
      name: "x",
      value: { kind: "missing", value: null },
    })),
  };
  invalid(() =>
    parseNativeOutbound(success({ tag: "rows", queryId, items: [row], nextCursor: null })),
  );
  const query = results[2]!;
  invalid(() => parseNativeOutbound(success({ ...query, rowCount: 4097 })));
  invalid(() =>
    parseNativeOutbound(success({ ...query, scene: { ...scene, elementCount: 1025 } })),
  );
  invalid(() =>
    parseNativeOutbound(
      success({ ...query, scene: { ...scene, lanes: [scene.lanes[0], scene.lanes[0]] } }),
    ),
  );
});

test("nested scopes, geometry, source metadata and tagged scalars are checked", () => {
  invalid(() =>
    parseNativeOutbound(
      success({ tag: "details", items: [{ ...entity, snapshotId: queryId }], nextCursor: null }),
    ),
  );
  invalid(() =>
    parseNativeOutbound(success({ ...results[2], scene: { ...scene, snapshotId: queryId } })),
  );
  for (const bad of [
    { ...element, bounds: { ...bounds, width: -1 } },
    { ...element, geometry: { kind: "ellipse", bounds } },
    { ...element, style: { ...element.style, strokeWidth: Infinity } },
    { ...element, source: { ...element.source, address: "0x0" } },
    {
      ...element,
      text: { ...element.text, lines: [{ text: "x", x: NaN, baseline: 0, width: 1 }] },
    },
    { ...element, bounds: { ...bounds, x: -0 } },
  ])
    invalid(() =>
      parseNativeOutbound(success({ tag: "elements", sceneId, items: [bad], nextCursor: null })),
    );
  for (const scalar of [
    { kind: "number", value: 1 },
    { kind: "boolean", value: "false" },
    { kind: "missing", value: "" },
  ]) {
    invalid(() =>
      parseNativeOutbound(
        success({
          tag: "rows",
          queryId,
          items: [{ statementIndex: 0, entity, values: [{ name: "x", value: scalar }] }],
          nextCursor: null,
        }),
      ),
    );
  }
});

test("validators reject exotic records without invoking getters", () => {
  invalid(() => validateSettings(Object.assign(Object.create({ extra: true }), DEFAULT_SETTINGS)));
  invalid(() => validateSettings({ ...DEFAULT_SETTINGS, [Symbol("extra")]: true }));
  let called = false;
  const settings = { ...DEFAULT_SETTINGS };
  Object.defineProperty(settings, "plotWidth", {
    get() {
      called = true;
      return 1024;
    },
    enumerable: true,
  });
  invalid(() => validateSettings(settings));
  assert.equal(called, false);
});

test("native framing keeps duplicate keys, invalid Unicode, depth and byte limits closed", () => {
  const invalidFrames = [
    '{"tag":"bye","tag":"bye","version":2}\n',
    '{"tag":"fatal","code":"InvalidFrame","message":"\\ud800"}\n',
    '{"tag":"bye","version":2}\r\n',
    '{"tag":"bye","version":2.0,"n":1e999}\n',
    " ".repeat(NATIVE_LIMITS.maxFrameBytes + 1),
  ];
  for (const frame of invalidFrames)
    assert.throws(() => new FrameDecoder(true).push(Buffer.from(frame)), { code: "InvalidFrame" });
  const decoder = new FrameDecoder(true);
  decoder.push(Buffer.from('{"tag":'));
  assert.throws(() => decoder.end(), { code: "InvalidFrame" });
});

test("native coordinate number mode never relaxes integer count or identity tokens", () => {
  for (const frame of [
    '{"tag":"bye","version":2.0}\n',
    '{"tag":"bye","version":2e0}\n',
    '{"tag":"success","version":2,"requestId":1,"snapshotId":null,"result":{}}\n',
    '{"tag":"success","version":2,"requestId":"1","snapshotId":null,"result":{"rowCount":1e0}}\n',
    '{"tag":"success","version":2,"requestId":"1","snapshotId":null,"result":{"rowCount":9007199254740992}}\n',
  ]) {
    assert.throws(() => {
      for (const value of new FrameDecoder(true).push(Buffer.from(frame))) {
        parseNativeOutbound(value);
      }
    });
  }
  const coordinates = Buffer.from('{"bounds":{"x":-0.5,"y":1e-7,"width":2.5,"height":3}}\n');
  assert.throws(() => new FrameDecoder().push(coordinates), { code: "InvalidFrame" });
  assert.deepEqual(new FrameDecoder(true).push(coordinates), [
    { bounds: { x: -0.5, y: 1e-7, width: 2.5, height: 3 } },
  ]);
});
