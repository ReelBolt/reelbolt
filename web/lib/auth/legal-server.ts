import { parseLegalVersions, type LegalVersions } from './legal';

/**
 * The versions of the legal documents this deployment is serving, read from the service that owns
 * them (F-7).
 *
 * `legal_documents` belongs to the Inference API, which also publishes new versions through
 * `PUT /api/v1/legal/documents/{document}`. It answers `GET /api/v1/legal/documents` anonymously,
 * which is what makes this usable from `proxy.ts`: the gate has to decide whether a session is
 * behind BEFORE any request has been authorized, so it cannot use the caller's own session to read
 * it.
 *
 * Every failure here is reported as `null` and every caller treats `null` as "cannot compare" —
 * see `proxy.ts` (no redirect) and the auth pages (they render without a version, and the API
 * refuses the submission until they have one). Guessing a version here would be the F-7 bug again,
 * one layer up.
 */

/** Where the Inference API is. Compose sets `INFERENCE_URL` on this service, mirroring the Go API. */
export function legalApiBaseUrl(env: Record<string, string | undefined> = process.env): string {
  return (env.INFERENCE_URL || 'http://inference:8080').trim().replace(/\/+$/, '');
}

/** How long one answer is reused. A publication therefore takes effect within this window. */
export const SERVED_VERSIONS_TTL_MS = 30_000;

/** How long a single read may take before it is abandoned, so a hung API cannot stall a page load. */
export const SERVED_VERSIONS_TIMEOUT_MS = 2_000;

type Fetcher = (input: string, init?: RequestInit) => Promise<Response>;

// Module-level, deliberately: this is a self-hosted `next start` server, where the proxy runs in
// one long-lived Node process, and the cache is exactly what keeps this off the hot path of every
// navigation. It is best-effort — a cold process simply re-reads — and it never outlives the TTL,
// so a published version is live for every gated request within SERVED_VERSIONS_TTL_MS.
let cache: { at: number; value: LegalVersions | null } | null = null;

/** Drops the cache. Tests use it; nothing in the request path needs it. */
export function resetServedVersionsCache(): void {
  cache = null;
}

/**
 * The versions the deployment currently serves, or null when it cannot be asked or will not answer
 * with both. `now` and `fetcher` are injectable so the cache and the failure paths are testable
 * without a server.
 */
export async function servedLegalVersions(options?: {
  fetcher?: Fetcher;
  env?: Record<string, string | undefined>;
  now?: () => number;
  ttlMs?: number;
  timeoutMs?: number;
}): Promise<LegalVersions | null> {
  const now = options?.now ?? Date.now;
  const ttlMs = options?.ttlMs ?? SERVED_VERSIONS_TTL_MS;
  const at = now();

  if (cache && at - cache.at < ttlMs) return cache.value;

  const value = await read(options);
  cache = { at, value };
  return value;
}

async function read(options?: {
  fetcher?: Fetcher;
  env?: Record<string, string | undefined>;
  timeoutMs?: number;
}): Promise<LegalVersions | null> {
  const fetcher = options?.fetcher ?? fetch;
  const timeoutMs = options?.timeoutMs ?? SERVED_VERSIONS_TIMEOUT_MS;
  const url = `${legalApiBaseUrl(options?.env)}/api/v1/legal/documents`;

  try {
    const response = await fetcher(url, {
      headers: { accept: 'application/json' },
      // Never cached by the platform: this function owns the caching, and a second, invisible one
      // would make the effective TTL depend on a header nobody here sets.
      cache: 'no-store',
      signal: AbortSignal.timeout(timeoutMs),
    });
    if (!response.ok) return null;
    // The API answers 503 with a JSON body when a document has no row; only a 2xx is parsed.
    return parseLegalVersions(await response.json());
  } catch {
    // Unreachable, timed out, or not JSON. "No answer" is not "no version", and it is never
    // allowed to become a comparison.
    return null;
  }
}
