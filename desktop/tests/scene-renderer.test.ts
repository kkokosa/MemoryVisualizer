import assert from "node:assert/strict";
import test from "node:test";
import { DesktopOperationGate } from "../renderer/operation-gate.js";
import {
  annotationBasis,
  applyDetachedAnnotation,
  canExportCurrentScene,
  changeAnnotationBasis,
  isDirtyAfterSave,
  restoreRecipeAnnotations,
  type AnnotationScope,
} from "../renderer/annotation-scope.js";
import {
  canonicalAddress,
  canonicalUint64,
  collectScenePages,
  diagnosticRange,
  draftSettings,
  fittedBounds,
  elementBounds,
  highlightQuery,
  initialBounds,
  readableBounds,
  resizedBounds,
  revealBounds,
  viewScale,
  readSettings,
  textareaDiagnosticRange,
  zoomBounds,
} from "../renderer/model.js";
import { createSceneSvg, SVG_NAMESPACE } from "../renderer/scene-renderer.js";
import type {
  DesktopOutcome,
  DesktopValue,
  NativeResult,
  SceneElement,
  SceneGapMarkers,
  SceneInfo,
  WorkspaceAPI,
} from "../src/native-types.js";

class TestNode {
  readonly attributes = new Map<string, string>();
  readonly children: TestNode[] = [];
  readonly listeners = new Map<string, (event: unknown) => void>();
  textContent = "";
  constructor(
    readonly namespace: string,
    readonly tag: string,
  ) {}
  setAttribute(key: string, value: string) {
    this.attributes.set(key, value);
  }
  setAttributeNS(namespace: string, key: string, value: string) {
    assert.equal(namespace, "http://www.w3.org/XML/1998/namespace");
    this.setAttribute(key, value);
  }
  appendChild(node: TestNode) {
    this.children.push(node);
    return node;
  }
  addEventListener(event: string, callback: (event: unknown) => void) {
    this.listeners.set(event, callback);
  }
}
const document = {
  createElementNS(namespace: string, tag: string) {
    assert.equal(namespace, SVG_NAMESPACE);
    assert.ok(["svg", "rect", "line", "g", "defs", "clipPath", "text", "desc"].includes(tag));
    return new TestNode(namespace, tag);
  },
} as unknown as Document;
const bounds = { x: 0, y: 0, width: 1600, height: 144 };
const scene: SceneInfo = {
  schemaVersion: 2,
  layout: "compact",
  gaps: null,
  sceneId: "scene-1",
  snapshotId: "snapshot-1",
  bounds,
  lanes: [
    { id: "lane-0", runtime: 0, heap: 2, bounds: { x: 528, y: 16, width: 1024, height: 96 } },
  ],
  theme: { background: "#ffffff", stroke: "#202020", text: "#202020" },
  redaction: { addresses: false, strings: false, paths: false, labels: false },
  elementCount: 1,
  status: "complete",
  truncationReasons: [],
};
const element: SceneElement = {
  id: "element-0",
  laneId: "lane-0",
  layer: 4,
  geometry: { kind: "rectangle", bounds: { x: 528.125, y: 56, width: 32.5, height: 16 } },
  bounds: { x: 528.125, y: 56, width: 32.5, height: 16 },
  style: { fill: "#112233", stroke: "#202020", strokeWidth: 1.5 },
  text: {
    bounds: { x: 524, y: 57, width: 64, height: 14 },
    lines: [{ text: '<script>alert("x")</script>&', x: 524, baseline: 68, width: 64 }],
    cellWidth: 8,
    fontSize: 12,
    lineHeight: 14,
    fill: "#ffffff",
    replacedCodeUnits: 0,
    isTruncated: false,
  },
  source: {
    runtime: 3,
    heap: 2,
    statementIndex: 0,
    kind: "object",
    address: "0xfffffffffffffff0",
    size: "16",
    segmentAddress: "0xffffffffffffff00",
    methodTable: "0xffffffffffffffff",
  },
  isClipped: false,
};
function nodes(root: TestNode): TestNode[] {
  return [root, ...root.children.flatMap(nodes)];
}
function render(info: SceneInfo = scene, items: SceneElement[] = [element]) {
  return createSceneSvg(document, info, items) as unknown as TestNode;
}

