import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import {
  cp,
  mkdir,
  mkdtemp,
  readFile,
  readlink,
  readdir,
  rename,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { fileURLToPath } from "node:url";
import test from "node:test";
import { assertContainedSymlinks } from "./bundle-links.mjs";

test("alternate workspace destinations never replace existing files or folders", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "memoryvisualizer-bundle-output-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  const file = join(root, "preserved.txt");
  await writeFile(file, "running build");
  const script = fileURLToPath(new URL("./build-workspace.mjs", import.meta.url));
  for (const destination of [root, file]) {
    const result = spawnSync(process.execPath, [script, "--output", destination], {
      encoding: "utf8",
      timeout: 10000,
    });
    assert.equal(result.error, undefined);
    assert.notEqual(result.status, 0);
    assert.ok(result.stderr.includes(`Output already exists: ${destination}`));
  }
  const empty = spawnSync(process.execPath, [script, "--output", ""], {
    encoding: "utf8",
    timeout: 10000,
  });
  assert.notEqual(empty.status, 0);
  assert.match(empty.stderr, /Output path must not be empty/);
  assert.deepEqual(await readdir(root), ["preserved.txt"]);
  assert.equal(await readFile(file, "utf8"), "running build");
});

test("ordinary bundle files pass containment", async (t) => {
  const root = await mkdtemp(join(tmpdir(), "memoryvisualizer-bundle-"));
  t.after(() => rm(root, { recursive: true, force: true }));
  await writeFile(join(root, "file"), "contents");
  await assertContainedSymlinks(root);
});

test(
  "framework links remain portable after copying and removing the source",
  {
    skip: process.platform === "win32" && "Relative symlink creation requires Windows privileges.",
  },
  async (t) => {
    const root = await mkdtemp(join(tmpdir(), "memoryvisualizer-bundle-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const source = join(root, "source");
    const copy = join(root, "copy");
    const relocated = join(root, "relocated");
    await mkdir(join(source, "Framework.framework", "Versions", "A"), { recursive: true });
    await writeFile(join(source, "Framework.framework", "Versions", "A", "Framework"), "runtime");
    await symlink("A", join(source, "Framework.framework", "Versions", "Current"));
    await symlink("Versions/Current/Framework", join(source, "Framework.framework", "Framework"));
    await cp(source, copy, { recursive: true, verbatimSymlinks: true });
    await assertContainedSymlinks(copy);
    await rm(source, { recursive: true });
    await rename(copy, relocated);
    await assertContainedSymlinks(relocated);
    assert.equal(
      await readlink(join(relocated, "Framework.framework", "Versions", "Current")),
      "A",
    );
    assert.equal(
      await readFile(join(relocated, "Framework.framework", "Framework"), "utf8"),
      "runtime",
    );
  },
);

test(
  "escaping, absolute, and broken bundle links are rejected",
  {
    skip: process.platform === "win32" && "Relative symlink creation requires Windows privileges.",
  },
  async (t) => {
    const root = await mkdtemp(join(tmpdir(), "memoryvisualizer-bundle-"));
    t.after(() => rm(root, { recursive: true, force: true }));
    const bundle = join(root, "bundle");
    await mkdir(bundle);
    await writeFile(join(root, "outside"), "outside");
    await writeFile(join(bundle, "inside"), "inside");
    const link = join(bundle, "link");
    await symlink("../outside", link);
    await assert.rejects(assertContainedSymlinks(bundle), /escapes/);
    await rm(link);
    await symlink(join(bundle, "inside"), link);
    await assert.rejects(assertContainedSymlinks(bundle), /must be relative/);
    await rm(link);
    await symlink("missing", link);
    await assert.rejects(assertContainedSymlinks(bundle), { code: "ENOENT" });
  },
);
