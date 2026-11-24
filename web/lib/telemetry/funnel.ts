/**
 * The alpha funnel (alpha-launch T-018): `alpha.signup`, `alpha.first_project`, `alpha.first_run`,
 * `alpha.first_export_initiated`, each fired **once per user** on the real transition.
 *
 * Three properties, and each is a test:
 *
 * 1. **On the transition, not on a page load.** `emitFunnelEvent` is called by the code that observes
 *    the transition (a project was created, a run started, an export finished) — never from a bare
 *    effect that runs whenever a page mounts.
 * 2. **Once per user.** A marker in `localStorage`, keyed by event and user id, is written **before**
 *    the send. A re-render, a double click, or a retry after a failed POST therefore cannot fire it
 *    twice: the second call sees the marker and returns. The delivery is at-most-once.
 * 3. **Consent-gated.** With consent withheld (or never given), nothing is sent. See `consent.ts`.
 *
 * Nothing here is ever blocked on consent: the caller does not await anything, and a refusal or a
 * failed send changes no behaviour the person can see.
 */

import { isAlphaFunnelEvent, isRunBlockedDetail, type AlphaFunnelEvent, type RunBlockedDetail } from './allowlist';
import { sendFunnelEvent } from './client';
import { telemetryEnabled } from './config';
import { readTelemetryConsent } from './consent';

const SENT_MARKER_PREFIX = 'rf-funnel-sent:';

/** The `localStorage` key that records that this event already fired for this user. */
export function funnelMarkerKey(event: AlphaFunnelEvent, userId: string, detail?: RunBlockedDetail): string {
  return `${SENT_MARKER_PREFIX}${event}:${userId}${detail ? `:${detail}` : ''}`;
}

/** True when the event has already been claimed for this user. */
export function hasFunnelEventBeenSent(event: AlphaFunnelEvent, userId: string, detail?: RunBlockedDetail): boolean {
  if (typeof window === 'undefined' || !userId) return false;
  try {
    return window.localStorage.getItem(funnelMarkerKey(event, userId, detail)) !== null;
  } catch {
    return false;
  }
}

/**
 * Emits one funnel event for one user, at most once. Returns true only when this call claimed and
 * sent the event; false when it was already sent, when there is no consent, when telemetry is off, or
 * when there is no user to key it to.
 *
 * The marker is written before the send, so the event can never fire twice — the direction to be
 * wrong in for a funnel that must not double-count.
 */
export function emitFunnelEvent(event: AlphaFunnelEvent, userId: string, detail?: RunBlockedDetail): boolean {
  if (typeof window === 'undefined') return false;
  if (!isAlphaFunnelEvent(event) || !userId) return false;
  // `detail` belongs to run_blocked alone, and run_blocked needs one: a mismatch would be refused by the
  // server after the marker was written, losing the event silently.
  if (event === 'alpha.run_blocked' ? !isRunBlockedDetail(detail) : detail !== undefined) return false;
  if (!telemetryEnabled()) return false;
  if (readTelemetryConsent() !== 'granted') return false;
  if (hasFunnelEventBeenSent(event, userId, detail)) return false;

  try {
    window.localStorage.setItem(funnelMarkerKey(event, userId, detail), new Date().toISOString());
  } catch {
    // Storage unavailable: without a marker we cannot promise "once", so we do not send at all
    // rather than risk double-counting the funnel.
    return false;
  }

  void sendFunnelEvent(detail ? { name: event, detail } : { name: event });
  return true;
}

/** Clears every funnel marker (tests, and a deliberate reset). */
export function clearFunnelEventMarkers(): void {
  if (typeof window === 'undefined') return;
  try {
    const keys: string[] = [];
    for (let i = 0; i < window.localStorage.length; i += 1) {
      const key = window.localStorage.key(i);
      if (key && key.startsWith(SENT_MARKER_PREFIX)) keys.push(key);
    }
    keys.forEach((key) => window.localStorage.removeItem(key));
  } catch {
    // Nothing to clear if storage is unavailable.
  }
}
