import { contextBridge, ipcRenderer } from "electron";
import type { DesktopApi, BridgeRequest, BridgeOutcome } from "./bridge-types.js";
import type { OperationInput } from "./worker-client.js";
import type { Progress } from "./protocol.js";

let counter = 0n;
const active = new Set<string>();
const cancelling = new Set<string>();
const maximumOutstanding = 8;

// Sandboxed preloads cannot require local modules. Keep this early admission
// guard tiny; main independently applies the complete shared protocol schema.
function uint64(value: unknown, nonzero: boolean): value is string {
  return (
    typeof value === "string" &&
    value.length <= 20 &&
    /^(0|[1-9][0-9]*)(?![\s\S])/.test(value) &&
    (!nonzero || value !== "0") &&
    BigInt(value) <= 18446744073709551615n
  );
}

function snapshot(value: unknown): value is string {
  return (
    typeof value === "string" &&
    value.length === 36 &&
    /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/.test(value) &&
    value !== "00000000-0000-0000-0000-000000000000"
  );
}

function boundedNumber(value: unknown, min: number, max: number): value is number {
  return typeof value === "number" && Number.isSafeInteger(value) && value >= min && value <= max;
}

function text(value: unknown): value is string {
  if (typeof value !== "string" || value.length === 0 || value.length > 8192) return false;
  let length = 0;
  for (const character of value) {
    const unit = character.charCodeAt(0);
    if (character.length === 1 && unit >= 0xd800 && unit <= 0xdfff) return false;
    if (++length > 4096) return false;
  }
  return true;
}

function permitted(input: OperationInput): boolean {
  switch (input.operation) {
    case "capabilities":
      return true;
    case "snapshot.load":
      return boundedNumber(input.args.delayMs, 0, 5000);
    case "snapshot.dispose":
      return snapshot(input.snapshotId);
    case "query":
    case "details":
      return (
        snapshot(input.snapshotId) &&
        boundedNumber(input.args.pageSize, 1, 128) &&
        (input.args.cursor === null || uint64(input.args.cursor, false)) &&
        (input.operation === "query" ? text(input.args.text) : uint64(input.args.objectId, true))
      );
    case "scene":
      return snapshot(input.snapshotId) && boundedNumber(input.args.maxItems, 1, 128);
    case "recipe.validate":
      return text(input.args.recipe.query);
    case "export":
      return snapshot(input.snapshotId) && boundedNumber(input.args.maxBytes, 1, 32768);
  }
}

function rejected(requestId: string, code: string, message: string): BridgeRequest {
  return { requestId, result: Promise.resolve({ ok: false, error: { code, message } }) };
}

function begin(input: OperationInput): BridgeRequest {
  const requestId = (++counter).toString();
  if (!permitted(input))
    return rejected(requestId, "InvalidRequest", "Invalid operation arguments.");
  if (active.size >= maximumOutstanding) {
    return rejected(requestId, "Busy", "Request limit reached.");
  }
  active.add(requestId);
  const result: Promise<BridgeOutcome> = ipcRenderer
    .invoke("mv:request", { requestId, ...input })
    .catch(() => ({
      ok: false,
      error: { code: "WorkerExited", message: "Desktop bridge disconnected." },
    }))
    .finally(() => {
      active.delete(requestId);
      cancelling.delete(requestId);
    });
  return { requestId, result };
}

const api: DesktopApi = {
  capabilities: () => begin({ operation: "capabilities", snapshotId: null, args: {} }),
  loadFixture: (delayMs) =>
    begin({
      operation: "snapshot.load",
      snapshotId: null,
      args: { source: "fixture:tiny", delayMs },
    }),
  disposeSnapshot: (snapshotId) => begin({ operation: "snapshot.dispose", snapshotId, args: {} }),
  query: (snapshotId, text, pageSize, cursor) =>
    begin({ operation: "query", snapshotId, args: { text, pageSize, cursor } }),
  details: (snapshotId, objectId, pageSize, cursor) =>
    begin({ operation: "details", snapshotId, args: { objectId, pageSize, cursor } }),
  scene: (snapshotId, maxItems) => begin({ operation: "scene", snapshotId, args: { maxItems } }),
  validateRecipe: (query) =>
    begin({
      operation: "recipe.validate",
      snapshotId: null,
      args: { recipe: { schemaVersion: 1, query } },
    }),
  exportSvg: (snapshotId, maxBytes) =>
    begin({ operation: "export", snapshotId, args: { format: "svg", maxBytes } }),
  cancel: async (requestId) => {
    if (!uint64(requestId, true)) {
      return { ok: false, error: { code: "InvalidRequest", message: "Invalid request ID." } };
    }
    if (!active.has(requestId) || cancelling.has(requestId)) return { ok: true };
    cancelling.add(requestId);
    try {
      return await ipcRenderer.invoke("mv:cancel", requestId);
    } catch {
      return {
        ok: false,
        error: { code: "WorkerExited", message: "Desktop bridge disconnected." },
      };
    }
  },
  onProgress: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, progress: Progress) => listener(progress);
    ipcRenderer.on("mv:progress", handler);
    return () => ipcRenderer.removeListener("mv:progress", handler);
  },
  onFailure: (listener) => {
    const handler = (_event: Electron.IpcRendererEvent, error: { code: string; message: string }) =>
      listener(error);
    ipcRenderer.on("mv:failure", handler);
    return () => ipcRenderer.removeListener("mv:failure", handler);
  },
};
contextBridge.exposeInMainWorld("memoryVisualizer", Object.freeze(api));