const gaps: SceneGapMarkers = {
  offsets: [620.25, 941.75],
  band: { x: 0, y: 16, width: 12, height: 96 },
  lines: [
    { start: { x: 2, y: 8 }, finish: { x: 5, y: 14 } },
    { start: { x: 7, y: 8 }, finish: { x: 10, y: 14 } },
  ],
  style: { fill: "#eeeeee", stroke: "#606060", strokeWidth: 1 },
  legend: {
    bounds: { x: 528, y: 120, width: 320, height: 14 },
    lines: [{ text: "// Compressed address gaps", x: 528, baseline: 131, width: 200 }],
    cellWidth: 8,
    fontSize: 12,
    lineHeight: 14,
    fill: "#202020",
    replacedCodeUnits: 0,
    isTruncated: false,
  },
};

test("new scenes default to compact while explicit linear settings round trip", () => {
  assert.equal(readSettings(draftSettings()).layout, "compact");
  const linear = { ...readSettings(draftSettings()), layout: "linear" as const };
  assert.deepEqual(readSettings(draftSettings(linear)), linear);
});

test("gap glyphs use only shared positioned primitives and never become selectable source elements", () => {
  const tree = render({ ...scene, gaps });
  const all = nodes(tree);
  assert.equal(tree.attributes.get("data-scene-version"), "2");
  assert.equal(tree.attributes.get("data-address-layout"), "compact");
  const markers = all.filter((node) => node.attributes.get("data-kind") === "address-gap");
  assert.deepEqual(
    markers.map((node) => node.attributes.get("transform")),
    ["translate(620.25,0)", "translate(941.75,0)"],
  );
  for (const marker of markers) {
    assert.equal(marker.attributes.has("data-element-id"), false);
    assert.equal(marker.attributes.has("data-address"), false);
    assert.equal(marker.attributes.has("tabindex"), false);
    assert.equal(marker.listeners.size, 0);
    assert.equal(marker.children[0]?.attributes.get("width"), "12");
    assert.equal(marker.children[1]?.attributes.get("x1"), "2");
    assert.equal(marker.children[1]?.attributes.get("y2"), "14");
    assert.equal(marker.children[1]?.attributes.get("stroke"), "#606060");
  }
  assert.equal(
    all.filter((node) => node.tag === "text" && node.textContent.startsWith("//")).length,
    0,
  );
  const description = all.filter((node) => node.tag === "desc");
  assert.equal(description.length, 1);
  assert.equal(description[0]?.textContent, "// Compressed address gaps");
  assert.equal(description[0]?.children.length, 0);
  assert.equal(tree.attributes.get("aria-describedby"), "address-layout-description");
  const bounded = render(
    { ...scene, gaps: { ...gaps, offsets: Array.from({ length: 2000 }, (_, i) => i) } },
    [],
  );
  assert.equal(
    nodes(bounded).filter((node) => node.attributes.get("data-kind") === "address-gap").length,
    1025,
  );
  assert.ok(nodes(bounded).length < 4200);
  for (const hidden of [
    { ...scene, gaps, layout: "linear" as const },
    { ...scene, gaps, redaction: { ...scene.redaction, addresses: true } },
  ]) {
    const rendered = nodes(render(hidden));
    assert.ok(rendered.every((node) => node.attributes.get("data-kind") !== "address-gap"));
    assert.ok(rendered.every((node) => !node.textContent.startsWith("//")));
  }
});

test("heap lanes retain their identity without painting a misleading enclosing rectangle", () => {
  const lane = nodes(render()).find((node) => node.attributes.get("id") === "lane-0")!;
  assert.equal(lane.tag, "g");
  assert.equal(lane.attributes.get("data-runtime"), "0");
  assert.equal(lane.attributes.get("data-heap"), "2");
  assert.equal(lane.attributes.has("stroke"), false);
  assert.equal(lane.children.length, 0);
});

