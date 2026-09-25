let activeRequest;
let snapshot;
let generation = 0;
const status = document.getElementById("status");
const api = window.memoryVisualizer;
api.onFailure((error) => {
  generation++;
  activeRequest = undefined;
  snapshot = undefined;
  status.textContent = error.message;
});
api.onProgress((event) => {
  if (event.requestId === activeRequest?.requestId) {
    status.textContent = `${event.phase}: ${event.completed}/${event.total}`;
  }
});
document.getElementById("load").addEventListener("click", async () => {
  const current = ++generation;
  snapshot = undefined;
  const request = api.loadFixture(500);
  activeRequest = request;
  status.textContent = "Loading synthetic fixture.";
  const outcome = await request.result;
  if (current !== generation) return;
  activeRequest = undefined;
  snapshot = outcome.ok ? outcome.value.snapshotId : undefined;
  status.textContent = outcome.ok ? "Synthetic snapshot loaded." : outcome.error.message;
});
document.getElementById("cancel").addEventListener("click", () => {
  if (activeRequest) void api.cancel(activeRequest.requestId);
});
document.getElementById("dispose").addEventListener("click", async () => {
  if (!snapshot) return;
  const current = ++generation;
  const request = api.disposeSnapshot(snapshot);
  snapshot = undefined;
  activeRequest = request;
  const outcome = await request.result;
  if (current !== generation) return;
  activeRequest = undefined;
  status.textContent = outcome.ok ? "Snapshot disposed." : outcome.error.message;
});
