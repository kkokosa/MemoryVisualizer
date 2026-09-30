import {
  NATIVE_LIMITS,
  type Bounds,
  type SceneElement,
  type SceneInfo,
  type SceneText,
} from "../src/native-types.js";

export const SVG_NAMESPACE = "http://www.w3.org/2000/svg";
const XML_NAMESPACE = "http://www.w3.org/XML/1998/namespace";

export type SceneRenderOptions = {
  hiddenLayers?: readonly number[];
  onSelect?: (id: string) => void;
  onHover?: (id: string | null) => void;
  onFocus?: (id: string) => void;
};

function color(value: string): string {
  return /^(?:#[0-9a-f]{6}|none)$/i.test(value) ? value : "#202020";
}

function number(value: number): string {
  if (!Number.isFinite(value)) throw new Error("Scene coordinates must be finite.");
  return String(value);
}

/** Only positioned worker geometry is rendered; no addresses are converted to scene coordinates. */
export function createSceneSvg(
  document: Document,
  scene: SceneInfo,
  elements: readonly SceneElement[],
  options: SceneRenderOptions = {},
): SVGSVGElement {
  const create = <K extends keyof SVGElementTagNameMap>(
    name: K,
    attributes: Record<string, string> = {},
  ): SVGElementTagNameMap[K] => {
    const element = document.createElementNS(SVG_NAMESPACE, name);
    for (const [key, value] of Object.entries(attributes)) element.setAttribute(key, value);
    return element;
  };
  const boundsAttributes = (bounds: Bounds) => ({
    x: number(bounds.x),
    y: number(bounds.y),
    width: number(bounds.width),
    height: number(bounds.height),
  });
  const appendText = (parent: SVGElement, text: SceneText, id: string) => {
    const clipId = `${id}-text-clip`;
    const defs = create("defs");
    const clip = create("clipPath", { id: clipId, clipPathUnits: "userSpaceOnUse" });
    clip.appendChild(create("rect", boundsAttributes(text.bounds)));
    defs.appendChild(clip);
    parent.appendChild(defs);
    const textGroup = create("g", {
      "clip-path": `url(#${clipId})`,
      "data-text-replacements": String(text.replacedCodeUnits),
      "data-text-truncated": String(text.isTruncated),
    });
    for (const line of text.lines) {
      if (line.text.length === 0) continue;
      const textElement = create("text", {
        x: number(line.x),
        y: number(line.baseline),
        "font-family": "monospace",
        "font-size": number(text.fontSize),
        "font-variant-ligatures": "none",
        fill: color(text.fill),
        textLength: number(line.width),
        lengthAdjust: "spacingAndGlyphs",
      });
      textElement.setAttributeNS(XML_NAMESPACE, "xml:space", "preserve");
      textElement.textContent = line.text;
      textGroup.appendChild(textElement);
    }
    parent.appendChild(textGroup);
  };
  const svg = create("svg", {
    version: "1.1",
    width: number(scene.bounds.width),
    height: number(scene.bounds.height),
    viewBox: [scene.bounds.x, scene.bounds.y, scene.bounds.width, scene.bounds.height]
      .map(number)
      .join(" "),
    role: "group",
    "aria-label": "Positioned memory scene. Tab to elements; Enter to select.",
    "data-scene-version": String(scene.schemaVersion),
    "data-scene-status": scene.status,
    "data-scene-truncation": scene.truncationReasons.join(","),
    "data-address-layout": scene.redaction.addresses
      ? "schematic"
      : scene.layout === "compact"
        ? "compact"
        : "relative",
    "data-redact-addresses": String(scene.redaction.addresses),
    "data-redact-strings": String(scene.redaction.strings),
    "data-redact-paths": String(scene.redaction.paths),
    "data-redact-labels": String(scene.redaction.labels),
  });
  svg.appendChild(
    create("rect", { ...boundsAttributes(scene.bounds), fill: color(scene.theme.background) }),
  );
  if (scene.layout === "compact" && !scene.redaction.addresses && scene.gaps) {
    const gaps = scene.gaps;
    for (const [index, offset] of gaps.offsets
      .slice(0, NATIVE_LIMITS.maxSceneItems + 1)
      .entries()) {
      const group = create("g", {
        id: `address-gap-${index}`,
        "data-kind": "address-gap",
        transform: `translate(${number(offset)},0)`,
        "pointer-events": "none",
        role: "img",
        "aria-label": "Compressed address gap",
      });
      group.appendChild(
        create("rect", { ...boundsAttributes(gaps.band), fill: color(gaps.style.fill) }),
      );
      for (const line of gaps.lines.slice(0, 2)) {
        group.appendChild(
          create("line", {
            x1: number(line.start.x),
            y1: number(line.start.y),
            x2: number(line.finish.x),
            y2: number(line.finish.y),
            fill: "none",
            stroke: color(gaps.style.stroke),
            "stroke-width": number(gaps.style.strokeWidth),
          }),
        );
      }
      svg.appendChild(group);
    }
    const description = create("desc", { id: "address-layout-description" });
    description.textContent = gaps.legend.lines.map((line) => line.text).join(" ");
    svg.appendChild(description);
    svg.setAttribute("aria-describedby", "address-layout-description");
  }
  for (const [index, lane] of scene.lanes.entries()) {
    const group = create("g", {
      id: /^lane-\d+$/.test(lane.id) ? lane.id : `lane-${index}`,
      "data-kind": "lane",
    });
    if (!scene.redaction.addresses) {
      if (lane.runtime !== null) group.setAttribute("data-runtime", String(lane.runtime));
      if (lane.heap !== null) group.setAttribute("data-heap", String(lane.heap));
    }
    svg.appendChild(group);
  }
  const hidden = new Set(options.hiddenLayers ?? []);
  for (const [index, element] of elements.slice(0, 1024).entries()) {
    if (hidden.has(element.layer)) continue;
    const id = /^element-\d+$/.test(element.id) ? element.id : `element-${index}`;
    const source = scene.redaction.addresses ? null : element.source;
    const group = create("g", {
      id,
      class: "scene-element",
      "data-element-id": element.id,
      "data-lane": element.laneId,
      "data-layer": String(element.layer),
      "data-clipped": String(element.isClipped),
      tabindex: "0",
      role: "button",
      "aria-label": source
        ? `${source.kind}, runtime ${source.runtime}, heap ${source.heap}, address ${source.address}, size ${source.size}`
        : `Scene element ${id}, layer ${element.layer}`,
      "aria-pressed": "false",
    });
    if (source) {
      group.setAttribute("data-runtime", String(source.runtime));
      group.setAttribute("data-heap", String(source.heap));
      group.setAttribute("data-statement", String(source.statementIndex));
      group.setAttribute("data-kind", source.kind);
      group.setAttribute("data-address", source.address);
      group.setAttribute("data-size", source.size);
      if (source.segmentAddress !== null)
        group.setAttribute("data-segment-address", source.segmentAddress);
      if (source.methodTable !== null) group.setAttribute("data-method-table", source.methodTable);
    }
    const geometry = element.geometry;
    const shape =
      geometry.kind === "rectangle"
        ? create("rect", boundsAttributes(geometry.bounds))
        : create("line", {
            x1: number(geometry.start.x),
            y1: number(geometry.start.y),
            x2: number(geometry.finish.x),
            y2: number(geometry.finish.y),
          });
    shape.setAttribute("class", "scene-shape");
    shape.setAttribute("fill", color(element.style.fill));
    shape.setAttribute("stroke", color(element.style.stroke));
    shape.setAttribute("stroke-width", number(element.style.strokeWidth));
    group.appendChild(shape);
    if (element.text) appendText(group, element.text, id);
    group.addEventListener("click", () => options.onSelect?.(element.id));
    group.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        event.stopPropagation();
        options.onSelect?.(element.id);
      }
    });
    group.addEventListener("pointerenter", () => options.onHover?.(element.id));
    group.addEventListener("pointerleave", () => options.onHover?.(null));
    group.addEventListener("focus", () => {
      options.onHover?.(element.id);
      options.onFocus?.(element.id);
    });
    group.addEventListener("blur", () => options.onHover?.(null));
    svg.appendChild(group);
  }
  return svg;
}