test("pin camera starts with readable text and preserves pixel scale through pane resizing", () => {
  const pin: SceneElement = {
    ...element,
    layer: 5,
    geometry: { kind: "line", start: { x: 1000, y: 56 }, finish: { x: 1000, y: 72 } },
    bounds: { x: 1000, y: 56, width: 0, height: 16 },
    text: { ...element.text!, bounds: { x: 600, y: 57, width: 392, height: 14 } },
  };
  const original = structuredClone(pin);
  const large = { ...bounds, height: 50000 };
  const viewport = { width: 840, height: 280 };
  const camera = initialBounds(large, [pin], viewport);
  assert.deepEqual(camera, { x: 584, y: 0, width: 840, height: 280 });
  assert.equal(
    Math.min(viewport.width / camera.width, viewport.height / camera.height) * pin.text!.fontSize,
    12,
  );
  const next = { width: 620, height: 320 };
  const resized = resizedBounds(camera, viewport, next);
  assert.equal(
    Math.min(next.width / resized.width, next.height / resized.height) * pin.text!.fontSize,
    12,
  );
  const farAway = { x: 820, y: 49000, width: 420, height: 28 };
  const revealed = revealBounds(resized, farAway);
  assert.ok(revealed.x <= farAway.x && revealed.x + revealed.width >= farAway.x + farAway.width);
  assert.ok(revealed.y <= farAway.y && revealed.y + revealed.height >= farAway.y + farAway.height);
  assert.equal(revealed.width, resized.width);
  assert.equal(revealed.height, resized.height);
  assert.equal(viewScale(camera, viewport), 1);
  assert.ok(viewScale(camera, viewport) / viewScale(large, viewport) > 100);
  assert.deepEqual(elementBounds(pin), { x: 600, y: 56, width: 400, height: 16 });
  assert.deepEqual(pin, original, "Camera changes must not relayout or mutate worker primitives.");
  assert.deepEqual(initialBounds(bounds, [element], viewport), fittedBounds(bounds));
  assert.deepEqual(fittedBounds(large), large, "Explicit Fit must remain available.");
  assert.throws(() => readableBounds(large, [pin], { width: 0, height: 0 }), /viewport/);
  const veryTallPin = { ...pin, text: { ...pin.text!, bounds: { ...pin.text!.bounds, y: 2048 } } };
  const tallCamera = readableBounds(large, [veryTallPin], viewport);
  assert.equal(tallCamera.y, 2032);
});

test("uint64 inputs are canonicalized losslessly and invalid ranges are rejected", () => {
  assert.equal(canonicalUint64("0xffffffffffffffff"), "18446744073709551615");
  assert.equal(canonicalUint64("9007199254740993"), "9007199254740993");
  assert.equal(canonicalAddress("18446744073709551615"), "0xffffffffffffffff");
  assert.equal(canonicalAddress("9007199254740993"), "0x0020000000000001");
  assert.equal(canonicalUint64("00000042"), "42");
  assert.equal(canonicalUint64(" 0XABCDEF "), "11259375");
  for (const value of ["18446744073709551616", "-1", "1e3", "1.5", "", "0x", "+4", "12suffix"]) {
    assert.throws(() => canonicalUint64(value), Error);
  }
  const settings = draftSettings();
  settings.viewportEnabled = true;
  settings.viewportStart = "0xffffffffffffffff";
  settings.viewportSize = "1";
  assert.deepEqual(readSettings(settings).viewport, { start: "18446744073709551615", size: "1" });
  assert.throws(() => readSettings({ ...settings, viewportSize: "2" }), /2\^64/);
  assert.throws(() => readSettings({ ...settings, maxResults: "4097" }), /4096/);
  assert.throws(() => readSettings({ ...settings, maxElements: "1025" }), /1024/);
  assert.throws(() => readSettings({ ...settings, plotWidth: "63" }), /64/);
});

test("syntax highlighting retains untrusted text and diagnostic offsets use UTF16 code units", () => {
  const query = 'MATCH (obj: Object) RETURN "🧠<script>&";\r\nbad';
  const tokens = highlightQuery(query);
  assert.equal(tokens.map((token) => token.text).join(""), query);
  assert.equal(tokens[0]?.kind, "keyword");
  assert.ok(tokens.some((token) => token.kind === "string" && token.text.includes("<script>")));
  const offset = query.indexOf("bad");
  assert.deepEqual(
    diagnosticRange(query, {
      code: "MQL001",
      message: "Invalid token",
      span: { offset, length: 3, line: 2, column: 1 },
    }),
    [offset, offset + 3],
  );
  assert.equal(
    query.slice(
      ...diagnosticRange(query, {
        code: "MQL001",
        message: "Emoji",
        span: { offset: query.indexOf("🧠"), length: 2, line: 1, column: 1 },
      }),
    ),
    "🧠",
  );
  assert.deepEqual(
    diagnosticRange(query, {
      code: "MQL001",
      message: "EOF",
      span: { offset: query.length, length: 0, line: 2, column: 4 },
    }),
    [query.length, query.length],
  );
});

