/**
 * The payload preview (alpha-launch T-018): the exact JSON a send would carry, rendered to the
 * person before they opt in. This is the E11 runner precedent — "asked once, in plain words, showing
 * what would be sent" — applied to the browser.
 *
 * The preview is built from the same `allowlist.ts` builders the real sends use, so what is shown and
 * what is sent can never drift.
 */

import {
  buildClientErrorPayload,
  type AlphaFunnelEvent,
  type ClientErrorPayload,
  type ConsentDecisionPayload,
  type FunnelEventPayload,
} from './allowlist';

/** A sample funnel event payload, exactly as it would be posted. */
export function previewFunnelEventPayload(event: AlphaFunnelEvent = 'alpha.signup'): FunnelEventPayload {
  return { name: event };
}

/**
 * The consent-decision payloads, one per possible choice. Both are shown, because either answer sends
 * exactly one of these and the decision itself is the one thing a declining browser still sends.
 */
export function previewConsentDecisionPayloads(): ConsentDecisionPayload[] {
  return [{ name: 'telemetry.consent.granted' }, { name: 'telemetry.consent.declined' }];
}

/**
 * A representative error payload, with a placeholder where a real error's own type would go. The
 * keys are the allowlist, so the preview names every field that is ever collected — and shows that
 * the error's message and stack are not among them.
 */
export function previewClientErrorPayload(): ClientErrorPayload {
  return buildClientErrorPayload({
    name: 'TypeError',
    route: '/app/projects/2f0d5b7c-1a34-4c2e-9f10-8b6a1d2e3f40?tab=recent',
  });
}

/** The exact JSON of a sample payload, for display. */
export function previewTelemetryPayload(kind: 'funnel' | 'error' | 'consent'): string {
  if (kind === 'consent') return JSON.stringify(previewConsentDecisionPayloads(), null, 2);
  return JSON.stringify(kind === 'funnel' ? previewFunnelEventPayload() : previewClientErrorPayload(), null, 2);
}
