import type { Progress, Success } from "./protocol.js";

export type BridgeOutcome =
  { ok: true; value: Success } | { ok: false; error: { code: string; message: string } };
export type BridgeRequest = { requestId: string; result: Promise<BridgeOutcome> };
export type ControlOutcome = { ok: true } | { ok: false; error: { code: string; message: string } };
export type DesktopApi = {
  capabilities(): BridgeRequest;
  loadFixture(delayMs: number): BridgeRequest;
  disposeSnapshot(snapshotId: string): BridgeRequest;
  query(snapshotId: string, text: string, pageSize: number, cursor: string | null): BridgeRequest;
  details(
    snapshotId: string,
    objectId: string,
    pageSize: number,
    cursor: string | null,
  ): BridgeRequest;
  scene(snapshotId: string, maxItems: number): BridgeRequest;
  validateRecipe(query: string): BridgeRequest;
  exportSvg(snapshotId: string, maxBytes: number): BridgeRequest;
  cancel(requestId: string): Promise<ControlOutcome>;
  onProgress(listener: (progress: Progress) => void): () => void;
  onFailure(listener: (error: { code: string; message: string }) => void): () => void;
};
