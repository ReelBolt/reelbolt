import { useCallback, useEffect, useRef, useState } from "react";
import { Button, Skeleton } from "@mantine/core";
import { useInterval } from "@mantine/hooks";
import {
  IconActivity, IconFolders, IconHome2, IconPlugConnectedX, IconSettings,
} from "@tabler/icons-react";
import type { ActionResult, View } from "./shell/types";
import type { Bridge } from "./shell/bridge";
import { ShellUnavailable } from "./shell/bridge";
import { screenForState, screenFromShell, type Screen } from "./shell/derive";
import { Home } from "./screens/Home";
import { Activity } from "./screens/Activity";
import { Settings } from "./screens/Settings";
import { Folders } from "./screens/Folders";
import { Notice } from "./ui/kit";
import logo from "./assets/logo.svg";

// How often the window re-reads the shell. The shell publishes its own view on
// change; this is the window catching up, so it is a poll and not a push, which
// is what keeps the host free of a listening socket.
const POLL_MS = 1000;

// How long a "Saved." style message stays before it clears itself. Errors stay
// until dismissed: a person must not miss why something did not happen.
const MESSAGE_MS = 6000;

interface Props {
  bridge: Bridge;
  preview?: boolean;
}

export interface Message {
  text: string;
  ok: boolean;
}

// Act is how a screen asks the shell to do something. It resolves once the
// shell has answered and the view has been replaced.
export type Act = (action: string, arg?: string) => Promise<ActionResult | null>;

const NAV: { id: Screen; label: string; Icon: typeof IconHome2 }[] = [
  { id: "home", label: "Home", Icon: IconHome2 },
  { id: "activity", label: "Activity", Icon: IconActivity },
  { id: "folders", label: "Folders", Icon: IconFolders },
  { id: "settings", label: "Settings", Icon: IconSettings },
];

// App is the whole window: a navigation rail, one screen, one action channel.
//
// The live state of this computer is on Home, and the rail's footer repeats it
// in one word, so a person looking at Settings can still see that the machine
// stopped an hour ago.
export function App({ bridge, preview }: Props) {
  const [view, setView] = useState<View | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [message, setMessage] = useState<Message | null>(null);
  const [screen, setScreen] = useState<Screen | null>(null);
  const [busy, setBusy] = useState<string | null>(null);
  const timer = useRef<number | null>(null);
  const mainRef = useRef<HTMLElement | null>(null);

  const refresh = useCallback(async () => {
    try {
      const next = await bridge.view();
      setView(next);
      setError(null);
    } catch (err) {
      if (err instanceof ShellUnavailable) setError(err.message);
      else setError(err instanceof Error ? err.message : String(err));
    }
  }, [bridge]);

  useEffect(() => {
    void refresh();
  }, [refresh]);

  useInterval(() => void refresh(), POLL_MS, { autoInvoke: true });

  const say = useCallback((text: string | null | undefined, ok: boolean) => {
    if (timer.current) window.clearTimeout(timer.current);
    if (!text) {
      setMessage(null);
      return;
    }
    setMessage({ text, ok });
    if (ok) timer.current = window.setTimeout(() => setMessage(null), MESSAGE_MS);
  }, []);

  const act: Act = useCallback(
    async (action, arg) => {
      setBusy(action + (arg ? ":" + arg : ""));
      try {
        const res: ActionResult = await bridge.do(action, arg);
        if (res.view) setView(res.view);
        say(res.message, res.ok);
        const next = screenFromShell(res.screen);
        if (next) setScreen(next);
        return res;
      } catch (err) {
        say(err instanceof Error ? err.message : String(err), false);
        return null;
      } finally {
        setBusy(null);
      }
    },
    [bridge, say],
  );

  // A new screen starts at its top, not wherever the last one was scrolled to.
  useEffect(() => {
    mainRef.current?.scrollTo({ top: 0 });
  }, [screen]);

  if (error && !view) {
    return <Disconnected onRetry={() => void refresh()} />;
  }

  if (!view) {
    return <Loading />;
  }

  const active = screen ?? screenForState(view);
  const running = (view.jobs ?? []).filter((j) => j.running).length;

  return (
    <div className="rb-shell">
      <nav className="rb-rail" aria-label="Main">
        <div className="rb-brand">
          <img src={logo} width={28} height={28} alt="" style={{ borderRadius: 7 }} />
          <div className="rb-brand-text">
            <div className="rb-brand-name">ReelBolt</div>
            <div className="rb-brand-sub">Desktop app</div>
          </div>
        </div>
        {NAV.map(({ id, label, Icon }) => (
          <button
            key={id}
            type="button"
            className="rb-nav-item"
            aria-current={active === id ? "page" : undefined}
            onClick={() => setScreen(id)}
            title={label}
          >
            <Icon size={18} stroke={1.6} aria-hidden />
            <span className="rb-nav-label">{label}</span>
            {id === "activity" && running > 0 ? <span className="rb-nav-count">{running}</span> : null}
          </button>
        ))}
        <div className="rb-rail-foot">
          <div style={{ color: "var(--rb-text-muted)", fontWeight: 500 }}>
            {view.device.name || "This computer"}
          </div>
          <div>Version {view.version}</div>
        </div>
      </nav>

      <main className="rb-main" ref={mainRef}>
        {preview || message || error ? (
          <div className="rb-page" style={{ paddingTop: 20, paddingBottom: 0 }}>
            <div style={{ display: "grid", gap: 8 }}>
              {preview ? (
                <Notice tone="warn" title="Preview">
                  This window is showing sample data. No button here changes anything on your computer.
                </Notice>
              ) : null}
              {error ? (
                <Notice tone="warn" title="Lost touch with the app">
                  Showing the last known state. Trying again every second.
                </Notice>
              ) : null}
              {message ? (
                <Notice tone={message.ok ? "ok" : "bad"} onClose={() => say(null, true)}>
                  {message.text}
                </Notice>
              ) : null}
            </div>
          </div>
        ) : null}

        <div key={active}>
          {active === "home" ? (
            <Home view={view} bridge={bridge} act={act} busy={busy} go={setScreen} />
          ) : null}
          {active === "activity" ? <Activity view={view} act={act} busy={busy} /> : null}
          {active === "folders" ? <Folders bridge={bridge} act={act} busy={busy} /> : null}
          {active === "settings" ? (
            <Settings
              view={view}
              bridge={bridge}
              act={act}
              busy={busy}
              onSaved={(res) => {
                if (res.view) setView(res.view);
                say(res.message ?? (res.ok ? "Saved." : "That change was not saved."), res.ok);
              }}
            />
          ) : null}
        </div>
      </main>
    </div>
  );
}

