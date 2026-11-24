/**
 * Pure collector for the "Copy diagnostics" report (Help menu). It captures the shell's layout and
 * storage SHAPE so a stuck mobile navbar can be diagnosed. Privacy: storage and cookie KEY NAMES
 * only, never values; no email or names.
 */

const MAX_STYLE_CHARS = 2000;
const MAX_STACK = 8;
const MAX_LIST = 50;
const MAX_NAME = 100;

/** The serialized `context.diagnostics` may be at most this many UTF-8 bytes; the server enforces the same cap. */
export const MAX_DIAGNOSTICS_BYTES = 16384;

/** Nesting depth of a JSON value: the root object is 1 and every nested object or array adds 1. */
export function jsonDepth(value: unknown): number {
  if (value === null || typeof value !== 'object') return 0;
  let deepest = 0;
  for (const child of Object.values(value)) deepest = Math.max(deepest, jsonDepth(child));
  return deepest + 1;
}

// Known key-name prefixes. Anything else is reported only as `<other>` plus a length bucket.
const KNOWN_PREFIXES = [
  'reelbolt:', 'reelbolt.', 'reelbolt_', 'reelbolt-', 'reelforge:', 'reelforge.', 'reelforge-', 'rb_', 'rb.', 'rb-',
  'mantine-', 'theme',
];
// '@' or its percent-encoded form '%40' (a key builder that URI-encodes an address).
const EMAIL_RE = /[^\s:/;=,]+(?:@|%40)[^\s:/;=,]+/gi;
const UUID_RE = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;
// A long run of id-like characters (hex, base64url, timeline ids) that also holds a digit.
const LONG_ID_RE = /(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]{16,}/g;

/**
 * The one place a storage / cookie / IndexedDB name is made safe to report: known prefixes only,
 * emails and the current user's id or email become `<user>`, UUIDs and long ids become `<id>`.
 */
export function redactKeyName(name: string, userHints: readonly string[] = []): string {
  const bucket = name.length <= 16 ? '<=16' : name.length <= 48 ? '<=48' : '>48';
  if (!KNOWN_PREFIXES.some((p) => name.startsWith(p))) return `<other:${bucket}>`;
  let out = name;
  for (const hint of userHints) {
    if (hint.length >= 3) out = out.split(hint).join('<user>').split(encodeURIComponent(hint)).join('<user>');
  }
  out = out.replace(EMAIL_RE, '<user>').replace(UUID_RE, '<id>').replace(LONG_ID_RE, '<id>');
  if (out.includes('@') || out.length > 120) return `<other:${bucket}>`;
  return out;
}

/** Strings from the readable `reelbolt_user` cookie that identify the user; used only to redact, never reported. */
function userHintsFrom(cookie: string): string[] {
  const raw = cookie.split(';').map((p) => p.trim()).find((p) => p.startsWith('reelbolt_user='));
  if (!raw) return [];
  const hints: string[] = [];
  try {
    const parsed: unknown = JSON.parse(decodeURIComponent(raw.slice('reelbolt_user='.length)));
    if (parsed && typeof parsed === 'object') {
      for (const v of Object.values(parsed)) if (typeof v === 'string' && v.length >= 3) hints.push(v);
    }
  } catch {
    // opaque cookie: the email and long-id patterns still apply
  }
  return hints;
}

/** Browser family + major version and OS family only; the full UA string is never reported. */
export function reduceUserAgent(ua: string): string {
  const browser =
    /Edg\/(\d+)/.exec(ua)?.[1] ? `Edge ${/Edg\/(\d+)/.exec(ua)?.[1]}`
    : /(?:OPR|Opera)\/(\d+)/.exec(ua) ? `Opera ${/(?:OPR|Opera)\/(\d+)/.exec(ua)?.[1]}`
    : /(?:Chrome|CriOS)\/(\d+)/.exec(ua) ? `Chrome ${/(?:Chrome|CriOS)\/(\d+)/.exec(ua)?.[1]}`
    : /(?:Firefox|FxiOS)\/(\d+)/.exec(ua) ? `Firefox ${/(?:Firefox|FxiOS)\/(\d+)/.exec(ua)?.[1]}`
    : /Version\/(\d+).*Safari/.exec(ua) ? `Safari ${/Version\/(\d+)/.exec(ua)?.[1]}`
    : 'other';
  const os = /Android/.test(ua) ? 'Android' : /iPhone|iPad|iPod/.test(ua) ? 'iOS' : /Windows/.test(ua) ? 'Windows'
    : /Mac OS X|Macintosh/.test(ua) ? 'macOS' : /Linux|X11/.test(ua) ? 'Linux' : 'other';
  return `${browser} on ${os}`;
}

