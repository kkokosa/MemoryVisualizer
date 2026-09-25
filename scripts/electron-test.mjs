import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { createRequire } from "node:module";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const require = createRequire(import.meta.url);
const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const executable = require("electron");
for (const scenario of ["normal", "crash"]) {
  await new Promise((done, reject) => {
    const child = spawn(
      executable,
      [
        resolve(root, "desktop"),
        "--integration-test",
        ...(scenario === "crash" ? ["--crash-test"] : []),
      ],
      {
        shell: false,
        stdio: ["ignore", "pipe", "pipe"],
        env: { ...process.env, ELECTRON_ENABLE_LOGGING: "0" },
      },
    );
    let stdout = "";
    child.stdout.setEncoding("utf8").on("data", (chunk) => {
      stdout = (stdout + chunk).slice(-8192);
    });
    let stderr = "";
    child.stderr.setEncoding("utf8").on("data", (chunk) => {
      stderr = (stderr + chunk).slice(-8192);
    });
    const timer = setTimeout(() => child.kill("SIGKILL"), 30_000);
    child.on("error", (error) => {
      clearTimeout(timer);
      reject(error);
    });
    child.on("close", (code) => {
      clearTimeout(timer);
      try {
        assert.equal(code, 0, `Electron ${scenario} failed: ${stderr}`);
        assert.match(stdout, /Electron lifecycle integration passed/);
        const pid = Number(/Owned worker PID: (\d+)/.exec(stdout)?.[1]);
        assert.ok(Number.isSafeInteger(pid) && pid > 0, "Missing owned worker PID.");
        assert.throws(() => process.kill(pid, 0), /ESRCH/, "App exit left an orphan worker.");
        console.log(`${scenario}: ${stdout.trim()}`);
        done();
      } catch (error) {
        reject(error);
      }
    });
  });
}
