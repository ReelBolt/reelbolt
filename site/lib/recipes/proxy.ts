// The same-origin recipe proxy, written once and used by both twins: the Node route handlers under
// app/api/recipes (docker) and the Pages Functions under functions/api/recipes (static export). Change one,
// change both: they only differ in how they cache (Cache API on Pages, none on Node).
//
// Rules: GET and HEAD only; only the allowlisted query parameters are forwarded, each capped; NO cookies and
// NO Authorization header go upstream; Range and If-* pass through for media; every upstream failure, an
// unset REELBOLT_API_URL or an unexpected answer is an empty list (list, facets) or a bare 404 (everything
// else). Neither a stack nor the upstream URL ever reaches the browser.
import { isRecipeId, parseDetail, parseFacets, parseList, type RecipeFacets, type RecipeList } from './types.ts';

export type RecipeKind = 'list' | 'facets' | 'detail' | 'preview' | 'poster';

/** Cache lifetime of list, facets and detail answers, in seconds. */
export const RECIPES_CACHE_SECONDS = 60;
export const RECIPES_CACHE_CONTROL = `public, max-age=${RECIPES_CACHE_SECONDS}`;
const MEDIA_DEFAULT_CACHE_CONTROL = 'public, max-age=3600';
const UPSTREAM_TIMEOUT_MS = 8000;

const SORTS = ['new', 'copied', 'rated'] as const;
const KEY = /^[A-Za-z0-9_.:-]{1,64}$/;
// The cost classes are words ("Uses credits and a voice"), so spaces are allowed there.
const COST = /^[A-Za-z0-9_.: -]{1,64}$/;
const CURSOR = /^[A-Za-z0-9_\-=.~+/]{1,512}$/;
export const MAX_QUERY_CHARS = 100;
export const MAX_LIMIT = 50;
export const DEFAULT_LIMIT = 24;

/** The upstream origin, or null when unset or not an http(s) URL. A trailing slash is dropped. */
export function upstreamBase(apiUrl: string | undefined): string | null {
  const raw = (apiUrl ?? '').trim();
  if (raw === '') return null;
  try {
    const url = new URL(raw);
    if (url.protocol !== 'https:' && url.protocol !== 'http:') return null;
    return `${url.origin}${url.pathname.replace(/\/+$/, '')}`;
  } catch {
    return null;
  }
}

/**
 * The query parameters that may go upstream (q, category, cost, stepType, language, sort, cursor, limit),
 * each validated and capped. Anything else, and any value that fails its check, is dropped. The order is
 * fixed, so the result doubles as a cache key.
 */
export function buildListQuery(input: URLSearchParams): URLSearchParams {
  const out = new URLSearchParams();
  const q = (input.get('q') ?? '').replace(/[\u0000-\u001F\u007F]/g, ' ').trim();
  if (q !== '') out.set('q', Array.from(q).slice(0, MAX_QUERY_CHARS).join(''));
  for (const name of ['category', 'cost', 'stepType', 'language'] as const) {
    const value = input.get(name);
    if (value !== null && (name === 'cost' ? COST : KEY).test(value)) out.set(name, value);
  }
  const sort = input.get('sort');
  if (sort !== null && (SORTS as readonly string[]).includes(sort)) out.set('sort', sort);
  const cursor = input.get('cursor');
  if (cursor !== null && CURSOR.test(cursor)) out.set('cursor', cursor);
  const limit = input.get('limit');
  if (limit !== null && /^\d{1,3}$/.test(limit)) {
    const n = Number(limit);
    if (n >= 1) out.set('limit', String(Math.min(n, MAX_LIMIT)));
  }
  return out;
}

/** The upstream URL for one proxied route, or null when it cannot be built (unset base, bad id). */
export function upstreamUrl(
  apiUrl: string | undefined,
  kind: RecipeKind,
  id?: string,
  search?: URLSearchParams,
): string | null {
  const base = upstreamBase(apiUrl);
  if (base === null) return null;
  const root = `${base}/api/v1/public/marketplace`;
  if (kind === 'facets') return `${root}/facets`;
  if (kind === 'list') {
    const query = buildListQuery(search ?? new URLSearchParams()).toString();
    return `${root}/recipes${query === '' ? '' : `?${query}`}`;
  }
  if (!isRecipeId(id)) return null;
  const tail = kind === 'detail' ? '' : `/${kind}`;
  return `${root}/recipes/${encodeURIComponent(id)}${tail}`;
}

/** Same-origin cache key for a list or facets request: path plus the sanitised query, nothing else. */
export function recipesCacheKey(requestUrl: string, kind: RecipeKind): string {
  const url = new URL(requestUrl);
  const query = kind === 'list' ? buildListQuery(url.searchParams).toString() : '';
  return `${url.origin}${url.pathname}${query === '' ? '' : `?${query}`}`;
}

const FORWARD_REQUEST_HEADERS = ['range', 'if-range', 'if-none-match', 'if-modified-since'];

/** The only request headers sent upstream: Range and If-* for media, an Accept for JSON. Never cookies or Authorization. */
export function forwardRequestHeaders(incoming: Headers, media: boolean): Headers {
  const out = new Headers();
  if (!media) {
    out.set('accept', 'application/json');
    return out;
  }
  for (const name of FORWARD_REQUEST_HEADERS) {
    const value = incoming.get(name);
    if (value !== null && value.length <= 256) out.set(name, value);
  }
  return out;
}

