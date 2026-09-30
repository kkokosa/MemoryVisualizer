import assert from "node:assert/strict";
import { mkdtemp, mkdir, writeFile, readFile, readdir, rm, symlink } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join, basename } from "node:path";
import test from "node:test";
import { DEFAULT_SETTINGS, type WorkspaceDocument } from "../src/native-types.js";
import {
  identifyDump,
  identityMatches,
  MAX_RECIPE_BYTES,
  readRecipe,
  recipeDependency,
  recipeForSave,
  validateRecipe,
  writeRecipe,
} from "../src/recipes.js";

const document: WorkspaceDocument = {
  query:
    'MATCH (o:Object) WHERE o.Address = 18446744073709551615 RETURN o AS BOX(Label="<script>")',
  settings: { ...DEFAULT_SETTINGS, viewport: { start: "18446744073709551615", size: "1" } },
  annotations: [{ elementId: "element-0", text: "<img src=x onerror=alert(1)>" }],
  hiddenLayers: [0, 1],
};
function recipe() {
  return {
    schemaVersion: 2,
    ...document,
    snapshot: {
      locator: "example.dmp",
      size: "18446744073709551615",
      modifiedUtc: "2026-09-29T00:00:00.000Z",
    },
  };
}

test("recipe exact version/schema, portable locator and uint64 are data", () => {
  assert.deepEqual(validateRecipe(recipe()), recipe());
  for (const value of [
    { ...recipe(), schemaVersion: 3 },
    { ...recipe(), settings: { ...document.settings, layout: "auto" } },
    { ...recipe(), dacPath: "evil.dll" },
    { ...recipe(), settings: { ...document.settings, script: "evil" } },
    {
      ...recipe(),
      annotations: [
        { elementId: "element-0", text: "a" },
        { elementId: "element-0", text: "b" },
      ],
    },
    ...[
      "../outside.dmp",
      "/root/file.dmp",
      "C:\\file.dmp",
      "https://host/file",
      "a//b",
      "a/./b",
      "a\\b",
      "file\0",
    ].map((locator) => ({ ...recipe(), snapshot: { ...recipe().snapshot, locator } })),
    ...["1\n", "01", "-1", "18446744073709551616"].map((size) => ({
      ...recipe(),
      snapshot: { ...recipe().snapshot, size },
    })),
  ])
    assert.throws(() => validateRecipe(value));
});

