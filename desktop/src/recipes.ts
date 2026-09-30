import { open, link, unlink, stat, realpath } from "node:fs/promises";
import { basename, dirname, isAbsolute, relative, resolve, sep } from "node:path";
import { randomUUID } from "node:crypto";
import { validateWorkspaceDocument } from "./native-protocol.js";
import { isValidUnicode, ProtocolError } from "./protocol.js";
import type { Recipe, WorkspaceDocument } from "./native-types.js";

export const MAX_RECIPE_BYTES = 262144;
export type DumpIdentity = { path: string; size: string; modifiedUtc: string };

function parseRecipeJson(source: string): unknown {
  let offset = 0;
  const whitespace = () => {
    while (/[ \t\r\n]/.test(source[offset] ?? "\0")) offset++;
  };
  const string = (): string => {
    const start = offset++;
    while (offset < source.length) {
      const character = source[offset++];
      if (character === "\\") offset++;
      else if (character === '"') {
        const value: unknown = JSON.parse(source.slice(start, offset));
        if (typeof value !== "string" || !isValidUnicode(value)) badRecipe();
        return value;
      }
    }
    return badRecipe();
  };
  const value = (depth: number): void => {
    if (depth > 16) badRecipe("Recipe exceeds the nesting limit.");
    whitespace();
    const first = source[offset];
    if (first === '"') {
      string();
      return;
    }
    if (first === "{" || first === "[") {
      offset++;
      const end = first === "{" ? "}" : "]";
      const seen = new Set<string>();
      whitespace();
      if (source[offset] === end) {
        offset++;
        return;
      }
      for (;;) {
        whitespace();
        if (first === "{") {
          if (source[offset] !== '"') badRecipe();
          const key = string();
          if (seen.has(key)) badRecipe("Recipe contains duplicate properties.");
          seen.add(key);
          whitespace();
          if (source[offset++] !== ":") badRecipe();
        }
        value(depth + 1);
        whitespace();
        const separator = source[offset++];
        if (separator === end) return;
        if (separator !== ",") badRecipe();
      }
    }
    const literal =
      /^(?:true|false|null|-?(?:0|[1-9][0-9]*)(?:\.[0-9]+)?(?:[eE][+-]?[0-9]+)?)/.exec(
        source.slice(offset),
      );
    if (!literal) badRecipe();
    offset += literal[0].length;
  };
  whitespace();
  if (source[offset] !== "{") badRecipe();
  value(0);
  whitespace();
  if (offset !== source.length) badRecipe();
  return JSON.parse(source) as unknown;
}

function badRecipe(message = "Invalid or unsupported recipe document."): never {
  throw new ProtocolError("InvalidRecipe", message);
}

function record(value: unknown, keys: string[]): Record<string, unknown> {
  if (value === null || typeof value !== "object" || Array.isArray(value)) badRecipe();
  if (Object.keys(value).sort().join(",") !== keys.sort().join(",")) badRecipe();
  return value as Record<string, unknown>;
}

export function validateRecipe(value: unknown): Recipe {
  const item = record(value, [
    "schemaVersion",
    "query",
    "settings",
    "annotations",
    "hiddenLayers",
    "snapshot",
  ]);
  if (item.schemaVersion !== 1 && item.schemaVersion !== 2)
    badRecipe("Unsupported recipe version. This workspace reads versions 1 and 2.");
  const settings =
    item.schemaVersion === 1
      ? {
          ...record(item.settings, [
            "plotWidth",
            "viewport",
            "redaction",
            "maxResults",
            "maxElements",
          ]),
          layout: "linear",
        }
      : item.settings;
  const document = validateWorkspaceDocument({
    query: item.query,
    settings,
    annotations: item.annotations,
    hiddenLayers: item.hiddenLayers,
  });
  let snapshot: Recipe["snapshot"] = null;
  if (item.snapshot !== null) {
    const source = record(item.snapshot, ["locator", "size", "modifiedUtc"]);
    if (
      typeof source.locator !== "string" ||
      source.locator.length === 0 ||
      source.locator.length > 1024 ||
      !isValidUnicode(source.locator) ||
      /[\u0000-\u001f\\:]/.test(source.locator) ||
      source.locator.startsWith("/") ||
      source.locator.split("/").some((part) => part === "" || part === "." || part === "..") ||
      typeof source.size !== "string" ||
      !/^(0|[1-9][0-9]{0,19})(?![\s\S])/.test(source.size) ||
      BigInt(source.size) > 18446744073709551615n ||
      typeof source.modifiedUtc !== "string" ||
      !/^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z(?![\s\S])/.test(source.modifiedUtc) ||
      !Number.isFinite(Date.parse(source.modifiedUtc)) ||
      new Date(source.modifiedUtc).toISOString() !== source.modifiedUtc
    )
      badRecipe("Recipe snapshot locator or identity is invalid.");
    snapshot = {
      locator: source.locator,
      size: source.size,
      modifiedUtc: source.modifiedUtc,
    };
  }
  return { schemaVersion: 2, ...document, snapshot };
}

