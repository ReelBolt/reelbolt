/**
 * The struct-as-allowlist for what client-side telemetry collects (alpha-launch T-018). It follows
 * the E11 runner precedent: the collected fields are a fixed, named struct, and everything else is
 * dropped rather than "not added" — so a caller that passes an extra field (a form value, a prompt, a
 * file name) cannot leak it, because only the named fields are read out.
 *
 * Three shapes are collected, and only these three:
 *
 * - a **funnel event** — the event name, one of four;
 * - a **client error report** — the error's class name, and the route it happened on, redacted;
 * - a **consent decision** — the event name, one of two, recording the choice itself. This is the
 *   one send a declining browser still makes (see `consent.ts` for why it is outside the gate).
 *
 * The error's message and stack are **not collected at all**: they can carry user content, and the
 * server will not rely on the browser's own redaction, so the smallest honest thing is not to send
 * them. Never collected under any setting: files, media, prompts, project or document names, form
 * field values, or anything the person typed. `preview.ts` renders exactly this struct back to the
 * person before they opt in.
 */

/** The first-touch events the alpha measures, each fired once per user (run events: once per detail). */
export type AlphaFunnelEvent =
  | 'alpha.signup'
  | 'alpha.first_project'
  | 'alpha.first_run'
  | 'alpha.first_export_initiated'
  | 'alpha.run_page_opened'
  | 'alpha.run_clicked'
  | 'alpha.run_blocked';

/** Why a run was refused: a closed set, never free text. Only `alpha.run_blocked` carries one. */
export type RunBlockedDetail =
  | 'readiness'
  | 'notes_required'
  | 'plan'
  | 'credits'
  | 'estimate_gate'
  | 'rate_limited'
  | 'source'
  | 'other';

export const RUN_BLOCKED_DETAILS: readonly RunBlockedDetail[] = [
  'readiness',
  'notes_required',
  'plan',
  'credits',
  'estimate_gate',
  'rate_limited',
  'source',
  'other',
];

export function isRunBlockedDetail(value: unknown): value is RunBlockedDetail {
  return typeof value === 'string' && (RUN_BLOCKED_DETAILS as readonly string[]).includes(value);
}

/** The closed set of funnel event names; anything else is not a funnel event. */
export const ALPHA_FUNNEL_EVENTS: readonly AlphaFunnelEvent[] = [
  'alpha.signup',
  'alpha.first_project',
  'alpha.first_run',
  'alpha.first_export_initiated',
  'alpha.run_page_opened',
  'alpha.run_clicked',
  'alpha.run_blocked',
];

/**
 * The two consent-decision events the browser reports, one per choice. They are a separate shape
 * from a funnel event: they record the choice itself, not a product step.
 */
export type TelemetryConsentEvent = 'telemetry.consent.granted' | 'telemetry.consent.declined';

/** The closed set of consent-decision event names; anything else is not a consent decision. */
export const TELEMETRY_CONSENT_EVENTS: readonly TelemetryConsentEvent[] = [
  'telemetry.consent.granted',
  'telemetry.consent.declined',
];

/** The two upgrade-prompt events: not once-per-user, and each carries a closed surface token. */
export type UpgradeEvent = 'billing.upgrade_prompt_shown' | 'billing.upgrade_clicked';
export const UPGRADE_EVENTS: readonly UpgradeEvent[] = ['billing.upgrade_prompt_shown', 'billing.upgrade_clicked'];

/** Where an upgrade prompt was shown; a closed set, never free text. */
export type UpgradeSurface = 'insufficient_credits_alert' | 'insufficient_credits_toast' | 'plan_gate';
export const UPGRADE_SURFACES: readonly UpgradeSurface[] = ['insufficient_credits_alert', 'insufficient_credits_toast', 'plan_gate'];

export function isUpgradeEvent(value: unknown): value is UpgradeEvent {
  return typeof value === 'string' && (UPGRADE_EVENTS as readonly string[]).includes(value);
}
export function isUpgradeSurface(value: unknown): value is UpgradeSurface {
  return typeof value === 'string' && (UPGRADE_SURFACES as readonly string[]).includes(value);
}

/** The exact fields an upgrade event carries. */
export interface UpgradeEventPayload {
  name: UpgradeEvent;
  detail: UpgradeSurface;
}

/** The exact fields a funnel event carries. */
export interface FunnelEventPayload {
  name: AlphaFunnelEvent;
  /** Only on `alpha.run_blocked`: one token of the closed `RunBlockedDetail` set. */
  detail?: RunBlockedDetail;
}

