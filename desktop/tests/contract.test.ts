import assert from "node:assert/strict";
import { existsSync, readFileSync } from "node:fs";
import { resolve } from "node:path";
import test from "node:test";
import { decodeFrame, encodeFrame, FrameDecoder } from "../src/framing.js";
import {
  CAPABILITIES,
  ERROR_CODES,
  FATAL_CODES,
  LIMITS,
  OPERATIONS,
  parseInbound,
  parseOutbound,
  ProtocolError,
  VERSION,
  type Inbound,
  type Outbound,
  type Request,
} from "../src/protocol.js";

const rootFixtures = resolve("protocol", "fixtures.json");
const fixturePath = existsSync(rootFixtures)
  ? rootFixtures
  : resolve("..", "protocol", "fixtures.json");
const fixtures = JSON.parse(readFileSync(fixturePath, "utf8")) as {
  validInbound: unknown[];
  invalidInbound: unknown[];
  validOutbound: unknown[];
  invalidOutbound: unknown[];
  textBoundaryCases: { textUnit: string; repeat: number; valid: boolean }[];
  validJson: string[];
  invalidJson: string[];
};
const utf8 = new TextEncoder();
const scope = "00000000-0000-0000-0000-000000000001";
const maxUint64 = "18446744073709551615";
const item = {
  objectId: maxUint64,
  address: "0xffffffffffffffff",
  size: maxUint64,
};
const query: Request = {
  tag: "request",
  version: VERSION,
  requestId: "1",
  snapshotId: scope,
  operation: "query",
  args: { text: "objects 🧠", pageSize: 128, cursor: null },
};

function protocolFailure(action: () => unknown, code: string): void {
  assert.throws(action, (error: unknown) => {
    assert.ok(error instanceof ProtocolError);
    assert.equal(error.code, code);
    assert.equal(
      error.message,
      code === "InvalidFrame" ? "Invalid protocol frame." : "Invalid protocol message.",
    );
    return true;
  });
}

function invalidMessage(action: () => unknown): void {
  protocolFailure(action, "ProtocolViolation");
}

function invalidFrame(action: () => unknown): void {
  protocolFailure(action, "InvalidFrame");
}

test("shared fixtures exercise both directions and round-trip through framing", async (t) => {
  for (const [direction, parse] of [
    ["Inbound", parseInbound],
    ["Outbound", parseOutbound],
  ] as const) {
    const valid = fixtures[`valid${direction}`];
    const invalid = fixtures[`invalid${direction}`];
    assert.ok(valid.length >= 15);
    assert.ok(invalid.length >= 50);
    for (const [index, value] of valid.entries()) {
      await t.test(`valid${direction}[${index}]`, () => {
        const frame = parse(value);
        assert.deepEqual(frame, value);
        const encoded = encodeFrame(frame);
        assert.equal(encoded[encoded.length - 1], 10);
        assert.deepEqual(decodeFrame(encoded.subarray(0, -1)), value);
      });
    }
    for (const [index, value] of invalid.entries()) {
      await t.test(`invalid${direction}[${index}]`, () => {
        invalidMessage(() => parse(value));
      });
    }
  }
});

test("shared scalar boundaries and raw lexical fixtures match F#", () => {
  for (const fixture of fixtures.textBoundaryCases) {
    const text = fixture.textUnit.repeat(fixture.repeat);
    for (const value of [
      { ...query, args: { ...query.args, text } },
      {
        tag: "request",
        version: VERSION,
        requestId: "1",
        snapshotId: null,
        operation: "recipe.validate",
        args: { recipe: { schemaVersion: 1, query: text } },
      },
    ]) {
      const decoded = decodeFrame(utf8.encode(JSON.stringify(value)));
      if (fixture.valid) assert.deepEqual(parseInbound(decoded), value);
      else invalidMessage(() => parseInbound(decoded));
    }
  }
  for (const value of fixtures.validJson) {
    assert.deepEqual(decodeFrame(utf8.encode(value)), JSON.parse(value) as unknown);
  }
  for (const value of fixtures.invalidJson) {
    invalidFrame(() => decodeFrame(utf8.encode(value)));
  }
});

