// Protocol v2 is deliberately separate from the closed synthetic v1 protocol.
export const NATIVE_LIMITS = {
  maxFrameBytes: 65536,
  maxOutstanding: 8,
  maxPageSize: 32,
  maxSceneItems: 1024,
  maxResults: 4096,
  maxQueryLength: 16384,
  progressIntervalMs: 100,
} as const;

export type Redaction = { addresses: boolean; strings: boolean; paths: boolean; labels: boolean };
export type SceneSettings = {
  plotWidth: number;
  viewport: { start: string; size: string } | null;
  redaction: Redaction;
  maxResults: number;
  maxElements: number;
};
export const DEFAULT_SETTINGS: SceneSettings = {
  plotWidth: 1024,
  viewport: null,
  redaction: { addresses: false, strings: false, paths: false, labels: false },
  maxResults: 4096,
  maxElements: 1024,
};
export type Bounds = { x: number; y: number; width: number; height: number };
export type Source = {
  runtime: number;
  heap: number;
  statementIndex: number;
  kind: string;
  address: string;
  size: string;
  segmentAddress: string | null;
  methodTable: string | null;
};
export type SceneElement = {
  id: string;
  laneId: string;
  layer: number;
  geometry:
    | { kind: "rectangle"; bounds: Bounds }
    | { kind: "line"; start: { x: number; y: number }; finish: { x: number; y: number } };
  bounds: Bounds;
  style: { fill: string; stroke: string; strokeWidth: number };
  text: {
    bounds: Bounds;
    lines: { text: string; x: number; baseline: number; width: number }[];
    cellWidth: number;
    fontSize: number;
    lineHeight: number;
    fill: string;
    replacedCodeUnits: number;
    isTruncated: boolean;
  } | null;
  source: Source | null;
  isClipped: boolean;
};
export type Diagnostic = {
  code: string;
  message: string;
  span: { offset: number; length: number; line: number; column: number };
};
export type Entity = {
  kind: string;
  snapshotId: string;
  runtime: number;
  heap: number;
  segmentAddress: string;
  address: string;
  size: string;
  heapKind: string;
  end: string | null;
  methodTable: string | null;
  type: string | null;
  generation: number | null;
  isFree: boolean | null;
};
export type QueryValue =
  | { kind: "uint64" | "text"; value: string }
  | { kind: "boolean"; value: boolean }
  | { kind: "entity"; value: Entity }
  | { kind: "missing"; value: null };
export type Row = {
  statementIndex: number;
  entity: Entity;
  values: { name: string; value: QueryValue }[];
};
export type SceneInfo = {
  schemaVersion: 1;
  sceneId: string;
  snapshotId: string;
  bounds: Bounds;
  lanes: { id: string; runtime: number | null; heap: number | null; bounds: Bounds }[];
  theme: { background: string; stroke: string; text: string };
  redaction: Redaction;
  elementCount: number;
  status: "complete" | "truncated" | "cancelled" | "failed";
  truncationReasons: string[];
};
export type NativeResult =
  | { tag: "snapshot"; objectCount: string; sourcePartial: boolean; diagnosticCount: number }
  | { tag: "disposed" }
  | {
      tag: "query";
      queryId: string;
      status: "complete" | "truncated" | "cancelled" | "failed";
      sourceAvailable: boolean;
      sourcePartial: boolean;
      sourceDiagnosticCount: number;
      rowCount: number;
      candidates: string;
      truncationReasons: string[];
      diagnostics: Diagnostic[];
      scene: SceneInfo | null;
    }
  | { tag: "rows"; queryId: string; items: Row[]; nextCursor: string | null }
  | { tag: "elements"; sceneId: string; items: SceneElement[]; nextCursor: string | null }
  | { tag: "details"; items: Entity[]; nextCursor: string | null }
  | { tag: "export"; byteLength: string };
