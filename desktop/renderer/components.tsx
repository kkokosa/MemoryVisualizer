import { useEffect, useRef, useState, type KeyboardEvent, type PointerEvent } from "react";
import type { Bounds, Diagnostic, SceneElement, SceneInfo } from "../src/native-types.js";
import {
  diagnosticRange,
  fittedBounds,
  highlightQuery,
  textareaDiagnosticRange,
  zoomBounds,
  type SettingsDraft,
} from "./model.js";
import { createSceneSvg } from "./scene-renderer.js";

export function Splitter({
  side,
  value,
  onChange,
}: {
  side: "left" | "right";
  value: number;
  onChange: (value: number) => void;
}) {
  const drag = useRef<{ x: number; value: number } | null>(null);
  const change = (next: number) => onChange(Math.max(0, Math.min(8, next)));
  return (
    <div
      className="splitter"
      role="separator"
      tabIndex={0}
      aria-label={side === "left" ? "Resize snapshot pane" : "Resize inspector pane"}
      aria-orientation="vertical"
      aria-valuemin={side === "left" ? 200 : 240}
      aria-valuemax={side === "left" ? 360 : 400}
      aria-valuenow={(side === "left" ? 200 : 240) + value * 20}
      aria-controls={side === "left" ? "library-pane" : "inspector-pane"}
      onPointerDown={(event) => {
        if (event.button !== 0) return;
        event.currentTarget.setPointerCapture(event.pointerId);
        drag.current = { x: event.clientX, value };
      }}
      onPointerMove={(event) => {
        if (!drag.current) return;
        const delta =
          Math.round((event.clientX - drag.current.x) / 20) * (side === "left" ? 1 : -1);
        change(drag.current.value + delta);
      }}
      onPointerUp={() => {
        drag.current = null;
      }}
      onPointerCancel={() => {
        drag.current = null;
      }}
      onKeyDown={(event) => {
        if (event.key === "Home") change(0);
        else if (event.key === "End") change(8);
        else if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
          change(value + (event.key === "ArrowRight" ? 1 : -1) * (side === "left" ? 1 : -1));
        } else return;
        event.preventDefault();
      }}
    />
  );
}

export function QueryEditor({
  query,
  onChange,
  diagnostics,
  diagnosticsCurrent,
  readOnly = false,
}: {
  query: string;
  onChange: (value: string) => void;
  diagnostics: Diagnostic[];
  diagnosticsCurrent: boolean;
  readOnly?: boolean;
}) {
  const textarea = useRef<HTMLTextAreaElement>(null);
  const highlights = useRef<HTMLPreElement>(null);
  const focusDiagnostic = (diagnostic: Diagnostic) => {
    const editor = textarea.current;
    if (!editor) return;
    const [rawStart] = diagnosticRange(query, diagnostic);
    const [start, end] = textareaDiagnosticRange(query, diagnostic);
    editor.focus();
    editor.setSelectionRange(start, end);
    const line = query.slice(0, rawStart).split(/\r\n|\r|\n/).length - 1;
    editor.scrollTop = Math.max(0, line * 22 - 44);
    if (highlights.current) highlights.current.scrollTop = editor.scrollTop;
  };
  return (
    <section className="editor-card" aria-labelledby="editor-title">
      <header className="section-heading">
        <h2 id="editor-title">
          <span className="eyebrow">01</span> Query composition
        </h2>
        <span className="muted">{query.length.toLocaleString()} / 16,384 UTF-16 units</span>
      </header>
      <label className="sr-only" htmlFor="query-editor">
        MQL query editor
      </label>
      <div className="editor-surface">
        <pre className="query-highlight" ref={highlights} aria-hidden="true">
          {highlightQuery(query).map((token, index) => (
            <span key={index} className={`token-${token.kind}`}>
              {token.text}
            </span>
          ))}
          {"\n"}
        </pre>
        <textarea
          id="query-editor"
          data-testid="query-editor"
          ref={textarea}
          value={query}
          readOnly={readOnly}
          maxLength={16384}
          wrap="off"
          spellCheck={false}
          autoCapitalize="off"
          autoComplete="off"
          aria-describedby="query-help"
          onChange={(event) => onChange(event.target.value)}
          onScroll={(event) => {
            if (!highlights.current) return;
            highlights.current.scrollTop = event.currentTarget.scrollTop;
            highlights.current.scrollLeft = event.currentTarget.scrollLeft;
          }}
        />
      </div>
      <p id="query-help" className="hint editor-help">
        {readOnly
          ? "Document editing is paused while the recipe is opening."
          : "MQL is data, not executable code. Compose MATCH / RETURN / AS BOX or PIN; use semicolons between statements."}
      </p>
      {diagnostics.length > 0 && (
        <div className="diagnostics" aria-label="Query diagnostics">
          {!diagnosticsCurrent && (
            <p>Editor changed; run again for current diagnostic locations.</p>
          )}
          {diagnostics.map((diagnostic, index) => (
            <button
              key={index}
              className="diagnostic"
              disabled={!diagnosticsCurrent}
              onClick={() => focusDiagnostic(diagnostic)}
            >
              <strong>{diagnostic.code}</strong> {diagnostic.message}
              <span>
                Line {diagnostic.span.line}, column {diagnostic.span.column} · select source
              </span>
            </button>
          ))}
        </div>
      )}
    </section>
  );
}