function objectPaths(value: unknown, path: (string | number)[] = []): (string | number)[][] {
  if (value === null || typeof value !== "object") return [];
  if (Array.isArray(value)) {
    return value.flatMap((entry: unknown, index: number) => objectPaths(entry, [...path, index]));
  }
  return [
    path,
    ...Object.entries(value).flatMap(([key, entry]) => objectPaths(entry, [...path, key])),
  ];
}

function atPath(value: unknown, path: (string | number)[]): Record<string, unknown> {
  let target = value;
  for (const key of path) {
    target = (target as Record<string | number, unknown>)[key];
  }
  return target as Record<string, unknown>;
}

test("all fields are required and unknown fields are rejected at every object level", () => {
  for (const [values, parse] of [
    [fixtures.validInbound, parseInbound],
    [fixtures.validOutbound, parseOutbound],
  ] as const) {
    for (const original of values) {
      for (const path of objectPaths(original)) {
        const extra = structuredClone(original);
        atPath(extra, path).unexpectedField = null;
        invalidMessage(() => parse(extra));
        for (const key of Object.keys(atPath(original, path))) {
          const missing = structuredClone(original);
          delete atPath(missing, path)[key];
          invalidMessage(() => parse(missing));
        }
      }
    }
  }
});

test("non-JSON shapes, unsafe integers, and invalid Unicode are rejected", () => {
  for (const value of [
    null,
    undefined,
    true,
    1,
    "hello",
    [],
    new Date(),
    Object.create({ tag: "shutdown", version: 1 }),
  ]) {
    invalidMessage(() => parseInbound(value));
    invalidMessage(() => parseOutbound(value));
  }
  for (const pageSize of [
    NaN,
    Infinity,
    -Infinity,
    Number.MAX_SAFE_INTEGER + 1,
    1.5,
    -1,
    undefined,
    null,
    true,
    "1",
  ]) {
    invalidMessage(() => parseInbound({ ...query, args: { ...query.args, pageSize } }));
  }
  for (const text of ["\ud800", "\udc00", "a\ud800z", "\udc00\ud800"]) {
    invalidMessage(() => parseInbound({ ...query, args: { ...query.args, text } }));
    invalidMessage(() => parseOutbound({ tag: "fatal", code: "InvalidFrame", message: text }));
  }
  assert.equal(parseInbound({ ...query, args: { ...query.args, text: "🧠" } }).tag, "request");
});

test("operation discriminants expose exactly their argument types", () => {
  function readArgument(request: Request): string {
    switch (request.operation) {
      case "capabilities":
      case "snapshot.dispose":
        return request.operation;
      case "snapshot.load":
        return request.args.source;
      case "query":
        return request.args.text;
      case "scene":
        return String(request.args.maxItems);
      case "details":
        return request.args.objectId;
      case "recipe.validate":
        return request.args.recipe.query;
      case "export":
        return request.args.format;
      default: {
        const exhaustive: never = request;
        return exhaustive;
      }
    }
  }
  for (const value of fixtures.validInbound) {
    const frame = parseInbound(value);
    if (frame.tag === "request") assert.equal(typeof readArgument(frame), "string");
  }
});

test("canonical string forms never accept trailing line terminators", () => {
  for (const suffix of ["\n", "\r", "\r\n", "\u2028", "\u2029"]) {
    invalidMessage(() =>
      parseInbound({ tag: "hello", versions: [1], extensions: ["test.name" + suffix] }),
    );
    invalidMessage(() => parseInbound({ ...query, requestId: "1" + suffix }));
    invalidMessage(() => parseInbound({ ...query, snapshotId: scope + suffix }));
    invalidMessage(() => parseInbound({ ...query, args: { ...query.args, cursor: "0" + suffix } }));
    for (const invalidItem of [
      { ...item, objectId: "1" + suffix },
      { ...item, address: item.address + suffix },
      { ...item, size: "0" + suffix },
    ]) {
      invalidMessage(() =>
        parseOutbound({
          tag: "success",
          version: VERSION,
          requestId: "1",
          snapshotId: scope,
          result: { tag: "page", items: [invalidItem], nextCursor: null, truncated: false },
        }),
      );
    }
  }
  assert.doesNotThrow(() => parseInbound({ ...query, args: { ...query.args, text: "objects\n" } }));
});

