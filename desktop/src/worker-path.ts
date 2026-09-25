import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";

export function workerPath(): string {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..", "..");
  const configuration = process.env.CONFIGURATION ?? "Release";
  if (!["Release", "Debug"].includes(configuration))
    throw new Error("Invalid build configuration.");
  const rid = process.env.RUNTIME_IDENTIFIER;
  if (rid && !["win-x64", "linux-x64", "osx-arm64", "osx-x64"].includes(rid)) {
    throw new Error("Invalid worker runtime identifier.");
  }
  return resolve(
    root,
    "bin",
    "MemoryVisualizer.Worker",
    configuration,
    "net11.0",
    ...(rid ? [rid] : []),
    process.platform === "win32" ? "MemoryVisualizer.Worker.exe" : "MemoryVisualizer.Worker",
  );
}
