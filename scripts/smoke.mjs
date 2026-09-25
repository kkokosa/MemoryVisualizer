import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const configuration = process.env.CONFIGURATION ?? "Release";
assert.ok(["Debug", "Release"].includes(configuration), "CONFIGURATION must be Debug or Release");
const rid = process.env.RUNTIME_IDENTIFIER;
assert.ok(!rid || /^[a-z0-9]+-[a-z0-9]+$/.test(rid), "Invalid RUNTIME_IDENTIFIER");
const version = JSON.parse(readFileSync(path.join(root, "package.json"), "utf8")).version;
const dotnet = process.env.DOTNET_HOST_PATH ?? "dotnet";

for (const name of ["MemoryVisualizer.Worker", "MemoryVisualizer.Cli"]) {
  const directory = path.join(root, "bin", name, configuration, "net11.0", ...(rid ? [rid] : []));
  const dll = path.join(directory, `${name}.dll`);
  const apphost = path.join(directory, process.platform === "win32" ? `${name}.exe` : name);
  assert.ok(existsSync(dll), `Missing output: ${dll}`);
  assert.ok(existsSync(apphost), `Missing apphost: ${apphost}`);

  for (const [command, prefix] of [
    [dotnet, [dll]],
    [apphost, []],
  ]) {
    for (const args of [
      [],
      ["--help"],
      ["-h"],
      ["help"],
      ["--version"],
      ["version"],
      ["--bad"],
      ["--help", "extra"],
    ]) {
      const result = spawnSync(command, [...prefix, ...args], {
        cwd: root,
        encoding: "utf8",
        timeout: 15000,
        env: { ...process.env, DOTNET_NOLOGO: "1" },
      });
      assert.ifError(result.error);
      const invalid = args.includes("--bad") || args.length > 1;
      assert.equal(result.status, invalid ? 2 : 0, `${name} ${args}: ${result.stderr}`);
      if (invalid) {
        assert.equal(result.stdout, "");
        assert.match(result.stderr, /Unsupported arguments/);
      } else {
        assert.equal(result.stderr, "");
        if (args.some((arg) => arg.includes("version"))) {
          assert.ok(result.stdout.trim().startsWith(`${name} ${version}`), result.stdout);
        } else {
          assert.ok(result.stdout.includes(`Usage: ${name}`), result.stdout);
          assert.match(result.stdout, /not implemented/);
        }
      }
    }
  }
  console.log(
    `${name}: DLL and apphost help/version/error smoke passed (${configuration}${rid ? `/${rid}` : ""}).`,
  );
}
