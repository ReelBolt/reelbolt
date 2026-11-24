/**
 * The web client telemetry surface (alpha-launch T-018): the app-side consent surface, the alpha
 * funnel and client error reporting. Every export here is opt-in and best-effort — see
 * `docs/observability.md`, "Alpha funnel, client telemetry and consent".
 */

export { telemetryEnabled } from './config';
export {
  TELEMETRY_CONSENT_STORAGE_KEY,
  readTelemetryConsent,
  resetTelemetryConsent,
  subscribeTelemetryConsent,
  writeTelemetryConsent,
  type TelemetryConsentValue,
} from './consent';
export {
  ALPHA_FUNNEL_EVENTS,
  CLIENT_ERROR_FIELDS,
  FUNNEL_EVENT_FIELDS,
  RUN_BLOCKED_DETAILS,
  isRunBlockedDetail,
  type RunBlockedDetail,
  ROUTE_PLACEHOLDER,
  STATIC_ROUTE_SEGMENTS,
  TELEMETRY_CONSENT_EVENTS,
  buildClientErrorPayload,
  isAlphaFunnelEvent,
  routePath,
  type AlphaFunnelEvent,
  type ClientErrorPayload,
  type ConsentDecisionPayload,
  type FunnelEventPayload,
  type TelemetryConsentEvent,
  type TelemetryEventPayload,
} from './allowlist';
export {
  previewClientErrorPayload,
  previewConsentDecisionPayloads,
  previewFunnelEventPayload,
  previewTelemetryPayload,
} from './preview';
export { TELEMETRY_ERRORS_PATH, TELEMETRY_EVENTS_PATH, sendTelemetry, sendTelemetryConsentDecision } from './client';
export { clearFunnelEventMarkers, emitFunnelEvent, funnelMarkerKey, hasFunnelEventBeenSent } from './funnel';
export { runBlockDetailOf, runBlockDetailFromKey } from './run-events';
export { errorName, reportClientError, type ClientErrorContext } from './errors';
export { userKeyFromEmail } from './identity';
export {
  TELEMETRY_CONSENT_ALLOW,
  TELEMETRY_CONSENT_DENY,
  TELEMETRY_CONSENT_TITLE,
  TELEMETRY_CONSENT_WORDING,
  TelemetryConsentSurface,
} from './ConsentSurface';
export { emitUpgradeEvent } from './upgrade';
export { useUpgradePromptShown } from './useUpgradePromptShown';
export { UPGRADE_EVENTS, UPGRADE_SURFACES, isUpgradeEvent, isUpgradeSurface, type UpgradeEvent, type UpgradeSurface, type UpgradeEventPayload } from './allowlist';
