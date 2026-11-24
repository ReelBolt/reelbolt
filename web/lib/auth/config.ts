export interface AuthPageConfig {
  /** Whether this deployment takes self-serve sign-ups at all (Go `SIGNUP_ENABLED`, default false). */
  signupEnabled: boolean;
  /** Cloudflare Turnstile site key; empty when the challenge is not configured. */
  turnstileSiteKey: string;
  /**
   * The OAuth providers to offer buttons for (C10), in display order. A *display* switch like
   * signupEnabled: the Go endpoints answer 404 for a provider whose client id and secret are not
   * configured on the API, whatever this returns.
   */
  oauthProviders: string[];
}

/**
 * What the signup-dependent pages render, read from the **server** environment at request time.
 *
 * Runtime rather than build time: `web` ships as a precompiled Next standalone server, so a
 * `NEXT_PUBLIC_*` value would have to be baked into the image (and the Dockerfile has no build
 * args for one). Reading plain `process.env` per request is the same pattern `/api/auth/me`
 * already uses for `JWT_SIGNING_KEY`, and it means flipping `SIGNUP_ENABLED` is a container
 * restart rather than a rebuild. Both spellings are accepted, so a dev shell or a baked build
 * that only sets the `NEXT_PUBLIC_` form still works.
 *
 * The `NEXT_PUBLIC_` fallback only matters for the site key and the flag, which are not secrets.
 * `TURNSTILE_SECRET` stays server-side in Go and must never reach this file.
 *
 * The two legal versions used to be part of this shape, as build-time constants. They are not
 * configuration any more: they live in the `legal_documents` table and are read at runtime through
 * `legal-server.ts` (F-7).
 *
 * This is a *display* switch, never an authorization decision: the Go handler answers 404 to
 * `POST /api/v1/auth/signup` unless `SIGNUP_ENABLED` is set on the API, whatever this returns.
 */
export function authPageConfig(env: Record<string, string | undefined> = process.env): AuthPageConfig {
  return {
    signupEnabled: env.SIGNUP_ENABLED === 'true' || env.NEXT_PUBLIC_SIGNUP_ENABLED === 'true',
    turnstileSiteKey: (env.TURNSTILE_SITE_KEY || env.NEXT_PUBLIC_TURNSTILE_SITE_KEY || '').trim(),
    oauthProviders: oauthProvidersFromEnv(env),
  };
}

/**
 * The providers whose `OAUTH_*_ENABLED` flag is "true", in the order the buttons should appear.
 * Both spellings are accepted, like `SIGNUP_ENABLED`, so a dev shell or a baked build that only
 * sets the `NEXT_PUBLIC_` form still works.
 */
function oauthProvidersFromEnv(env: Record<string, string | undefined>): string[] {
  const enabled = (name: string) =>
    env[`OAUTH_${name}_ENABLED`] === 'true' || env[`NEXT_PUBLIC_OAUTH_${name}_ENABLED`] === 'true';
  return [
    ['google', 'GOOGLE'],
    ['github', 'GITHUB'],
  ]
    .filter(([, envName]) => enabled(envName))
    .map(([provider]) => provider);
}

/** A repeated `?token=` is never legitimate here; anything but a single string is "absent". */
export function singleParam(value: string | string[] | undefined): string {
  return typeof value === 'string' ? value : '';
}
