import {
  app,
  BrowserWindow,
  dialog,
  ipcMain,
  Menu,
  type IpcMainInvokeEvent,
  type IpcMainEvent,
  type MenuItemConstructorOptions,
} from "electron";
import { basename, dirname, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { NativeClient } from "./native-client.js";
import { validateSettings, validateWorkspaceDocument } from "./native-protocol.js";
import { ProtocolError } from "./protocol.js";
import { workerPath } from "./worker-path.js";
import {
  identifyDump,
  identityMatches,
  readRecipe,
  recipeDependency,
  recipeForSave,
  writeRecipe,
  type DumpIdentity,
} from "./recipes.js";
import type {
  DesktopCommand,
  DesktopEvent,
  DesktopOutcome,
  DesktopValue,
  NativeOperation,
  NativeSuccess,
  Recipe,
  WorkspaceState,
} from "./native-types.js";

const directory = dirname(fileURLToPath(import.meta.url));
const page = resolve(
  directory,
  "..",
  "..",
  ...(app.isPackaged ? [] : [".."]),
  "renderer-dist",
  "index.html",
);
const pageUrl = pathToFileURL(page).href;
let owner: BrowserWindow | undefined;
let worker: NativeClient | undefined;
let dump: DumpIdentity | null = null;
let unloadedDependency: { recipePath: string; snapshot: NonNullable<Recipe["snapshot"]> } | null =
  null;
let recipePath: string | null = null;
let dirty = false;
let closing = false;
let checkingClose = false;
let exclusive = false;
let activeInvocations = 0;
let operationEpoch = 0;
const active = new Set<string>();
let state: WorkspaceState = {
  snapshotId: null,
  dumpName: null,
  recipeName: null,
  objectCount: null,
  sourcePartial: false,
  diagnosticCount: 0,
};

function authorized(event: IpcMainInvokeEvent | IpcMainEvent): boolean {
  return (
    owner !== undefined &&
    !owner.isDestroyed() &&
    event.sender === owner.webContents &&
    event.senderFrame === owner.webContents.mainFrame &&
    event.senderFrame.url === pageUrl
  );
}

function emit(event: DesktopEvent): void {
  if (owner && !owner.isDestroyed()) owner.webContents.send("workspace:event", event);
}

function errorOutcome(error: unknown): DesktopOutcome {
  if (error instanceof ProtocolError)
    return { ok: false, error: { code: error.code, message: error.message } };
  if (error instanceof Error && "code" in error && error.code === "EEXIST") {
    return {
      ok: false,
      error: {
        code: "FileExists",
        message:
          "The destination already exists. Choose a new filename; existing files are never overwritten.",
      },
    };
  }
  return {
    ok: false,
    error: {
      code: "FileOperationFailed",
      message: "The operation failed. Check file access and disk space. Your edits were preserved.",
    },
  };
}

function keys(input: unknown, expected: string[]): Record<string, unknown> {
  if (
    input === null ||
    typeof input !== "object" ||
    Array.isArray(input) ||
    Object.keys(input).sort().join(",") !== expected.sort().join(",")
  ) {
    throw new ProtocolError("InvalidRequest", "Invalid desktop command.");
  }
  return input as Record<string, unknown>;
}

function text(value: unknown, pattern: RegExp, maximum: number): string {
  if (typeof value !== "string" || value.length > maximum || !pattern.test(value))
    throw new ProtocolError("InvalidRequest", "Invalid desktop command argument.");
  return value;
}

function command(input: unknown): DesktopCommand {
  if (input === null || typeof input !== "object" || !("kind" in input))
    throw new ProtocolError("InvalidRequest", "Invalid desktop command.");
  switch (input.kind) {
    case "openDump":
    case "openRecipe":
    case "dispose":
    case "restart":
      keys(input, ["kind"]);
      return { kind: input.kind };
    case "saveRecipe": {
      const item = keys(input, ["kind", "document"]);
      return { kind: "saveRecipe", document: validateWorkspaceDocument(item.document) };
    }
    case "run": {
      const item = keys(input, ["kind", "text", "settings"]);
      if (typeof item.text !== "string" || item.text.length > 16384)
        throw new ProtocolError("InvalidRequest", "Query exceeds the 16384 UTF-16 unit limit.");
      return { kind: "run", text: item.text, settings: validateSettings(item.settings) };
    }
    case "rows":
    case "elements": {
      const key = input.kind === "rows" ? "queryId" : "sceneId";
      const item = keys(input, ["kind", key, "cursor"]);
      const id = text(item[key], /^[1-9][0-9]*$/, 20);
      const cursor = item.cursor === null ? null : text(item.cursor, /^(0|[1-9][0-9]*)$/, 20);
      return input.kind === "rows"
        ? { kind: "rows", queryId: id, cursor }
        : { kind: "elements", sceneId: id, cursor };
    }
    case "details": {
      const item = keys(input, ["kind", "runtime", "address", "cursor"]);
      if (
        typeof item.runtime !== "number" ||
        !Number.isSafeInteger(item.runtime) ||
        item.runtime < 0 ||
        item.runtime > 2147483647
      )
        throw new ProtocolError("InvalidRequest", "Invalid runtime index.");
      return {
        kind: "details",
        runtime: item.runtime,
        address: text(item.address, /^0x[0-9a-f]{16}$/, 18),
        cursor: item.cursor === null ? null : text(item.cursor, /^(0|[1-9][0-9]*)$/, 20),
      };
    }
    case "export": {
      const item = keys(input, ["kind", "sceneId"]);
      return { kind: "export", sceneId: text(item.sceneId, /^[1-9][0-9]*$/, 20) };
    }
    default:
      throw new ProtocolError("InvalidRequest", "Unknown desktop command.");
  }
}

async function startWorker(): Promise<void> {
  const executable = app.isPackaged
    ? resolve(
        process.resourcesPath,
        "worker",
        process.platform === "win32" ? "MemoryVisualizer.Worker.exe" : "MemoryVisualizer.Worker",
      )
    : workerPath();
  const client = new NativeClient(executable);
  worker = client;
  client.on("progress", (value) => emit({ kind: "progress", value }));
  client.on("failure", (failure: ProtocolError) => {
    if (worker !== client) return;
    operationEpoch++;
    state = { ...state, snapshotId: null, objectCount: null };
    console.error(`Native worker failure: ${failure.code}`);
    emit({
      kind: "failure",
      code: failure.code,
      message: `${failure.message} Edits are preserved. Restart the worker, then deliberately reopen the dump and run.`,
    });
  });
  await client.ready;
}

async function request(input: NativeOperation): Promise<NativeSuccess> {
  if (!worker)
    throw new ProtocolError("WorkerExited", "Worker unavailable. Restart it explicitly.");
  const handle = worker.request(input);
  active.add(handle.requestId);
  try {
    return await handle.result;
  } finally {
    active.delete(handle.requestId);
  }
}

function snapshot(): string {
  if (!state.snapshotId)
    throw new ProtocolError("SnapshotNotFound", "Open a supported dump first.");
  return state.snapshotId;
}

async function confirmReplacement(replacingRecipe: boolean): Promise<boolean> {
  if (!dirty) return true;
  const answer = await dialog.showMessageBox(owner!, {
    type: "question",
    title: "Unsaved recipe changes",
    message: replacingRecipe
      ? "Replace the current recipe and discard its unsaved changes?"
      : "Open another dump while keeping your unsaved query and settings?",
    detail: replacingRecipe
      ? "Cancel and save first to keep a copy. Changes are discarded only after the new recipe opens successfully."
      : "Your query and settings remain in the editor. Save them as a recipe to keep a copy.",
    buttons: replacingRecipe ? ["Cancel", "Replace recipe"] : ["Cancel", "Keep edits and open"],
    defaultId: 0,
    cancelId: 0,
    noLink: true,
  });
  return answer.response === 1;
}

async function trustDac(): Promise<{
  dacPath: string | null;
  cachePath: string | null;
  allowNetwork: boolean;
} | null> {
  const choice = await dialog.showMessageBox(owner!, {
    type: "warning",
    title: "Trust the target runtime DAC",
    message:
      "Analysis loads native code from a DAC matching the dump's runtime build and architecture.",
    detail:
      "Choose a DAC from a runtime distribution you trust, or your protected symbol-layout cache. Dump-adjacent libraries and recipe-supplied paths are never trusted. Network access is off by default.",
    buttons: ["Select matching DAC", "Select trusted cache", "Cancel"],
    defaultId: 2,
    cancelId: 2,
    noLink: true,
  });
  if (choice.response === 2) return null;
  if (choice.response === 0) {
    const selection = await dialog.showOpenDialog(owner!, {
      title: "Select trusted DAC for runtime 0",
      properties: ["openFile"],
      filters: [
        { name: "Native DAC", extensions: ["dll", "so", "dylib"] },
        { name: "All files", extensions: ["*"] },
      ],
    });
    return selection.canceled || !selection.filePaths[0]
      ? null
      : { dacPath: selection.filePaths[0], cachePath: null, allowNetwork: false };
  }
  const selection = await dialog.showOpenDialog(owner!, {
    title: "Select protected trusted symbol cache",
    properties: ["openDirectory"],
  });
  if (selection.canceled || !selection.filePaths[0]) return null;
  let allowNetwork = false;
  if (process.platform === "win32") {
    const network = await dialog.showMessageBox(owner!, {
      type: "question",
      title: "Symbol cache network policy",
      message: "Use this cache offline?",
      detail:
        "Optional downloads contact Microsoft's official HTTPS symbol server and disclose runtime binary names/build identifiers, never heap contents. Signature and exact-version checks remain enabled.",
      buttons: ["Offline only", "Allow official DAC download", "Cancel"],
      defaultId: 0,
      cancelId: 2,
      noLink: true,
    });
    if (network.response === 2) return null;
    allowNetwork = network.response === 1;
  }
  return { dacPath: null, cachePath: selection.filePaths[0], allowNetwork };
}

async function importDump(path: string): Promise<boolean> {
  const identity = await identifyDump(path);
  const policy = await trustDac();
  if (!policy) return false;
  const result = await request({
    operation: "snapshot.load",
    snapshotId: null,
    args: { path: identity.path, ...policy },
  });
  if (result.result.tag !== "snapshot" || !result.snapshotId)
    throw new ProtocolError("ProtocolError", "Worker returned an invalid snapshot.");
  dump = identity;
  unloadedDependency = null;
  state = {
    ...state,
    snapshotId: result.snapshotId,
    dumpName: basename(path),
    objectCount: result.result.objectCount,
    sourcePartial: result.result.sourcePartial,
    diagnosticCount: result.result.diagnosticCount,
  };
  return true;
}

async function openRecipe(): Promise<DesktopValue> {
  if (!(await confirmReplacement(true))) return { kind: "cancelled" };
  const chosen = await dialog.showOpenDialog(owner!, {
    title: "Open MemoryVisualizer recipe",
    properties: ["openFile"],
    filters: [{ name: "MemoryVisualizer recipe", extensions: ["mvrecipe", "json"] }],
  });
  const path = chosen.filePaths[0];
  if (chosen.canceled || !path) return { kind: "cancelled" };
  const recipe = await readRecipe(path);
  let dependency: "ready" | "missing" | "mismatch" | "unloaded" = "unloaded";
  let loaded = false;
  let candidate = await recipeDependency(path, recipe);
  if (recipe.snapshot) {
    dependency = !candidate
      ? "missing"
      : identityMatches(recipe.snapshot, candidate)
        ? "unloaded"
        : "mismatch";
    const selection = await dialog.showMessageBox(owner!, {
      type: dependency === "mismatch" ? "warning" : "question",
      title: "External dump dependency",
      message:
        dependency === "missing"
          ? "This recipe's dump is missing."
          : dependency === "mismatch"
            ? "The dump's size or modification time differs from the saved recipe."
            : "Open the recipe's external dump?",
      detail:
        "Recipes contain no heap data. Size and timestamp detect changes but are not cryptographic proof of identity. Loading always requires a fresh trusted DAC/cache choice. Queries are never run automatically.",
      buttons: [
        "Open recipe only",
        ...(candidate ? ["Load selected dump", "Relocate dump"] : ["Relocate dump"]),
        "Cancel",
      ],
      defaultId: 0,
      cancelId: candidate ? 3 : 2,
      noLink: true,
    });
    if (selection.response === (candidate ? 3 : 2)) return { kind: "cancelled" };
    const relocate = candidate ? selection.response === 2 : selection.response === 1;
    if (relocate) {
      const relocated = await dialog.showOpenDialog(owner!, {
        title: "Relocate recipe dump",
        properties: ["openFile"],
      });
      if (relocated.canceled || !relocated.filePaths[0]) return { kind: "cancelled" };
      candidate = await identifyDump(relocated.filePaths[0]);
      if (!identityMatches(recipe.snapshot, candidate)) {
        const confirm = await dialog.showMessageBox(owner!, {
          type: "warning",
          title: "Dump identity mismatch",
          message: "This dump does not match the recipe's size and modification time.",
          detail: "Proceed only if this is the dump you intend to analyze. Results may differ.",
          buttons: ["Cancel", "Use this dump"],
          defaultId: 0,
          cancelId: 0,
          noLink: true,
        });
        if (confirm.response !== 1) return { kind: "cancelled" };
      }
    }
    if (candidate && (relocate || selection.response === 1)) {
      if (!(await importDump(candidate.path))) return { kind: "cancelled" };
      dependency = identityMatches(recipe.snapshot, candidate) ? "ready" : "mismatch";
      loaded = true;
    }
  }
  if (!loaded) {
    if (state.snapshotId)
      await request({ operation: "snapshot.dispose", snapshotId: snapshot(), args: {} });
    state = {
      ...state,
      snapshotId: null,
      dumpName: null,
      objectCount: null,
      sourcePartial: false,
      diagnosticCount: 0,
    };
    dump = null;
    unloadedDependency = recipe.snapshot ? { recipePath: path, snapshot: recipe.snapshot } : null;
  }
  recipePath = path;
  state = { ...state, recipeName: basename(path) };
  return {
    kind: "recipe",
    document: {
      query: recipe.query,
      settings: recipe.settings,
      annotations: recipe.annotations,
      hiddenLayers: recipe.hiddenLayers,
    },
    state: { ...state },
    dependency,
  };
}

async function execute(input: DesktopCommand): Promise<DesktopValue> {
  switch (input.kind) {
    case "openDump": {
      if (!(await confirmReplacement(false))) return { kind: "cancelled" };
      const chosen = await dialog.showOpenDialog(owner!, {
        title: "Open supported .NET heap dump",
        properties: ["openFile"],
        filters: [
          { name: "Heap dumps", extensions: ["dmp", "dump", "core"] },
          { name: "All files", extensions: ["*"] },
        ],
      });
      if (chosen.canceled || !chosen.filePaths[0] || !(await importDump(chosen.filePaths[0])))
        return { kind: "cancelled" };
      return { kind: "state", state: { ...state } };
    }
    case "openRecipe":
      return openRecipe();
    case "saveRecipe": {
      const chosen = await dialog.showSaveDialog(owner!, {
        title: "Save recipe as a new file",
        defaultPath: recipePath
          ? `${recipePath.replace(/\.(mvrecipe|json)$/i, "")}-copy.mvrecipe`
          : "workspace.mvrecipe",
        filters: [{ name: "MemoryVisualizer recipe", extensions: ["mvrecipe"] }],
        properties: ["createDirectory", "showOverwriteConfirmation"],
      });
      if (chosen.canceled || !chosen.filePath) return { kind: "cancelled" };
      const recipe = recipeForSave(input.document, chosen.filePath, dump, unloadedDependency);
      await writeRecipe(chosen.filePath, recipe);
      if (
        dump &&
        recipe.snapshot?.locator === basename(dump.path) &&
        dirname(chosen.filePath) !== dirname(dump.path)
      ) {
        await dialog.showMessageBox(owner!, {
          type: "info",
          title: "Portable recipe saved",
          message: "The dump is outside this recipe's folder.",
          detail:
            "Only its filename and size/timestamp identity were saved, not an external absolute path. Reopening may require relocating the dump.",
          buttons: ["OK"],
        });
      }
      recipePath = chosen.filePath;
      if (!dump)
        unloadedDependency = recipe.snapshot ? { recipePath, snapshot: recipe.snapshot } : null;
      state = { ...state, recipeName: basename(chosen.filePath) };
      return { kind: "saved", name: basename(chosen.filePath) };
    }
    case "run":
      return {
        kind: "native",
        value: await request({
          operation: "query.run",
          snapshotId: snapshot(),
          args: { text: input.text, settings: input.settings },
        }),
      };
    case "rows":
      return {
        kind: "native",
        value: await request({
          operation: "query.page",
          snapshotId: snapshot(),
          args: { queryId: input.queryId, cursor: input.cursor, pageSize: 32 },
        }),
      };
    case "elements":
      return {
        kind: "native",
        value: await request({
          operation: "scene.page",
          snapshotId: snapshot(),
          args: { sceneId: input.sceneId, cursor: input.cursor, pageSize: 32 },
        }),
      };
    case "details":
      return {
        kind: "native",
        value: await request({
          operation: "details",
          snapshotId: snapshot(),
          args: {
            runtime: input.runtime,
            address: input.address,
            cursor: input.cursor,
            pageSize: 32,
          },
        }),
      };
    case "export": {
      const id = snapshot();
      const selected = await dialog.showSaveDialog(owner!, {
        title: "Export shared-engine SVG to a new file",
        defaultPath: "memory-map.svg",
        filters: [{ name: "SVG vector diagram", extensions: ["svg"] }],
        properties: ["createDirectory", "showOverwriteConfirmation"],
      });
      if (selected.canceled || !selected.filePath) return { kind: "cancelled" };
      return {
        kind: "native",
        value: await request({
          operation: "export",
          snapshotId: id,
          args: { sceneId: input.sceneId, path: selected.filePath },
        }),
      };
    }
    case "dispose":
      if (state.snapshotId)
        await request({ operation: "snapshot.dispose", snapshotId: state.snapshotId, args: {} });
      state = {
        ...state,
        snapshotId: null,
        dumpName: null,
        objectCount: null,
        sourcePartial: false,
        diagnosticCount: 0,
      };
      return { kind: "state", state: { ...state } };
    case "restart":
      operationEpoch++;
      await worker?.close();
      state = {
        ...state,
        snapshotId: null,
        objectCount: null,
        sourcePartial: false,
        diagnosticCount: 0,
      };
      await startWorker();
      return { kind: "state", state: { ...state } };
  }
}

ipcMain.handle("workspace:invoke", async (event, value: unknown): Promise<DesktopOutcome> => {
  let acquired = false;
  let admitted = false;
  try {
    if (!authorized(event)) throw new ProtocolError("InvalidRequest", "Window is not authorized.");
    const input = command(value);
    if (activeInvocations >= 8) throw new ProtocolError("Busy", "Desktop request limit reached.");
    const mutates = !["rows", "elements", "details"].includes(input.kind);
    if (exclusive || (mutates && activeInvocations !== 0))
      throw new ProtocolError("Busy", "Wait for the current operation or cancel it.");
    if (mutates) {
      exclusive = true;
      acquired = true;
    }
    activeInvocations++;
    admitted = true;
    const epoch = operationEpoch;
    const result = await execute(input);
    if (epoch !== operationEpoch && input.kind !== "restart")
      throw new ProtocolError(
        "StaleSnapshot",
        "The workspace changed before this operation completed.",
      );
    return { ok: true, value: result };
  } catch (error) {
    return errorOutcome(error);
  } finally {
    if (acquired) exclusive = false;
    if (admitted) activeInvocations--;
  }
});
ipcMain.handle("workspace:cancel", (event): void => {
  if (!authorized(event)) throw new ProtocolError("InvalidRequest", "Window is not authorized.");
  for (const id of active) worker?.cancel(id);
});
ipcMain.on("workspace:dirty", (event, value: unknown) => {
  if (authorized(event) && typeof value === "boolean") {
    dirty = value;
    owner?.setDocumentEdited(value);
  }
});

function menu(): void {
  const item = (
    label: string,
    accelerator: string,
    action: Extract<DesktopEvent, { kind: "menu" }>["action"],
  ): MenuItemConstructorOptions => ({
    label,
    accelerator,
    click: () => emit({ kind: "menu", action }),
  });
  const template: MenuItemConstructorOptions[] = [
    ...(process.platform === "darwin" ? [{ role: "appMenu" as const }] : []),
    {
      label: "File",
      submenu: [
        item("Open Dump...", "CmdOrCtrl+O", "openDump"),
        item("Open Recipe...", "CmdOrCtrl+Shift+O", "openRecipe"),
        item("Save Recipe As...", "CmdOrCtrl+S", "saveRecipe"),
        item("Export SVG...", "CmdOrCtrl+Shift+E", "export"),
        { type: "separator" },
        { role: process.platform === "darwin" ? "close" : "quit" },
      ],
    },
    { role: "editMenu" },
    {
      label: "Query",
      submenu: [item("Run", "CmdOrCtrl+Enter", "run"), item("Cancel", "Escape", "cancel")],
    },
    { role: "windowMenu" },
  ];
  Menu.setApplicationMenu(Menu.buildFromTemplate(template));
}

export function createWorkspaceWindow(): BrowserWindow {
  const window = new BrowserWindow({
    width: 1440,
    height: 960,
    minWidth: 900,
    minHeight: 640,
    title: "MemoryVisualizer",
    webPreferences: {
      preload: resolve(directory, "workspace-preload.cjs"),
      nodeIntegration: false,
      contextIsolation: true,
      sandbox: true,
      webSecurity: true,
      devTools: !app.isPackaged,
    },
  });
  window.webContents.setWindowOpenHandler(() => ({ action: "deny" }));
  window.webContents.on("will-navigate", (event) => event.preventDefault());
  window.webContents.on("will-attach-webview", (event) => event.preventDefault());
  window.webContents.session.setPermissionRequestHandler((_contents, _permission, callback) =>
    callback(false),
  );
  window.webContents.session.setPermissionCheckHandler(() => false);
  window.webContents.session.webRequest.onBeforeRequest((details, callback) =>
    callback({ cancel: !details.url.startsWith("file:") }),
  );
  window.on("close", (event) => {
    if (closing) return;
    event.preventDefault();
    if (checkingClose) return;
    checkingClose = true;
    void (async () => {
      try {
        if (dirty) {
          const choice = await dialog.showMessageBox(window, {
            type: "question",
            title: "Unsaved recipe changes",
            message: "Close without saving your recipe changes?",
            buttons: ["Keep editing", "Discard and close"],
            defaultId: 0,
            cancelId: 0,
            noLink: true,
          });
          if (choice.response !== 1) return;
        }
        closing = true;
        await worker?.close();
        window.destroy();
        app.quit();
      } finally {
        checkingClose = false;
      }
    })();
  });
  return window;
}

app.on("before-quit", (event) => {
  if (!closing && owner && !owner.isDestroyed()) {
    event.preventDefault();
    owner.close();
  }
});
app.on("window-all-closed", () => app.quit());
void app
  .whenReady()
  .then(async () => {
    menu();
    owner = createWorkspaceWindow();
    await owner.loadFile(page);
    try {
      await startWorker();
    } catch (error) {
      const outcome = errorOutcome(error);
      if (!outcome.ok) emit({ kind: "failure", ...outcome.error });
    }
  })
  .catch(() => {
    console.error("Desktop assets could not be loaded.");
    void (worker?.close() ?? Promise.resolve()).finally(() => app.exit(1));
  });
