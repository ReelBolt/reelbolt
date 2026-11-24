/**
 * Plain-language wording for the account's data and deletion screens. Pure functions, so the login
 * page (a server component) and the settings cards (client components) say the same thing.
 */
import { msg, type Msg } from '@/i18n/msg';
import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';

/** The number of days between asking to delete an account and the deletion (api/handlers/auth/account.go). */
export const DELETION_GRACE_DAYS = 30;

/**
 * "10 November 2026" ("10 novembre 2026" in Italian) for an ISO date, or null when the value is missing or not a date. The day is
 * read in UTC because that is how the server computes it, and it is the date the email names.
 */
export function formatDeletionDate(iso: string | null | undefined, locale: string = 'en-GB'): string | null {
  if (!iso) return null;
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return null;
  // `locale` is the active UI language (`useFormat().locale` / `getFormat().locale`); the English default is en-GB.
  return date.toLocaleDateString(locale === 'en' || locale === 'en-XA' ? 'en-GB' : locale, { day: 'numeric', month: 'long', year: 'numeric', timeZone: 'UTC' });
}

/** "in 17 hours", "in 1 hour", "in 12 minutes" — how long until another copy can be requested. */
export function formatWait(seconds: number, t: T = enT): string {
  if (seconds < 3600) return t('libLabels.account.waitMinutes', { count: Math.max(1, Math.ceil(seconds / 60)) });
  return t('libLabels.account.waitHours', { count: Math.ceil(seconds / 3600) });
}

/** The provider's name as people know it, for a provider id the server sent ("google", "github"). */
export function providerDisplayName(provider: string, t: T = enT): string {
  switch (provider.trim().toLowerCase()) {
    case 'google':
      return 'Google';
    case 'github':
      return 'GitHub';
    default:
      return provider.trim() || t('libLabels.account.providerFallback');
  }
}

/** The notice a sign-in that cancelled a pending deletion shows. Components: resolve the Msg versions. */
export const DELETION_CANCELLED_TITLE = enT('libLabels.account.deletionCancelledTitle');
export const DELETION_CANCELLED_MESSAGE = enT('libLabels.account.deletionCancelledMessage');
export const DELETION_CANCELLED_TITLE_MSG: Msg = msg('libLabels.account.deletionCancelledTitle');
export const DELETION_CANCELLED_MESSAGE_MSG: Msg = msg('libLabels.account.deletionCancelledMessage');
