import { useEffect, useRef, useState } from "react";
import type {
  DesktopCommand,
  DesktopValue,
  Entity,
  NativeProgress,
  NativeResult,
  Row,
  SceneElement,
  WorkspaceDocument,
  WorkspaceState,
} from "../src/native-types.js";
import { Diagram, QueryEditor, Settings, Splitter } from "./components.js";
import { DesktopOperationGate } from "./operation-gate.js";
import {
  annotationBasis,
  applyDetachedAnnotation,
  canExportCurrentScene,
  changeAnnotationBasis,
  isDirtyAfterSave,
  restoreRecipeAnnotations,
  sameAnnotationBasis,
  type AnnotationBasis,
  type AnnotationScope,
} from "./annotation-scope.js";
import {
  boundedInteger,
  canonicalAddress,
  collectScenePages,
  draftSettings,
  formatValue,
  LAYERS,
  readSettings,
  TEMPLATES,
  unwrapNative,
  type SettingsDraft,
} from "./model.js";

type QueryResult = Extract<NativeResult, { tag: "query" }>;
type ResultState = { result: QueryResult; query: string; revision: number; basis: AnnotationBasis };
type DetailState = { items: Entity[]; nextCursor: string | null; runtime: number; address: string };
type MenuAction = "openDump" | "openRecipe" | "saveRecipe" | "run" | "cancel" | "export";
const EMPTY_WORKSPACE: WorkspaceState = {
  snapshotId: null,
  dumpName: null,
  recipeName: null,
  objectCount: null,
  sourcePartial: false,
  diagnosticCount: 0,
};

