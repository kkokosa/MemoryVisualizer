import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const script = resolve(dirname(fileURLToPath(import.meta.url)), "workspace-scenario.mjs");
const [dump, dac] = process.argv.slice(2);
if (!dump || !dac)
  throw new Error("Expected generated WithHeap dump and matching trusted DAC paths.");
const child = spawn(require("electron"), [script, dump, dac], {
  shell: false,
  stdio: ["ignore", "pipe", "pipe"],
});
let stdout = "";
let stderr = "";
child.stdout.setEncoding("utf8").on("data", (chunk) => {
  stdout = (stdout + chunk).slice(-8192);
});
child.stderr.setEncoding("utf8").on("data", (chunk) => {
  stderr = (stderr + chunk).slice(-8192);
});
const timer = setTimeout(() => child.kill("SIGKILL"), 210000);
try {
  const code = await new Promise((resolve, reject) => {
    child.once("error", reject);
    child.once("close", resolve);
  });
  assert.equal(code, 0, `Native desktop failed: ${stdout}\n${stderr}`);
  assert.match(stdout, /Native desktop integration passed/);
  for (const match of stdout.matchAll(/Owned native worker PID: (\d+)/g)) {
    assert.throws(
      () => process.kill(Number(match[1]), 0),
      /ESRCH/,
      "Desktop left an owned worker running.",
    );
  }
  console.log(stdout.trim());
} finally {
  clearTimeout(timer);
}
