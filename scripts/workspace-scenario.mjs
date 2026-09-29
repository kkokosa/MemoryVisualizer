import assert from "node:assert/strict";
import childProcess from "node:child_process";
import { syncBuiltinESMExports } from "node:module";
import { mkdtemp, readFile, rm, stat } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { app, BrowserWindow, dialog, ipcMain } from "electron";

const root = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const [dump, dac] = process.argv.slice(2);
assert.ok(dump && dac, "Use only a generated WithHeap fixture and its matching offline DAC.");
const folder = await mkdtemp(join(tmpdir(), "MemoryVisualizer-desktop-"));
const recipePath = join(folder, "workspace.mvrecipe");
const svgPath = join(folder, "scene.svg");
const workers = [];
const queryResults = [];
const handle = ipcMain.handle.bind(ipcMain);
let commitBarrier = null;
ipcMain.handle = (channel, listener) =>
  handle(channel, async (event, ...args) => {
    const outcome = await listener(event, ...args);
    if (channel === "workspace:cancel" && commitBarrier) commitBarrier.cancelled = true;
    if (channel === "workspace:invoke" && commitBarrier?.kind === args[0]?.kind && outcome.ok) {
      const barrier = commitBarrier;
      barrier.committed = true;
      await new Promise((release) => {
        barrier.release = release;
      });
    }
    return outcome;
  });
const holdCommittedReply = (kind) => {
  assert.equal(commitBarrier, null);
  commitBarrier = { kind, committed: false, cancelled: false, release: null };
};
const spawn = childProcess.spawn;
childProcess.spawn = function (file, args, options) {
  const child = spawn(file, args, options);
  if (args?.includes("--backend=native")) {
    const record = { child, ready: false };
    workers.push(record);
    console.log(`Owned native worker PID: ${child.pid}`);
    let buffer = "";
    child.stdout.on("data", (chunk) => {
      buffer += chunk.toString("utf8");
      for (;;) {
        const end = buffer.indexOf("\n");
        if (end < 0) break;
        const frame = JSON.parse(buffer.slice(0, end));
        buffer = buffer.slice(end + 1);
        if (frame.tag === "ready") record.ready = true;
        if (frame.tag === "success" && frame.result.tag === "query")
          queryResults.push(frame.result);
      }
      assert.ok(buffer.length <= 65536);
    });
  }
  return child;
};
syncBuiltinESMExports();
const openPaths = [];
const savePaths = [];
let unsavedPrompts = 0;
let permitQuit = false;
let exitCode = 0;
app.on("before-quit", (event) => {
  if (!permitQuit) event.preventDefault();
});
dialog.showOpenDialog = async () => {
  const file = openPaths.shift();
  assert.ok(file, "Unexpected native open dialog.");
  return { canceled: false, filePaths: [file] };
};
dialog.showSaveDialog = async () => {
  const file = savePaths.shift();
  assert.ok(file, "Unexpected native save dialog.");
  return { canceled: false, filePath: file };
};
dialog.showMessageBox = async (_window, options) => {
  if (options.title === "Trust the target runtime DAC")
    return { response: 0, checkboxChecked: false };
  if (options.title === "Unsaved recipe changes") {
    unsavedPrompts++;
    return { response: 1, checkboxChecked: false };
  }
  if (options.title === "External dump dependency") return { response: 1, checkboxChecked: false };
  if (options.title === "Portable recipe saved") return { response: 0, checkboxChecked: false };
  throw new Error(`Unexpected native message dialog: ${options.title}`);
};

const wait = async (predicate, message, milliseconds = 30000) => {
  const deadline = performance.now() + milliseconds;
  while (!(await predicate())) {
    assert.ok(performance.now() < deadline, message);
    await new Promise((resolve) => setTimeout(resolve, 25));
  }
};
let window;
const evaluate = async (source) => {
  try {
    return await window.webContents.executeJavaScript(source, true);
  } catch (error) {
    throw new Error(`Renderer assertion failed: ${source.slice(0, 240)}`, { cause: error });
  }
};
const click = async (label) => {
  await evaluate(`(() => {
    const button = [...document.querySelectorAll("button")].find(item => item.textContent.trim().includes(${JSON.stringify(label)}));
    if (!button || button.disabled) throw new Error("Missing or disabled button: " + ${JSON.stringify(label)});
    button.click();
  })()`);
};
const editor = async (value) => {
  await evaluate(`(() => {
    const input = document.querySelector("textarea");
    const setter = Object.getOwnPropertyDescriptor(HTMLTextAreaElement.prototype, "value").set;
    setter.call(input, ${JSON.stringify(value)});
    input.dispatchEvent(new Event("input", {bubbles:true}));
  })()`);
};
const cancelAfterCommit = async () => {
  await wait(() => commitBarrier.committed, "Native operation did not commit.", 120000);
  await click("Cancel");
  await wait(() => commitBarrier.cancelled, "Cancellation did not reach native main.");
  assert.equal(
    await evaluate(`document.querySelector('[data-testid="open-dump"]').disabled`),
    true,
    "Cancel released stateful mutations before the authoritative terminal reply.",
  );
  const barrier = commitBarrier;
  commitBarrier = null;
  barrier.release();
};