test("capabilities and every advertised limit are exact", () => {
  const ready = { tag: "ready", version: VERSION, capabilities: CAPABILITIES };
  assert.deepEqual(parseOutbound(ready), ready);
  assert.deepEqual(CAPABILITIES.operations, OPERATIONS);
  for (const name of Object.keys(LIMITS) as (keyof typeof LIMITS)[]) {
    invalidMessage(() =>
      parseOutbound({
        ...ready,
        capabilities: {
          ...CAPABILITIES,
          limits: { ...LIMITS, [name]: LIMITS[name] + 1 },
        },
      }),
    );
  }
});

test("all error and fatal codes accept their bounded message shape", () => {
  for (const code of ERROR_CODES) {
    const frame = {
      tag: "error",
      version: VERSION,
      requestId: maxUint64,
      snapshotId: null,
      error: { code, message: "m".repeat(256), retryable: false },
    };
    assert.deepEqual(parseOutbound(frame), frame);
    invalidMessage(() =>
      parseOutbound({ ...frame, error: { ...frame.error, message: "m".repeat(257) } }),
    );
  }
  for (const code of FATAL_CODES) {
    const frame = { tag: "fatal", code, message: "m".repeat(256) };
    assert.deepEqual(parseOutbound(frame), frame);
    invalidMessage(() => parseOutbound({ ...frame, message: "m".repeat(257) }));
  }
});

test("text and extension string/array limits are inclusive", () => {
  const longest = { ...query, args: { ...query.args, text: "x".repeat(4096) } };
  assert.deepEqual(parseInbound(longest), longest);
  invalidMessage(() =>
    parseInbound({ ...longest, args: { ...longest.args, text: "x".repeat(4097) } }),
  );
  const recipe = {
    tag: "request",
    version: VERSION,
    requestId: "1",
    snapshotId: null,
    operation: "recipe.validate",
    args: { recipe: { schemaVersion: VERSION, query: "x".repeat(4096) } },
  };
  assert.deepEqual(parseInbound(recipe), recipe);
  invalidMessage(() =>
    parseInbound({
      ...recipe,
      args: { recipe: { ...recipe.args.recipe, query: "x".repeat(4097) } },
    }),
  );
  const hello = {
    tag: "hello",
    versions: [1, 2, 3, 4, 5, 6, 7, 8],
    extensions: ["a." + "b".repeat(62), "a.b", "a.c", "a.d", "a.e", "a.f", "a.g", "a.h"],
  };
  assert.deepEqual(parseInbound(hello), hello);
  invalidMessage(() => parseInbound({ ...hello, versions: [...hello.versions, 9] }));
  invalidMessage(() => parseInbound({ ...hello, extensions: [...hello.extensions, "a.i"] }));
  invalidMessage(() => parseInbound({ ...hello, extensions: ["a." + "b".repeat(63)] }));
});

test("text and message lengths count Unicode scalars, not UTF-16 units", () => {
  const text = "🧠".repeat(4096);
  assert.equal(text.length, 8192);
  const request = { ...query, args: { ...query.args, text } };
  const parsed = parseInbound(request);
  assert.deepEqual(parsed, request);
  assert.deepEqual(decodeFrame(encodeFrame(parsed).subarray(0, -1)), request);
  invalidMessage(() => parseInbound({ ...query, args: { ...query.args, text: text + "🧠" } }));
  const fatal = {
    tag: "fatal",
    code: "InvalidFrame",
    message: "🧠".repeat(256),
  };
  assert.deepEqual(parseOutbound(fatal), fatal);
  invalidMessage(() => parseOutbound({ ...fatal, message: fatal.message + "🧠" }));
});

test("wire numeric tokens use canonical nonnegative safe integer syntax", () => {
  for (const token of [
    "-0",
    "-1",
    "+1",
    "01",
    "1.0",
    "1.0000000000000001",
    "1e0",
    "1E+0",
    "9007199254740992",
    "18446744073709551615",
    "1e999",
  ]) {
    invalidFrame(() => decodeFrame(utf8.encode(`{"n":${token}}`)));
  }
  assert.deepEqual(decodeFrame(utf8.encode('{"n":9007199254740991}')), {
    n: Number.MAX_SAFE_INTEGER,
  });
  assert.deepEqual(decodeFrame(utf8.encode('{"n":0}')), { n: 0 });
  invalidMessage(() =>
    parseInbound({
      tag: "request",
      version: VERSION,
      requestId: "1",
      snapshotId: null,
      operation: "snapshot.load",
      args: { source: "fixture:tiny", delayMs: -0 },
    }),
  );
});

