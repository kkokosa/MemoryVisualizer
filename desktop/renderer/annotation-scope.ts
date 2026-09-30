import type { DesktopValue, NativeResult, WorkspaceDocument } from "../src/native-types.js";
import { readSettings, type SettingsDraft } from "./model.js";

export type Annotation = WorkspaceDocument["annotations"][number];
export type AnnotationBasis = { query: string; settings: string; snapshotGeneration: number };
export type AnnotationScope = {
  basis: AnnotationBasis;
  attached: Annotation[];
  detached: (Annotation & { reason: string })[];
};

export function isDirtyAfterSave(
  savedRevision: number,
  latestRevision: number,
  detachedNoteCount: number,
): boolean {
  return savedRevision !== latestRevision || detachedNoteCount > 0;
}

export function annotationBasis(
  query: string,
  settings: SettingsDraft,
  snapshotGeneration: number,
): AnnotationBasis {
  let normalized: unknown = settings;
  try {
    normalized = readSettings(settings);
  } catch {
    // Invalid drafts are a distinct basis too; reverting must not reattach notes implicitly.
  }
  return { query, settings: JSON.stringify(normalized), snapshotGeneration };
}

export function sameAnnotationBasis(left: AnnotationBasis, right: AnnotationBasis): boolean {
  return (
    left.query === right.query &&
    left.settings === right.settings &&
    left.snapshotGeneration === right.snapshotGeneration
  );
}

export function canExportCurrentScene(
  result: Extract<NativeResult, { tag: "query" }> | null,
  runBasis: AnnotationBasis | null,
  query: string,
  settings: SettingsDraft,
  snapshotGeneration: number,
): boolean {
  if (
    !result ||
    !runBasis ||
    result.status !== "complete" ||
    !result.sourceAvailable ||
    result.sourcePartial ||
    result.scene?.status !== "complete"
  )
    return false;
  try {
    const parsed = readSettings(settings);
    if (result.scene.layout !== parsed.layout) return false;
    for (const flag of ["addresses", "strings", "paths", "labels"] as const) {
      if (result.scene.redaction[flag] !== parsed.redaction[flag]) return false;
    }
  } catch {
    return false;
  }
  return sameAnnotationBasis(runBasis, annotationBasis(query, settings, snapshotGeneration));
}

export function changeAnnotationBasis(
  scope: AnnotationScope,
  basis: AnnotationBasis,
  reason: string,
): AnnotationScope {
  if (sameAnnotationBasis(scope.basis, basis)) return scope;
  return {
    basis,
    attached: [],
    detached: [...scope.detached, ...scope.attached.map((note) => ({ ...note, reason }))],
  };
}

export function restoreRecipeAnnotations(
  scope: AnnotationScope,
  basis: AnnotationBasis,
  annotations: readonly Annotation[],
  dependency: Extract<DesktopValue, { kind: "recipe" }>["dependency"] = "ready",
): AnnotationScope {
  if (dependency === "mismatch") {
    return {
      basis,
      attached: [],
      detached: [
        ...scope.detached,
        ...annotations.map((note) => ({
          ...note,
          reason: "Recipe snapshot fingerprint mismatch",
        })),
      ],
    };
  }
  return { basis, attached: annotations.map((note) => ({ ...note })), detached: scope.detached };
}

export function applyDetachedAnnotation(
  scope: AnnotationScope,
  index: number,
  elementId: string,
  sceneBasis: AnnotationBasis,
): AnnotationScope {
  const note = scope.detached[index];
  if (
    !note ||
    !sameAnnotationBasis(scope.basis, sceneBasis) ||
    !/^element-\d+$/.test(elementId) ||
    scope.attached.some((item) => item.elementId === elementId)
  )
    return scope;
  return {
    ...scope,
    attached: [...scope.attached, { elementId, text: note.text }],
    detached: scope.detached.filter((_, noteIndex) => noteIndex !== index),
  };
}
