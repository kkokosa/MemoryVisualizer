import { app, BrowserWindow, ipcMain, type IpcMainInvokeEvent } from "electron";
import { dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { WorkerClient, type RequestHandle } from "./worker-client.js";
import { workerPath } from "./worker-path.js";
import { parseInbound, ProtocolError, type Progress } from "./protocol.js";
import type { BridgeOutcome, ControlOutcome } from "./bridge-types.js";

const directory = dirname(fileURLToPath(import.meta.url));
const shell = resolve(directory, "..", "..", "shell.html");
const shellUrl = pathToFileURL(shell).href;
const integration = process.argv.includes("--integration-test");
let owner: BrowserWindow | undefined;
let worker: WorkerClient | undefined;
let exiting = false;
let lastToken = 0n;
const handles = new Map<string, RequestHandle>();

function authorized(event: IpcMainInvokeEvent): boolean {
  return (
    owner !== undefined &&
    !owner.isDestroyed() &&
    event.sender === owner.webContents &&
    event.senderFrame === owner.webContents.mainFrame &&
    event.senderFrame.url === shellUrl
  );
}

function errorOutcome(error: unknown): Exclude<BridgeOutcome, { ok: true }> {
  return {
    ok: false,
    error: {
      code: error instanceof ProtocolError ? error.code : "InternalError",
      message: error instanceof ProtocolError ? error.message : "Desktop operation failed.",
    },
  };
}

ipcMain.handle("mv:request", async (event, input: unknown): Promise<BridgeOutcome> => {
  try {
    if (!authorized(event)) throw new ProtocolError("InvalidRequest", "Window is not authorized.");
    if (typeof input !== "object" || input === null || Array.isArray(input)) {
      throw new ProtocolError("InvalidRequest", "Invalid operation.");
    }
    const keys = Object.keys(input).sort().join(",");
    if (keys !== "args,operation,requestId,snapshotId") {
      throw new ProtocolError("InvalidRequest", "Invalid operation fields.");
    }
    const parsed = parseInbound({ ...input, tag: "request", version: 1 });
    if (parsed.tag !== "request") throw new ProtocolError("InvalidRequest", "Invalid operation.");
    const token = parsed.requestId;
    if (BigInt(token) <= lastToken)
      throw new ProtocolError("InvalidRequest", "Request ID was reused.");
    lastToken = BigInt(token);
    if (!worker) throw new ProtocolError("WorkerExited", "Worker is unavailable.");
    const handle = worker.request(parsed);
    handles.set(token, handle);
    try {
      const value = await handle.result;
      return { ok: true, value: { ...value, requestId: token } };
    } finally {
      handles.delete(token);
    }
  } catch (error) {
    return errorOutcome(error);
  }
});

ipcMain.handle("mv:cancel", (event, token: unknown): ControlOutcome => {
  try {
    if (!authorized(event)) throw new ProtocolError("InvalidRequest", "Window is not authorized.");
    const parsed = parseInbound({ tag: "cancel", version: 1, requestId: token });
    if (parsed.tag !== "cancel") throw new ProtocolError("InvalidRequest", "Invalid cancellation.");
    const handle = handles.get(parsed.requestId);
    if (handle) worker?.cancel(handle.requestId);
    return { ok: true };
  } catch (error) {
    return errorOutcome(error);
  }
});

export function createWindow(): BrowserWindow {
  const window = new BrowserWindow({
    show: !integration,
    width: 740,
    height: 420,
    webPreferences: {
      preload: resolve(directory, "preload.cjs"),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true,
      webSecurity: true,
      devTools: integration,
    },
  });
  window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  window.webContents.on("will-navigate", (event) => event.preventDefault());
  window.webContents.session.setPermissionRequestHandler((_contents, _permission, callback) =>
    callback(false),
  );
  window.webContents.session.setPermissionCheckHandler(() => false);
  return window;
}

app.on("window-all-closed", () => app.quit());
app.on("before-quit", (event) => {
  if (exiting) return;
  event.preventDefault();
  exiting = true;
  void (worker?.close() ?? Promise.resolve()).finally(() => app.quit());
});

async function start(): Promise<void> {
  try {
    worker = new WorkerClient(workerPath());
    worker.on("failure", (error: ProtocolError) => {
      // Fixed code only, never request content or dependency stderr.
      console.error(`Worker failure: ${error.code}`);
      if (owner && !owner.isDestroyed()) {
        owner.webContents.send("mv:failure", { code: error.code, message: error.message });
      }
    });
    await worker.ready;
    owner = createWindow();
    worker.on("progress", (progress: Progress) => {
      if (!owner || owner.isDestroyed()) return;
      const token = [...handles].find(([, handle]) => handle.requestId === progress.requestId)?.[0];
      if (token) owner.webContents.send("mv:progress", { ...progress, requestId: token });
    });
    owner.webContents.on("render-process-gone", () => app.quit());
    await owner.loadFile(shell);
    if (integration) {
      const { runElectronScenario } = await import("../tests/electron-scenario.js");
      await runElectronScenario(
        owner,
        worker,
        createWindow,
        shell,
        process.argv.includes("--crash-test"),
      );
      console.log("Electron lifecycle integration passed.");
      app.quit();
    }
  } catch (error) {
    console.error("Desktop startup or lifecycle failed. Check the worker executable and runtime.");
    if (integration && error instanceof Error) console.error(error.stack);
    await worker?.close();
    app.exit(1);
  }
}

// Electron waits for ESM evaluation before emitting ready; top-level await would deadlock.
void app.whenReady().then(start);
