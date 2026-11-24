// What a visitor's own link carries through the signed-out pages (docs/design/new-user-funnel.md
// sections 3, 6 and 7): the audience (`?for=<persona>`), the campaign parameters (`utm_*`), the
// page of ours they landed on (`?lp=`), the plan a pricing card started (`?plan=`) and a return
// path (`?next=`).
//
// Everything here lives in the URL the person opened and in the links and requests built from it.
// Nothing in this file is written to or read from the device, so none of it depends on cookie
// consent. (The plan and the return path are kept across the account-creation round trip by
// `signup-intent.ts`, which is functional storage and says so.) Every
// value is untrusted: it is sanitized on the way in (an invalid value is dropped, never rejected,
// because a signup must not fail on marketing data), and the API sanitizes it again.
//
// Pure apart from `browserLandingPath`, the one function that reads the browser.

import { sanitizeLandingPath, sanitizeSource, sanitizeUtm } from '@/lib/attribution';
import { PERSONA_PARAM, sanitizePersona, type Persona } from '@/lib/attribution/persona';
import type { SignupAttribution, SignupRequest } from '@/lib/types/auth';
import { safeNextRedirect } from './redirect';

/** The campaign parameters of the link, sanitized. Absent fields are absent keys. */
export type CampaignParams = Omit<SignupAttribution, 'landingPath'>;

/** Link parameter -> the field it fills, in the order the parameters are written. */
export const CAMPAIGN_PARAMS = [
  ['utm_source', 'source'],
  ['utm_medium', 'medium'],
  ['utm_campaign', 'campaign'],
  ['utm_content', 'content'],
  ['utm_term', 'term'],
] as const satisfies readonly (readonly [string, keyof CampaignParams])[];

export interface SignupCarry {
  /** The audience the landing page (or the tracked link) named. */
  persona?: Persona;
  /** The campaign parameters; an empty object when the link had none. */
  campaign: CampaignParams;
  /**
   * The page of ours the visitor first landed on (`?lp=`), when the page that sent them here said
   * so. A page reached from another page of ours cannot see where the visit began; the marketing
   * site's signup link can, and names it here.
   */
  landingPath?: string;
  /** The plan a pricing card started (`creator`, `pro`...). Carried between the pages, never trusted. */
  plan?: string;
  /** A validated same-origin path to return to (see `safeNextRedirect`). */
  next?: string;
}

/** Nothing carried. */
export const EMPTY_CARRY: SignupCarry = { campaign: {} };

/** A plan token as the pricing page writes it; anything else is dropped. */
const PLAN_RE = /^[a-z0-9][a-z0-9_-]{0,31}$/;

export function sanitizePlan(value: string | undefined): string | undefined {
  if (!value) return undefined;
  const token = value.trim().toLowerCase();
  return PLAN_RE.test(token) ? token : undefined;
}

/** The link parameter that names the landing page (a path, sanitized like every landing path). */
export const LANDING_PARAM = 'lp';

/** A repeated query parameter is never legitimate here; anything but a single string is absent. */
function single(value: string | string[] | undefined): string | undefined {
  return typeof value === 'string' && value !== '' ? value : undefined;
}

/**
 * Reads the carry from a page's `searchParams`. Unknown personas, invalid campaign values, an
 * unsafe `next` and a malformed plan are dropped one by one; what survives is safe to put in a link.
 */
export function readSignupCarry(params: Record<string, string | string[] | undefined>): SignupCarry {
  const campaign: CampaignParams = {};
  for (const [param, field] of CAMPAIGN_PARAMS) {
    const raw = single(params[param]);
    const value = field === 'source' ? sanitizeSource(raw) : sanitizeUtm(raw);
    if (value) campaign[field] = value;
  }
  const carry: SignupCarry = { campaign };
  const persona = sanitizePersona(single(params[PERSONA_PARAM]));
  if (persona) carry.persona = persona;
  const landingPath = sanitizeLandingPath(single(params[LANDING_PARAM]));
  if (landingPath) carry.landingPath = landingPath;
  const plan = sanitizePlan(single(params.plan));
  if (plan) carry.plan = plan;
  const next = safeNextRedirect(single(params.next));
  if (next) carry.next = next;
  return carry;
}

/**
 * The marketing part of the carry (`for`, `utm_*` and, with a campaign, `lp`) as query pairs, in a
 * fixed order. The landing path rides only with a campaign parameter, like `landingPath` in the
 * signup request: on its own it would describe a touch with no source behind it.
 */