const PASS_RESPONSE_HEADERS = [
  'content-type',
  'content-length',
  'content-range',
  'accept-ranges',
  'cache-control',
  'etag',
  'last-modified',
];

/** The only response headers handed back for media: nothing upstream-identifying (no Set-Cookie, Server, Via). */
export function passthroughResponseHeaders(upstream: Headers): Headers {
  const out = new Headers();
  for (const name of PASS_RESPONSE_HEADERS) {
    const value = upstream.get(name);
    if (value !== null) out.set(name, value);
  }
  if (!out.has('cache-control')) out.set('cache-control', MEDIA_DEFAULT_CACHE_CONTROL);
  out.set('x-content-type-options', 'nosniff');
  return out;
}

export function bareNotFound(): Response {
  return new Response(null, { status: 404, headers: { 'cache-control': 'no-store' } });
}

export function methodNotAllowed(): Response {
  return new Response(null, { status: 405, headers: { allow: 'GET, HEAD' } });
}

export const EMPTY_LIST: RecipeList = { items: [], nextCursor: null };
export const EMPTY_FACETS: RecipeFacets = { costClasses: [], categories: [], stepTypes: [], languages: [] };

export type FetchLike = (input: string, init?: RequestInit) => Promise<Response>;

export interface ProxyInput {
  /** REELBOLT_API_URL. */
  apiUrl: string | undefined;
  kind: RecipeKind;
  /** Recipe id, for detail, preview and poster. */
  id?: string;
  request: Request;
  fetchImpl?: FetchLike;
}

export interface ProxyResult {
  response: Response;
  /** True when this is a real upstream answer worth caching (never an empty fallback or a 404). */
  cacheable: boolean;
}

const MEDIA_TYPE: Record<'preview' | 'poster', RegExp> = {
  preview: /^video\/mp4\b/i,
  poster: /^image\/jpeg\b/i,
};

async function callUpstream(url: string, headers: Headers, method: string, fetchImpl: FetchLike): Promise<Response | null> {
  try {
    return await fetchImpl(url, {
      method,
      headers,
      redirect: 'manual',
      signal: AbortSignal.timeout(UPSTREAM_TIMEOUT_MS),
      // Never forward cookies or ambient credentials.
      credentials: 'omit',
    } as RequestInit);
  } catch {
    return null;
  }
}

function jsonResponse(body: unknown, request: Request, cacheControl: string): Response {
  return new Response(request.method === 'HEAD' ? null : JSON.stringify(body), {
    headers: { 'content-type': 'application/json; charset=utf-8', 'cache-control': cacheControl },
  });
}

/** Handles one proxied request for either twin. Never throws. */
export async function proxyRecipes({ apiUrl, kind, id, request, fetchImpl = fetch }: ProxyInput): Promise<ProxyResult> {
  if (request.method !== 'GET' && request.method !== 'HEAD') return { response: methodNotAllowed(), cacheable: false };
  const searchParams = new URL(request.url).searchParams;
  const url = upstreamUrl(apiUrl, kind, id, searchParams);
  const jsonKind = kind === 'list' || kind === 'facets';
  const empty = (): ProxyResult => ({
    response:
      kind === 'list'
        ? jsonResponse(EMPTY_LIST, request, 'no-store')
        : kind === 'facets'
          ? jsonResponse(EMPTY_FACETS, request, 'no-store')
          : bareNotFound(),
    cacheable: false,
  });
  if (url === null) return empty();

  if (kind === 'preview' || kind === 'poster') {
    const upstream = await callUpstream(url, forwardRequestHeaders(request.headers, true), request.method, fetchImpl);
    if (upstream === null) return empty();
    const { status } = upstream;
    if (status === 304) return { response: new Response(null, { status, headers: passthroughResponseHeaders(upstream.headers) }), cacheable: false };
    if (status !== 200 && status !== 206) return empty();
    if (!MEDIA_TYPE[kind].test(upstream.headers.get('content-type') ?? '')) return empty();
    // The body is streamed straight through (no buffering), so a Range answer stays a Range answer.
    return {
      response: new Response(request.method === 'HEAD' ? null : upstream.body, {
        status,
        headers: passthroughResponseHeaders(upstream.headers),
      }),
      cacheable: false,
    };
  }

  const upstream = await callUpstream(url, forwardRequestHeaders(request.headers, false), request.method === 'HEAD' ? 'GET' : request.method, fetchImpl);
  if (upstream === null || upstream.status !== 200) return empty();
  let parsed: unknown;
  try {
    parsed = await upstream.json();
  } catch {
    return empty();
  }
  if (jsonKind) {
    const body = kind === 'list' ? parseList(parsed) : parseFacets(parsed);
    return { response: jsonResponse(body, request, RECIPES_CACHE_CONTROL), cacheable: true };
  }
  // Detail: re-serialised through the parser, so only the contract's fields ever reach the browser.
  const detail = parseDetail(parsed);
  if (detail === null) return empty();
  return { response: jsonResponse(detail, request, RECIPES_CACHE_CONTROL), cacheable: true };
}