test("SVG uses safe namespace primitives with exact engine geometry, clipping and typography", () => {
  const tree = render();
  const all = nodes(tree);
  assert.equal(tree.attributes.get("viewBox"), "0 0 1600 144");
  const group = all.find((node) => node.attributes.get("id") === "element-0")!;
  const rect = group.children[0]!;
  assert.equal(rect.tag, "rect");
  assert.equal(rect.attributes.get("x"), "528.125");
  assert.equal(rect.attributes.get("width"), "32.5");
  assert.equal(rect.attributes.get("fill"), "#112233");
  assert.equal(rect.attributes.get("stroke-width"), "1.5");
  assert.equal(group.attributes.get("data-address"), "0xfffffffffffffff0");
  assert.equal(group.attributes.get("data-method-table"), "0xffffffffffffffff");
  const clip = all.find((node) => node.tag === "clipPath")!;
  assert.equal(clip.attributes.get("id"), "element-0-text-clip");
  assert.equal(clip.attributes.get("clipPathUnits"), "userSpaceOnUse");
  assert.equal(clip.children[0]?.attributes.get("width"), "64");
  const text = all.find((node) => node.tag === "text")!;
  assert.equal(text.textContent, '<script>alert("x")</script>&');
  assert.equal(text.children.length, 0);
  assert.equal(text.attributes.get("x"), "524");
  assert.equal(text.attributes.get("y"), "68");
  assert.equal(text.attributes.get("textLength"), "64");
  assert.equal(text.attributes.get("lengthAdjust"), "spacingAndGlyphs");
  assert.equal(text.attributes.get("font-variant-ligatures"), "none");
  assert.equal(text.attributes.get("xml:space"), "preserve");
  assert.ok(all.every((node) => !node.attributes.has("style") && !node.attributes.has("onclick")));
});

test("selection uses exact IDs and redacted scenes never retain source attributes", () => {
  let selected = "";
  const tree = createSceneSvg(document, scene, [element], {
    onSelect: (id) => {
      selected = id;
    },
  }) as unknown as TestNode;
  const group = nodes(tree).find((node) => node.attributes.get("id") === "element-0")!;
  group.listeners.get("click")?.({});
  assert.equal(selected, element.id);
  const hidden = createSceneSvg(document, scene, [element], {
    hiddenLayers: [4],
  }) as unknown as TestNode;
  assert.ok(!nodes(hidden).some((node) => node.attributes.has("data-element-id")));
  const redacted = render({ ...scene, redaction: { ...scene.redaction, addresses: true } });
  for (const node of nodes(redacted)) {
    for (const key of [
      "data-address",
      "data-method-table",
      "data-segment-address",
      "data-runtime",
      "data-size",
    ]) {
      assert.equal(node.attributes.has(key), false);
    }
    assert.ok(!node.attributes.get("aria-label")?.includes("ffffffff"));
  }
  assert.equal(redacted.attributes.get("data-address-layout"), "schematic");
});

test("unsafe style inputs cannot create external resource references; line geometry is direct", () => {
  const line: SceneElement = {
    ...element,
    geometry: { kind: "line", start: { x: 700.25, y: 40 }, finish: { x: 700.25, y: 96 } },
    style: { fill: "url(https://example.invalid/)", stroke: "#202020", strokeWidth: 2 },
  };
  const shape = nodes(render(scene, [line])).find((node) => node.tag === "line")!;
  assert.equal(shape.attributes.get("x1"), "700.25");
  assert.equal(shape.attributes.get("x2"), "700.25");
  assert.equal(shape.attributes.get("y2"), "96");
  assert.equal(shape.attributes.get("fill"), "#202020");
  assert.throws(() => render({ ...scene, bounds: { ...bounds, width: Infinity } }), /finite/);
});

test("scene paging stops at 32 pages and 1024 items without fetching the whole heap", async () => {
  let calls = 0;
  const api: Pick<WorkspaceAPI, "invoke"> = {
    async invoke(command) {
      assert.equal(command.kind, "elements");
      calls++;
      return {
        ok: true,
        value: {
          kind: "native",
          value: {
            tag: "success",
            version: 3,
            requestId: String(calls),
            snapshotId: "snapshot-1",
            result: {
              tag: "elements",
              sceneId: "scene-1",
              items: Array.from({ length: 32 }, () => element),
              nextCursor: `page-${calls}`,
            },
          },
        },
      };
    },
  };
  const result = await collectScenePages(api, "scene-1", () => true);
  assert.equal(calls, 32);
  assert.equal(result.items.length, 1024);
  assert.equal(result.bounded, true);
});

