import { formatWhen } from "./derive";
import type { Tone } from "./derive";

// The Local model screen's document and its pure logic. The server behind it is
// the person's own; ReelBolt only passes requests through this app.

export interface LocalModelStatus {
  enabled: boolean;
  baseUrl: string;
  active: number;
  served: number;
  failed: number;
  lastRequest?: string;
  serverKind?: string;
  models?: string[];
  reachable: boolean;
}

export interface LocalModelDoc {
  available: boolean;
  enabled: boolean;
  baseUrl: string;
  allowRemoteHost: boolean;
  status: LocalModelStatus;
}

export interface DetectedServer {
  kind: string;
  baseUrl: string;
  models: string[];
}

export interface LocalModelPatch {
  enabled: boolean;
  baseUrl: string;
  allowRemoteHost: boolean;
  consent: boolean;
}

export interface LocalModelResult {
  ok: boolean;
  message?: string;
  doc: LocalModelDoc;
  servers?: DetectedServer[];
}

export type LocalModelTone = Tone;

// A zero-state document, used before the first answer arrives.
export const EMPTY_LOCAL_MODEL: LocalModelDoc = {
  available: true, enabled: false, baseUrl: "http://127.0.0.1:11434", allowRemoteHost: false,
  status: { enabled: false, baseUrl: "", active: 0, served: 0, failed: 0, reachable: false },
};

// localModelStatusLine is the one-line state: off, paused, waiting, serving N or
// unreachable. `paused` is the runner's own pause (it also stops sharing).
export function localModelStatusLine(
  doc: LocalModelDoc, paused: boolean,
): { text: string; tone: LocalModelTone } {
  if (!doc.enabled) return { text: "Sharing is off", tone: "idle" };
  if (paused) return { text: "Paused. Nothing is shared while this computer is paused.", tone: "idle" };
  const st = doc.status;
  if (!st.reachable) return { text: "Can't reach your model. Is it running?", tone: "bad" };
  if (st.active > 0) return { text: "Serving " + st.active + (st.active === 1 ? " request" : " requests"), tone: "working" };
  return { text: "Waiting for requests", tone: "ok" };
}

// The switch can only be turned ON once the person has agreed; turning it off
// never needs anything.
export function canSwitchOn(currentlyOn: boolean, consent: boolean): boolean {
  return currentlyOn || consent;
}

// looksRemote is a courtesy check for the address field so the screen can ask
// for the "another computer" box before the shell has to refuse. The shell
// still validates; this only picks the wording.
export function looksRemote(raw: string): boolean {
  const m = /^[a-z]+:\/\/(\[[^\]]+\]|[^/:?#]+)/i.exec(raw.trim());
  if (!m) return false;
  const host = m[1].replace(/^\[|\]$/g, "").toLowerCase();
  return !(host === "localhost" || host.endsWith(".localhost") || host === "::1" || /^127\./.test(host));
}

// validateAddress returns a sentence when the address cannot work, or "".
export function validateAddress(raw: string, allowRemote: boolean): string {
  const v = raw.trim();
  if (!v) return "";
  if (!/^https?:\/\/[^/\s]/i.test(v)) {
    return "The address should start with http:// or https://, for example http://127.0.0.1:11434.";
  }
  if (/[?#]/.test(v)) return "The address should end at the port, with nothing after a ? or #.";
  if (/^https?:\/\/[^/]*@/i.test(v)) return "The address can't contain a user name or password.";
  if (!allowRemote && looksRemote(v)) {
    return "That address is on another computer. Tick the box below to use it.";
  }
  return "";
}

export function lastRequestLabel(st: LocalModelStatus, now: Date = new Date()): string {
  return st.lastRequest ? formatWhen(st.lastRequest, now) : "none yet";
}
