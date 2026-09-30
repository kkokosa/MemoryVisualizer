import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { App } from "./App.js";
import "./styles.css";

const container = document.getElementById("root");
if (!container) throw new Error("Missing renderer root.");
createRoot(container).render(
  <StrictMode>
    {window.workspace ? (
      <App />
    ) : (
      <main className="bridge-error">
        <h1>Memory Visualizer</h1>
        <p>
          The isolated desktop bridge is unavailable. Start the Electron application, not this page
          in a browser.
        </p>
      </main>
    )}
  </StrictMode>,
);