export function Diagram({
  scene,
  elements,
  hiddenLayers,
  selectedId,
  onSelect,
  onHover,
}: {
  scene: SceneInfo;
  elements: SceneElement[];
  hiddenLayers: number[];
  selectedId: string | null;
  onSelect: (id: string) => void;
  onHover: (id: string | null) => void;
}) {
  const host = useRef<HTMLDivElement>(null);
  const svg = useRef<SVGSVGElement | null>(null);
  const callbacks = useRef({ onSelect, onHover });
  callbacks.current = { onSelect, onHover };
  const initial = fittedBounds(scene.bounds);
  const [view, setView] = useState<Bounds>(initial);
  const drag = useRef<{ x: number; y: number; view: Bounds; moved: boolean } | null>(null);
  const suppressClick = useRef(false);
  useEffect(() => {
    const element = createSceneSvg(document, scene, elements, {
      hiddenLayers,
      onSelect: (id) => {
        if (!suppressClick.current) callbacks.current.onSelect(id);
      },
      onHover: (id) => callbacks.current.onHover(id),
    });
    svg.current = element;
    host.current?.replaceChildren(element);
    return () => {
      element.remove();
      svg.current = null;
    };
  }, [scene, elements, hiddenLayers]);
  useEffect(() => {
    setView(fittedBounds(scene.bounds));
  }, [scene]);
  useEffect(() => {
    const viewport = host.current?.parentElement;
    if (!viewport) return;
    const wheel = (event: WheelEvent) => {
      event.preventDefault();
      setView((current) =>
        zoomBounds(current, event.deltaY > 0 ? 1.12 : 1 / 1.12, fittedBounds(scene.bounds)),
      );
    };
    viewport.addEventListener("wheel", wheel, { passive: false });
    return () => viewport.removeEventListener("wheel", wheel);
  }, [scene]);
  useEffect(() => {
    svg.current?.setAttribute("viewBox", `${view.x} ${view.y} ${view.width} ${view.height}`);
  }, [view, scene, elements, hiddenLayers]);
  useEffect(() => {
    for (const group of svg.current?.querySelectorAll("[data-element-id]") ?? []) {
      const selected = group.getAttribute("data-element-id") === selectedId;
      group.setAttribute("data-selected", String(selected));
      group.setAttribute("aria-pressed", String(selected));
    }
  }, [selectedId, scene, elements, hiddenLayers]);
  const zoom = (factor: number) => setView((current) => zoomBounds(current, factor, initial));
  const keyboard = (event: KeyboardEvent<HTMLDivElement>) => {
    const delta = event.shiftKey ? 0.25 : 0.08;
    if (event.key === "+" || event.key === "=") zoom(0.8);
    else if (event.key === "-") zoom(1.25);
    else if (event.key === "0" || event.key.toLowerCase() === "f") setView(initial);
    else if (event.key === "ArrowLeft") setView((v) => ({ ...v, x: v.x - v.width * delta }));
    else if (event.key === "ArrowRight") setView((v) => ({ ...v, x: v.x + v.width * delta }));
    else if (event.key === "ArrowUp") setView((v) => ({ ...v, y: v.y - v.height * delta }));
    else if (event.key === "ArrowDown") setView((v) => ({ ...v, y: v.y + v.height * delta }));
    else if (event.key === "[" || event.key === "]") {
      const visible = elements.filter((element) => !hiddenLayers.includes(element.layer));
      const index = visible.findIndex((element) => element.id === selectedId);
      const next =
        visible[(index + (event.key === "]" ? 1 : -1) + visible.length) % visible.length];
      if (next) onSelect(next.id);
    } else return;
    event.preventDefault();
  };
  const pointerDown = (event: PointerEvent<HTMLDivElement>) => {
    if (event.button !== 0) return;
    suppressClick.current = false;
    drag.current = { x: event.clientX, y: event.clientY, view, moved: false };
  };
  return (
    <div className="diagram">
      <div className="diagram-tools" role="group" aria-label="Diagram navigation">
        <button onClick={() => zoom(0.8)} aria-label="Zoom in">
          +
        </button>
        <button onClick={() => zoom(1.25)} aria-label="Zoom out">
          −
        </button>
        <button onClick={() => setView(initial)}>Fit scene</button>
        <span className="muted">{Math.round((initial.width / view.width) * 100)}%</span>
        <span className="hint">Drag to pan · + / − zoom · F fit · [ / ] select</span>
      </div>
      <p className="diagram-scale" data-testid="address-layout-note">
        {scene.redaction.addresses
          ? "Schematic view: address positions and byte sizes are hidden."
          : scene.layout === "compact"
            ? scene.gaps
              ? `Compact overview: ${scene.gaps.offsets.length} empty address gaps compressed. // marks breaks in the address scale.`
              : "Compact overview: no empty gaps between the selected ranges."
            : "Linear address scale: empty address gaps keep their full size."}
      </p>
      <div
        className="diagram-viewport"
        data-testid="memory-diagram"
        tabIndex={0}
        role="region"
        aria-label="Memory diagram"
        aria-describedby="diagram-keyboard-help"
        onKeyDown={keyboard}
        onPointerDown={pointerDown}
        onPointerMove={(event) => {
          const start = drag.current;
          if (!start) return;
          const dx = event.clientX - start.x;
          const dy = event.clientY - start.y;
          if (Math.abs(dx) + Math.abs(dy) < 4 && !start.moved) return;
          start.moved = true;
          suppressClick.current = true;
          event.currentTarget.setPointerCapture(event.pointerId);
          const rect = svg.current?.getBoundingClientRect();
          if (!rect || rect.width === 0 || rect.height === 0) return;
          const scale = Math.min(rect.width / start.view.width, rect.height / start.view.height);
          setView({ ...start.view, x: start.view.x - dx / scale, y: start.view.y - dy / scale });
        }}
        onPointerUp={() => {
          drag.current = null;
          setTimeout(() => {
            suppressClick.current = false;
          }, 0);
        }}
        onPointerLeave={(event) => {
          if (!event.currentTarget.hasPointerCapture(event.pointerId)) drag.current = null;
        }}
        onLostPointerCapture={() => {
          drag.current = null;
        }}
        onPointerCancel={() => {
          drag.current = null;
          suppressClick.current = false;
        }}
      >
        <div className="svg-host" ref={host} />
        {elements.length === 0 && (
          <p className="diagram-empty">This scene contains no matching drawing elements.</p>
        )}
      </div>
      <p id="diagram-keyboard-help" className="sr-only">
        Arrow keys pan. Plus and minus zoom. F or zero fits the scene. Brackets select previous or
        next visible element. Tab focuses elements; Enter or Space selects one.
      </p>
    </div>
  );
}

