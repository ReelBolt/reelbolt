/**
 * The one place the app-side telemetry consent value's storage key and reader live (alpha-launch
 * T-018). Both sides of the gate import it — the consent surface (which writes it, and re-renders
 * from it) and the emitters in `funnel.ts` / `errors.ts` (which re-read it at the moment something
 * would be sent). Keeping the read in one module is what stops the two from disagreeing about what
 * "consent granted" means. It mirrors `site/lib/consent.ts`, which covers the marketing site; this
 * one covers `/app`, which had no consent surface before.
 *
 * Deliberately dependency-free and framework-free: the surface is a client component and this module
 * is also read from plain functions, so it must not import React.
 *
 * The consent here is for **client-side telemetry on the device** — the ePrivacy art. 5(3) case —
 * so it is opt-in: an unanswered visitor (also the answer whenever storage is unavailable, e.g.
 * private browsing) has **not** consented.
 *
 * The **decision itself** is reported to the server (product-events-funnel T-005): a granted or
 * declined choice is posted to the ingest endpoint, and `null` sends nothing. This send is
 * deliberately **outside** the consent gate that guards `funnel.ts` / `errors.ts`: it is the one
 * transmission a declining browser still makes, because recording the choice is how the choice is
 * honoured — without it a decliner is indistinguishable from a drop-off. Do not "fix" it by adding a
 * consent check here; that would erase the very fact the funnel needs.
 *
 * The **device stays authoritative**: the decision is posted only when the choice was actually
 * stored, so we never tell the server a decision this device could not remember. When storage is
 * unavailable (private browsing, storage disabled, quota) nothing is sent, and the server learns
 * nothing — the same direction as the missing `localStorage` value.
 */

import { sendTelemetryConsentDecision } from './client';

export type TelemetryConsentValue = 'granted' | 'denied' | null;

export const TELEMETRY_CONSENT_STORAGE_KEY = 'rf-app-telemetry-consent';

const listeners = new Set<() => void>();

/** The stored choice, or `null` when none has been made (and whenever storage is unavailable). */
export function readTelemetryConsent(): TelemetryConsentValue {
  if (typeof window === 'undefined') return null;
  try {
    const stored = window.localStorage.getItem(TELEMETRY_CONSENT_STORAGE_KEY);
    return stored === 'granted' || stored === 'denied' ? stored : null;
  } catch {
    return null;
  }
}

/**
 * Writes the choice, reports it to the server, and notifies every subscriber in this tab.
 *
 * A granted or declined choice is posted (see the module doc comment for why a decline is sent too);
 * `null` sends nothing. The send happens **only when the choice was stored** — if the device could
 * not remember the choice, we do not claim it to the server either.
 */
export function writeTelemetryConsent(value: TelemetryConsentValue): void {
  let stored = false;
  try {
    if (value === null) window.localStorage.removeItem(TELEMETRY_CONSENT_STORAGE_KEY);
    else window.localStorage.setItem(TELEMETRY_CONSENT_STORAGE_KEY, value);
    stored = true;
  } catch {
    // A failed write means no choice is found later and nothing is sent — the safe direction.
  }
  if (value !== null && stored) void sendTelemetryConsentDecision(value);
  notify();
}

/** Clears the choice, so the consent surface asks again. */
export function resetTelemetryConsent(): void {
  writeTelemetryConsent(null);
}

/**
 * Subscribes to consent changes: this tab's own writes, and other tabs' (the `storage` event).
 * Returns an unsubscribe function.
 */
export function subscribeTelemetryConsent(callback: () => void): () => void {
  if (typeof window === 'undefined') return () => {};
  listeners.add(callback);
  window.addEventListener('storage', callback);
  return () => {
    listeners.delete(callback);
    window.removeEventListener('storage', callback);
  };
}

function notify(): void {
  listeners.forEach((listener) => listener());
}
