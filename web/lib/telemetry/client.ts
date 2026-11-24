/**
 * The transport for client telemetry (alpha-launch T-018). A first-party endpoint on the Inference
 * API — the E11 runner precedent's "first-party endpoint, no third-party SDK that phones home on its
 * own". Every call is best-effort: a failure is swallowed, because telemetry must never block,
 * interrupt or surface to the person using the product.
 */

import type {
  ClientErrorPayload,
  ConsentDecisionPayload,
  FunnelEventPayload,
  TelemetryEventPayload,
} from './allowlist';

export const TELEMETRY_EVENTS_PATH = '/api/v1/telemetry/events';
export const TELEMETRY_ERRORS_PATH = '/api/v1/telemetry/errors';

export interface ClientEventsBody {
  events: TelemetryEventPayload[];
}

/**
 * Posts a telemetry body. Never throws and never rejects: a network failure, a 404 (the deployment
 * has not opted in) or a 500 all end the same way — the report is dropped and the app carries on.
 * `keepalive` lets a report survive a navigation that is already under way.
 */
export async function sendTelemetry(path: string, body: unknown): Promise<void> {
  try {
    await fetch(path, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
      keepalive: true,
    });
  } catch {
    // Best effort. Nothing about telemetry may reach the person using the product.
  }
}

/** Convenience wrapper for the funnel endpoint. */
export function sendFunnelEvent(payload: FunnelEventPayload): Promise<void> {
  return sendTelemetry(TELEMETRY_EVENTS_PATH, { events: [payload] } satisfies ClientEventsBody);
}

/**
 * Delivers the telemetry consent decision itself, to the same events endpoint. This is the one send
 * a **declining** browser still makes: the choice is recorded so the server can tell a deliberate
 * absence apart from a real drop-off. It is therefore deliberately **outside** the consent gate that
 * guards `funnel.ts` / `errors.ts` — recording the choice is how the choice is honoured. Best-effort
 * like every other send; a failure is swallowed.
 */
export function sendTelemetryConsentDecision(value: 'granted' | 'denied'): Promise<void> {
  const payload: ConsentDecisionPayload = {
    name: value === 'granted' ? 'telemetry.consent.granted' : 'telemetry.consent.declined',
  };
  return sendTelemetry(TELEMETRY_EVENTS_PATH, { events: [payload] } satisfies ClientEventsBody);
}

/** Convenience wrapper for the error endpoint. */
export function sendClientError(payload: ClientErrorPayload): Promise<void> {
  return sendTelemetry(TELEMETRY_ERRORS_PATH, payload);
}
