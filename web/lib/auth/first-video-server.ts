import { legalApiBaseUrl } from './legal-server';

/**
 * Whether the first-video credit grant is really on for this deployment, so the signup page can
 * promise it only when it is true (a self-hosted default makes no promise).
 *
 * The Inference API answers `GET /api/v1/billing/promotions/first-video` anonymously with
 * `{"enabled": true|false}`, and 404 when billing is off. Only a 200 with `enabled === true` is a
 * yes; every other outcome (404, error, timeout, bad JSON) is a no.
 */

export const FIRST_VIDEO_TTL_MS = 30_000;
export const FIRST_VIDEO_TIMEOUT_MS = 2_000;

type Fetcher = (input: string, init?: RequestInit) => Promise<Response>;

type Options = {
  fetcher?: Fetcher;
  env?: Record<string, string | undefined>;
  now?: () => number;
  ttlMs?: number;
  timeoutMs?: number;
};

// Module-level on purpose, like legal-server.ts: best-effort, never outlives the TTL.
let cache: { at: number; value: boolean } | null = null;

/** Drops the cache. Tests use it. */
export function resetFirstVideoCache(): void {
  cache = null;
}

export async function firstVideoGrantOffered(options?: Options): Promise<boolean> {
  const now = options?.now ?? Date.now;
  const ttlMs = options?.ttlMs ?? FIRST_VIDEO_TTL_MS;
  const at = now();
  if (cache && at - cache.at < ttlMs) return cache.value;

  const value = await read(options);
  cache = { at, value };
  return value;
}

async function read(options?: Options): Promise<boolean> {
  const fetcher = options?.fetcher ?? fetch;
  const timeoutMs = options?.timeoutMs ?? FIRST_VIDEO_TIMEOUT_MS;
  const url = `${legalApiBaseUrl(options?.env)}/api/v1/billing/promotions/first-video`;
  try {
    const response = await fetcher(url, {
      headers: { accept: 'application/json' },
      cache: 'no-store',
      signal: AbortSignal.timeout(timeoutMs),
    });
    if (response.status !== 200) return false;
    const body: unknown = await response.json();
    return typeof body === 'object' && body !== null && (body as { enabled?: unknown }).enabled === true;
  } catch {
    return false;
  }
}
