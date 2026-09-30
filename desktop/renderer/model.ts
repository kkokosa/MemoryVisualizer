import {
  DEFAULT_SETTINGS,
  NATIVE_LIMITS,
  type Bounds,
  type DesktopOutcome,
  type Diagnostic,
  type QueryValue,
  type SceneElement,
  type SceneSettings,
  type WorkspaceAPI,
} from "../src/native-types.js";

export const UINT64_MAX = (1n << 64n) - 1n;
export const LAYERS = ["Memory", "Segments", "Generations", "Free objects", "Objects", "Pins"];
export const TEMPLATES = [
  {
    name: "Segments + generations",
    description: "A useful first illustration, without scanning every object.",
    query:
      "MATCH (seg: Segment) RETURN seg AS BOX (Background = Blue, Width = 40);\n" +
      "MATCH (gen: Generation) RETURN gen.Generation AS BOX " +
      "(Label = gen.Generation, LabelPosition = InnerCenter, Background = Grey, Width = 24);",
  },
  {
    name: "Generation boundaries",
    description: "Label generation intervals in their runtime and heap lanes.",
    query:
      "MATCH (gen: Generation) RETURN gen.Address, gen.Size, gen.Generation " +
      "AS BOX (Label = gen.Generation, Background = Green, Width = 24);",
  },
  {
    name: "Live object pins",
    description: "Bounded live-object markers; narrow with a viewport or predicate.",
    query:
      "MATCH (obj: Object) WHERE obj.IsFree = false RETURN obj.Address, obj.Size, obj.Type " +
      "AS PIN (Label = obj.Type, LabelPosition = OuterLeft, Background = Blue);",
  },
  {
    name: "Free space",
    description: "Show captured free entries, not inferred gaps.",
    query:
      "MATCH (seg: Segment) RETURN seg AS BOX (Background = Grey, Width = 32);\n" +
      "MATCH (obj: Object) WHERE obj.IsFree = true RETURN obj.Address, obj.Size " +
      "AS BOX (Background = Yellow, Width = 20);",
  },
] as const;

export type SettingsDraft = {
  layout: SceneSettings["layout"];
  plotWidth: string;
  maxResults: string;
  maxElements: string;
  viewportEnabled: boolean;
  viewportStart: string;
  viewportSize: string;
  redaction: SceneSettings["redaction"];
};

export function draftSettings(settings: SceneSettings = DEFAULT_SETTINGS): SettingsDraft {
  return {
    layout: settings.layout,
    plotWidth: String(settings.plotWidth),
    maxResults: String(settings.maxResults),
    maxElements: String(settings.maxElements),
    viewportEnabled: settings.viewport !== null,
    viewportStart: settings.viewport?.start ?? "0",
    viewportSize: settings.viewport?.size ?? "4096",
    redaction: { ...settings.redaction },
  };
}

export function canonicalUint64(value: string, label = "Value"): string {
  const normalized = value.trim();
  if (normalized.length > 64 || !/^(?:[0-9]+|0x[0-9a-f]+)$/i.test(normalized)) {
    throw new Error(`${label} must be an unsigned decimal or 0x hexadecimal integer.`);
  }
  const parsed = BigInt(normalized);
  if (parsed > UINT64_MAX) throw new Error(`${label} exceeds uint64.`);
  return parsed.toString();
}

export function canonicalAddress(value: string): string {
  return `0x${BigInt(canonicalUint64(value, "Address")).toString(16).padStart(16, "0")}`;
}

export function boundedInteger(
  value: string,
  minimum: number,
  maximum: number,
  label: string,
): number {
  if (!/^[0-9]+$/.test(value)) throw new Error(`${label} must be a whole number.`);
  const parsed = Number(value);
  if (!Number.isSafeInteger(parsed) || parsed < minimum || parsed > maximum) {
    throw new Error(`${label} must be between ${minimum} and ${maximum}.`);
  }
  return parsed;
}

export function readSettings(draft: SettingsDraft): SceneSettings {
  if (draft.layout !== "linear" && draft.layout !== "compact")
    throw new Error("Address layout must be compact or linear.");
  const viewport = draft.viewportEnabled
    ? {
        start: canonicalUint64(draft.viewportStart, "Viewport start"),
        size: canonicalUint64(draft.viewportSize, "Viewport size"),
      }
    : null;
  if (viewport && BigInt(viewport.start) + BigInt(viewport.size) > UINT64_MAX + 1n) {
    throw new Error("Viewport end must not exceed 2^64.");
  }
  return {
    layout: draft.layout,
    plotWidth: boundedInteger(draft.plotWidth, 64, 4096, "Plot width"),
    maxResults: boundedInteger(draft.maxResults, 1, NATIVE_LIMITS.maxResults, "Maximum results"),
    maxElements: boundedInteger(
      draft.maxElements,
      1,
      NATIVE_LIMITS.maxSceneItems,
      "Maximum elements",
    ),
    viewport,
    redaction: { ...draft.redaction },
  };
}

