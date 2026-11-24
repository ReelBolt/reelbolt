import { useCallback, useEffect, useState } from "react";
import { Button, Checkbox, Switch, TextInput } from "@mantine/core";
import { IconRefresh, IconSearch } from "@tabler/icons-react";
import type { Bridge } from "../shell/bridge";
import {
  EMPTY_LOCAL_MODEL, canSwitchOn, lastRequestLabel, localModelStatusLine, validateAddress,
} from "../shell/localmodel";
import type { DetectedServer, LocalModelDoc } from "../shell/localmodel";
import { Chip, ICON, Notice, Row, Section, StatePill, Surface } from "../ui/kit";

// What the person is agreeing to, in one place so the checkbox and the tests
// read the same sentence.
export const CONSENT_TEXT =
  "While sharing is on, prompts from members of this workspace are sent to the model on this computer.";

export interface LocalModelViewProps {
  doc: LocalModelDoc;
  paused: boolean;
  consent: boolean;
  address: string;
  allowRemote: boolean;
  servers: DetectedServer[] | null;
  busy: boolean;
  // message is the last answer from the shell; ok says which colour it is.
  message: { ok: boolean; text: string } | null;
  onConsent: (v: boolean) => void;
  onToggle: (on: boolean) => void;
  onAddress: (v: string) => void;
  onAllowRemote: (v: boolean) => void;
  onUse: (s: DetectedServer) => void;
  onSaveAddress: () => void;
  onDetect: () => void;
  onCheck: () => void;
}

const SWITCH_LABEL = "Share my local model with my workspace";

// LocalModelView is the whole screen section, driven only by props, so every
// state of it can be rendered and tested without a shell.
export function LocalModelView(p: LocalModelViewProps) {
  const { doc } = p;
  const line = localModelStatusLine(doc, p.paused);
  const st = doc.status;
  const addressError = validateAddress(p.address, p.allowRemote);
  const models = st.models ?? [];
  return (
    <Section
      id="local-model"
      title="Local model"
      lead="Let your workspace use the AI model that runs on this computer."
    >
      <Surface>
        <div style={{ padding: "16px 20px" }} className="rb-row-help">
          The model keeps running on this computer. ReelBolt only passes requests through this app&apos;s
          existing connection, so nothing is opened to the internet and no one can reach your computer
          from outside. It&apos;s off until you turn it on.
        </div>
        {!doc.available ? (
          <div style={{ padding: "0 20px 16px" }}>
            <Notice tone="plain">Sharing a local model isn&apos;t available in this window.</Notice>
          </div>
        ) : null}
        {!doc.enabled ? (
          <div style={{ padding: "0 20px 8px" }}>
            <Checkbox
              checked={p.consent}
              onChange={(e) => p.onConsent(e.currentTarget.checked)}
              label={CONSENT_TEXT}
            />
          </div>
        ) : null}
        <Row
          label={SWITCH_LABEL}
          control={
            <Switch
              checked={doc.enabled}
              disabled={!doc.available || p.busy || !canSwitchOn(doc.enabled, p.consent)}
              onChange={(e) => p.onToggle(e.currentTarget.checked)}
              aria-label={SWITCH_LABEL}
            />
          }
          help={!doc.enabled && !p.consent ? "Tick the box above first." : undefined}
        />
        <div style={{ padding: "0 20px 16px", display: "flex", alignItems: "center", gap: 12, flexWrap: "wrap" }}>
          <StatePill tone={line.tone}>{doc.enabled ? "On" : "Off"}</StatePill>
          <span role="status" aria-live="polite" className="rb-row-help" style={{ margin: 0 }}>
            {line.text}
          </span>
        </div>
      </Surface>

      <Surface style={{ marginTop: 16 }}>
        <Row
          label="Where your model is"
          help="Pick a server found on this computer, or type its address."
          stack
        >
          <div style={{ display: "flex", flexDirection: "column", gap: 10, marginTop: 8 }}>
            <div>
              <Button
                variant="default" size="sm" leftSection={<IconSearch {...ICON} />}
                loading={p.busy} onClick={p.onDetect}
              >
                Look for my model
              </Button>
            </div>
            <div aria-live="polite">
              {p.servers && p.servers.length === 0 ? (
                <span className="rb-row-help">No model server was found on this computer.</span>
              ) : null}
              {p.servers && p.servers.length > 0 ? (
                <ul aria-label="Model servers found" style={{ listStyle: "none", margin: 0, padding: 0 }}>
                  {p.servers.map((s) => (
                    <li key={s.baseUrl} style={{ display: "flex", gap: 12, alignItems: "center", padding: "6px 0" }}>
                      <span style={{ flex: 1, minWidth: 0, fontSize: 14 }}>
                        <strong>{s.kind || "Model server"}</strong> at {s.baseUrl}
                        <span className="rb-row-help">
                          {" "}({s.models.length} {s.models.length === 1 ? "model" : "models"})
                        </span>
                      </span>
                      <Button
                        size="xs" variant="light" disabled={p.busy}
                        aria-label={"Use " + (s.kind || "this server") + " at " + s.baseUrl}
                        onClick={() => p.onUse(s)}
                      >
                        Use this
                      </Button>
                    </li>
                  ))}
                </ul>
              ) : null}
            </div>
            <TextInput
              label="Address"
              value={p.address}
              onChange={(e) => p.onAddress(e.currentTarget.value)}
              error={addressError || undefined}
              placeholder="http://127.0.0.1:11434"
              spellCheck={false}
              autoCapitalize="off"
            />
            <Checkbox
              checked={p.allowRemote}
              onChange={(e) => p.onAllowRemote(e.currentTarget.checked)}
              label="My model runs on another computer on my network"
            />
            {p.allowRemote ? (
              <Notice tone="warn">
                Only use a computer you trust on a network you trust. Prompts will travel between the two
                computers without ReelBolt protecting them, and anyone on that network could read them.
              </Notice>
            ) : null}
            <div>
              <Button size="sm" disabled={p.busy || !!addressError} onClick={p.onSaveAddress}>
                Save address
              </Button>
            </div>
          </div>
        </Row>
      </Surface>

      <Surface style={{ marginTop: 16 }}>
        <Row
          label="Models found"
          help={models.length ? undefined : "None yet. Start your model server, then check again."}
          control={
            <Button
              variant="default" size="sm" leftSection={<IconRefresh {...ICON} />}
              loading={p.busy} onClick={p.onCheck}
            >
              Check again
            </Button>
          }
        >
          {models.length ? (
            <ul aria-label="Models" style={{ listStyle: "none", margin: "8px 0 0", padding: 0, display: "flex", flexWrap: "wrap", gap: 6 }}>
              {models.map((m) => (
                <li key={m}><Chip>{m}</Chip></li>
              ))}
            </ul>
          ) : null}
        </Row>
        <div style={{ padding: "0 20px 16px", display: "flex", gap: 24, flexWrap: "wrap" }} className="rb-row-help">
          <span>Requests served: {st.served}</span>
          <span>Failed: {st.failed}</span>
          <span>Last request: {lastRequestLabel(st)}</span>
        </div>
      </Surface>

      <div aria-live="polite" style={{ marginTop: 12 }}>
        {p.message ? <Notice tone={p.message.ok ? "ok" : "bad"}>{p.message.text}</Notice> : null}
      </div>
    </Section>
  );
}