// remedyFor maps a remedy kind onto the action the host handles. It mirrors
// ui.RemedyAction in Go, and the Go test is the one that guarantees every kind
// has an action; this is the window's half of the same contract. The schedule
// remedy is answered by the Settings screen, so it has no action here.
export function remedyFor(view: View): string {
  switch (view.remedy?.kind) {
    case "pair":
      return "pair";
    case "start":
      return "start-runner";
    case "resume":
      return "resume";
    case "runtime":
      return "setup-runtime";
    case "update":
      return "check-for-update";
    default:
      return "";
  }
}

function Disconnected({ onRetry }: { onRetry: () => void }) {
  return (
    <div style={{ display: "grid", placeItems: "center", height: "100dvh", padding: 24 }}>
      <div style={{ maxWidth: 420, textAlign: "center" }}>
        <IconPlugConnectedX size={32} stroke={1.4} style={{ color: "var(--rb-text-faint)" }} aria-hidden />
        <h1 className="rb-page-title" style={{ marginTop: 14 }}>The app is not responding</h1>
        <p className="rb-page-lead" style={{ marginInline: "auto" }}>
          This window could not reach ReelBolt on your computer. Quit ReelBolt from the system tray and
          open it again.
        </p>
        <Button mt="lg" variant="default" onClick={onRetry}>
          Try again
        </Button>
      </div>
    </div>
  );
}

function Loading() {
  return (
    <div className="rb-shell" aria-busy="true" aria-label="Loading">
      <div className="rb-rail">
        <Skeleton height={28} width={120} mb={18} radius="sm" />
        {NAV.map((n) => (
          <Skeleton key={n.id} height={30} mb={6} radius="md" />
        ))}
      </div>
      <div className="rb-page" style={{ width: "100%" }}>
        <Skeleton height={26} width={200} radius="sm" />
        <Skeleton height={14} width={360} mt={12} radius="sm" />
        <Skeleton height={150} mt={28} radius="lg" />
        <Skeleton height={220} mt={28} radius="lg" />
      </div>
    </div>
  );
}