export type HighlightToken = { text: string; kind: "plain" | "keyword" | "string" | "number" };
const keywords = new Set(
  "MATCH WHERE RETURN AS BOX PIN DRAW MEMORY AND STARTS IN OVERLAPS TRUE FALSE OBJECT SEGMENT GENERATION LABEL LABELPOSITION BACKGROUND WIDTH INNERCENTER OUTERLEFT RUNTIME HEAP".split(
    " ",
  ),
);

export function highlightQuery(text: string): HighlightToken[] {
  const expression = /"(?:\\.|[^"\\])*"?|\b0x[0-9a-f]+\b|\b[0-9]+\b|\b[A-Za-z_][A-Za-z_0-9]*\b/gi;
  const tokens: HighlightToken[] = [];
  let cursor = 0;
  for (const match of text.matchAll(expression)) {
    if (match.index > cursor) tokens.push({ text: text.slice(cursor, match.index), kind: "plain" });
    const value = match[0];
    tokens.push({
      text: value,
      kind: value.startsWith('"')
        ? "string"
        : /^[0-9]/.test(value)
          ? "number"
          : keywords.has(value.toUpperCase())
            ? "keyword"
            : "plain",
    });
    cursor = match.index + value.length;
  }
  if (cursor < text.length) tokens.push({ text: text.slice(cursor), kind: "plain" });
  return tokens;
}

export function diagnosticRange(text: string, diagnostic: Diagnostic): [number, number] {
  const start = Math.max(0, Math.min(text.length, diagnostic.span.offset));
  return [start, Math.max(start, Math.min(text.length, start + diagnostic.span.length))];
}

export function textareaDiagnosticRange(text: string, diagnostic: Diagnostic): [number, number] {
  const [start, end] = diagnosticRange(text, diagnostic);
  // Textarea values normalize line endings; worker spans still address the lossless UTF-16 source.
  const normalizedOffset = (offset: number) => text.slice(0, offset).replace(/\r\n?/g, "\n").length;
  return [normalizedOffset(start), normalizedOffset(end)];
}

export function formatValue(value: QueryValue): string {
  switch (value.kind) {
    case "missing":
      return "—";
    case "entity":
      return `${value.value.kind} · runtime ${value.value.runtime} · ${value.value.address}`;
    default:
      return String(value.value);
  }
}

export function unwrapNative(outcome: DesktopOutcome) {
  if (!outcome.ok) throw new Error(`${outcome.error.code}: ${outcome.error.message}`);
  if (outcome.value.kind === "cancelled") throw new Error("Operation cancelled.");
  if (outcome.value.kind !== "native") throw new Error("Unexpected worker response.");
  return outcome.value.value;
}

export async function collectScenePages(
  api: Pick<WorkspaceAPI, "invoke">,
  sceneId: string,
  isCurrent: () => boolean,
): Promise<{ items: SceneElement[]; bounded: boolean }> {
  let cursor: string | null = null;
  const items: SceneElement[] = [];
  const visited = new Set<string>();
  for (let page = 0; page < 32; page++) {
    if (!isCurrent()) return { items: [], bounded: false };
    const response = unwrapNative(await api.invoke({ kind: "elements", sceneId, cursor }));
    if (!isCurrent()) return { items: [], bounded: false };
    if (response.result.tag !== "elements" || response.result.sceneId !== sceneId) {
      throw new Error("Scene page belongs to a different scene.");
    }
    if (response.result.items.length > NATIVE_LIMITS.maxPageSize) {
      throw new Error("Worker exceeded the scene page limit.");
    }
    items.push(...response.result.items.slice(0, NATIVE_LIMITS.maxSceneItems - items.length));
    cursor = response.result.nextCursor;
    if (cursor === null) return { items, bounded: false };
    if (visited.has(cursor)) throw new Error("Worker returned a repeated scene cursor.");
    visited.add(cursor);
    if (items.length >= NATIVE_LIMITS.maxSceneItems) return { items, bounded: true };
  }
  return { items, bounded: cursor !== null };
}

export function fittedBounds(bounds: Bounds): Bounds {
  return { ...bounds, width: Math.max(1, bounds.width), height: Math.max(1, bounds.height) };
}

export function zoomBounds(view: Bounds, factor: number, original: Bounds): Bounds {
  const width = Math.min(original.width * 16, Math.max(original.width / 64, view.width * factor));
  const ratio = width / view.width;
  const height = view.height * ratio;
  return {
    x: view.x + (view.width - width) / 2,
    y: view.y + (view.height - height) / 2,
    width,
    height,
  };
}
