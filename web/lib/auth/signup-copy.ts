import { enT } from '@/lib/i18n-default';
import type { T } from '@/i18n/translator';

/** Where a visitor who cannot sign up (shared network, lost mail) can reach a person. */
export const SUPPORT_EMAIL = 'support@reelbolt.ai';

/** "about 1 hour", "about 20 minutes": the time a visitor waits when sign-ups are rate limited. */
export function describeWait(seconds: number | undefined, t: T = enT): string {
  if (!seconds || seconds <= 0) return t('libLabels.auth.wait.short');
  if (seconds < 90) return t('libLabels.auth.wait.minute');
  const minutes = Math.ceil(seconds / 60);
  if (minutes < 60) return t('libLabels.auth.wait.minutes', { count: minutes });
  const hours = Math.ceil(seconds / 3600);
  if (hours >= 24) return t('libLabels.auth.wait.day');
  return hours === 1 ? t('libLabels.auth.wait.hour') : t('libLabels.auth.wait.hours', { count: hours });
}

/**
 * The friendly refusal shown when the per-network sign-up limit is hit (A01). Pass `useT()` for the
 * active language; without it the English sentence is returned.
 */
export function signupRateLimitMessage(retryAfterSeconds: number | undefined, t: T = enT): string {
  return t('libLabels.auth.signupRateLimit', { wait: describeWait(retryAfterSeconds, t), email: SUPPORT_EMAIL });
}
