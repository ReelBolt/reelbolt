// The one place the consent value's storage key and its reader live. It is imported by both
// sides of the gate: `CookieConsentProvider` (which writes it, and re-renders from it) and
// `lib/gtag.ts` (which re-reads it at the moment an event would be sent). Keeping the read in one
// module is what stops the two from disagreeing about what "consent granted" means.
//
// Deliberately dependency-free and framework-free: the provider is a client component and this
// module is also read from plain functions, so it must not import React.

export type ConsentValue = 'granted' | 'denied' | null;

export const CONSENT_STORAGE_KEY = 'rf-cookie-consent';

/**
 * The visitor's stored choice, or `null` when they have not made one (also the answer whenever
 * storage is unavailable — private browsing, storage disabled — which is why every caller treats
 * `null` as "no consent" rather than as an error).
 */
export function readStoredConsent(): ConsentValue {
  try {
    const stored = window.localStorage.getItem(CONSENT_STORAGE_KEY);
    return stored === 'granted' || stored === 'denied' ? stored : null;
  } catch {
    return null;
  }
}
