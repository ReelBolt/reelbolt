// Same-origin cookie-consent value written by the marketing site (key shared on purpose).
export type ConsentValue = 'granted' | 'denied' | null;

export const CONSENT_STORAGE_KEY = 'rf-cookie-consent';

export function readStoredConsent(): ConsentValue {
  try {
    const stored = window.localStorage.getItem(CONSENT_STORAGE_KEY);
    return stored === 'granted' || stored === 'denied' ? stored : null;
  } catch {
    return null;
  }
}