test("stale generation discards in-flight scene pages and prevents another request", async () => {
  let current = true;
  let calls = 0;
  const api: Pick<WorkspaceAPI, "invoke"> = {
    async invoke() {
      calls++;
      current = false;
      return {
        ok: true,
        value: {
          kind: "native",
          value: {
            tag: "success",
            version: 3,
            requestId: "1",
            snapshotId: "snapshot-1",
            result: { tag: "elements", sceneId: "scene-1", items: [element], nextCursor: "next" },
          },
        },
      };
    },
  };
  assert.deepEqual(await collectScenePages(api, "scene-1", () => current), {
    items: [],
    bounded: false,
  });
  assert.equal(calls, 1);
  await collectScenePages(api, "scene-1", () => false);
  assert.equal(calls, 1);
});

test("paging rejects wrong scene IDs, oversized pages and repeated cursors", async () => {
  const response = (id: string, items: SceneElement[], next: string | null): DesktopOutcome => ({
    ok: true,
    value: {
      kind: "native",
      value: {
        tag: "success",
        version: 3,
        requestId: "1",
        snapshotId: "snapshot-1",
        result: { tag: "elements", sceneId: id, items, nextCursor: next },
      },
    },
  });
  await assert.rejects(
    () =>
      collectScenePages(
        {
          invoke: async () => response("wrong", [element], null),
        },
        "scene-1",
        () => true,
      ),
    /different scene/,
  );
  await assert.rejects(
    () =>
      collectScenePages(
        {
          invoke: async () =>
            response(
              "scene-1",
              Array.from({ length: 33 }, () => element),
              null,
            ),
        },
        "scene-1",
        () => true,
      ),
    /page limit/,
  );
  await assert.rejects(
    () =>
      collectScenePages(
        {
          invoke: async () => response("scene-1", [element], "repeat"),
        },
        "scene-1",
        () => true,
      ),
    /repeated/,
  );
});

test("fit and zoom operate on positioned bounds, never absolute source addresses", () => {
  assert.deepEqual(fittedBounds({ x: 5, y: 10, width: 0, height: 0 }), {
    x: 5,
    y: 10,
    width: 1,
    height: 1,
  });
  assert.deepEqual(zoomBounds(bounds, 0.5, bounds), { x: 400, y: 36, width: 800, height: 72 });
  assert.equal(zoomBounds(bounds, 1 / 10000, bounds).width, bounds.width / 64);
  assert.equal(zoomBounds(bounds, 10000, bounds).width, bounds.width * 16);
});

test("query A to query B detaches ordinal annotations and never silently reattaches on return", () => {
  const basisA = annotationBasis("query A", draftSettings(), 1);
  const basisB = annotationBasis("query B", draftSettings(), 1);
  const original: AnnotationScope = {
    basis: basisA,
    attached: [{ elementId: "element-0", text: "Note for A, not B's element-0" }],
    detached: [],
  };
  const changed = changeAnnotationBasis(original, basisB, "Query changed");
  assert.deepEqual(changed.attached, []);
  assert.deepEqual(changed.detached, [
    {
      elementId: "element-0",
      text: "Note for A, not B's element-0",
      reason: "Query changed",
    },
  ]);
  assert.deepEqual(changeAnnotationBasis(changed, basisA, "Query changed").attached, []);
  assert.deepEqual(original.attached, [
    { elementId: "element-0", text: "Note for A, not B's element-0" },
  ]);
  assert.equal(applyDetachedAnnotation(changed, 0, "element-0", basisA), changed);
  const reapplied = applyDetachedAnnotation(changed, 0, "element-0", basisB);
  assert.equal(reapplied.attached[0]?.text, original.attached[0]?.text);
  assert.deepEqual(reapplied.detached, []);
});