test("atomic recipe save preserves existing files and leaves no temporary files", async () => {
  const folder = await mkdtemp(join(tmpdir(), "MemoryVisualizer-recipes-"));
  try {
    const path = join(folder, "saved.mvrecipe");
    await writeRecipe(path, validateRecipe(recipe()));
    assert.deepEqual(await readRecipe(path), recipe());
    const before = await readFile(path);
    await assert.rejects(writeRecipe(path, { ...validateRecipe(recipe()), query: "changed" }), {
      code: "EEXIST",
    });
    assert.deepEqual(await readFile(path), before);
    assert.deepEqual(await readdir(folder), ["saved.mvrecipe"]);
    await assert.rejects(
      writeRecipe(join(folder, "missing", "file.mvrecipe"), validateRecipe(recipe())),
      { code: "ENOENT" },
    );
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});

test("bounded strict UTF-8 JSON rejects duplicate keys, oversized files and nesting", async () => {
  const folder = await mkdtemp(join(tmpdir(), "MemoryVisualizer-recipes-"));
  try {
    const path = join(folder, "input.mvrecipe");
    const valid = JSON.stringify(recipe());
    for (const bytes of [
      Buffer.from(valid.replace('"schemaVersion":2', '"schemaVersion":3,"schemaVersion":2')),
      Buffer.from(valid.replace('"schemaVersion":2', '"schemaVersion":2,"schema\\u0056ersion":2')),
      Buffer.from(valid.replace('"query":', '"query":"x","query":')),
      Buffer.from(`{"x":${"[".repeat(18)}0${"]".repeat(18)}}`),
      Buffer.from([0xff]),
      Buffer.from(`\ufeff${valid}`),
      Buffer.from(`${valid}${" ".repeat(MAX_RECIPE_BYTES - Buffer.byteLength(valid) + 1)}`),
    ]) {
      await writeFile(path, bytes);
      await assert.rejects(readRecipe(path));
    }
    await writeFile(path, `${valid}${" ".repeat(MAX_RECIPE_BYTES - Buffer.byteLength(valid))}`);
    assert.deepEqual(await readRecipe(path), recipe());
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});

test("legacy recipe v1 migrates explicitly to linear without losing document data", async () => {
  const { layout: _layout, ...legacySettings } = document.settings;
  const legacy = { ...recipe(), schemaVersion: 1, settings: legacySettings };
  const migrated = {
    ...recipe(),
    settings: { ...document.settings, layout: "linear" },
  };
  assert.deepEqual(validateRecipe(legacy), migrated);
  assert.throws(() => validateRecipe({ ...legacy, settings: document.settings }));
  assert.throws(() => validateRecipe({ ...recipe(), settings: legacySettings }));
  const folder = await mkdtemp(join(tmpdir(), "MemoryVisualizer-recipe-migration-"));
  try {
    const original = join(folder, "legacy.mvrecipe");
    const saved = join(folder, "upgraded.mvrecipe");
    const bytes = JSON.stringify(legacy);
    await writeFile(original, bytes);
    const opened = await readRecipe(original);
    assert.deepEqual(opened, migrated);
    await writeRecipe(saved, opened);
    assert.deepEqual(await readRecipe(saved), migrated);
    assert.equal(JSON.parse(await readFile(saved, "utf8")).schemaVersion, 2);
    assert.equal(await readFile(original, "utf8"), bytes);
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});

test("identity checks actual files; save never leaks an outside absolute path", async () => {
  const folder = await mkdtemp(join(tmpdir(), "MemoryVisualizer-recipes-"));
  try {
    const path = join(folder, "source.dmp");
    await writeFile(path, "synthetic");
    await mkdir(join(folder, "recipes"));
    const identity = await identifyDump(path);
    const local = recipeForSave(document, join(folder, "saved.mvrecipe"), identity);
    assert.equal(local.snapshot?.locator, "source.dmp");
    assert.ok(local.snapshot && identityMatches(local.snapshot, identity));
    assert.deepEqual(await recipeDependency(join(folder, "saved.mvrecipe"), local), identity);
    const outside = recipeForSave(document, join(folder, "recipes", "saved.mvrecipe"), identity);
    assert.equal(outside.snapshot?.locator, basename(path));
    assert.equal(JSON.stringify(outside).includes(folder), false);
    assert.equal(await recipeDependency(join(folder, "recipes", "saved.mvrecipe"), outside), null);
    await writeFile(path, "changed file contents");
    assert.equal(identityMatches(local.snapshot!, await identifyDump(path)), false);
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});

test("recipe dependency cannot escape through a directory link", async () => {
  const folder = await mkdtemp(join(tmpdir(), "MemoryVisualizer-recipes-"));
  try {
    const base = join(folder, "recipe");
    const outside = join(folder, "outside");
    await mkdir(base);
    await mkdir(outside);
    await writeFile(join(outside, "dump.dmp"), "synthetic");
    await symlink(outside, join(base, "link"), process.platform === "win32" ? "junction" : "dir");
    const input = validateRecipe({
      ...recipe(),
      snapshot: { ...recipe().snapshot, locator: "link/dump.dmp" },
    });

    test("editing and saving an unloaded recipe preserves its external dependency", () => {
      const original = validateRecipe(recipe());
      const oldPath = join(tmpdir(), "recipes", "old.mvrecipe");
      const sameFolder = recipeForSave(document, join(tmpdir(), "recipes", "new.mvrecipe"), null, {
        recipePath: oldPath,
        snapshot: original.snapshot!,
      });
      assert.deepEqual(sameFolder.snapshot, original.snapshot);
      const relocatedRecipe = recipeForSave(
        document,
        join(tmpdir(), "elsewhere", "new.mvrecipe"),
        null,
        {
          recipePath: oldPath,
          snapshot: original.snapshot!,
        },
      );
      assert.equal(relocatedRecipe.snapshot?.locator, "example.dmp");
      assert.equal(relocatedRecipe.snapshot?.size, original.snapshot?.size);
      assert.equal(relocatedRecipe.snapshot?.modifiedUtc, original.snapshot?.modifiedUtc);
    });
    await assert.rejects(recipeDependency(join(base, "recipe.mvrecipe"), input), /escapes/);
  } finally {
    await rm(folder, { recursive: true, force: true });
  }
});