type Answer = { ok: boolean; message?: string; doc: LocalModelDoc; servers?: DetectedServer[] };

// LocalModelSection owns the state and talks to the shell.
export function LocalModelSection({ bridge, paused }: { bridge: Bridge; paused: boolean }) {
  const [doc, setDoc] = useState<LocalModelDoc>(EMPTY_LOCAL_MODEL);
  const [consent, setConsent] = useState(false);
  const [address, setAddress] = useState("");
  const [allowRemote, setAllowRemote] = useState(false);
  const [servers, setServers] = useState<DetectedServer[] | null>(null);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null);

  const adopt = useCallback((d: LocalModelDoc) => {
    setDoc(d);
    setAddress(d.baseUrl);
    setAllowRemote(d.allowRemoteHost);
  }, []);

  useEffect(() => {
    let live = true;
    bridge.localModel().then((d) => live && adopt(d)).catch(() => undefined);
    // Counters and reachability change on their own; re-read them quietly.
    const t = window.setInterval(() => {
      bridge.localModel().then((d) => live && setDoc(d)).catch(() => undefined);
    }, 5000);
    return () => {
      live = false;
      window.clearInterval(t);
    };
  }, [bridge, adopt]);

  const run = async (fn: () => Promise<Answer>): Promise<Answer | null> => {
    setBusy(true);
    try {
      const res = await fn();
      setDoc(res.doc);
      if (res.servers) setServers(res.servers);
      setMessage(res.message ? { ok: res.ok, text: res.message } : null);
      return res;
    } catch (err) {
      setMessage({ ok: false, text: err instanceof Error ? err.message : String(err) });
      return null;
    } finally {
      setBusy(false);
    }
  };

  const save = async (enabled: boolean, baseUrl: string, remote: boolean) => {
    const res = await run(() =>
      bridge.localModelSave({ enabled, baseUrl, allowRemoteHost: remote, consent }));
    if (res?.ok) adopt(res.doc);
  };

  return (
    <LocalModelView
      doc={doc} paused={paused} consent={consent} address={address} allowRemote={allowRemote}
      servers={servers} busy={busy} message={message}
      onConsent={setConsent}
      onToggle={(on) => void save(on, address, allowRemote)}
      onAddress={setAddress}
      onAllowRemote={setAllowRemote}
      onUse={(s) => void save(doc.enabled, s.baseUrl, false)}
      onSaveAddress={() => void save(doc.enabled, address, allowRemote)}
      onDetect={() => void run(() => bridge.localModelDetect())}
      onCheck={() => void run(() => bridge.localModelCheck())}
    />
  );
}