test("page and scene arrays accept 128 items and reject 129", () => {
  for (const tag of ["page", "scene"] as const) {
    const result =
      tag === "page"
        ? { tag, items: Array.from({ length: 128 }, () => item), nextCursor: null, truncated: true }
        : {
            tag,
            schemaVersion: VERSION,
            items: Array.from({ length: 128 }, () => item),
            truncated: true,
          };
    const frame = { tag: "success", version: VERSION, requestId: "1", snapshotId: scope, result };
    assert.deepEqual(parseOutbound(frame), frame);
    invalidMessage(() =>
      parseOutbound({ ...frame, result: { ...result, items: [...result.items, item] } }),
    );
  }
});

test("decoder handles every two-chunk boundary including split UTF-8", () => {
  const bytes = encodeFrame(query);
  for (let split = 0; split <= bytes.length; split++) {
    const decoder = new FrameDecoder();
    const frames = [
      ...decoder.push(bytes.subarray(0, split)),
      ...decoder.push(bytes.subarray(split)),
    ];
    assert.deepEqual(frames, [query]);
    decoder.end();
  }
  const decoder = new FrameDecoder();
  const frames = [];
  for (const byte of bytes) frames.push(...decoder.push(Uint8Array.of(byte)));
  assert.deepEqual(frames, [query]);
  decoder.end();
});

test("coalesced frames and trailing fragments preserve ordering without retained output", () => {
  const hello: Inbound = { tag: "hello", versions: [1], extensions: [] };
  const frames = [hello, query, { tag: "shutdown", version: VERSION }] satisfies Inbound[];
  const bytes = Buffer.concat(frames.map((frame) => encodeFrame(frame)));
  const decoder = new FrameDecoder();
  assert.deepEqual(decoder.push(bytes.subarray(0, bytes.length - 1)), frames.slice(0, -1));
  assert.deepEqual(decoder.push(bytes.subarray(-1)), frames.slice(-1));
  assert.deepEqual(decoder.push(new Uint8Array()), []);
  decoder.end();

  const many = new FrameDecoder();
  const batch = Buffer.concat(Array.from({ length: 1000 }, () => encodeFrame(hello)));
  assert.equal(many.push(batch).length, 1000);
  assert.deepEqual(many.push(new Uint8Array()), []);
  many.end();
});

test("maximum frame byte length excludes LF and counts UTF-8 bytes", () => {
  const prefix = '{"text":"';
  const suffix = '"}';
  const overhead = utf8.encode(prefix + suffix).length;
  const content =
    "é".repeat(Math.floor((LIMITS.maxFrameBytes - overhead) / 2)) +
    "x".repeat((LIMITS.maxFrameBytes - overhead) % 2);
  const text = prefix + content + suffix;
  const bytes = utf8.encode(text);
  assert.equal(bytes.length, LIMITS.maxFrameBytes);
  assert.deepEqual(decodeFrame(bytes), { text: content });
  const decoder = new FrameDecoder();
  assert.deepEqual(decoder.push(bytes), []);
  assert.deepEqual(decoder.push(Uint8Array.of(10)), [{ text: content }]);
  decoder.end();

  const tooLong = Buffer.concat([bytes, Uint8Array.of(32)]);
  invalidFrame(() => decodeFrame(tooLong));
  invalidFrame(() => new FrameDecoder().push(tooLong));
  const fragmented = new FrameDecoder();
  fragmented.push(bytes);
  invalidFrame(() => fragmented.push(Uint8Array.of(32)));
});

