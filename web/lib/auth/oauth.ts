/**
 * OAuth sign-in display helpers (C10). The Go API is the authority: a provider answers 404 unless
 * its client id and secret are configured there. Which buttons to show is asked of the API itself
 * (`oauth-server.ts`); `OAUTH_GOOGLE_ENABLED`/`OAUTH_GITHUB_ENABLED` are only the fallback for when
 * it cannot be asked.
 */

import { enT } from '@/lib/i18n-default';
import type { T } from '@/i18n/translator';

const KNOWN_PROVIDERS = new Set(['google', 'github']);

/**
 * The label for a provider button, falling back to a humanised id for an unknown one. Pass the
 * component's `useT()` for the active language; without it the English label is returned.
 */
export function oauthProviderLabel(provider: string, t: T = enT): string {
  return KNOWN_PROVIDERS.has(provider) ? t(`libLabels.auth.oauthProvider.${provider}`) : t('libLabels.auth.oauthProvider.other', { provider });
}

const OAUTH_REASONS = new Set([
  'oauth_email_unverified',
  'oauth_no_account',
  'oauth_account_unverified',
  'oauth_rate_limited',
  'oauth_delete_blocked',
  'oauth_no_workspace',
  'oauth_failed',
]);

/**
 * The reason codes the Go OAuth callback can send back as `?error=` on /login. Anything else maps
 * to the generic sentence, so a new server-side code can never render an empty notice. Pass `useT()`
 * (or `getT()`) for the active language; without it the English sentence is returned.
 */
export function oauthErrorNotice(reason: string, t: T = enT): string {
  return t(`libLabels.auth.oauthError.${OAUTH_REASONS.has(reason) ? reason : 'oauth_failed'}`);
}