function marketingPairs(carry: SignupCarry): [string, string][] {
  const pairs: [string, string][] = [];
  if (carry.persona) pairs.push([PERSONA_PARAM, carry.persona]);
  let campaign = false;
  for (const [param, field] of CAMPAIGN_PARAMS) {
    const value = carry.campaign[field];
    if (value) {
      pairs.push([param, value]);
      campaign = true;
    }
  }
  if (campaign && carry.landingPath) pairs.push([LANDING_PARAM, carry.landingPath]);
  return pairs;
}

function appendQuery(href: string, pairs: [string, string][]): string {
  if (pairs.length === 0) return href;
  // URLSearchParams percent-encodes `+` (legal in a UTM value, a space to a query reader) and the
  // `/`, `?` and `&` of a return path.
  const query = new URLSearchParams(pairs).toString();
  return `${href}${href.includes('?') ? '&' : '?'}${query}`;
}

/**
 * A link between the signed-out pages (`/signup` <-> `/login`) that keeps everything the visitor
 * arrived with: `for`, `plan`, `next` and the campaign parameters. With nothing to carry the href
 * comes back unchanged.
 */
export function withCarry(href: string, carry: SignupCarry): string {
  const pairs = marketingPairs(carry);
  if (carry.plan) pairs.push(['plan', carry.plan]);
  if (carry.next) pairs.push(['next', carry.next]);
  return appendQuery(href, pairs);
}

/**
 * The Go API's OAuth start endpoint for a provider, carrying the persona and the campaign
 * parameters as `for` and `utm_*` (plus `lp`, the landing page, when the link named one). The API
 * packs what it knows into the OAuth `state`, so it survives the provider round trip without
 * anything being stored on the device; a parameter it does not know is ignored.
 */
export function oauthStartHref(provider: string, carry: SignupCarry = EMPTY_CARRY): string {
  return appendQuery(`/api/v1/auth/oauth/${provider}/start`, marketingPairs(carry));
}

/**
 * The `persona` and `attribution` fields of the signup request. Only sanitized, present values are
 * sent, and each object is omitted when it would be empty. `landingPath` rides along only with a
 * campaign parameter: on its own it would record a touch with no source behind it.
 *
 * The landing page is the one the link named (`carry.landingPath`: the marketing site knows where
 * the visit began); without it, `landingPath` (this document, when it was itself the landing).
 */
export function signupMarketingFields(
  carry: SignupCarry,
  landingPath?: string,
): Pick<SignupRequest, 'persona' | 'plan' | 'attribution'> {
  const fields: Pick<SignupRequest, 'persona' | 'plan' | 'attribution'> = {};
  if (carry.persona) fields.persona = carry.persona;
  const plan = sanitizePlan(carry.plan);
  if (plan) fields.plan = plan;
  const attribution: SignupAttribution = { ...carry.campaign };
  if (Object.keys(attribution).length > 0) {
    const path = carry.landingPath ?? sanitizeLandingPath(landingPath);
    if (path) attribution.landingPath = path;
    fields.attribution = attribution;
  }
  return fields;
}

/**
 * The path of the document the visitor opened, when that document was where they landed: no
 * referrer, or a referrer on another origin. A same-origin referrer means they came from another
 * page of ours (usually the marketing site), whose path this page cannot know, so the answer is
 * "unknown" rather than a guess.
 */
export function directLandingPath(input: {
  /** `document.referrer`. */
  referrer: string;
  /** `window.location.origin`. */
  origin: string;
  /** The URL the document was loaded with (the navigation entry), before any client-side navigation. */
  documentUrl: string;
}): string | undefined {
  try {
    if (input.referrer && new URL(input.referrer).origin === input.origin) return undefined;
    const url = new URL(input.documentUrl, input.origin);
    return url.origin === input.origin ? sanitizeLandingPath(url.pathname) : undefined;
  } catch {
    return undefined;
  }
}

/** `directLandingPath` for the current document. Undefined on the server and whenever it cannot tell. */
export function browserLandingPath(): string | undefined {
  if (typeof window === 'undefined') return undefined;
  let documentUrl = window.location.href;
  try {
    // The navigation entry keeps the URL the document was LOADED with: after a client-side move
    // from /login to /signup the address bar says /signup, but the landing was /login.
    const entry = window.performance?.getEntriesByType?.('navigation')[0];
    if (entry?.name) documentUrl = entry.name;
  } catch {
    // no Performance Timeline here: the current address is the best answer
  }
  return directLandingPath({
    referrer: document.referrer,
    origin: window.location.origin,
    documentUrl,
  });
}
