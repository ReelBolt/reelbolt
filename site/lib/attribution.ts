// Pure first-touch attribution helpers (no React, no storage). Sanitizers follow the
// product-analytics contract: lowercase + trim first; an invalid field is dropped, never rejected.

export const FIRST_TOUCH_COOKIE = 'rb_ft';
export const VISITOR_ID_KEY = 'rf-visitor-id';
export const FIRST_TOUCH_MAX_AGE_SECONDS = 30 * 24 * 60 * 60;
const MAX_COOKIE_VALUE_BYTES = 512;

const UTM_RE = /^[a-z0-9][a-z0-9._~+-]{0,63}$/;
const HOST_RE = /^[a-z0-9][a-z0-9.-]{0,99}$/;
const PATH_RE = /^\/[a-z0-9/_~.-]{0,119}$/;

export interface Attribution {
  source?: string;
  medium?: string;
  campaign?: string;
  content?: string;
  term?: string;
  referrerHost?: string;
  landingPath?: string;
}

export interface FirstTouch extends Attribution {
  visitorId: string;
  ts: number;
}

function clean(value: string | null | undefined, re: RegExp): string | undefined {
  if (!value) return undefined;
  const v = value.trim().toLowerCase();
  return re.test(v) ? v : undefined;
}

export const sanitizeUtm = (v: string | null | undefined) => clean(v, UTM_RE);

/** A bare host name (no scheme, port or path), never one of our own. */
export function sanitizeHost(value: string | null | undefined, ownHost?: string) {
  const host = clean(value, HOST_RE);
  if (!host) return undefined;
  if (ownHost && host === ownHost.trim().toLowerCase()) return undefined;
  if (host === 'reelbolt.ai' || host.endsWith('.reelbolt.ai')) return undefined;
  return host;
}

export function sanitizeReferrerHost(referrer: string | null | undefined, ownHost?: string) {
  if (!referrer) return undefined;
  let hostname: string;
  try {
    hostname = new URL(referrer.trim()).hostname.toLowerCase();
  } catch {
    return undefined;
  }
  return sanitizeHost(hostname, ownHost);
}

export function sanitizeLandingPath(path: string | null | undefined) {
  if (!path) return undefined;
  const stripped = path.split(/[?#]/)[0];
  if (stripped.startsWith('//')) return undefined;
  return clean(stripped, PATH_RE);
}

export function parseAttribution(input: {
  search: string;
  referrer: string;
  pathname: string;
  ownHost?: string;
}): Attribution {
  const p = new URLSearchParams(input.search);
  const out: Attribution = {
    source: sanitizeUtm(p.get('utm_source')),
    medium: sanitizeUtm(p.get('utm_medium')),
    campaign: sanitizeUtm(p.get('utm_campaign')),
    content: sanitizeUtm(p.get('utm_content')),
    term: sanitizeUtm(p.get('utm_term')),
    referrerHost: sanitizeReferrerHost(input.referrer, input.ownHost),
    landingPath: sanitizeLandingPath(input.pathname),
  };
  for (const k of Object.keys(out) as (keyof Attribution)[]) {
    if (out[k] === undefined) delete out[k];
  }
  return out;
}

export function sourceLabel(a: Attribution): string {
  return a.source ?? a.referrerHost ?? 'direct';
}

function b64urlEncode(s: string): string {
  const bytes = new TextEncoder().encode(s);
  let bin = '';
  bytes.forEach((b) => (bin += String.fromCharCode(b)));
  return btoa(bin).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function b64urlDecode(s: string): string {
  const pad = s.replace(/-/g, '+').replace(/_/g, '/');
  const bin = atob(pad + '='.repeat((4 - (pad.length % 4)) % 4));
  return new TextDecoder().decode(Uint8Array.from(bin, (c) => c.charCodeAt(0)));
}

/** base64url(JSON) of the rb_ft payload; null if it would exceed 512 bytes. */
export function encodeFirstTouch(ft: FirstTouch): string | null {
  const payload: Record<string, string | number> = { v: 1 };
  if (ft.source) payload.s = ft.source;
  if (ft.medium) payload.m = ft.medium;
  if (ft.campaign) payload.c = ft.campaign;
  if (ft.content) payload.n = ft.content;
  if (ft.term) payload.t = ft.term;
  if (ft.referrerHost) payload.r = ft.referrerHost;
  if (ft.landingPath) payload.l = ft.landingPath;
  payload.vid = ft.visitorId;
  payload.ts = ft.ts;
  const encoded = b64urlEncode(JSON.stringify(payload));
  return encoded.length <= MAX_COOKIE_VALUE_BYTES ? encoded : null;
}

export function decodeFirstTouch(value: string): FirstTouch | null {
  try {
    const o = JSON.parse(b64urlDecode(value)) as Record<string, unknown>;
    if (o.v !== 1 || typeof o.vid !== 'string' || typeof o.ts !== 'number') return null;
    const str = (k: string) => (typeof o[k] === 'string' ? (o[k] as string) : undefined);
    const ft: FirstTouch = {
      visitorId: o.vid,
      ts: o.ts,
      source: str('s'),
      medium: str('m'),
      campaign: str('c'),
      content: str('n'),
      term: str('t'),
      referrerHost: str('r'),
      landingPath: str('l'),
    };
    for (const k of Object.keys(ft) as (keyof FirstTouch)[]) {
      if (ft[k] === undefined) delete ft[k];
    }
    return ft;
  } catch {
    return null;
  }
}