export async function identifyDump(path: string): Promise<DumpIdentity> {
  const canonical = await realpath(path);
  const info = await stat(canonical, { bigint: true });
  if (!info.isFile()) throw new ProtocolError("InvalidDump", "Choose a regular dump file.");
  return { path: canonical, size: info.size.toString(), modifiedUtc: info.mtime.toISOString() };
}

export function identityMatches(
  expected: NonNullable<Recipe["snapshot"]>,
  actual: DumpIdentity,
): boolean {
  return expected.size === actual.size && expected.modifiedUtc === actual.modifiedUtc;
}

export function recipeForSave(
  document: WorkspaceDocument,
  recipePath: string,
  dump: DumpIdentity | null,
  unloaded?: { recipePath: string; snapshot: NonNullable<Recipe["snapshot"]> } | null,
): Recipe {
  dump ??= unloaded
    ? {
        path: resolve(dirname(unloaded.recipePath), ...unloaded.snapshot.locator.split("/")),
        size: unloaded.snapshot.size,
        modifiedUtc: unloaded.snapshot.modifiedUtc,
      }
    : null;
  let snapshot: Recipe["snapshot"] = null;
  if (dump) {
    const candidate = relative(dirname(recipePath), dump.path);
    const inScope =
      candidate.length > 0 &&
      !isAbsolute(candidate) &&
      candidate !== ".." &&
      !candidate.startsWith(`..${sep}`);
    snapshot = {
      locator: (inScope ? candidate : basename(dump.path)).split(sep).join("/"),
      size: dump.size,
      modifiedUtc: dump.modifiedUtc,
    };
  }
  return validateRecipe({ schemaVersion: 2, ...document, snapshot });
}

export async function recipeDependency(
  recipePath: string,
  recipe: Recipe,
): Promise<DumpIdentity | null> {
  if (!recipe.snapshot) return null;
  const folder = await realpath(dirname(recipePath));
  const candidate = resolve(folder, ...recipe.snapshot.locator.split("/"));
  try {
    const identity = await identifyDump(candidate);
    const scoped = relative(folder, identity.path);
    if (isAbsolute(scoped) || scoped === ".." || scoped.startsWith(`..${sep}`)) {
      throw new ProtocolError(
        "InvalidRecipe",
        "Recipe dependency escapes its folder through a link. Relocate it explicitly.",
      );
    }
    return identity;
  } catch (error) {
    if (error instanceof Error && "code" in error && error.code === "ENOENT") return null;
    throw error;
  }
}

export async function readRecipe(path: string): Promise<Recipe> {
  const file = await open(path, "r");
  try {
    if (!(await file.stat()).isFile()) badRecipe("Choose a regular recipe file.");
    const buffer = Buffer.alloc(MAX_RECIPE_BYTES + 1);
    let count = 0;
    while (count < buffer.length) {
      const next = await file.read(buffer, count, buffer.length - count, null);
      if (next.bytesRead === 0) break;
      count += next.bytesRead;
    }
    if (count > MAX_RECIPE_BYTES) badRecipe("Recipe exceeds the 256 KiB document limit.");
    let parsed: unknown;
    try {
      parsed = parseRecipeJson(
        new TextDecoder("utf-8", { fatal: true, ignoreBOM: true }).decode(
          buffer.subarray(0, count),
        ),
      );
    } catch {
      return badRecipe("Recipe is not valid UTF-8 JSON.");
    }
    return validateRecipe(parsed);
  } finally {
    await file.close();
  }
}

export async function writeRecipe(path: string, recipe: Recipe): Promise<void> {
  const bytes = Buffer.from(`${JSON.stringify(validateRecipe(recipe), null, 2)}\n`, "utf8");
  if (bytes.length > MAX_RECIPE_BYTES) badRecipe("Recipe exceeds the 256 KiB document limit.");
  const temporary = resolve(dirname(path), `.memoryvisualizer-${randomUUID()}.tmp`);
  const file = await open(temporary, "wx", 0o600);
  try {
    try {
      await file.writeFile(bytes);
      await file.sync();
    } finally {
      await file.close();
    }
    // A same-directory hard link publishes a complete file without overwriting.
    await link(temporary, path);
  } finally {
    await unlink(temporary);
  }
}