test("settings and successful snapshot replacements detach notes, including reopening the same dump", () => {
  const settings = draftSettings();
  const original: AnnotationScope = {
    basis: annotationBasis("query", settings, 1),
    attached: [{ elementId: "element-0", text: "Source-specific note" }],
    detached: [],
  };
  assert.equal(
    changeAnnotationBasis(original, annotationBasis("query", settings, 1), "No change"),
    original,
  );
  const changedSettings = changeAnnotationBasis(
    original,
    annotationBasis("query", { ...settings, plotWidth: "2048" }, 1),
    "Scene settings changed",
  );
  assert.deepEqual(changedSettings.attached, []);
  assert.equal(changedSettings.detached[0]?.reason, "Scene settings changed");
  const replaced = changeAnnotationBasis(
    original,
    annotationBasis("query", settings, 2),
    "Snapshot replaced",
  );
  assert.deepEqual(replaced.attached, []);
  assert.equal(replaced.detached[0]?.text, "Source-specific note");
  assert.equal(
    changeAnnotationBasis(
      original,
      annotationBasis("query", { ...settings, plotWidth: "01024" }, 1),
      "Equivalent spelling",
    ),
    original,
  );
});

test("explicit recipe restoration preserves same-basis annotations across a fresh native snapshot", () => {
  const original: AnnotationScope = {
    basis: annotationBasis("query", draftSettings(), 1),
    attached: [{ elementId: "element-0", text: "Saved recipe note" }],
    detached: [],
  };
  const restored = restoreRecipeAnnotations(
    original,
    annotationBasis("query", draftSettings(), 2),
    original.attached,
  );
  assert.deepEqual(restored.attached, original.attached);
  assert.deepEqual(restored.detached, []);
  assert.notEqual(restored.attached, original.attached);
  assert.equal(
    changeAnnotationBasis(restored, annotationBasis("query", draftSettings(), 2), "Same basis"),
    restored,
  );
});

test("deliberate reattachment cannot overwrite an existing note or target a stale scene", () => {
  const basis = annotationBasis("query", draftSettings(), 3);
  const scope: AnnotationScope = {
    basis,
    attached: [{ elementId: "element-0", text: "Current note" }],
    detached: [{ elementId: "element-0", text: "Earlier note", reason: "Query changed" }],
  };
  assert.equal(applyDetachedAnnotation(scope, 0, "element-0", basis), scope);
  assert.equal(
    applyDetachedAnnotation(scope, 0, "element-1", { ...basis, snapshotGeneration: 2 }),
    scope,
  );
  const applied = applyDetachedAnnotation(scope, 0, "element-1", basis);
  assert.deepEqual(applied.attached, [
    { elementId: "element-0", text: "Current note" },
    { elementId: "element-1", text: "Earlier note" },
  ]);
  assert.deepEqual(applied.detached, []);
});

test("accepted mismatched recipe snapshot detaches saved ordinal notes before any run", () => {
  const basis = annotationBasis("same query", draftSettings(), 2);
  const original: AnnotationScope = { basis, attached: [], detached: [] };
  const savedNotes = [{ elementId: "element-0", text: "Belongs to the original dump" }];
  const restored = restoreRecipeAnnotations(original, basis, savedNotes, "mismatch");
  assert.deepEqual(restored.attached, []);
  assert.deepEqual(restored.detached, [
    {
      elementId: "element-0",
      text: "Belongs to the original dump",
      reason: "Recipe snapshot fingerprint mismatch",
    },
  ]);
  assert.deepEqual(
    changeAnnotationBasis(restored, basis, "Manual run uses same basis").attached,
    [],
  );
  assert.deepEqual(
    restoreRecipeAnnotations(original, basis, savedNotes, "ready").attached,
    savedNotes,
  );
  assert.deepEqual(
    restoreRecipeAnnotations(original, basis, savedNotes, "missing").attached,
    savedNotes,
  );
  assert.deepEqual(
    restoreRecipeAnnotations(original, basis, savedNotes, "unloaded").attached,
    savedNotes,
  );
});

