import { createElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { MantineProvider } from "@mantine/core";
import { describe, expect, it } from "vitest";
import { CONSENT_TEXT, LocalModelView } from "../screens/LocalModel";
import type { LocalModelViewProps } from "../screens/LocalModel";
import {
  EMPTY_LOCAL_MODEL, canSwitchOn, localModelStatusLine, looksRemote, validateAddress,
} from "./localmodel";
import type { LocalModelDoc } from "./localmodel";

const noop = () => {};

function doc(mutate: (d: LocalModelDoc) => void = () => {}): LocalModelDoc {
  const d: LocalModelDoc = structuredClone(EMPTY_LOCAL_MODEL);
  mutate(d);
  return d;
}

function html(over: Partial<LocalModelViewProps> = {}): string {
  const props: LocalModelViewProps = {
    doc: doc(), paused: false, consent: false, address: "http://127.0.0.1:11434", allowRemote: false,
    servers: null, busy: false, message: null,
    onConsent: noop, onToggle: noop, onAddress: noop, onAllowRemote: noop, onUse: noop,
    onSaveAddress: noop, onDetect: noop, onCheck: noop, ...over,
  };
  return renderToStaticMarkup(createElement(MantineProvider, null, createElement(LocalModelView, props)));
}

const SWITCH = /<input[^>]*aria-label="Share my local model with my workspace"[^>]*>/;

describe("consent gate", () => {
  it("only lets the switch go on once consent is given", () => {
    expect(canSwitchOn(false, false)).toBe(false);
    expect(canSwitchOn(false, true)).toBe(true);
    expect(canSwitchOn(true, false)).toBe(true);
  });

  it("renders the switch disabled and the consent text until agreed", () => {
    const off = html();
    expect(off).toContain(CONSENT_TEXT);
    expect(SWITCH.exec(off)?.[0]).toContain("disabled");
    expect(SWITCH.exec(html({ consent: true }))?.[0]).not.toContain("disabled");
  });
});

describe("status line", () => {
  it("names each state", () => {
    expect(localModelStatusLine(doc(), false).text).toBe("Sharing is off");
    const on = doc((d) => { d.enabled = true; d.status.enabled = true; });
    expect(localModelStatusLine(on, false).text).toMatch(/Can't reach/);
    on.status.reachable = true;
    expect(localModelStatusLine(on, false).text).toBe("Waiting for requests");
    on.status.active = 2;
    expect(localModelStatusLine(on, false).text).toBe("Serving 2 requests");
    expect(localModelStatusLine(on, true).text).toMatch(/^Paused/);
  });

  it("is announced politely", () => {
    expect(html()).toContain('role="status"');
    expect(html()).toContain('aria-live="polite"');
  });
});

describe("detected servers", () => {
  it("lists each with a Use this action", () => {
    const out = html({ servers: [{ kind: "Ollama", baseUrl: "http://127.0.0.1:11434", models: ["llama3", "qwen"] }] });
    expect(out).toContain("Ollama");
    expect(out).toContain("Use this");
    expect(out).toContain("2 models");
  });

  it("says so when nothing was found", () => {
    expect(html({ servers: [] })).toContain("No model server was found on this computer.");
  });
});

describe("address validation", () => {
  it("accepts loopback and refuses other hosts until the box is ticked", () => {
    expect(validateAddress("http://127.0.0.1:11434", false)).toBe("");
    expect(validateAddress("http://localhost:1234", false)).toBe("");
    expect(looksRemote("http://192.168.1.20:11434")).toBe(true);
    expect(validateAddress("http://192.168.1.20:11434", false)).toMatch(/another computer/);
    expect(validateAddress("http://192.168.1.20:11434", true)).toBe("");
  });

  it("explains bad addresses and shows the error on the field", () => {
    expect(validateAddress("ollama:11434", false)).toMatch(/http:\/\//);
    expect(validateAddress("http://u:p@127.0.0.1", false)).toMatch(/user name/);
    expect(html({ address: "http://10.0.0.5:11434" })).toContain("That address is on another computer.");
  });

  it("warns when the remote option is ticked", () => {
    expect(html({ allowRemote: true })).toContain("Only use a computer you trust");
    expect(html()).not.toContain("Only use a computer you trust");
  });
});
