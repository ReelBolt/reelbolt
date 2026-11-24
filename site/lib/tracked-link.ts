// The tracked-link redirect of the static export: GET /l/<code> on Cloudflare Pages
// (functions/l/[code].ts). The self-hosted stack does the same job in nginx (`location ~* "^/(?:api/v1/)?l/..."`),
// which proxies straight to the API; this is the Pages twin, written here so node --test can drive it.
//
// The API (GET /api/v1/l/<code>) counts the click and answers a 302 with a RELATIVE Location: the
// landing page plus the link's campaign parameters. This relays that Location and nothing else.
//
// Rules:
//  - A posted link must never break. Whatever goes wrong, the answer is a 302 to the home page:
//      * a code that cannot exist (malformed): the bare `/`. Nothing is called.
//      * a well-formed code whose click could not be counted here (REELBOLT_API_URL unset, a
//        timeout, a network error, any answer that is not a 302, a Location that is not a plain
//        own-origin path): `/?utm_content=<code>`, what nginx answers for its own 429 and an API 5xx
//        (`@link_home`). Unlike nginx, the API's 404 (tracked links switched off) also lands
//        here, so on Pages a switched-off link still joins its visits to the link. The click is not counted, but the visit still belongs to the link: a URL
//        that carries a link's code as utm_content joins that link.
//    The API's own answers are relayed as they are, including its 302 to `/` for an unknown or
//    archived code.
//  - Never an open redirect: only a path on this host is relayed.
//  - What goes upstream from the caller: the User-Agent and the browser's request-kind headers
//    (Sec-Fetch-Mode, Sec-Fetch-Dest, Sec-Purpose, Purpose, X-Purpose, X-Moz), because that is how
//    the API tells a link preview, a crawler or a prefetch from a person's click. NO cookie, NO
//    Authorization and no address header does: the API keeps a counter, not the visitor.
//  - With SITE_FORWARD_TOKEN set, the request also carries it as X-ReelBolt-Site-Token. The origin
//    (nginx) checks it and then limits this relay in its own zone instead of the per-address one,
//    which every click relayed from Cloudflare would otherwise share. The header stops at that edge
//    and grants nothing else. The token is never logged and never part of an answer.
//  - `Cache-Control: no-store` on every answer, so nothing replays the redirect without the count.
//
// Reachable from site/functions, so no JSON import attribute anywhere in this module's imports.
import { upstreamBase, type FetchLike } from './recipes/proxy.ts';

/** `^[a-z0-9][a-z0-9-]{1,39}$`, the link-code pattern of docs/design/new-user-funnel.md section 4. */
const LINK_CODE_RE = /^[a-z0-9][a-z0-9-]{1,39}$/;

/** Where a request goes when its code cannot be a link code: the home page, nothing attached. */
export const TRACKED_LINK_FALLBACK = '/';
/** Short on purpose: the visitor is waiting on a redirect, and the fallback is the home page. */
export const TRACKED_LINK_TIMEOUT_MS = 3000;
/** The header the origin checks to recognise this relay (nginx: `link_relay`). */
export const SITE_TOKEN_HEADER = 'x-reelbolt-site-token';
const MAX_LOCATION_CHARS = 2048;
const MAX_USER_AGENT_CHARS = 512;
const MAX_REQUEST_KIND_CHARS = 64;

/**
 * The browser's own description of the request, forwarded next to the User-Agent when present.
 * A real click is a navigation to a document; a prefetch or a preview says so in one of the others.
 */
export const REQUEST_KIND_HEADERS = [
  'sec-fetch-mode',
  'sec-fetch-dest',
  'sec-purpose',
  'purpose',
  'x-purpose',
  'x-moz',
] as const;

/**
 * Where a well-formed code goes when its click could not be counted: the home page with the code
 * as `utm_content`, so the visit still joins the link (nginx: `@link_home`).
 */
export function trackedLinkFallback(code: string | null): string {
  return code === null ? TRACKED_LINK_FALLBACK : `/?utm_content=${code}`;
}

/**
 * The link code of a route parameter, or null when it cannot be one. Trimmed and lowercased first,
 * so a re-cased link still finds its code (codes are stored in lower case).
 */
export function linkCode(raw: string | string[] | null | undefined): string | null {
  const value = Array.isArray(raw) ? (raw.length === 1 ? raw[0] : null) : raw;
  if (typeof value !== 'string') return null;
  const code = value.trim().toLowerCase();
  return LINK_CODE_RE.test(code) ? code : null;
}