const DATA_ATTR_ALLOWLIST = new Set([
  'data-opened', 'data-resizing', 'data-mantine-color-scheme', 'data-layout', 'data-testid', 'data-tour', 'data-position',
]);

const byteLength = (v: unknown) => new TextEncoder().encode(JSON.stringify(v)).length;
const names = (list: string[]) => list.slice(0, MAX_LIST).map((n) => n.slice(0, MAX_NAME));

/** Drops the largest droppable field until the report fits, marking it `truncated: true`. */
export function capDiagnostics(report: Record<string, unknown>): Record<string, unknown> {
  if (byteLength(report) <= MAX_DIAGNOSTICS_BYTES) return report;
  const out = JSON.parse(JSON.stringify(report)) as Record<string, Record<string, unknown> | unknown>;
  out.truncated = true;
  const protectedKeys = new Set(['time', 'buildId', 'truncated']);
  while (byteLength(out) > MAX_DIAGNOSTICS_BYTES) {
    let best: { parent: Record<string, unknown>; key: string; size: number } | null = null;
    const consider = (parent: Record<string, unknown>, key: string) => {
      const size = byteLength(parent[key]);
      if (!best || size > best.size) best = { parent, key, size };
    };
    for (const k of Object.keys(out)) {
      if (protectedKeys.has(k)) continue;
      const v = out[k];
      if (v && typeof v === 'object' && !Array.isArray(v) && Object.keys(v).length > 0) {
        for (const kk of Object.keys(v)) consider(v as Record<string, unknown>, kk);
      } else consider(out, k);
    }
    if (!best) break;
    const { parent, key } = best as { parent: Record<string, unknown>; key: string };
    delete parent[key];
  }
  return out;
}

type Rect = { x: number; y: number; width: number; height: number };

function rect(el: Element): Rect {
  const r = el.getBoundingClientRect();
  return { x: r.x, y: r.y, width: r.width, height: r.height };
}

function dataAttrs(el: Element | null): Record<string, string> {
  const out: Record<string, string> = {};
  if (!el) return out;
  for (const a of Array.from(el.attributes)) if (DATA_ATTR_ALLOWLIST.has(a.name)) out[a.name] = a.value;
  return out;
}

function describe(el: Element): string {
  const classes = Array.from(el.classList).slice(0, 3).map((c) => `.${c}`).join('');
  return `${el.tagName.toLowerCase()}${el.id ? `#${el.id}` : ''}${classes}`;
}

function stackAt(doc: Document, win: Window, x: number, y: number) {
  if (typeof doc.elementsFromPoint !== 'function') return [];
  return doc.elementsFromPoint(x, y).slice(0, MAX_STACK).map((el) => {
    const cs = win.getComputedStyle(el);
    return {
      el: describe(el),
      testid: el.getAttribute('data-testid'),
      position: cs.position,
      zIndex: cs.zIndex,
    };
  });
}

function storageKeys(read: () => Storage, hints: readonly string[]): string[] {
  try {
    const s = read();
    const keys: string[] = [];
    for (let i = 0; i < s.length; i += 1) {
      const k = s.key(i);
      if (k !== null) keys.push(k);
    }
    return names(keys.map((k) => redactKeyName(k, hints)).sort());
  } catch {
    return ['<unavailable>'];
  }
}

export function cookieNames(cookie: string): string[] {
  return cookie
    .split(';')
    .map((p) => p.split('=')[0].trim())
    .filter(Boolean);
}

export interface DiagnosticsEnv {
  win: Window;
  doc: Document;
  buildId?: string;
}

