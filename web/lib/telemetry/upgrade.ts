/**
 * `billing.upgrade_prompt_shown` / `billing.upgrade_clicked`: first-party, consent-gated like the
 * funnel, the server keeps the first shown and first clicked per surface for each user. The only detail is a closed surface token.
 * Best-effort: never blocks or surfaces anything to the person.
 */
import { isUpgradeEvent, isUpgradeSurface, type UpgradeEvent, type UpgradeEventPayload, type UpgradeSurface } from './allowlist';
import { sendTelemetry, TELEMETRY_EVENTS_PATH, type ClientEventsBody } from './client';
import { telemetryEnabled } from './config';
import { readTelemetryConsent } from './consent';

export function emitUpgradeEvent(event: UpgradeEvent, surface: UpgradeSurface): boolean {
  if (typeof window === 'undefined') return false;
  if (!isUpgradeEvent(event) || !isUpgradeSurface(surface)) return false;
  if (!telemetryEnabled()) return false;
  if (readTelemetryConsent() !== 'granted') return false;
  const payload: UpgradeEventPayload = { name: event, detail: surface };
  void sendTelemetry(TELEMETRY_EVENTS_PATH, { events: [payload] } satisfies ClientEventsBody);
  return true;
}