async function run() {
  try {
    await import(pathToFileURL(join(root, "desktop", "dist", "src", "main.js")).href);
    await app.whenReady();
    await wait(() => BrowserWindow.getAllWindows().length > 0, "Workspace window was not created.");
    window = BrowserWindow.getAllWindows()[0];
    await wait(() => !window.webContents.isLoading(), "Workspace assets did not load.");
    await wait(
      () => evaluate(`Boolean(window.workspace && document.querySelector("textarea"))`),
      "React workspace/preload unavailable.",
    );
    await wait(() => workers[0]?.ready, "Native packaged bridge handshake unavailable.");
    const boundary = await evaluate(
      `({node:typeof window.require,process:typeof window.process,keys:Object.keys(window.workspace).sort(),query:document.querySelector("textarea").value})`,
    );
    assert.equal(boundary.node, "undefined");
    assert.equal(boundary.process, "undefined");
    assert.deepEqual(boundary.keys, ["cancel", "invoke", "onEvent", "setDirty"]);
    const prefs = window.webContents.getLastWebPreferences();
    assert.equal(prefs.sandbox, true);
    assert.equal(prefs.contextIsolation, true);
    assert.equal(prefs.nodeIntegration, false);
    const unauthorized = new BrowserWindow({
      show: false,
      webPreferences: {
        preload: join(root, "desktop", "dist", "src", "workspace-preload.cjs"),
        sandbox: true,
        contextIsolation: true,
        nodeIntegration: false,
      },
    });
    await unauthorized.loadURL(window.webContents.getURL());
    const rejected = await unauthorized.webContents.executeJavaScript(
      `window.workspace.invoke({kind:"restart"})`,
    );
    assert.equal(rejected.ok, false);
    assert.equal(rejected.error.code, "InvalidRequest");
    unauthorized.destroy();

    openPaths.push(dump, dac);
    holdCommittedReply("openDump");
    await click("Open memory dump");
    await cancelAfterCommit();
    await wait(
      () =>
        evaluate(
          `[...document.querySelectorAll("button")].some(b=>b.textContent.includes("Run query")&&!b.disabled)`,
        ),
      "Dump did not become usable.",
      120000,
    );
    const query = "MATCH (s:Segment) RETURN s AS BOX(Label=s.Heap,Background=Blue)";
    await editor(query);
    await click("Run query");
    await wait(() => queryResults.length === 1, "MQL did not execute.");
    assert.equal(queryResults[0].status, "complete");
    assert.equal(queryResults[0].sourcePartial, false);
    assert.ok(queryResults[0].scene.elementCount > 0);
    await wait(
      () => evaluate(`Boolean(document.querySelector("svg [data-element-id]"))`),
      "Shared SVG was not rendered.",
    );
    window.focus();
    window.webContents.focus();
    await evaluate(`document.querySelector("svg [data-element-id]").focus()`);
    window.webContents.sendInputEvent({ type: "keyDown", keyCode: "Return" });
    window.webContents.sendInputEvent({ type: "keyUp", keyCode: "Return" });
    await wait(
      () =>
        evaluate(
          `document.querySelector("svg [data-element-id]")?.getAttribute("aria-pressed") === "true"`,
        ),
      "Enter did not select the focused SVG element.",
    );
    const paneWidth = await evaluate(`(() => {
      const separator = document.querySelector('[role="separator"][aria-label="Resize snapshot pane"]');
      separator.focus();
      return Number(separator.getAttribute("aria-valuenow"));
    })()`);
    window.webContents.sendInputEvent({ type: "keyDown", keyCode: "Right" });
    window.webContents.sendInputEvent({ type: "keyUp", keyCode: "Right" });
    await wait(
      () =>
        evaluate(
          `Number(document.querySelector('[role="separator"][aria-label="Resize snapshot pane"]').getAttribute("aria-valuenow")) === ${paneWidth + 20}`,
        ),
      "Right arrow did not resize the snapshot pane by 20 pixels.",
    );
    await wait(
      () =>
        evaluate(
          `[...document.querySelectorAll("button")].some(b=>b.textContent.trim()==="Export SVG"&&!b.disabled)`,
        ),
      "Scene export not ready.",
    );
    savePaths.push(svgPath);
    holdCommittedReply("export");
    await click("Export SVG");
    await cancelAfterCommit();
    await wait(
      () =>
        evaluate(
          `document.querySelector('[data-testid="workspace-status"]').textContent.includes("Exported shared-engine SVG")`,
        ),
      "Committed export was incorrectly discarded after Cancel.",
    );
    await wait(async () => {
      try {
        return (await stat(svgPath)).size > 0;
      } catch (error) {
        if (error.code === "ENOENT") return false;
        throw error;
      }
    }, "Shared SVG was not exported.");
    const svg = await readFile(svgPath, "utf8");
    assert.match(svg, /<svg/);
    assert.doesNotMatch(svg, /<script/);
    savePaths.push(recipePath);
    holdCommittedReply("saveRecipe");
    await click("Save recipe");
    await cancelAfterCommit();
    await wait(
      () => evaluate(`document.body.textContent.includes("workspace.mvrecipe")`),
      "Committed recipe save was incorrectly discarded after Cancel.",
    );
    await wait(async () => {
      try {
        return (await stat(recipePath)).size > 0;
      } catch (error) {
        if (error.code === "ENOENT") return false;
        throw error;
      }
    }, "Recipe not saved.");
    const recipe = JSON.parse(await readFile(recipePath, "utf8"));
    assert.equal(recipe.schemaVersion, 1);
    assert.equal(recipe.query, query);
    assert.equal(JSON.stringify(recipe).includes(dac), false);
    assert.equal(JSON.stringify(recipe).includes(dump), false);
    await editor("MATCH (o:Object) RETURN unsupported");
    await click("Run query");
    await wait(() => queryResults.length === 2, "Diagnostic query did not finish.");
    assert.equal(queryResults[1].status, "failed");
    assert.ok(queryResults[1].diagnostics[0].span.length > 0);
    await wait(
      () =>
        evaluate(
          `[...document.querySelectorAll("button")].some(b=>b.textContent.includes("Run query")&&!b.disabled)`,
        ),
      "Diagnostic query did not release the UI.",
    );
    await editor(query);
    await click("Run query");
    await wait(() => queryResults.length === 3, "Recovery query did not finish.");
    await wait(
      () => evaluate(`document.querySelectorAll("svg rect").length > 0`),
      "Recovery scene was not rendered.",
    );
    await editor(`${query};`);
    const beforeCrash = await evaluate("document.querySelector('textarea').value");
    await evaluate(`(() => {
    window.__testFailure = null;
    window.workspace.onEvent(event => { if(event.kind === "failure") window.__testFailure = event; });
  })()`);
    workers[0].child.kill("SIGKILL");
    await wait(
      () =>
        evaluate(
          `Boolean(window.__testFailure && document.body.textContent.includes(window.__testFailure.message))`,
        ),
      "Worker failure was not visible.",
    );
    assert.equal(await evaluate("document.querySelector('textarea').value"), beforeCrash);
    assert.equal(queryResults.length, 3, "Crash silently replayed a query.");
    assert.equal(
      await evaluate(`document.querySelectorAll("svg rect").length`),
      0,
      "Crash retained a stale diagram.",
    );
    await evaluate("window.workspace.setDirty(true)");
    window.close();
    await wait(() => window.isDestroyed(), "Unsaved close did not finish.");
    assert.ok(unsavedPrompts > 0, "Unsaved changes were not protected.");
    assert.equal(openPaths.length, 0);
    assert.equal(savePaths.length, 0);
    console.log("Native desktop integration passed.");
  } catch (error) {
    console.error(error);
    for (const record of workers) if (record.child.exitCode === null) record.child.kill("SIGKILL");
    exitCode = 1;
  } finally {
    await rm(folder, { recursive: true, force: true });
    permitQuit = true;
    app.exit(exitCode);
  }
}

// Electron emits ready only after its ESM entry point finishes evaluation.
void run();
