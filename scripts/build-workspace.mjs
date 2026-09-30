import { spawnSync } from "node:child_process";
import { createRequire } from "node:module";
import { cp, mkdir, readFile, rename, rm, stat, writeFile } from "node:fs/promises";
import { dirname, resolve, join } from "node:path";
import { fileURLToPath } from "node:url";
import { randomUUID } from "node:crypto";
import { parseArgs } from "node:util";
import { assertContainedSymlinks } from "./bundle-links.mjs";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const require = createRequire(import.meta.url);
const hosts = {
  "win32-x64": "win-x64",
  "linux-x64": "linux-x64",
  "darwin-x64": "osx-x64",
  "darwin-arm64": "osx-arm64",
};
const rid = hosts[`${process.platform}-${process.arch}`];
if (!rid)
  throw new Error("Unsupported workspace host. Build on Windows/Linux x64 or macOS x64/arm64.");
const { values } = parseArgs({ options: { output: { type: "string" } }, allowPositionals: false });
if (values.output !== undefined && values.output.trim() === "")
  throw new Error("Output path must not be empty.");
const destination = resolve(root, values.output ?? join("artifacts", `workspace-${rid}`));
try {
  await stat(destination);
  throw new Error(`Output already exists: ${destination}. Choose a new --output folder.`);
} catch (error) {
  if (error.code !== "ENOENT") throw error;
}
function run(executable, args, timeout = 300000, env = process.env) {
  const result = spawnSync(executable, args, {
    cwd: root,
    stdio: "inherit",
    shell: false,
    timeout,
    env,
  });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error(`${executable} failed (${result.status}).`);
}
if (!process.env.npm_execpath)
  throw new Error("Use npm run build:workspace from the repository root.");
const sdk = JSON.parse(await readFile(join(root, "global.json"), "utf8")).sdk.version;
const version = spawnSync("dotnet", ["--version"], { cwd: root, encoding: "utf8", shell: false });
if (version.error) throw version.error;
if (version.status !== 0 || version.stdout.trim() !== sdk)
  throw new Error(`Build requires the exact SDK ${sdk}.`);
run(process.execPath, [process.env.npm_execpath, "run", "build:desktop"]);
run(process.execPath, [join(root, "scripts", "install-electron.mjs")]);
const staging = resolve(root, "artifacts", `.workspace-${randomUUID()}`);
await mkdir(process.platform === "darwin" ? staging : dirname(staging), { recursive: true });
try {
  const electronDist = dirname(require("electron"));
  const distribution =
    process.platform === "darwin" ? resolve(electronDist, "..", "..") : electronDist;
  const appRoot = process.platform === "darwin" ? join(staging, "MemoryVisualizer.app") : staging;
  await cp(distribution, appRoot, {
    recursive: true,
    force: false,
    errorOnExist: true,
    verbatimSymlinks: true,
  });
  const resources =
    process.platform === "darwin"
      ? join(appRoot, "Contents", "Resources")
      : join(appRoot, "resources");
  const app = join(resources, "app");
  await mkdir(app, { recursive: true });
  await rm(join(resources, "default_app.asar"), { force: true });
  await cp(join(root, "desktop", "dist", "src"), join(app, "dist", "src"), {
    recursive: true,
    errorOnExist: true,
    force: false,
  });
  await cp(join(root, "renderer-dist"), join(app, "renderer-dist"), {
    recursive: true,
    errorOnExist: true,
    force: false,
  });
  await writeFile(
    join(app, "package.json"),
    JSON.stringify(
      {
        name: "memoryvisualizer",
        version: "0.1.0-dev",
        productName: "MemoryVisualizer",
        type: "module",
        main: "dist/src/main.js",
      },
      null,
      2,
    ),
  );
  let notices = "MemoryVisualizer development workspace - third-party notices\n\n";
  for (const packageName of ["react", "react-dom", "scheduler"]) {
    notices += `\n--- ${packageName} ---\n${await readFile(join(dirname(require.resolve(`${packageName}/package.json`)), "LICENSE"), "utf8")}\n`;
  }
  await writeFile(join(app, "THIRD-PARTY-NOTICES.txt"), notices);
  run("dotnet", [
    "publish",
    join(root, "src", "MemoryVisualizer.Worker"),
    "-c",
    "Release",
    "-r",
    rid,
    "--self-contained",
    "true",
    "-p:RestoreLockedMode=true",
    "-o",
    join(resources, "worker"),
  ]);
  if (process.platform === "win32")
    await rename(join(staging, "electron.exe"), join(staging, "MemoryVisualizer.exe"));
  if (process.platform === "linux")
    await rename(join(staging, "electron"), join(staging, "MemoryVisualizer"));
  const worker = join(
    resources,
    "worker",
    process.platform === "win32" ? "MemoryVisualizer.Worker.exe" : "MemoryVisualizer.Worker",
  );
  run(worker, ["--version"], 10000, {
    ...process.env,
    DOTNET_ROOT: join(staging, "no-sdk"),
    DOTNET_ROOT_X64: join(staging, "no-sdk"),
    DOTNET_ROOT_ARM64: join(staging, "no-sdk"),
    DOTNET_MULTILEVEL_LOOKUP: "0",
    PATH: process.platform === "win32" ? `${process.env.SystemRoot}\\System32` : "/usr/bin:/bin",
  });
  await assertContainedSymlinks(appRoot);
  await rename(staging, destination);
  console.log(`Built runnable workspace: ${destination}`);
  console.log(
    "No Node.js, npm, .NET SDK, or installed .NET runtime is needed to launch this folder. Installers, signing, and updates are not included.",
  );
} finally {
  await rm(staging, { recursive: true, force: true });
}