export function Settings({
  draft,
  onChange,
  error,
  disabled = false,
}: {
  draft: SettingsDraft;
  onChange: (draft: SettingsDraft) => void;
  error: string | null;
  disabled?: boolean;
}) {
  const field = (
    key: "plotWidth" | "maxResults" | "maxElements",
    title: string,
    min: number,
    max: number,
  ) => (
    <label className="field">
      {title}
      <input
        type="number"
        data-testid={`setting-${key}`}
        min={min}
        max={max}
        step={1}
        value={draft[key]}
        onChange={(event) => onChange({ ...draft, [key]: event.target.value })}
      />
    </label>
  );
  return (
    <fieldset
      className="inspector-section settings-section"
      aria-labelledby="settings-title"
      disabled={disabled}
    >
      <h3 id="settings-title">Scene settings</h3>
      <label className="field">
        Address layout
        <select
          data-testid="setting-layout"
          value={draft.layout}
          aria-describedby="layout-help"
          onChange={(event) => {
            const layout = event.target.value;
            if (layout === "compact" || layout === "linear") onChange({ ...draft, layout });
          }}
        >
          <option value="compact">Compact overview (compress empty gaps)</option>
          <option value="linear">Linear (true address spacing)</option>
        </select>
      </label>
      <p className="hint" id="layout-help">
        Compact preserves byte proportions within occupied ranges and marks omitted gaps. Linear
        preserves address distances. Run again after changing layout.
      </p>
      {field("plotWidth", "Plot width (scene units)", 64, 4096)}
      <div className="field-pair">
        {field("maxResults", "Max rows", 1, 4096)}
        {field("maxElements", "Max elements", 1, 1024)}
      </div>
      <label className="check">
        <input
          type="checkbox"
          data-testid="viewport-enabled"
          checked={draft.viewportEnabled}
          onChange={(event) => onChange({ ...draft, viewportEnabled: event.target.checked })}
        />
        Explicit address viewport
      </label>
      <fieldset disabled={!draft.viewportEnabled} className="plain-fieldset">
        <legend className="sr-only">Address viewport</legend>
        <label className="field">
          Start address (uint64)
          <input
            type="text"
            data-testid="viewport-start"
            value={draft.viewportStart}
            spellCheck={false}
            onChange={(event) => onChange({ ...draft, viewportStart: event.target.value })}
          />
        </label>
        <label className="field">
          Size in bytes (uint64)
          <input
            type="text"
            data-testid="viewport-size"
            value={draft.viewportSize}
            spellCheck={false}
            onChange={(event) => onChange({ ...draft, viewportSize: event.target.value })}
          />
        </label>
      </fieldset>
      <p className="hint">Decimal or 0x hexadecimal. Addresses retain all 64 bits.</p>
      <fieldset className="plain-fieldset redaction">
        <legend>Worker-side redaction</legend>
        {(["addresses", "strings", "paths", "labels"] as const).map((key) => (
          <label key={key} className="check">
            <input
              type="checkbox"
              data-testid={`redact-${key}`}
              checked={draft.redaction[key]}
              onChange={(event) =>
                onChange({
                  ...draft,
                  redaction: { ...draft.redaction, [key]: event.target.checked },
                })
              }
            />
            {key[0]!.toUpperCase() + key.slice(1)}
          </label>
        ))}
      </fieldset>
      <p className="hint">
        Changing redaction clears the preview. Run to rebuild it in the worker. Address redaction
        removes source associations.
      </p>
      {error && (
        <p className="field-error" role="alert">
          {error}
        </p>
      )}
    </fieldset>
  );
}
