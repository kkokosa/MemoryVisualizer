import { contextBridge, ipcRenderer } from "electron";
import type { DesktopCommand, DesktopEvent, DesktopOutcome, WorkspaceAPI } from "./native-types.js";

let pending = 0;
let cancelling: Promise<void> | undefined;
let lastDirty = false;
let listeners = 0;

// Sandboxed preloads cannot import local runtime modules. Main repeats the full
// schema validation; this traversal bounds the clone before invoking IPC.
function bounded(value: unknown): boolean {
  let nodes = 0;
  let characters = 0;
  function visit(item: unknown, depth: number): boolean {
    if (++nodes > 8192 || depth > 8) return false;
    if (typeof item === "string") {
      characters += item.length;
      return item.length <= 16384 && characters <= 196608;
    }
    if (item === null || typeof item === "boolean") return true;
    if (typeof item === "number") return Number.isSafeInteger(item);
    if (typeof item !== "object") return false;
    if (Array.isArray(item))
      return item.length <= 256 && item.every((child) => visit(child, depth + 1));
    const keys = Object.keys(item);
    if (keys.length > 16) return false;
    return keys.every((key) => {
      const descriptor = Object.getOwnPropertyDescriptor(item, key);
      return (
        key.length <= 32 &&
        descriptor !== undefined &&
        "value" in descriptor &&
        visit(descriptor.value, depth + 1)
      );
    });
  }
  return visit(value, 0);
}

const api: WorkspaceAPI = {
  async invoke(command: DesktopCommand): Promise<DesktopOutcome> {
    if (!bounded(command))
      return {
        ok: false,
        error: {
          code: "InvalidRequest",
          message: "Desktop request exceeds the bounded data contract.",
        },
      };
    if (pending >= 8)
      return { ok: false, error: { code: "Busy", message: "Desktop request limit reached." } };
    pending++;
    try {
      return await ipcRenderer.invoke("workspace:invoke", command);
    } catch {
      return {
        ok: false,
        error: {
          code: "WorkerExited",
          message: "Desktop bridge disconnected. Edits have not been discarded.",
        },
      };
    } finally {
      pending--;
    }
  },
  cancel(): Promise<void> {
    if (!cancelling) {
      cancelling = ipcRenderer.invoke("workspace:cancel").finally(() => {
        cancelling = undefined;
      });
    }
    return cancelling!;
  },
  setDirty(value: boolean): void {
    if (typeof value !== "boolean" || value === lastDirty) return;
    lastDirty = value;
    ipcRenderer.send("workspace:dirty", value);
  },
  onEvent(callback: (event: DesktopEvent) => void): () => void {
    if (typeof callback !== "function" || listeners >= 4)
      throw new Error("Invalid workspace event subscription.");
    listeners++;
    let removed = false;
    const listener = (_event: Electron.IpcRendererEvent, value: DesktopEvent) => callback(value);
    ipcRenderer.on("workspace:event", listener);
    return () => {
      if (removed) return;
      removed = true;
      listeners--;
      ipcRenderer.removeListener("workspace:event", listener);
    };
  },
};
contextBridge.exposeInMainWorld("workspace", Object.freeze(api));
