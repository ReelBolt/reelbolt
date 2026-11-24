import React from "react";
import ReactDOM from "react-dom/client";
import { MantineProvider } from "@mantine/core";
import "@fontsource-variable/geist";
import "@fontsource-variable/geist-mono";
import "@mantine/core/styles.css";
import "./styles/app.css";
import { App } from "./App";
import { theme } from "./theme";
import { demoBridge, demoEnabled, httpBridge, type Bridge } from "./shell/bridge";
import { demoFolders, demoSetupView, demoView } from "./shell/demo";

// The window's entry point. Which bridge it uses is decided here and nowhere
// else: the HTTP bridge against the running shell, or - in a Vite development
// server with ?demo=1, and only there - the labelled preview document.
const scenario = new URLSearchParams(window.location.search).get("scenario");
const bridge: Bridge = demoEnabled()
  ? demoBridge(scenario === "setup" ? demoSetupView : demoView, scenario === "setup" ? { ...demoFolders, links: [] } : demoFolders)
  : httpBridge;

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <MantineProvider theme={theme} defaultColorScheme="auto">
      <App bridge={bridge} preview={demoEnabled()} />
    </MantineProvider>
  </React.StrictMode>,
);
