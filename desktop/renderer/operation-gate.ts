import type { DesktopCommand } from "../src/native-types.js";

const authoritativeCommands = new Set<DesktopCommand["kind"]>([
  "openDump",
  "openRecipe",
  "saveRecipe",
  "dispose",
  "restart",
  "export",
]);

export class DesktopOperationGate {
  private currentGeneration = 0;
  private active: {
    generation: number;
    kind: DesktopCommand["kind"];
    submitted: boolean;
    cancellationRequested: boolean;
  } | null = null;

  get generation(): number {
    return this.currentGeneration;
  }

  get cancellationRequested(): boolean {
    return this.active?.cancellationRequested ?? false;
  }

  begin(kind: DesktopCommand["kind"]): number {
    this.currentGeneration++;
    this.active = {
      generation: this.currentGeneration,
      kind,
      submitted: false,
      cancellationRequested: false,
    };
    return this.currentGeneration;
  }

  submit(generation: number): boolean {
    if (!this.isCurrent(generation) || !this.active || this.active.cancellationRequested)
      return false;
    this.active.submitted = true;
    return true;
  }

  isCurrent(generation: number): boolean {
    return this.currentGeneration === generation;
  }

  cancel(): "await-terminal" | "invalidated" {
    if (this.active && authoritativeCommands.has(this.active.kind)) {
      // Cancellation is a request, not proof that a native commit did not happen.
      this.active.cancellationRequested = true;
      return "await-terminal";
    }
    this.invalidate();
    return "invalidated";
  }

  finish(generation: number): void {
    if (this.isCurrent(generation)) this.active = null;
  }

  invalidate(): void {
    this.currentGeneration++;
    this.active = null;
  }
}