export async function collectShellDiagnostics({ win, doc, buildId }: DiagnosticsEnv): Promise<Record<string, unknown>> {
  const nav = win.navigator;
  const hints = userHintsFrom(doc.cookie);
  const vv = win.visualViewport;
  const rootStyle = win.getComputedStyle(doc.documentElement);
  const navbar = doc.querySelector('.mantine-AppShell-navbar');
  const burger = doc.querySelector('.mantine-Burger-root');

  let navbarInfo: Record<string, unknown> | null = null;
  let center: ReturnType<typeof stackAt> = [];
  if (navbar) {
    const cs = win.getComputedStyle(navbar);
    const r = rect(navbar);
    navbarInfo = {
      rect: r,
      transform: cs.transform,
      translate: cs.translate,
      visibility: cs.visibility,
      display: cs.display,
      opacity: cs.opacity,
      zIndex: cs.zIndex,
      position: cs.position,
      width: cs.width,
      height: cs.height,
      inert: navbar.hasAttribute('inert'),
      hidden: navbar.hasAttribute('hidden'),
      children: navbar.children.length,
    };
    center = stackAt(doc, win, r.x + r.width / 2, r.y + r.height / 2);
  }

  const inline = Array.from(doc.querySelectorAll('style[data-mantine-styles="inline"]'));
  const appShellStyles = inline
    .map((s) => s.textContent ?? '')
    .filter((t) => t.includes('app-shell'))
    .map((t) => t.slice(0, MAX_STYLE_CHARS));

  const overlays = Array.from(
    doc.querySelectorAll('[data-portal], .mantine-Overlay-root, .mantine-Drawer-root, .mantine-Modal-root'),
  ).map((el) => ({ el: describe(el), rect: rect(el) }));

  let databases: string[] = [];
  try {
    const idb = win.indexedDB as IDBFactory & { databases?: () => Promise<Array<{ name?: string }>> };
    if (typeof idb?.databases === 'function') {
      databases = (await idb.databases()).map((d) => d.name ?? '').filter(Boolean);
    }
  } catch {
    databases = ['<unavailable>'];
  }

  const allCookies = cookieNames(doc.cookie);
  const cookies = names(allCookies.map((n) => redactKeyName(n, hints)));
  const standalone = typeof win.matchMedia === 'function' ? win.matchMedia('(display-mode: standalone)').matches : false;

  return capDiagnostics({
    time: new Date().toISOString(),
    buildId: buildId ?? null,
    userAgent: reduceUserAgent(nav.userAgent),
    standalone,
    viewport: {
      innerWidth: win.innerWidth,
      innerHeight: win.innerHeight,
      visualViewport: vv ? { width: vv.width, height: vv.height, scale: vv.scale, offsetTop: vv.offsetTop } : null,
      devicePixelRatio: win.devicePixelRatio,
      orientation: win.screen?.orientation?.type ?? null,
    },
    visibilityState: doc.visibilityState,
    shell: {
      burgerOpened: burger ? burger.hasAttribute('data-opened') || burger.getAttribute('aria-expanded') === 'true' : null,
      appShellRootData: dataAttrs(doc.querySelector('.mantine-AppShell-root')),
      navbar: navbarInfo,
      cssVars: {
        '--app-shell-navbar-transform': rootStyle.getPropertyValue('--app-shell-navbar-transform'),
        '--app-shell-navbar-width': rootStyle.getPropertyValue('--app-shell-navbar-width'),
        '--app-shell-navbar-offset': rootStyle.getPropertyValue('--app-shell-navbar-offset'),
      },
      inlineStyleCount: inline.length,
      appShellInlineStyles: appShellStyles,
      stackAtNavbarCenter: center,
      stackAt40x200: stackAt(doc, win, 40, 200),
      overlays: overlays.slice(0, MAX_LIST),
      animations: typeof doc.getAnimations === 'function' ? doc.getAnimations().length : null,
      serviceWorkerController: Boolean(nav.serviceWorker?.controller),
    },
    storage: {
      localStorageKeys: storageKeys(() => win.localStorage, hints),
      sessionStorageKeys: storageKeys(() => win.sessionStorage, hints),
      indexedDbNames: names(databases.map((n) => redactKeyName(n, hints))),
      cookieNames: cookies,
      reelboltUserCookieCount: allCookies.filter((n) => n === 'reelbolt_user').length,
    },
  });
}

export async function buildDiagnosticsReport(env: DiagnosticsEnv): Promise<string> {
  return JSON.stringify(await collectShellDiagnostics(env), null, 2);
}
