/**
 * Which Google/GitHub sign-in buttons to show, asked of the Go API itself (C10).
 *
 * The API is the authority: its start/callback endpoints answer 404 for a provider whose client id
 * and secret are not configured, and `GET /api/v1/auth/oauth/providers` lists exactly the ones that
 * are. Asking it means a button exists precisely when the sign-in behind it works, so turning
 * Google or GitHub on is one step (set the credentials) instead of two that can disagree.
 *
 * When the API cannot be asked, the pages fall back to the display flags
 * (`OAUTH_GOOGLE_ENABLED`/`OAUTH_GITHUB_ENABLED`, read by `authPageConfig`), which is what they did
 * before this endpoint existed.
 */

/** What the API says about provider sign-in on this deployment. */
export interface OfferedOAuth {
  /** Providers with credentials configured, in display order. */
  providers: string[];
  /** Whether a provider sign-in may create a NEW account (it may only when signup is open). */
  createsAccount: boolean;
}

/** Where the Go API is. Compose sets `GO_API_URL` on this service. */
export function goApiBaseUrl(env: Record<string, string | undefined> = process.env): string {
  return (env.GO_API_URL || 'http://go-api:8080').trim().replace(/\/+$/, '');
}

export const OFFERED_OAUTH_TTL_MS = 30_000;
export const OFFERED_OAUTH_TIMEOUT_MS = 2_000;

type Fetcher = (input: string, init?: RequestInit) => Promise<Response>;

let cache: { at: number; value: OfferedOAuth | null } | null = null;

/** Drops the cache. Tests use it. */
export function resetOfferedOAuthCache(): void {
  cache = null;
}

const KNOWN = new Set(['google', 'github']);

/**
 * The providers to offer, or null when the API could not be asked (the caller then uses the env
 * flags). Cached for {@link OFFERED_OAUTH_TTL_MS}, so it stays off the hot path of every page view.
 */
export async function offeredOAuth(options?: {
  fetcher?: Fetcher;
  env?: Record<string, string | undefined>;
  now?: () => number;
}): Promise<OfferedOAuth | null> {
  const now = options?.now ?? Date.now;
  const at = now();
  if (cache && at - cache.at < OFFERED_OAUTH_TTL_MS) return cache.value;

  const fetcher = options?.fetcher ?? fetch;
  let value: OfferedOAuth | null = null;
  try {
    const response = await fetcher(`${goApiBaseUrl(options?.env)}/api/v1/auth/oauth/providers`, {
      headers: { accept: 'application/json' },
      cache: 'no-store',
      signal: AbortSignal.timeout(OFFERED_OAUTH_TIMEOUT_MS),
    });
    if (response.ok) {
      const body = (await response.json()) as { providers?: unknown; createsAccount?: unknown };
      const providers = Array.isArray(body.providers)
        ? body.providers.filter((p): p is string => typeof p === 'string' && KNOWN.has(p))
        : [];
      value = { providers, createsAccount: body.createsAccount === true };
    }
  } catch {
    value = null;
  }
  cache = { at, value };
  return value;
}
