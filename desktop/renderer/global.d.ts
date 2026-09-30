import type { WorkspaceAPI } from "../src/native-types.js";

declare global {
  interface Window {
    workspace: WorkspaceAPI;
  }
}