/** `{REELBOLT_API_URL}/api/v1/l/<code>`, or null when the origin is unset or not http(s). */
export function trackedLinkUpstream(apiUrl: string | undefined, code: string): string | null {
  const base = upstreamBase(apiUrl);
  return base === null ? null : `${base}/api/v1/l/${code}`;
}

/**
 * The Location to relay: the upstream's, when it is a path on this host; `fallback` otherwise (the
 * bare home page unless the caller names another).
 * Refused: an absolute or scheme-relative URL (`https://…`, `//host`), a backslash (browsers read
 * `/\host` as `//host`), whitespace or a control character, and anything implausibly long.
 */
export function relayLocation(location: string | null | undefined, fallback: string = TRACKED_LINK_FALLBACK): string {
  if (typeof location !== 'string' || location.length === 0 || location.length > MAX_LOCATION_CHARS) {
    return fallback;
  }
  if (!location.startsWith('/') || location.startsWith('//')) return fallback;
  if (/[\\\s\u0000-\u001F\u007F]/.test(location)) return fallback;
  return location;
}

/**
 * The only request headers sent upstream: the caller's User-Agent and, when the browser sent them,
 * its request-kind headers (`REQUEST_KIND_HEADERS`); plus the relay's own token when one is
 * configured. Never the caller's cookies, Authorization or address. The list is closed, so any
 * other header the caller sends (its own X-ReelBolt-Site-Token included) is dropped.
 */
export function trackedLinkRequestHeaders(incoming: Headers, siteToken?: string): Headers {
  const out = new Headers();
  const userAgent = incoming.get('user-agent');
  if (userAgent) out.set('user-agent', userAgent.slice(0, MAX_USER_AGENT_CHARS));
  for (const name of REQUEST_KIND_HEADERS) {
    const value = incoming.get(name)?.trim();
    if (value) out.set(name, value.slice(0, MAX_REQUEST_KIND_CHARS));
  }
  const token = siteToken?.trim();
  if (token) out.set(SITE_TOKEN_HEADER, token);
  return out;
}

function redirect(location: string): Response {
  return new Response(null, { status: 302, headers: { location, 'cache-control': 'no-store' } });
}

export interface TrackedLinkInput {
  /** REELBOLT_API_URL. */
  apiUrl: string | undefined;
  /** SITE_FORWARD_TOKEN. Sent upstream as X-ReelBolt-Site-Token when set; never logged. */
  siteToken?: string;
  /** The `[code]` route parameter, as the Pages router hands it over. */
  code: string | string[] | null | undefined;
  request: Request;
  fetchImpl?: FetchLike;
  timeoutMs?: number;
}

/** Handles one `/l/<code>` request. GET and HEAD only; never throws. */
export async function followTrackedLink({
  apiUrl,
  siteToken,
  code: rawCode,
  request,
  fetchImpl = fetch,
  timeoutMs = TRACKED_LINK_TIMEOUT_MS,
}: TrackedLinkInput): Promise<Response> {
  if (request.method !== 'GET' && request.method !== 'HEAD') {
    return new Response(null, { status: 405, headers: { allow: 'GET, HEAD', 'cache-control': 'no-store' } });
  }
  const code = linkCode(rawCode);
  // A code that cannot exist: the bare home page. From here on the fallback names the link.
  if (code === null) return redirect(TRACKED_LINK_FALLBACK);
  const fallback = trackedLinkFallback(code);
  const url = trackedLinkUpstream(apiUrl, code);
  if (url === null) return redirect(fallback);

  let upstream: Response;
  try {
    upstream = await fetchImpl(url, {
      // HEAD stays HEAD: the API counts it as a link checker, not a click.
      method: request.method,
      headers: trackedLinkRequestHeaders(request.headers, siteToken),
      // The redirect is the answer; following it would fetch the landing page instead.
      redirect: 'manual',
      signal: AbortSignal.timeout(timeoutMs),
      // No cookies are sent: a Worker's fetch carries none, and trackedLinkRequestHeaders names the only headers.
    } as RequestInit);
  } catch {
    return redirect(fallback);
  }
  // The body is never used; release the connection.
  await upstream.body?.cancel().catch(() => undefined);
  if (upstream.status !== 302) return redirect(fallback);
  return redirect(relayLocation(upstream.headers.get('location'), fallback));
}

/** Any other path under `/l/` (a deeper path, the bare folder): the home page, as nginx answers it. */
export function trackedLinkHome(): Response {
  return redirect(TRACKED_LINK_FALLBACK);
}