test("frames reject invalid UTF-8 and raw line delimiters", () => {
  for (const bytes of [
    Uint8Array.of(0xff),
    Uint8Array.of(0xc0, 0xaf),
    Uint8Array.of(0xe2, 0x82),
    Uint8Array.of(0xed, 0xa0, 0x80),
    Uint8Array.of(0xf4, 0x90, 0x80, 0x80),
    Buffer.concat([utf8.encode('{"text":"'), Uint8Array.of(0xff), utf8.encode('"}')]),
    utf8.encode("{}\r"),
    utf8.encode("{}\n"),
    utf8.encode("\ufeff{}"),
  ]) {
    invalidFrame(() => decodeFrame(bytes));
  }
  for (const text of ["\n", "\r\n", "{}\r\n", " \t\n", "\ufeff{}\n"]) {
    invalidFrame(() => new FrameDecoder().push(utf8.encode(text)));
  }
});

test("duplicate keys, invalid strings, malformed JSON, and trailing values fail safely", () => {
  for (const text of [
    "",
    " \t",
    "null",
    "[]",
    "true",
    "1",
    '"text"',
    "{}{}",
    "{} null",
    '{"tag":"hello","tag":"shutdown"}',
    '{"a":1,"\\u0061":2}',
    '{"nested":{"key":1,"key":2}}',
    '{"array":[{"key":1,"key":2}]}',
    '{"x":"\\ud800"}',
    '{"x":"\\udc00"}',
    '{"\\ud800":1}',
    '{"x":"\\ud800a"}',
    '{"x":"\\uZZZZ"}',
    '{"x":"\\q"}',
    '{"x":"unterminated}',
    '{"x":NaN}',
    '{"x":Infinity}',
    '{"x":01}',
    '{"x":1.}',
    '{"x":1e}',
    '{"x":}',
    '{"x":1,}',
    '{"x":[1,]}',
    '{"x":[,1]}',
    '{"x":true false}',
    '{"__proto__":1,"__proto__":2}',
  ]) {
    invalidFrame(() => decodeFrame(utf8.encode(text)));
  }
  assert.deepEqual(decodeFrame(utf8.encode('{"x":"\\ud83e\\udde0"}')), { x: "🧠" });
  const special = decodeFrame(utf8.encode('{"__proto__":{"safe":true}}'));
  assert.equal(Object.hasOwn(special as object, "__proto__"), true);
  assert.equal(({} as { safe?: boolean }).safe, undefined);
  invalidFrame(() => decodeFrame(utf8.encode('{"privatePath":"secret.dmp",}')));
});

test("JSON container nesting allows depth 16 and rejects depth 17", () => {
  function nested(depth: number): Uint8Array {
    return utf8.encode('{"x":'.repeat(depth) + "0" + "}".repeat(depth));
  }
  assert.doesNotThrow(() => decodeFrame(nested(16)));
  invalidFrame(() => decodeFrame(nested(17)));
  assert.doesNotThrow(() =>
    decodeFrame(utf8.encode('{"x":' + "[".repeat(15) + "0" + "]".repeat(15) + "}")),
  );
  invalidFrame(() =>
    decodeFrame(utf8.encode('{"x":' + "[".repeat(16) + "0" + "]".repeat(16) + "}")),
  );
});

test("EOF is clean only between frames; failures permanently close the decoder", () => {
  const clean = new FrameDecoder();
  clean.end();
  invalidFrame(() => clean.push(utf8.encode("{}\n")));
  for (const text of ["{", "{}", " ", '{"x":"']) {
    const decoder = new FrameDecoder();
    decoder.push(utf8.encode(text));
    invalidFrame(() => decoder.end());
    invalidFrame(() => decoder.push(utf8.encode("{}\n")));
  }
  const failed = new FrameDecoder();
  invalidFrame(() => failed.push(utf8.encode("\n")));
  invalidFrame(() => failed.push(utf8.encode("{}\n")));
  invalidFrame(() => failed.end());
});

test("encoder rejects invalid values and always emits a single bounded LF-terminated frame", () => {
  invalidMessage(() => encodeFrame({ ...query, args: { ...query.args, text: "\ud800" } }));
  invalidMessage(() => encodeFrame({ tag: "bye", version: 2 } as unknown as Outbound));
  const encoded = encodeFrame({
    ...query,
    args: { ...query.args, text: "line one\r\nline two" },
  });
  assert.equal(encoded.filter((byte) => byte === 10).length, 1);
  assert.equal(encoded.includes(13), false);
  assert.ok(encoded.length <= LIMITS.maxFrameBytes + 1);
});