function message(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

export function App() {
  const api = window.workspace;
  const [workspace, setWorkspace] = useState<WorkspaceState>(EMPTY_WORKSPACE);
  const [query, setQuery] = useState<string>(TEMPLATES[0].query);
  const [settings, setSettings] = useState(draftSettings);
  const latestBasisDocument = useRef<{ query: string; settings: SettingsDraft }>({
    query: TEMPLATES[0].query,
    settings: draftSettings(),
  });
  const [annotationScope, setAnnotationScopeState] = useState<AnnotationScope>(() => ({
    basis: annotationBasis(TEMPLATES[0].query, draftSettings(), 0),
    attached: [],
    detached: [],
  }));
  const annotationScopeRef = useRef(annotationScope);
  const setAnnotationScope = (update: (scope: AnnotationScope) => AnnotationScope) => {
    const next = update(annotationScopeRef.current);
    annotationScopeRef.current = next;
    setAnnotationScopeState(next);
  };
  const annotations = annotationScope.attached;
  const [hiddenLayers, setHiddenLayers] = useState<number[]>([]);
  const hiddenLayersRef = useRef<number[]>([]);
  const [dirty, setDirty] = useState(false);
  const [busy, setBusy] = useState<string | null>(null);
  const [cancellationPending, setCancellationPending] = useState(false);
  const [progress, setProgress] = useState<NativeProgress | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [notice, setNotice] = useState(
    "Open a dump, choose a composition, then run. Everything stays local.",
  );
  const [workerFailed, setWorkerFailed] = useState(false);
  const [dependencyWarning, setDependencyWarning] = useState<string | null>(null);
  const [resultState, setResultState] = useState<ResultState | null>(null);
  const [elements, setElements] = useState<SceneElement[]>([]);
  const [sceneBounded, setSceneBounded] = useState(false);
  const [rows, setRows] = useState<Row[]>([]);
  const [rowNext, setRowNext] = useState<string | null>(null);
  const [rowHistory, setRowHistory] = useState<(string | null)[]>([null]);
  const [rowsBusy, setRowsBusy] = useState(false);
  const [tab, setTab] = useState<"diagram" | "rows">("diagram");
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [hoverId, setHoverId] = useState<string | null>(null);
  const [details, setDetails] = useState<DetailState | null>(null);
  const [detailsBusy, setDetailsBusy] = useState(false);
  const [lookupRuntime, setLookupRuntime] = useState("0");
  const [lookupAddress, setLookupAddress] = useState("");
  const [leftSize, setLeftSize] = useState(2);
  const [rightSize, setRightSize] = useState(2);
  const operations = useRef(new DesktopOperationGate());
  const rowEpoch = useRef(0);
  const detailEpoch = useRef(0);
  const revision = useRef(0);
  const snapshotGeneration = useRef(0);
  const busyRef = useRef<string | null>(null);
  const pendingCancel = useRef<Promise<void>>(Promise.resolve());
  const actions = useRef<Record<MenuAction, () => void> | null>(null);

  const markDirty = () => {
    revision.current++;
    setDirty(true);
    api.setDirty(true);
  };
  const invalidateAnnotationSource = (reason: string) => {
    const generation = ++snapshotGeneration.current;
    setAnnotationScope((scope) =>
      changeAnnotationBasis(scope, { ...scope.basis, snapshotGeneration: generation }, reason),
    );
  };
  const editQuery = (value: string) => {
    if (value === latestBasisDocument.current.query || busyRef.current === "Opening recipe") return;
    latestBasisDocument.current.query = value;
    setAnnotationScope((scope) =>
      changeAnnotationBasis(
        scope,
        annotationBasis(value, latestBasisDocument.current.settings, snapshotGeneration.current),
        "Query changed",
      ),
    );
    setQuery(value);
    markDirty();
  };
  const clearResults = () => {
    rowEpoch.current++;
    detailEpoch.current++;
    setResultState(null);
    setElements([]);
    setRows([]);
    setRowNext(null);
    setRowHistory([null]);
    setRowsBusy(false);
    setSelectedId(null);
    setHoverId(null);
    setDetails(null);
    setDetailsBusy(false);
    setSceneBounded(false);
  };
  const requestCancellation = () => {
    pendingCancel.current = api.cancel().catch((cause: unknown) => {
      setError(`Cancellation failed: ${message(cause)}`);
    });
    return pendingCancel.current;
  };
  const cancel = () => {
    if (operations.current.cancellationRequested) return;
    if (operations.current.cancel() === "await-terminal") {
      setCancellationPending(true);
      setNotice("Cancellation requested. Waiting for the authoritative operation result.");
      void requestCancellation();
      return;
    }
    if (busyRef.current === "Running query") clearResults();
    rowEpoch.current++;
    detailEpoch.current++;
    busyRef.current = null;
    setBusy(null);
    setCancellationPending(false);
    setRowsBusy(false);
    setDetailsBusy(false);
    setProgress(null);
    setNotice("Cancelled. Your query, settings and annotations are unchanged.");
    void requestCancellation();
  };
  const execute = async (
    label: string,
    command: DesktopCommand,
    success: (value: DesktopValue, token: number) => Promise<void> | void,
  ) => {
    const token = operations.current.begin(command.kind);
    const previousBusy = busyRef.current !== null;
    busyRef.current = label;
    setBusy(label);
    setCancellationPending(false);
    setProgress(null);
    setError(null);
    try {
      if (previousBusy) await requestCancellation();
      else await pendingCancel.current;
      if (!operations.current.isCurrent(token)) return;
      if (!operations.current.submit(token)) {
        setNotice("Cancelled before the operation was submitted. The workspace is unchanged.");
        return;
      }
      const outcome = await api.invoke(command);
      if (!operations.current.isCurrent(token)) return;
      if (!outcome.ok) throw new Error(`${outcome.error.code}: ${outcome.error.message}`);
      if (outcome.value.kind === "cancelled") {
        setNotice("Cancelled. The current document is unchanged.");
        return;
      }
      await success(outcome.value, token);
    } catch (cause) {
      if (operations.current.isCurrent(token)) setError(message(cause));
    } finally {
      if (operations.current.isCurrent(token)) {
        operations.current.finish(token);
        busyRef.current = null;
        setBusy(null);
        setCancellationPending(false);
        setProgress(null);
      }
    }
  };
  const openDump = () => {
    if (workerFailed || busyRef.current !== null) return;
    void execute("Opening dump", { kind: "openDump" }, (value) => {
      if (value.kind !== "state") throw new Error("Expected snapshot state.");
      invalidateAnnotationSource("Snapshot replaced");
      setWorkspace(value.state);
      setDependencyWarning(null);
      clearResults();
      markDirty();
      setNotice("Snapshot loaded. Run the query when you are ready; nothing runs automatically.");
    });
  };
  const openRecipe = () => {
    if (workerFailed || busyRef.current !== null) return;
    if (annotationScopeRef.current.detached.length > 0) {
      setError(
        "Detached notes are local-only. Apply them to a current selection, or copy and remove them, before opening another recipe.",
      );
      return;
    }
    const openingRevision = revision.current;
    void execute("Opening recipe", { kind: "openRecipe" }, (value) => {
      if (value.kind !== "recipe") throw new Error("Expected a recipe document.");
      setWorkspace(value.state);
      clearResults();
      if (openingRevision !== revision.current) {
        invalidateAnnotationSource("Recipe dependency replaced the snapshot");
        setNotice(
          "Recipe dependency loaded, but edits made while opening were preserved. Open again to replace the document.",
        );
        return;
      }
      setQuery(value.document.query);
      setSettings(draftSettings(value.document.settings));
      latestBasisDocument.current = {
        query: value.document.query,
        settings: draftSettings(value.document.settings),
      };
      const generation = ++snapshotGeneration.current;
      setAnnotationScope((scope) =>
        restoreRecipeAnnotations(
          scope,
          annotationBasis(value.document.query, draftSettings(value.document.settings), generation),
          value.document.annotations,
          value.dependency,
        ),
      );
      setHiddenLayers(value.document.hiddenLayers);
      hiddenLayersRef.current = value.document.hiddenLayers;
      revision.current++;
      const hasDetachedNotes = annotationScopeRef.current.detached.length > 0;
      setDirty(hasDetachedNotes);
      api.setDirty(hasDetachedNotes);
      const dependencyMessage = {
        ready: "Snapshot dependency is ready.",
        missing: "Snapshot dependency is missing. Open the dump explicitly.",
        mismatch:
          "Accepted snapshot fingerprint differs from the recipe. Saved annotations are detached; review the source before running.",
        unloaded: "No snapshot dependency was loaded. Open a dump explicitly.",
      }[value.dependency];
      setDependencyWarning(value.dependency === "ready" ? null : dependencyMessage);
      setNotice(`Recipe opened. ${dependencyMessage} Run is always deliberate.`);
    });
  };
  const saveRecipe = () => {
    if (busyRef.current !== null) return;
    let document: WorkspaceDocument;
    try {
      document = {
        query: latestBasisDocument.current.query,
        settings: readSettings(latestBasisDocument.current.settings),
        annotations: annotationScopeRef.current.attached,
        hiddenLayers: hiddenLayersRef.current,
      };
    } catch (cause) {
      setError(message(cause));
      return;
    }
    const savingRevision = revision.current;
    void execute("Saving recipe", { kind: "saveRecipe", document }, (value) => {
      if (value.kind !== "saved") throw new Error("Expected a saved recipe.");
      setWorkspace((current) => ({ ...current, recipeName: value.name }));
      const detachedNoteCount = annotationScopeRef.current.detached.length;
      const remainsDirty = isDirtyAfterSave(savingRevision, revision.current, detachedNoteCount);
      setDirty(remainsDirty);
      api.setDirty(remainsDirty);
      if (revision.current === savingRevision) {
        setNotice(
          `Saved ${value.name}.${detachedNoteCount > 0 ? " Detached notes remain unsaved local work; the workspace is still dirty." : ""}`,
        );
      } else {
        setNotice(`Saved ${value.name}. Newer edits remain unsaved.`);
      }
    });
  };
  const run = () => {
    if (busyRef.current !== null && busyRef.current !== "Running query") return;
    if (!workspace.snapshotId || workerFailed) {
      setNotice("Open a snapshot before running a query.");
      return;
    }
    let parsedSettings;
    try {
      parsedSettings = readSettings(settings);
      if (query.trim().length === 0) throw new Error("Enter an MQL query first.");
    } catch (cause) {
      setError(message(cause));
      return;
    }
    const runRevision = revision.current;
    const runBasis = annotationBasis(query, settings, snapshotGeneration.current);
    const runQuery = query;
    const rowsRedacted = Object.values(parsedSettings.redaction).some(Boolean);
    clearResults();
    void execute(
      "Running query",
      { kind: "run", text: query, settings: parsedSettings },
      async (value, token) => {
        if (value.kind !== "native" || value.value.result.tag !== "query") {
          throw new Error("Expected query metadata.");
        }
        const result = value.value.result;
        const current = () => operations.current.isCurrent(token);
        setResultState({ result, query: runQuery, revision: runRevision, basis: runBasis });
        const [scenePage, rowPage] = await Promise.all([
          result.scene
            ? collectScenePages(api, result.scene.sceneId, current)
            : Promise.resolve({ items: [], bounded: false }),
          result.rowCount > 0 && !rowsRedacted
            ? api.invoke({ kind: "rows", queryId: result.queryId, cursor: null }).then(unwrapNative)
            : Promise.resolve(null),
        ]);
        if (!current()) return;
        if (rowPage) {
          if (rowPage.result.tag !== "rows" || rowPage.result.queryId !== result.queryId) {
            throw new Error("Row page belongs to a different query.");
          }
          if (rowPage.result.items.length > 32)
            throw new Error("Worker exceeded the row page limit.");
          setRows(rowPage.result.items);
          setRowNext(rowPage.result.nextCursor);
        }
        setElements(scenePage.items);
        setSceneBounded(scenePage.bounded);
        setNotice(
          `Query ${result.status}. ${result.rowCount.toLocaleString()} rows; ${scenePage.items.length} positioned elements loaded.` +
            (rowsRedacted ? " Raw rows are hidden while redaction is enabled." : ""),
        );
      },
    );
  };
  const exportScene = () => {
    const scene = resultState?.result.scene;
    if (busyRef.current !== null || workerFailed) return;
    if (
      !scene ||
      !canExportCurrentScene(
        resultState?.result ?? null,
        resultState?.basis ?? null,
        latestBasisDocument.current.query,
        latestBasisDocument.current.settings,
        snapshotGeneration.current,
      )
    ) {
      setNotice(
        "Export requires a complete, non-partial scene from the current query and settings. Run again before exporting.",
      );
      return;
    }
    void execute("Exporting SVG", { kind: "export", sceneId: scene.sceneId }, (value) => {
      if (value.kind !== "native" || value.value.result.tag !== "export") {
        throw new Error("Expected export confirmation.");
      }
      setNotice(
        `Exported shared-engine SVG (${value.value.result.byteLength} bytes). Recipe annotations and hidden layers are not applied to SVG export.`,
      );
    });
  };
  const restart = () => {
    if (busyRef.current !== null) return;
    void execute("Restarting worker", { kind: "restart" }, (value) => {
      if (value.kind !== "state") throw new Error("Expected worker state.");
      setWorkspace(value.state);
      setWorkerFailed(false);
      setDependencyWarning(null);
      invalidateAnnotationSource("Worker restarted");
      markDirty();
      clearResults();
      setNotice("Worker restarted. Your document is preserved. Open a dump and run explicitly.");
    });
  };
  actions.current = { openDump, openRecipe, saveRecipe, run, cancel, export: exportScene };
  useEffect(
    () =>
      api.onEvent((event) => {
        if (event.kind === "menu") actions.current?.[event.action]();
        else if (event.kind === "progress") {
          if (busyRef.current !== null) setProgress(event.value);
        } else {
          operations.current.invalidate();
          busyRef.current = null;
          setBusy(null);
          setCancellationPending(false);
          setProgress(null);
          setWorkerFailed(true);
          setDependencyWarning(null);
          invalidateAnnotationSource("Worker ownership lost");
          markDirty();
          setWorkspace((current) => ({ ...current, snapshotId: null, objectCount: null }));
          clearResults();
          setError(
            `${event.code}: ${event.message}. Your document is preserved. Restart the worker explicitly to continue.`,
          );
        }
      }),
    [api],
  );

  const editSettings = (next: SettingsDraft) => {
    if (
      JSON.stringify(next) === JSON.stringify(latestBasisDocument.current.settings) ||
      busyRef.current === "Opening recipe"
    )
      return;
    const previousSettings = latestBasisDocument.current.settings;
    latestBasisDocument.current.settings = next;
    setAnnotationScope((scope) =>
      changeAnnotationBasis(
        scope,
        annotationBasis(latestBasisDocument.current.query, next, snapshotGeneration.current),
        "Scene settings changed",
      ),
    );
    if (JSON.stringify(next.redaction) !== JSON.stringify(previousSettings.redaction)) {
      if (busyRef.current !== null || detailsBusy || rowsBusy) cancel();
      clearResults();
      setLookupAddress("");
      setLookupRuntime("0");
      setNotice(
        "Privacy settings changed. The old preview was cleared; run again before exporting.",
      );
    }
    setSettings(next);
    markDirty();
  };
  const select = (id: string) => {
    detailEpoch.current++;
    setDetailsBusy(false);
    setDetails(null);
    setSelectedId(id);
    const source = elements.find((item) => item.id === id)?.source;
    if (source && !settings.redaction.addresses) {
      setLookupRuntime(String(source.runtime));
      setLookupAddress(source.address);
    }
  };
  const loadRows = async (history: (string | null)[]) => {
    if (!resultState || busy || rowsBusy) return;
    const token = ++rowEpoch.current;
    const generation = operations.current.generation;
    setRowsBusy(true);
    try {
      const response = unwrapNative(
        await api.invoke({
          kind: "rows",
          queryId: resultState.result.queryId,
          cursor: history.at(-1) ?? null,
        }),
      );
      if (!operations.current.isCurrent(generation) || token !== rowEpoch.current) return;
      if (
        response.result.tag !== "rows" ||
        response.result.queryId !== resultState.result.queryId
      ) {
        throw new Error("Unexpected row page.");
      }
      if (response.result.items.length > 32) throw new Error("Worker exceeded the row page limit.");
      setRows(response.result.items);
      setRowNext(response.result.nextCursor);
      setRowHistory(history);
    } catch (cause) {
      if (operations.current.isCurrent(generation) && token === rowEpoch.current)
        setError(message(cause));
    } finally {
      if (token === rowEpoch.current) setRowsBusy(false);
    }
  };
  const loadDetails = async (cursor: string | null = null) => {
    if (
      !workspace.snapshotId ||
      Object.values(settings.redaction).some(Boolean) ||
      busy ||
      detailsBusy
    )
      return;
    const token = ++detailEpoch.current;
    const generation = operations.current.generation;
    try {
      const runtime =
        cursor && details
          ? details.runtime
          : boundedInteger(lookupRuntime, 0, 2147483647, "Runtime");
      const address = cursor && details ? details.address : canonicalAddress(lookupAddress);
      setDetailsBusy(true);
      const response = unwrapNative(
        await api.invoke({ kind: "details", runtime, address, cursor }),
      );
      if (!operations.current.isCurrent(generation) || token !== detailEpoch.current) return;
      if (response.result.tag !== "details" || response.result.items.length > 32) {
        throw new Error("Unexpected details page.");
      }
      setDetails({
        items: response.result.items,
        nextCursor: response.result.nextCursor,
        runtime,
        address,
      });
    } catch (cause) {
      if (operations.current.isCurrent(generation) && token === detailEpoch.current)
        setError(message(cause));
    } finally {
      if (token === detailEpoch.current) setDetailsBusy(false);
    }
  };

  let settingsError: string | null = null;
  try {
    readSettings(settings);
  } catch (cause) {
    settingsError = message(cause);
  }
  const selected = elements.find((element) => element.id === selectedId);
  const hovered = elements.find((element) => element.id === hoverId);
  const source = settings.redaction.addresses ? null : selected?.source;
  const hoverSource = settings.redaction.addresses ? null : hovered?.source;
  const result = resultState?.result;
  const scene = result?.scene;
  const editedSinceRun = resultState !== null && resultState.revision !== revision.current;
  const annotation = annotations.find((item) => item.elementId === selectedId)?.text ?? "";
  const canAnnotate =
    resultState !== null && sameAnnotationBasis(annotationScope.basis, resultState.basis);
  const redacted = Object.values(settings.redaction).some(Boolean);
  const active = workspace.snapshotId !== null && !workerFailed;
  const documentLocked = busy === "Opening recipe";
  const exportCurrent = canExportCurrentScene(
    result ?? null,
    resultState?.basis ?? null,
    query,
    settings,
    snapshotGeneration.current,
  );

  return (
    <div className="application" data-testid="workspace-ready">
      <header className="app-header">
        <div className="brand-mark" aria-hidden="true">
          M
        </div>
        <div className="brand">
          <h1>Memory Visualizer</h1>
          <span>LOCAL SNAPSHOT WORKSPACE</span>
        </div>
        <div className="document-title">
          {workspace.recipeName ?? "Untitled recipe"}
          {dirty && (
            <span className="dirty-mark" aria-label="Unsaved changes">
              {" "}
              ●
            </span>
          )}
        </div>
        <div className="header-actions">
          <button
            data-testid="save-recipe"
            onClick={saveRecipe}
            disabled={busy !== null || settingsError !== null}
          >
            Save recipe
          </button>
          <button
            data-testid="export-svg"
            onClick={exportScene}
            disabled={!exportCurrent || busy !== null || workerFailed}
            title={
              exportCurrent
                ? "Export includes all layers; notes are recipe-only."
                : "Run the current query/settings to produce a complete, non-partial scene before exporting."
            }
          >
            Export SVG
          </button>
          <button
            className="primary"
            data-testid="run-query"
            onClick={run}
            disabled={
              !active || settingsError !== null || (busy !== null && busy !== "Running query")
            }
          >
            <span aria-hidden="true">▶</span> {busy === "Running query" ? "Run again" : "Run query"}
          </button>
          <button
            data-testid="cancel-operation"
            onClick={cancel}
            disabled={cancellationPending || (!busy && !rowsBusy && !detailsBusy)}
          >
            Cancel
          </button>
        </div>
      </header>
      <div className="workspace" data-left={leftSize} data-right={rightSize}>
        <aside className="library pane" id="library-pane" aria-labelledby="library-title">
          <header className="pane-heading">
            <span className="eyebrow">WORKSPACE</span>
            <h2 id="library-title">Sources & recipes</h2>
          </header>
          <div className="library-actions">
            <button
              data-testid="open-dump"
              className="primary"
              onClick={openDump}
              disabled={busy !== null || workerFailed}
            >
              Open memory dump…
            </button>
            <button
              data-testid="open-recipe"
              onClick={openRecipe}
              disabled={busy !== null || workerFailed}
            >
              Open recipe…
            </button>
          </div>
          <section
            className="snapshot-card"
            aria-label="Active snapshot"
            data-testid="active-snapshot"
          >
            <div className="card-kicker">
              <span className={active ? "status-dot" : "status-dot inactive"} />
              {active ? "ACTIVE SNAPSHOT" : "NO ACTIVE SNAPSHOT"}
            </div>
            <strong>{workspace.dumpName ?? "A dump is your starting point"}</strong>
            {dependencyWarning && (
              <p className="field-error" data-testid="recipe-dependency-warning">
                {dependencyWarning}
              </p>
            )}
            {active ? (
              <dl className="compact-data">
                <dt>Captured objects</dt>
                <dd>{workspace.objectCount ?? "—"}</dd>
                <dt>Source</dt>
                <dd>{workspace.sourcePartial ? "Partial capture" : "Complete capture"}</dd>
                <dt>Diagnostics</dt>
                <dd>{workspace.diagnosticCount}</dd>
              </dl>
            ) : (
              <p className="hint">
                Native extraction stays in an isolated worker. No heap is loaded into this renderer.
              </p>
            )}
            {active && (
              <button
                className="text-button"
                disabled={busy !== null}
                onClick={() => {
                  void execute("Disposing snapshot", { kind: "dispose" }, (value) => {
                    if (value.kind !== "state") throw new Error("Expected disposed state.");
                    invalidateAnnotationSource("Snapshot closed");
                    setWorkspace(value.state);
                    setDependencyWarning(null);
                    clearResults();
                    markDirty();
                    setNotice("Snapshot closed. Your composition is preserved.");
                  });
                }}
              >
                Close snapshot
              </button>
            )}
          </section>
          <section className="templates" aria-labelledby="templates-title">
            <h3 id="templates-title">Start with a composition</h3>
            <p className="hint">Templates replace the editor only. They never run automatically.</p>
            {TEMPLATES.map((template, index) => (
              <button
                className="template-card"
                key={template.name}
                disabled={documentLocked}
                onClick={() => editQuery(template.query)}
              >
                <span className="template-number">0{index + 1}</span>
                <span>
                  <strong>{template.name}</strong>
                  <small>{template.description}</small>
                </span>
              </button>
            ))}
          </section>
          <div className="library-footer">
            <p className="hint">Offline by default · bounded queries · shared native SVG engine</p>
            <button data-testid="restart-worker" onClick={restart} disabled={busy !== null}>
              Restart worker
            </button>
          </div>
        </aside>
        <Splitter side="left" value={leftSize} onChange={setLeftSize} />
        <main className="workspace-main">
          <QueryEditor
            query={query}
            onChange={editQuery}
            readOnly={documentLocked}
            diagnostics={result?.diagnostics ?? []}
            diagnosticsCurrent={resultState?.query === query}
          />
          <section
            className="result-card"
            aria-labelledby="result-title"
            aria-busy={busy === "Running query"}
          >
            <header className="section-heading result-heading">
              <h2 id="result-title">
                <span className="eyebrow">02</span> Illustration
              </h2>
              <div
                className="tabs"
                role="tablist"
                aria-label="Query output"
                onKeyDown={(event) => {
                  let next: "diagram" | "rows";
                  if (event.key === "Home") next = "diagram";
                  else if (event.key === "End") next = "rows";
                  else if (event.key === "ArrowLeft" || event.key === "ArrowRight")
                    next = tab === "diagram" ? "rows" : "diagram";
                  else return;
                  event.preventDefault();
                  setTab(next);
                  document.getElementById(`${next}-tab`)?.focus();
                }}
              >
                <button
                  role="tab"
                  id="diagram-tab"
                  aria-controls="diagram-panel"
                  aria-selected={tab === "diagram"}
                  tabIndex={tab === "diagram" ? 0 : -1}
                  onClick={() => setTab("diagram")}
                >
                  Diagram
                </button>
                <button
                  role="tab"
                  id="rows-tab"
                  aria-controls="rows-panel"
                  aria-selected={tab === "rows"}
                  tabIndex={tab === "rows" ? 0 : -1}
                  onClick={() => setTab("rows")}
                >
                  Rows {result ? `(${result.rowCount})` : ""}
                </button>
              </div>
            </header>
            {result && (
              <div className="result-summary">
                <span className={`badge ${result.status === "complete" ? "complete" : "warning"}`}>
                  Query {result.status}
                </span>
                {scene && (
                  <span className={`badge ${scene.status === "complete" ? "complete" : "warning"}`}>
                    Scene {scene.status}
                  </span>
                )}
                {(result.sourcePartial || workspace.sourcePartial) && (
                  <span className="badge warning">Partial source</span>
                )}
                {!result.sourceAvailable && (
                  <span className="badge warning">Source unavailable</span>
                )}
                {editedSinceRun && <span className="badge warning">Edited since run</span>}
                <span>
                  {elements.length} / {scene?.elementCount ?? 0} elements · {result.candidates}{" "}
                  candidates
                </span>
              </div>
            )}
            {result && !exportCurrent && (
              <p className="limit-warning" data-testid="export-warning">
                Export requires a complete snapshot and a complete result matching the current query
                and settings. Run again after editing; view-only layers and notes do not affect
                export.
              </p>
            )}
            {result?.truncationReasons.length || scene?.truncationReasons.length || sceneBounded ? (
              <p className="limit-warning">
                Bounded output:{" "}
                {[
                  ...(result?.truncationReasons ?? []),
                  ...(scene?.truncationReasons ?? []),
                  ...(sceneBounded ? ["Renderer page limit"] : []),
                ].join(", ")}
                . Narrow the query or viewport.
              </p>
            ) : null}
            {tab === "diagram" ? (
              <div
                className="output-panel"
                id="diagram-panel"
                role="tabpanel"
                aria-labelledby="diagram-tab"
              >
                {scene ? (
                  <Diagram
                    scene={scene}
                    elements={elements}
                    hiddenLayers={hiddenLayers}
                    selectedId={selectedId}
                    onSelect={select}
                    onHover={setHoverId}
                  />
                ) : (
                  <div className="empty-state">
                    <div className="empty-glyph" aria-hidden="true">
                      <span />
                      <span />
                      <span />
                    </div>
                    <h3>
                      {busy === "Running query"
                        ? "Building your illustration…"
                        : result
                          ? "No scene produced"
                          : "Make memory visible"}
                    </h3>
                    <p>
                      {result
                        ? "Review query diagnostics or try a drawing template."
                        : "Open a memory dump and run the segment + generation composition to begin."}
                    </p>
                    <p className="hint">
                      Up to 1,024 positioned elements. Exact 64-bit source identity.
                    </p>
                  </div>
                )}
              </div>
            ) : (
              <div
                className="rows-panel"
                id="rows-panel"
                role="tabpanel"
                aria-labelledby="rows-tab"
              >
                {redacted ? (
                  <p className="empty-message">Raw rows are hidden while redaction is enabled.</p>
                ) : (
                  <>
                    <div className="table-scroll">
                      <table>
                        <caption className="sr-only">
                          Bounded query result page, at most 32 rows
                        </caption>
                        <thead>
                          <tr>
                            <th>Statement</th>
                            <th>Entity / source</th>
                            <th>Projection</th>
                          </tr>
                        </thead>
                        <tbody>
                          {rows.map((row, index) => (
                            <tr key={index}>
                              <td>{row.statementIndex + 1}</td>
                              <td>
                                <strong>{row.entity.kind}</strong>
                                <br />
                                Runtime {row.entity.runtime} · heap {row.entity.heap}
                                <br />
                                <code>{row.entity.address}</code>
                                <br />
                                {row.entity.size} bytes
                              </td>
                              <td>
                                {row.values.map((value, valueIndex) => (
                                  <div key={valueIndex}>
                                    <span className="muted">{value.name}: </span>
                                    <code>{formatValue(value.value)}</code>
                                  </div>
                                ))}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                      {rows.length === 0 && (
                        <p className="empty-message">
                          No rows on this page. Run a query to load results.
                        </p>
                      )}
                    </div>
                    <div className="pagination">
                      <button
                        disabled={rowHistory.length <= 1 || rowsBusy || busy !== null}
                        onClick={() => void loadRows(rowHistory.slice(0, -1))}
                      >
                        Previous
                      </button>
                      <span>
                        Page {rowHistory.length} · {rows.length} rows · one page retained
                      </span>
                      <button
                        disabled={!rowNext || rowsBusy || busy !== null}
                        onClick={() => void loadRows([...rowHistory, rowNext])}
                      >
                        Next
                      </button>
                    </div>
                  </>
                )}
              </div>
            )}
            <div className="selection-status">
              {hoverSource
                ? `Runtime ${hoverSource.runtime} · heap ${hoverSource.heap} · ${hoverSource.kind} · ${hoverSource.address} · ${hoverSource.size} bytes`
                : hovered
                  ? `${hovered.id} · source association unavailable or redacted`
                  : "Hover or select a scene element to inspect its exact source."}
            </div>
          </section>
        </main>
        <Splitter side="right" value={rightSize} onChange={setRightSize} />
        <aside className="inspector pane" id="inspector-pane" aria-labelledby="inspector-title">
          <header className="pane-heading">
            <span className="eyebrow">INSPECTOR</span>
            <h2 id="inspector-title">Selection & style</h2>
          </header>
          <section className="inspector-section" data-testid="selection-inspector">
            <h3 data-testid="selected-element-id">{selected ? selected.id : "Nothing selected"}</h3>
            {selected ? (
              <>
                <dl className="compact-data">
                  <dt>Layer</dt>
                  <dd>{LAYERS[selected.layer] ?? selected.layer}</dd>
                  <dt>Clipped</dt>
                  <dd>{selected.isClipped ? "Yes" : "No"}</dd>
                  {source ? (
                    <>
                      <dt>Runtime / heap</dt>
                      <dd>
                        {source.runtime} / {source.heap}
                      </dd>
                      <dt>Address</dt>
                      <dd data-testid="selected-source-address">
                        <code>{source.address}</code>
                      </dd>
                      <dt>Size</dt>
                      <dd>{source.size} bytes</dd>
                      <dt>Segment</dt>
                      <dd>
                        <code>{source.segmentAddress ?? "—"}</code>
                      </dd>
                      <dt>Method table</dt>
                      <dd>
                        <code>{source.methodTable ?? "—"}</code>
                      </dd>
                    </>
                  ) : (
                    <>
                      <dt>Source</dt>
                      <dd>Unavailable or redacted</dd>
                    </>
                  )}
                  <dt>Fill / stroke</dt>
                  <dd>
                    <code>
                      {selected.style.fill} / {selected.style.stroke}
                    </code>
                  </dd>
                  <dt>Stroke width</dt>
                  <dd>{selected.style.strokeWidth}</dd>
                </dl>
                <p className="hint">
                  Resolved style comes from the shared engine. Edit Background, Width or Label in
                  MQL and run again.
                </p>
                {selected.text && (
                  <p className="hint">
                    Text replacements: {selected.text.replacedCodeUnits}; truncated:{" "}
                    {selected.text.isTruncated ? "yes" : "no"}.
                  </p>
                )}
                {source && (
                  <button
                    disabled={documentLocked}
                    onClick={() =>
                      editSettings({
                        ...settings,
                        viewportEnabled: true,
                        viewportStart: source.address,
                        viewportSize: source.size,
                      })
                    }
                  >
                    Use source as viewport
                  </button>
                )}
                <label className="field annotation-field">
                  Recipe annotation ({annotation.length}/512)
                  <textarea
                    data-testid="annotation-editor"
                    value={annotation}
                    maxLength={512}
                    rows={3}
                    disabled={
                      documentLocked ||
                      !canAnnotate ||
                      (!annotation && annotations.length + annotationScope.detached.length >= 128)
                    }
                    onChange={(event) => {
                      if (
                        busyRef.current === "Opening recipe" ||
                        !resultState ||
                        !sameAnnotationBasis(annotationScopeRef.current.basis, resultState.basis)
                      )
                        return;
                      const text = event.target.value;
                      setAnnotationScope((scope) => {
                        const next = scope.attached.filter(
                          (item) => item.elementId !== selected.id,
                        );
                        if (text) next.push({ elementId: selected.id, text });
                        return { ...scope, attached: next };
                      });
                      markDirty();
                    }}
                  />
                </label>
                {!canAnnotate && (
                  <p className="field-error">
                    Run the current composition before attaching notes to this selection.
                  </p>
                )}
              </>
            ) : (
              <p className="hint">Click a shape, Tab then Enter, or use [ / ] in the diagram.</p>
            )}
            <p className="hint">
              Annotations are recipe-only notes tied to element IDs, not shared-engine SVG content.
              Query, scene-setting or snapshot changes detach notes instead of attaching them to a
              different object. {annotations.length} attached notes.
            </p>
            {annotations.length > 0 && (
              <details className="saved-notes">
                <summary>Attached annotations</summary>
                {annotations.map((item) => (
                  <div key={item.elementId} className="saved-note">
                    <strong>{item.elementId}</strong>
                    <p>{item.text}</p>
                    <button
                      disabled={documentLocked}
                      onClick={() => {
                        setAnnotationScope((scope) => ({
                          ...scope,
                          attached: scope.attached.filter(
                            (note) => note.elementId !== item.elementId,
                          ),
                        }));
                        markDirty();
                      }}
                    >
                      Remove note for {item.elementId}
                    </button>
                  </div>
                ))}
              </details>
            )}
            {annotationScope.detached.length > 0 && (
              <section
                className="detached-notes"
                aria-labelledby="detached-notes-title"
                data-testid="detached-notes"
              >
                <h3 id="detached-notes-title">
                  Detached notes ({annotationScope.detached.length})
                </h3>
                <p className="field-error">
                  These notes are local-only and are NOT saved to the current recipe. They never
                  attach by ordinal ID automatically. Apply each deliberately to a current
                  selection, or copy its text before removing it or closing the workspace.
                </p>
                {annotationScope.detached.map((item, index) => (
                  <div key={index} className="saved-note">
                    <strong>Detached · formerly {item.elementId}</strong>
                    <p className="hint">{item.reason}</p>
                    <p>{item.text}</p>
                    <button
                      disabled={
                        documentLocked ||
                        !selected ||
                        !canAnnotate ||
                        !!annotation ||
                        annotations.length >= 128
                      }
                      onClick={() => {
                        if (!selected || !resultState || !canAnnotate) return;
                        setAnnotationScope((scope) =>
                          applyDetachedAnnotation(scope, index, selected.id, resultState.basis),
                        );
                        markDirty();
                      }}
                    >
                      Apply to selected element
                    </button>{" "}
                    <button
                      disabled={documentLocked}
                      onClick={() =>
                        setAnnotationScope((scope) => ({
                          ...scope,
                          detached: scope.detached.filter((_, noteIndex) => noteIndex !== index),
                        }))
                      }
                    >
                      Remove detached note
                    </button>
                  </div>
                ))}
              </section>
            )}
          </section>
          <section className="inspector-section" aria-labelledby="layers-title">
            <h3 id="layers-title">Visible layers</h3>
            <div className="layer-list">
              {LAYERS.map((label, index) => (
                <label className="check" key={label}>
                  <input
                    type="checkbox"
                    data-testid={`layer-${index}`}
                    checked={!hiddenLayers.includes(index)}
                    disabled={documentLocked}
                    onChange={(event) => {
                      if (busyRef.current === "Opening recipe") return;
                      const next = event.target.checked
                        ? hiddenLayersRef.current.filter((layer) => layer !== index)
                        : [...hiddenLayersRef.current, index].sort();
                      hiddenLayersRef.current = next;
                      setHiddenLayers(next);
                      markDirty();
                    }}
                  />
                  <span className={`layer-dot layer-${index}`} />
                  {index} · {label}
                </label>
              ))}
            </div>
            <p className="hint">
              Visibility is saved in recipes. Shared-engine SVG exports every scene layer.
            </p>
          </section>
          <Settings
            draft={settings}
            onChange={editSettings}
            error={settingsError}
            disabled={documentLocked}
          />
          <section className="inspector-section" aria-labelledby="details-title">
            <h3 id="details-title">Owned source details</h3>
            <p className="hint">
              Exact runtime + address lookup. One worker-owned page of up to 32 entries, never the
              entire heap.
            </p>
            <form
              onSubmit={(event) => {
                event.preventDefault();
                void loadDetails();
              }}
            >
              <label className="field">
                Runtime
                <input
                  type="number"
                  min={0}
                  step={1}
                  value={lookupRuntime}
                  onChange={(event) => setLookupRuntime(event.target.value)}
                />
              </label>
              <label className="field">
                Source address (uint64)
                <input
                  type="text"
                  spellCheck={false}
                  value={lookupAddress}
                  onChange={(event) => setLookupAddress(event.target.value)}
                />
              </label>
              <button
                type="submit"
                disabled={!active || redacted || busy !== null || detailsBusy || !lookupAddress}
              >
                Load details
              </button>
            </form>
            {redacted && (
              <p className="hint">Raw source lookup is disabled while redaction is enabled.</p>
            )}
            {details && (
              <div className="detail-list" aria-live="polite">
                {details.items.length === 0 && <p>No owned entity at this runtime/address.</p>}
                {details.items.map((item, index) => (
                  <dl className="compact-data detail-entry" key={index}>
                    <dt>Kind</dt>
                    <dd>{item.kind}</dd>
                    <dt>Type</dt>
                    <dd>{item.type ?? "—"}</dd>
                    <dt>Runtime / heap</dt>
                    <dd>
                      {item.runtime} / {item.heap}
                    </dd>
                    <dt>Address</dt>
                    <dd>
                      <code>{item.address}</code>
                    </dd>
                    <dt>Size</dt>
                    <dd>{item.size}</dd>
                    <dt>Method table</dt>
                    <dd>
                      <code>{item.methodTable ?? "—"}</code>
                    </dd>
                    <dt>Generation</dt>
                    <dd>{item.generation ?? "—"}</dd>
                  </dl>
                ))}
                <button
                  disabled={!details.nextCursor || detailsBusy || busy !== null}
                  onClick={() => void loadDetails(details.nextCursor)}
                >
                  Next details page
                </button>
              </div>
            )}
          </section>
        </aside>
      </div>
      <footer className="status-bar">
        <div
          className="status-copy"
          role="status"
          aria-live="polite"
          data-testid="workspace-status"
        >
          <span className={busy ? "status-dot working" : "status-dot"} />
          {busy
            ? cancellationPending
              ? `${busy} · cancellation requested; waiting for the authoritative result…`
              : `${busy}${progress ? ` · ${progress.phase}: ${progress.completed}` : "…"}`
            : notice}
        </div>
        <span className="status-meta">
          {dirty ? "Unsaved changes" : "Saved / unchanged"} · native protocol v3
        </span>
      </footer>
      {error && (
        <div className="error-banner" role="alert" data-testid="workspace-error">
          <strong>Workspace notice</strong>
          <span>{error}</span>
          {workerFailed && (
            <button onClick={restart} disabled={busy !== null}>
              Restart worker
            </button>
          )}
          <button onClick={() => setError(null)} aria-label="Dismiss error">
            Dismiss
          </button>
        </div>
      )}
    </div>
  );
}