test("export rejects redaction changes until a matching redacted rerun; view-only notes do not invalidate it", () => {
  const settings = draftSettings();
  const basis = annotationBasis("query", settings, 1);
  const result: Extract<NativeResult, { tag: "query" }> = {
    tag: "query",
    queryId: "1",
    status: "complete",
    sourceAvailable: true,
    sourcePartial: false,
    sourceDiagnosticCount: 0,
    rowCount: 1,
    candidates: "1",
    truncationReasons: [],
    diagnostics: [],
    scene,
  };
  assert.equal(canExportCurrentScene(result, basis, "query", settings, 1), true);
  const linear = { ...settings, layout: "linear" as const };
  assert.equal(canExportCurrentScene(result, basis, "query", linear, 1), false);
  const linearBasis = annotationBasis("query", linear, 1);
  assert.equal(canExportCurrentScene(result, linearBasis, "query", linear, 1), false);
  assert.equal(
    canExportCurrentScene(
      { ...result, scene: { ...scene, layout: "linear" } },
      linearBasis,
      "query",
      linear,
      1,
    ),
    true,
  );
  const noted: AnnotationScope = {
    basis,
    attached: [{ elementId: "element-0", text: "Compact view note" }],
    detached: [],
  };
  assert.equal(changeAnnotationBasis(noted, linearBasis, "Layout changed").attached.length, 0);
  const redacted = { ...settings, redaction: { ...settings.redaction, addresses: true } };
  assert.equal(canExportCurrentScene(result, basis, "query", redacted, 1), false);
  const redactedBasis = annotationBasis("query", redacted, 1);
  assert.equal(canExportCurrentScene(result, redactedBasis, "query", redacted, 1), false);
  const redactedResult = { ...result, scene: { ...scene, redaction: redacted.redaction } };
  assert.equal(canExportCurrentScene(redactedResult, redactedBasis, "query", redacted, 1), true);
  const annotated: AnnotationScope = {
    basis: redactedBasis,
    attached: [{ elementId: "element-0", text: "View-only note" }],
    detached: [],
  };
  assert.equal(canExportCurrentScene(redactedResult, annotated.basis, "query", redacted, 1), true);
  assert.equal(
    canExportCurrentScene(redactedResult, redactedBasis, "different query", redacted, 1),
    false,
  );
  assert.equal(
    canExportCurrentScene(
      redactedResult,
      redactedBasis,
      "query",
      { ...redacted, plotWidth: "2048" },
      1,
    ),
    false,
  );
  assert.equal(canExportCurrentScene(redactedResult, redactedBasis, "query", redacted, 2), false);
});

test("export rejects partial, truncated, failed, absent and invalid-settings results", () => {
  const settings = draftSettings();
  const basis = annotationBasis("query", settings, 1);
  const result: Extract<NativeResult, { tag: "query" }> = {
    tag: "query",
    queryId: "1",
    status: "complete",
    sourceAvailable: true,
    sourcePartial: false,
    sourceDiagnosticCount: 0,
    rowCount: 1,
    candidates: "1",
    truncationReasons: [],
    diagnostics: [],
    scene,
  };
  for (const invalid of [
    null,
    { ...result, sourcePartial: true },
    { ...result, sourceAvailable: false },
    { ...result, status: "truncated" as const },
    { ...result, status: "failed" as const },
    { ...result, scene: null },
    { ...result, scene: { ...scene, status: "truncated" as const } },
  ]) {
    assert.equal(canExportCurrentScene(invalid, basis, "query", settings, 1), false);
  }
  assert.equal(
    canExportCurrentScene(result, basis, "query", { ...settings, plotWidth: "" }, 1),
    false,
  );
});

test("saving query B remains dirty while query A notes are detached and excluded from the recipe", () => {
  const basisA = annotationBasis("query A", draftSettings(), 1);
  const original: AnnotationScope = {
    basis: basisA,
    attached: [{ elementId: "element-0", text: "Note for object A" }],
    detached: [],
  };
  const changed = changeAnnotationBasis(
    original,
    annotationBasis("query B", draftSettings(), 1),
    "Query changed",
  );
  assert.deepEqual(changed.attached, []);
  assert.equal(changed.detached[0]?.text, "Note for object A");
  assert.equal(isDirtyAfterSave(2, 2, changed.detached.length), true);
  assert.equal(isDirtyAfterSave(2, 3, 0), true);
  assert.equal(isDirtyAfterSave(2, 2, 0), false);
  const deliberatelyCleared = { ...changed, detached: [] };
  const reopened = restoreRecipeAnnotations(
    deliberatelyCleared,
    basisA,
    original.attached,
    "ready",
  );
  assert.deepEqual(reopened.attached, original.attached);
  assert.deepEqual(reopened.detached, []);
});

