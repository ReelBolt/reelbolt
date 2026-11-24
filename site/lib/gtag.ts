import { readStoredConsent } from '@/lib/consent';

/**
 * Fire a GA4 event, but only for a visitor who has actually granted consent.
 *
 * With Google Consent Mode the Google tag is on every page and `window.gtag` always exists, so
 * the check that matters is the second one: the stored choice must be `'granted'`. Before a
 * choice the tag still sends its own cookieless page-view ping (see `lib/ga-consent.ts`), and
 * after a rejection nothing at all, but this site adds no custom event on top of that. Re-reading the stored
 * value at the moment of the call, from the same module `CookieConsentProvider` renders from,
 * also covers a change of mind on the same page: accept, then reopen the banner from the
 * footer's "Cookie preferences" and reject.
 *
 * Safe to call from anywhere: it no-ops on the server, with no consent, and with no measurement ID
 * configured (no tag, so no `window.gtag`). See docs/marketing-site.md, "Cookie and consent: what
 * this site actually sets".
 */
export function trackEvent(name: string, params?: Record<string, unknown>): void {
  if (typeof window === 'undefined') return;
  if (readStoredConsent() !== 'granted') return;
  window.gtag?.('event', name, params);
}
