import assert from "node:assert/strict";
import {
  cp,
  mkdir,
  mkdtemp,
  readFile,
  readlink,
  rename,
  rm,
  symlink,
  writeFile,
} from "node:fs/promises";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { assertContainedSymlinks } from "./bundle-links.mjs";

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