test("diagnostic selection maps CRLF and bare CR to textarea offsets without changing raw UTF16 source", () => {
  const query = 'MATCH (o: Object)\r\nRETURN "🧠";\rBAD\r\n';
  const original = query;
  const textareaValue = query.replace(/\r\n?/g, "\n");
  const bad = {
    code: "MQL001",
    message: "Invalid token",
    span: { offset: query.indexOf("BAD"), length: 3, line: 3, column: 1 },
  };
  assert.deepEqual(diagnosticRange(query, bad), [query.indexOf("BAD"), query.indexOf("BAD") + 3]);
  assert.equal(textareaValue.slice(...textareaDiagnosticRange(query, bad)), "BAD");
  assert.equal(textareaDiagnosticRange(query, bad)[0], query.indexOf("BAD") - 1);
  const astral = {
    ...bad,
    span: { offset: query.indexOf("🧠"), length: 2, line: 2, column: 9 },
  };
  assert.equal(textareaValue.slice(...textareaDiagnosticRange(query, astral)), "🧠");
  const range = textareaDiagnosticRange(query, astral);
  assert.equal(range[1] - range[0], 2);
  const eof = { ...bad, span: { offset: query.length, length: 0, line: 4, column: 1 } };
  assert.deepEqual(textareaDiagnosticRange(query, eof), [
    textareaValue.length,
    textareaValue.length,
  ]);
  assert.deepEqual(diagnosticRange(query, eof), [query.length, query.length]);
  const acrossLines = { ...bad, span: { offset: 0, length: query.length, line: 1, column: 1 } };
  assert.equal(textareaValue.slice(...textareaDiagnosticRange(query, acrossLines)), textareaValue);
  assert.equal(
    highlightQuery(query)
      .map((token) => token.text)
      .join(""),
    original,
  );
  assert.equal(query, original);
});

test("Cancel after native commit applies delayed authoritative openDump/openRecipe/save success", async () => {
  const state = {
    snapshotId: "committed-snapshot-B",
    dumpName: "B.dmp",
    recipeName: "B.recipe.json",
    objectCount: "10",
    sourcePartial: false,
    diagnosticCount: 0,
  };
  const outcomes: ["openDump" | "openRecipe" | "saveRecipe", DesktopValue][] = [
    ["openDump", { kind: "state", state }],
    [
      "openRecipe",
      {
        kind: "recipe",
        state,
        dependency: "ready",
        document: {
          query: "query B",
          settings: readSettings(draftSettings()),
          annotations: [],
          hiddenLayers: [],
        },
      },
    ],
    ["saveRecipe", { kind: "saved", name: "B.recipe.json" }],
  ];
  for (const [command, committedValue] of outcomes) {
    const gate = new DesktopOperationGate();
    const generation = gate.begin(command);
    assert.equal(gate.submit(generation), true);
    let release!: (value: DesktopValue) => void;
    const heldReply = new Promise<DesktopValue>((resolve) => {
      release = resolve;
    });
    const applied: DesktopValue[] = [];
    const delivery = heldReply.then((value) => {
      if (gate.isCurrent(generation)) {
        applied.push(value);
        gate.finish(generation);
      }
    });
    assert.equal(gate.cancel(), "await-terminal", command);
    assert.equal(gate.cancellationRequested, true, command);
    assert.equal(gate.isCurrent(generation), true, command);
    assert.deepEqual(applied, [], "No speculative state change before the terminal reply");
    release(committedValue);
    await delivery;
    assert.deepEqual(applied, [committedValue], `${command} must publish the committed result`);
    assert.equal(gate.cancellationRequested, false);
  }
});

test("query/page cancellation still discards stale delayed success and permits a new generation", async () => {
  for (const kind of ["run", "rows", "elements", "details"] as const) {
    const gate = new DesktopOperationGate();
    const generation = gate.begin(kind);
    assert.equal(gate.submit(generation), true);
    let release!: () => void;
    const reply = new Promise<void>((resolve) => {
      release = resolve;
    });
    let applied = false;
    const delivery = reply.then(() => {
      if (gate.isCurrent(generation)) applied = true;
    });
    assert.equal(gate.cancel(), "invalidated");
    const newer = gate.begin("run");
    assert.equal(gate.submit(newer), true);
    release();
    await delivery;
    assert.equal(applied, false);
    assert.equal(gate.isCurrent(newer), true);
  }
});

test("cancelling a stateful operation before submission never invokes it", () => {
  const gate = new DesktopOperationGate();
  const generation = gate.begin("openDump");
  assert.equal(gate.cancel(), "await-terminal");
  assert.equal(gate.submit(generation), false);
  gate.finish(generation);
  assert.equal(gate.cancellationRequested, false);
  const next = gate.begin("openDump");
  assert.equal(gate.submit(next), true);
});