export type NativeOperation =
  | {
      operation: "snapshot.load";
      snapshotId: null;
      args: {
        path: string;
        dacPath: string | null;
        cachePath: string | null;
        allowNetwork: boolean;
      };
    }
  | { operation: "snapshot.dispose"; snapshotId: string; args: Record<string, never> }
  | { operation: "query.run"; snapshotId: string; args: { text: string; settings: SceneSettings } }
  | {
      operation: "query.page";
      snapshotId: string;
      args: { queryId: string; cursor: string | null; pageSize: number };
    }
  | {
      operation: "scene.page";
      snapshotId: string;
      args: { sceneId: string; cursor: string | null; pageSize: number };
    }
  | {
      operation: "details";
      snapshotId: string;
      args: { runtime: number; address: string; cursor: string | null; pageSize: number };
    }
  | { operation: "export"; snapshotId: string; args: { sceneId: string; path: string } };
export type NativeRequest = NativeOperation & { tag: "request"; version: 2; requestId: string };
export type NativeProgress = {
  tag: "progress";
  version: 2;
  requestId: string;
  snapshotId: string | null;
  phase: string;
  completed: string;
};
export type NativeSuccess = {
  tag: "success";
  version: 2;
  requestId: string;
  snapshotId: string | null;
  result: NativeResult;
};
export type NativeError = { code: string; message: string; retryable: boolean };
export const NATIVE_OPERATIONS = [
  "snapshot.load",
  "snapshot.dispose",
  "query.run",
  "query.page",
  "scene.page",
  "details",
  "export",
] as const;
export type NativeCapabilities = {
  backend: "native";
  operations: typeof NATIVE_OPERATIONS;
  limits: typeof NATIVE_LIMITS;
};
export type NativeInbound =
  | { tag: "hello"; versions: [2]; extensions: [] }
  | NativeRequest
  | { tag: "cancel"; version: 2; requestId: string }
  | { tag: "shutdown"; version: 2 };
export type NativeOutbound =
  | { tag: "ready"; version: 2; capabilities: NativeCapabilities }
  | NativeSuccess
  | NativeProgress
  | { tag: "error"; version: 2; requestId: string; snapshotId: string | null; error: NativeError }
  | { tag: "fatal"; code: string; message: string }
  | { tag: "bye"; version: 2 };

export type Recipe = {
  schemaVersion: 1;
  query: string;
  settings: SceneSettings;
  annotations: { elementId: string; text: string }[];
  hiddenLayers: number[];
  snapshot: { locator: string; size: string; modifiedUtc: string } | null;
};
export type WorkspaceDocument = {
  query: string;
  settings: SceneSettings;
  annotations: Recipe["annotations"];
  hiddenLayers: number[];
};
export type WorkspaceState = {
  snapshotId: string | null;
  dumpName: string | null;
  recipeName: string | null;
  objectCount: string | null;
  sourcePartial: boolean;
  diagnosticCount: number;
};
export type DesktopCommand =
  | { kind: "openDump" }
  | { kind: "openRecipe" }
  | { kind: "saveRecipe"; document: WorkspaceDocument }
  | { kind: "run"; text: string; settings: SceneSettings }
  | { kind: "rows"; queryId: string; cursor: string | null }
  | { kind: "elements"; sceneId: string; cursor: string | null }
  | { kind: "details"; runtime: number; address: string; cursor: string | null }
  | { kind: "export"; sceneId: string }
  | { kind: "dispose" }
  | { kind: "restart" };
export type DesktopValue =
  | { kind: "state"; state: WorkspaceState }
  | {
      kind: "recipe";
      document: WorkspaceDocument;
      state: WorkspaceState;
      dependency: "ready" | "missing" | "mismatch" | "unloaded";
    }
  | { kind: "saved"; name: string }
  | { kind: "native"; value: NativeSuccess }
  | { kind: "cancelled" };
export type DesktopOutcome =
  { ok: true; value: DesktopValue } | { ok: false; error: { code: string; message: string } };
export type DesktopEvent =
  | { kind: "progress"; value: NativeProgress }
  | { kind: "failure"; code: string; message: string }
  | {
      kind: "menu";
      action: "openDump" | "openRecipe" | "saveRecipe" | "run" | "cancel" | "export";
    };
export type WorkspaceAPI = {
  invoke(command: DesktopCommand): Promise<DesktopOutcome>;
  cancel(): Promise<void>;
  setDirty(dirty: boolean): void;
  onEvent(callback: (event: DesktopEvent) => void): () => void;
};
