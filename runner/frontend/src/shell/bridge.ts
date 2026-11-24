import { EMPTY_LOCAL_MODEL } from "./localmodel";
import type { LocalModelPatch, LocalModelResult, LocalModelDoc } from "./localmodel";
import type {
  ActionResult, FoldersView, MenuItem, PairingView, SettingsPatch, TelemetryPreview, View,
} from "./types";

// The window's only way out. Every call goes to the Go shell over the Wails
// host's INTERNAL asset server (application.ServiceOptions.Route), which is not
// a listening socket: the request never leaves the webview's own request path.
//
// There is deliberately no fallback that invents a view. A window that cannot
// reach the shell says so, because a window that shows plausible-looking state
// it did not get from the runner is the exact failure this shell exists to
// prevent. The demo mode below is opt-in, labelled on screen, and refused
// outside a development build.

export class ShellUnavailable extends Error {
  constructor(cause?: unknown) {
    super("The ReelBolt shell is not running this window.");
    this.name = "ShellUnavailable";
    this.cause = cause;
  }
}

export interface Bridge {
  view(): Promise<View>;
  menu(): Promise<MenuItem[]>;
  do(action: string, arg?: string): Promise<ActionResult>;
  // folders is GET /api/folders: the Folders screen's own document. It is
  // fetched separately because it is bigger than the view and changes on its
  // own schedule - one file finishing must not redraw the whole window.
  folders(): Promise<FoldersView>;
  pairing(): Promise<PairingView>;
  cancelPairing(): Promise<PairingView>;
  // telemetryPreview is what would be sent, from this machine. It is fetched on
  // demand rather than shipped in every view: a document that exists to be read
  // should only be built when someone asks to read it.
  telemetryPreview(): Promise<TelemetryPreview>;
  // saveSettings is POST /api/settings: a partial change the shell validates
  // as a whole before it writes anything.
  saveSettings(patch: SettingsPatch): Promise<SaveResult>;
  // The Local model screen: read, look for servers, save, re-check.
  localModel(): Promise<LocalModelDoc>;
  localModelDetect(): Promise<LocalModelResult>;
  localModelSave(patch: LocalModelPatch): Promise<LocalModelResult>;
  localModelCheck(): Promise<LocalModelResult>;
}

export interface SaveResult {
  ok: boolean;
  message?: string;
  view?: View;
}

async function call<T>(path: string, init?: RequestInit): Promise<T> {
  let res: Response;
  try {
    res = await fetch(path, {
      ...init,
      headers: { "Content-Type": "application/json", ...(init?.headers ?? {}) },
    });
  } catch (err) {
    throw new ShellUnavailable(err);
  }
  if (!res.ok && res.status !== 200) {
    throw new Error("the shell answered " + res.status + " for " + path);
  }
  return (await res.json()) as T;
}

export const httpBridge: Bridge = {
  view: () => call<View>("/api/view"),
  menu: () => call<MenuItem[]>("/api/menu"),
  do: (action, arg) =>
    call<ActionResult>("/api/do", { method: "POST", body: JSON.stringify({ action, arg }) }),
  folders: () => call<FoldersView>("/api/folders"),
  pairing: () => call<PairingView>("/api/pairing"),
  cancelPairing: () => call<PairingView>("/api/pairing", { method: "DELETE" }),
  telemetryPreview: () => call<TelemetryPreview>("/api/telemetry/preview"),
  saveSettings: (patch) =>
    call<SaveResult>("/api/settings", { method: "POST", body: JSON.stringify(patch) }),
  localModel: () => call<LocalModelDoc>("/api/local-model"),
  localModelDetect: () => call<LocalModelResult>("/api/local-model/detect", { method: "POST" }),
  localModelSave: (patch) =>
    call<LocalModelResult>("/api/local-model", { method: "POST", body: JSON.stringify(patch) }),
  localModelCheck: () => call<LocalModelResult>("/api/local-model/check", { method: "POST" }),
};

// demoBridge renders the screens from a fixed document.
//
// It exists so the window can be designed and reviewed without a desktop, and
// it is reachable ONLY when the page is loaded with ?demo=1 in a Vite dev
// server. Every screen it feeds shows a "preview data" banner, because the one
// thing a runner UI must never do is look like it is reporting a real machine
// when it is not.
export function demoBridge(view: View, folders?: FoldersView): Bridge {
  return {
    view: async () => view,
    menu: async () => [],
    do: async () => ({ ok: true, message: "Preview only: no action was performed.", view }),
    // The demo's folders document, when one was supplied. It is the same
    // "labelled preview" rule as the view: nothing here claims to be real.
    folders: async () => folders ?? {
      ready: true, running: true, summary: "Folder sync: up to date",
      links: [], projects: [], pickerAvailable: true,
    },
    pairing: async () => (view.device.paired
      ? { active: false }
      : { active: true, code: "7K4M-2QPX", url: "https://reelbolt.ai/app/runners/pair", message: "Enter this code in your browser. It expires in 15 minutes." }),
    cancelPairing: async () => ({ active: false }),
    // The demo bridge must not pretend a real machine produced this: the
    // placeholder text says so, and the preview banner is already on screen.
    telemetryPreview: async () => ({
      schema: "reelbolt.telemetry/1",
      consentVersion: "preview",
      disclosure: view.settings.telemetry.disclosure,
      enabled: view.settings.telemetry.enabled,
      available: view.settings.telemetry.available,
      crash: { kind: "crash", panicType: "<the kind of error>", frames: [] },
      jobFailure: { kind: "job_failure", jobType: "<the kind of job>" },
      fields: [{ name: "kind", note: "crash or job_failure" }],
      neverSent: ["File names, folders or anything on your disk", "Your name, email or computer name", "Project names or video content"],
      notes: ["Preview only: no report is sent from this window."],
    }),
    saveSettings: async () => ({ ok: true, message: "Preview only: nothing was saved." }),
    localModel: async () => EMPTY_LOCAL_MODEL,
    localModelDetect: async () => ({ ok: true, message: "Preview only.", doc: EMPTY_LOCAL_MODEL, servers: [] }),
    localModelSave: async () => ({ ok: true, message: "Preview only: nothing was saved.", doc: EMPTY_LOCAL_MODEL }),
    localModelCheck: async () => ({ ok: true, message: "Preview only.", doc: EMPTY_LOCAL_MODEL }),
  };
}

export function demoEnabled(): boolean {
  if (typeof window === "undefined") return false;
  if (!import.meta.env.DEV) return false;
  return new URLSearchParams(window.location.search).get("demo") === "1";
}