/** The exact fields a consent-decision event carries. */
export interface ConsentDecisionPayload {
  name: TelemetryConsentEvent;
}

/** Everything the events endpoint accepts from the browser: a funnel event or a consent decision. */
export type TelemetryEventPayload = FunnelEventPayload | ConsentDecisionPayload | UpgradeEventPayload;

/** The exact fields a client error report carries. */
export interface ClientErrorPayload {
  name: string;
  route: string;
}

/** The placeholder an opaque route segment is replaced with. */
export const ROUTE_PLACEHOLDER = ':id';

/** Truncation bounds, mirrored by the server before it records anything. */
export const MAX_NAME_LENGTH = 128;
export const MAX_ROUTE_LENGTH = 256;

/** The field names, for the preview and for tests that pin the allowlist. */
export const FUNNEL_EVENT_FIELDS: readonly (keyof FunnelEventPayload)[] = ['name', 'detail'];
export const CLIENT_ERROR_FIELDS: readonly (keyof ClientErrorPayload)[] = ['name', 'route'];

/**
 * The app's static route words. Pinned to `route-redaction.corpus.json` (the same file the server
 * reads) by `allowlist.test.ts`, so a word added on one side only fails a test rather than shipping.
 */
export const STATIC_ROUTE_SEGMENTS: readonly string[] = [
  'accept-terms',
  'admin',
  'agents',
  'app',
  'avatar-library',
  'billing',
  'brand-libraries',
  'calibration',
  'change-password',
  'checkout',
  'confirm',
  'dashboard',
  'data',
  'editor',
  'editor-coming-soon',
  'executions',
  'files',
  'forgot-password',
  'inference-providers',
  'invite',
  'links',
  'login',
  'marketing',
  'me',
  'media',
  'members',
  'new',
  'notifications',
  'offline',
  'org',
  'pair',
  'preview',
  'projects',
  'promotions',
  'reset-password',
  'review',
  'runners',
  'settings',
  'signup',
  'skills',
  'spike',
  'styles',
  'thumbnails',
  'timeline',
  'timelines',
  'unsubscribe',
  'usage',
  'users',
  'verify-email',
  'welcome',
  'workflows',
];

const STATIC_SEGMENT_SET = new Set(STATIC_ROUTE_SEGMENTS);

/** True when `value` is one of the four funnel events. */
export function isAlphaFunnelEvent(value: unknown): value is AlphaFunnelEvent {
  return typeof value === 'string' && (ALPHA_FUNNEL_EVENTS as readonly string[]).includes(value);
}

/** What a caller may hand `buildClientErrorPayload`; only the allowlisted fields are read. */
export interface ClientErrorInput {
  name?: unknown;
  route?: unknown;
}

/**
 * Builds the error payload by reading **only** the allowlisted fields. A non-string is dropped, and
 * each string is bounded.
 */
export function buildClientErrorPayload(input: ClientErrorInput): ClientErrorPayload {
  return {
    name: truncate(input.name, MAX_NAME_LENGTH) ?? 'Error',
    route: routePath(input.route),
  };
}

/**
 * Reduces a route to a bare, opaque-free pathname: the query string and fragment are dropped, and
 * every segment that is not one of the app's static route words becomes `:id`, so an
 * `/invite/<token>` can never ship the token. Anything that is not a bare pathname (a full URL, a
 * protocol-relative URL, a `javascript:` URL, a relative path) becomes the empty string.
 *
 * The server enforces the same rule (`RouteRedactor`); the shared corpus keeps the two identical.
 */
export function routePath(value: unknown): string {
  const raw = typeof value === 'string' ? value.trim() : '';
  if (!raw) return '';

  const withoutFragment = raw.split('#')[0] ?? '';
  const withoutQuery = withoutFragment.split('?')[0] ?? '';
  if (!withoutQuery.startsWith('/') || withoutQuery.startsWith('//')) return '';

  const redacted = withoutQuery
    .split('/')
    .map((segment) => (segment.length === 0 || STATIC_SEGMENT_SET.has(segment) ? segment : ROUTE_PLACEHOLDER))
    .join('/');

  return truncate(redacted, MAX_ROUTE_LENGTH) ?? '';
}

function truncate(value: unknown, max: number): string | null {
  if (typeof value !== 'string') return null;
  const trimmed = value.trim();
  if (!trimmed) return null;
  return trimmed.length <= max ? trimmed : trimmed.slice(0, max);
}
