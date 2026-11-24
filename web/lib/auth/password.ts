/**
 * The Go API's password rules (api/services/token_service.go: MinPasswordLength = 10,
 * MaxPasswordBytes = 72), mirrored so the form refuses what the API would refuse instead of
 * spending a round trip. Go measures BOTH bounds in bytes (`len(p)`), so these do too: a
 * character count would accept a string the API rejects, and refuse one it accepts.
 *
 * Client-side only — the API re-validates every set path regardless of what this says.
 */
import { enT } from '@/lib/i18n-default';
import type { T } from '@/i18n/translator';

export const MIN_PASSWORD_LENGTH = 10;
export const MAX_PASSWORD_BYTES = 72;

function byteLength(value: string): number {
  return new TextEncoder().encode(value).length;
}

/**
 * Returns the first rule the password breaks, or null when it passes. Pass the component's `useT()`
 * for the active language; without it the message is English.
 */
export function validatePassword(password: string, t: T = enT): string | null {
  if (byteLength(password) < MIN_PASSWORD_LENGTH) {
    return t('libLabels.auth.passwordMin', { count: MIN_PASSWORD_LENGTH });
  }
  if (byteLength(password) > MAX_PASSWORD_BYTES) {
    return t('libLabels.auth.passwordMax', { count: MAX_PASSWORD_BYTES });
  }
  return null;
}
