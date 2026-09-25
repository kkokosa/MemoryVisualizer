import assert from "node:assert/strict";
import { type BrowserWindow } from "electron";
import type { WorkerClient } from "../src/worker-client.js";

export async function runElectronScenario(
  window: BrowserWindow,
  worker: WorkerClient,
  createWindow: () => BrowserWindow,
  shell: string,
  crash: boolean,
): Promise<void> {
  // Electron exposes this runtime diagnostic but omits it from its public types.
  const readPreferences: unknown = Reflect.get(window.webContents, "getLastWebPreferences");
  assert.equal(typeof readPreferences, "function");
  if (typeof readPreferences !== "function") throw new Error("Missing preference diagnostic.");
  const preferences: unknown = Reflect.apply(readPreferences, window.webContents, []);
  if (
    typeof preferences !== "object" ||
    preferences === null ||
    !("sandbox" in preferences) ||
    !("contextIsolation" in preferences) ||
    !("nodeIntegration" in preferences)
  ) {
    throw new Error("Invalid runtime preference diagnostic.");
  }
  assert.equal(preferences.sandbox, true);
  assert.equal(preferences.contextIsolation, true);
  assert.equal(preferences.nodeIntegration, false);
  if (crash) {
    await window.webContents.executeJavaScript(`(() => {
      window.failureObserved = new Promise(resolve =>
        window.memoryVisualizer.onFailure(error => resolve(error.code)));
      window.pendingOutcome = window.memoryVisualizer.loadFixture(5000).result;
      return true;
    })()`);
    await new Promise((done) => setTimeout(done, 50));
    const pid = worker.child.pid!;
    worker.child.kill("SIGKILL");
    const observed: unknown = await window.webContents.executeJavaScript(`(async () => {
      const notification = await window.failureObserved;
      const pending = await window.pendingOutcome;
      const next = await window.memoryVisualizer.capabilities().result;
      return {notification, pendingCode: pending.error.code, nextCode: next.error.code};
    })()`);
    assert.deepEqual(observed, {
      notification: "WorkerExited",
      pendingCode: "WorkerExited",
      nextCode: "WorkerExited",
    });
    assert.equal(worker.child.pid, pid, "A crash must not silently restart the worker.");
    console.log(`Owned worker PID: ${pid}`);
    window.close();
    return;
  }
  const result: unknown = await window.webContents.executeJavaScript(`(async () => {
    const api = window.memoryVisualizer;
    if (typeof require !== "undefined" || typeof process !== "undefined"
        || api.invoke || api.send || api.openPath || api.spawn) throw Error("Privilege leak");
    const names = Object.keys(api).sort();
    const expected = ["capabilities","loadFixture","disposeSnapshot","query","details","scene",
      "validateRecipe","exportSvg","cancel","onProgress","onFailure"].sort();
    if (JSON.stringify(names) !== JSON.stringify(expected)) throw Error("Unexpected API");
    const denied = await api.query(null, "test", 129, null).result;
    if (denied.ok) throw Error("Invalid input accepted");
    let progress = 0;
    const unsubscribe = api.onProgress(() => progress++);
    const slow = api.loadFixture(1000);
    await new Promise(r => setTimeout(r, 180));
    await api.cancel(slow.requestId);
    const cancelled = await slow.result;
    if (cancelled.ok || cancelled.error.code !== "Cancelled") throw Error("Cancellation failed");
    if (progress < 1 || progress > 3) throw Error("Progress bound failed");
    unsubscribe();
    const loaded = await api.loadFixture(0).result;
    if (!loaded.ok) throw Error("Load failed");
    const snapshotId = loaded.value.snapshotId;
    const oversized = await api.query(snapshotId, "x".repeat(1000000), 1, null).result;
    if (oversized.ok || oversized.error.code !== "InvalidRequest") throw Error("Preload size cap failed");
    const blocked = api.query(snapshotId, {toString(){throw Error("Must not coerce")}}, 1, null);
    if ((await blocked.result).ok) throw Error("Preload type guard failed");
    const burst = Array.from({length: 9}, () => api.query(snapshotId, "bounded", 1, null));
    const outcomes = await Promise.all(burst.map(x => x.result));
    if (outcomes.filter(x => !x.ok && x.error.code === "Busy").length !== 1
        || outcomes.filter(x => x.ok).length !== 8) throw Error("Preload request cap failed");
    const page = await api.query(snapshotId, "synthetic", 128, null).result;
    if (!page.ok || page.value.result.items.length !== 3) throw Error("Query failed");
    if (!page.value.result.items.some(x => x.address === "0xffffffffffffffff"))
      throw Error("Lost uint64");
    const disposed = await api.disposeSnapshot(snapshotId).result;
    if (!disposed.ok) throw Error("Dispose failed");
    const stale = await api.scene(snapshotId, 1).result;
    if (stale.ok || stale.error.code !== "StaleSnapshot") throw Error("Stale result exposed");
    return true;
  })()`);
  assert.equal(result, true);
  const outsider = createWindow();
  try {
    await outsider.loadFile(shell);
    const denied: unknown = await outsider.webContents.executeJavaScript(
      "window.memoryVisualizer.capabilities().result",
    );
    assert.deepEqual(denied, {
      ok: false,
      error: { code: "InvalidRequest", message: "Window is not authorized." },
    });
  } finally {
    outsider.destroy();
  }
  await window.webContents.executeJavaScript(`(async () => {
    document.getElementById("load").click();
    document.getElementById("load").click();
    await new Promise(resolve => setTimeout(resolve, 100));
    document.getElementById("cancel").click();
    const deadline = performance.now() + 2000;
    while (document.getElementById("status").textContent !== "Request cancelled.") {
      if (performance.now() > deadline) throw Error("Older shell operation hid current cancellation");
      await new Promise(resolve => setTimeout(resolve, 10));
    }
  })()`);
  const pid = worker.child.pid;
  assert.ok(pid);
  await window.webContents.executeJavaScript("window.memoryVisualizer.loadFixture(5000).requestId");
  await new Promise((done) => setTimeout(done, 50));
  console.log(`Owned worker PID: ${pid}`);
  // Exercise window-all-closed -> before-quit, with work still active.
  window.close();
}
