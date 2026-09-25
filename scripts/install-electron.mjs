import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";

// Deliberate allowlist: run only the exact locked Electron package's installer.
const require = createRequire(import.meta.url);
const version = require("electron/package.json").version;
if (version !== "44.4.2") throw new Error("Unexpected Electron package version.");
if (process.env.ELECTRON_SKIP_BINARY_DOWNLOAD || process.env.ELECTRON_MIRROR) {
  throw new Error("Electron installation requires the official release download.");
}
const result = spawnSync(process.execPath, [require.resolve("electron/install.js")], {
  stdio: "inherit",
  shell: false,
  timeout: 180_000,
});
if (result.error) throw result.error;
process.exitCode = result.status ?? 1;
