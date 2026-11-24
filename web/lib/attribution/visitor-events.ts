// The signup page's visitor events (docs/design/new-user-funnel.md section 4): `signup.started` and
// `signup.submitted`, POSTed to the anonymous visit endpoint with the random visitor id. They are
// first-party beacons, NOT Google Analytics events, and both need cookie consent: the callers
// (components/analytics/SignupTracker.tsx) check it before anything here is used.
//
// Pure: no React, no storage, no DOM. The body has the same shape the marketing site sends for
// `site.visit` (site/lib/site-events.ts), so whichever beacon reaches the API first records the
// same first touch for the visitor.

import {
  decodeFirstTouch,
  sanitizeHost,
  sanitizeLandingPath,
  sanitizeSource,
  sanitizeUtm,
  type Attribution,
} from './index';

export const VISIT_ENDPOINT = '/api/v1/telemetry/visit';

export type SignupVisitorEvent = 'signup.started' | 'signup.submitted';

/** The JSON body of `POST /api/v1/telemetry/visit`. Absent fields are omitted. */
export interface VisitorEventBody {
  event: SignupVisitorEvent;
  visitorId: string;
  /** `utm_source` only, never a label: the API derives the label (source, else referrer host, else `direct`). */
  source?: string;
  medium?: string;
  campaign?: string;
  content?: string;
  term?: string;
  landingPath?: string;
  referrerHost?: string;
}

const UUID_V4_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/;

/** A visitor id as it is minted (a version-4 UUID), lowercased; anything else is null. */
export function sanitizeVisitorId(value: string | null | undefined): string | null {
  if (!value) return null;
  const id = value.trim().toLowerCase();
  return UUID_V4_RE.test(id) ? id : null;
}

/**
 * Which visitor this browser is, on the signup page. The same ONE RULE the marketing site follows
 * (`planIdentity` in site/lib/attribution-capture.ts): the first-touch cookie and the visitor id are
 * one fact.
 *
 * The API keeps one first touch per visitor id, first write wins, and an account's attribution is
 * whatever its `rb_ft` says. A fresh `rb_ft` written next to an id that already has a first touch
 * on the server would file the same person under one link as a visitor and under another as an
 * account. So:
 *
 *  - a readable `rb_ft` exists (it decodes and names a visitor id): that id IS the visitor, and the
 *    id kept in localStorage follows the cookie, never the other way round;
 *  - this page wrote a fresh `rb_ft`: a new first touch is a NEW visitor, `candidate`. An id left
 *    in localStorage by an earlier visit is not continued under a different first touch;
 *  - no readable cookie and none written (no campaign parameters on the address, or cookies are
 *    blocked): there is no first touch to keep in step with, so the stored id is kept, and
 *    `candidate` is used only when nothing is stored. Minting here would create a new visitor on
 *    every page load.
 *
 * `candidate` is a new random id; it is used only where the rule says a visitor starts.
 */
export function planSignupIdentity(input: {
  /** The visitor id inside the readable `rb_ft`, or null when there is none. */
  firstTouchVisitorId: string | null;
  /** The id localStorage holds, or null. */
  storedVisitorId: string | null;
  candidate: string;
  /** Whether a fresh `rb_ft` naming `candidate` is now in place. Ignored when a readable cookie existed. */
  freshFirstTouchWritten: boolean;
}): string {
  if (input.firstTouchVisitorId) return input.firstTouchVisitorId;
  if (input.freshFirstTouchWritten) return input.candidate;
  return input.storedVisitorId ?? input.candidate;
}

export interface StoredFirstTouch {
  /** The stored touch, every field sanitized again (a cookie is whatever the browser holds). */
  touch: Attribution;
  /** The visitor id inside the cookie, when it is a version-4 UUID. */
  visitorId?: string;
}

/** Reads an `rb_ft` cookie value. Null when it cannot be decoded: the caller then has no stored touch. */
export function readStoredFirstTouch(cookieValue: string | null | undefined, ownHost?: string): StoredFirstTouch | null {
  if (!cookieValue) return null;
  const ft = decodeFirstTouch(cookieValue);
  if (!ft) return null;
  const touch: Attribution = {
    source: sanitizeSource(ft.source, ownHost),
    medium: sanitizeUtm(ft.medium),
    campaign: sanitizeUtm(ft.campaign),
    content: sanitizeUtm(ft.content),
    term: sanitizeUtm(ft.term),
    referrerHost: sanitizeHost(ft.referrerHost, ownHost),
    landingPath: sanitizeLandingPath(ft.landingPath),
  };
  for (const k of Object.keys(touch) as (keyof Attribution)[]) {
    if (touch[k] === undefined) delete touch[k];
  }
  const vid = sanitizeVisitorId(ft.visitorId);
  return { touch, ...(vid ? { visitorId: vid } : {}) };
}

/** True when the touch carries at least one campaign parameter. */
export function hasCampaignParams(a: Attribution): boolean {
  return Boolean(a.source || a.medium || a.campaign || a.content || a.term);
}

/**
 * The landing page of a first touch that STARTS on the signup page: no readable `rb_ft` exists and
 * the address carries a campaign parameter, which is the only case in which this page starts one.
 *
 *  1. the page the address names (`lp`). The page that sent the visitor here knows where the visit
 *     began; the signup page does not;
 *  2. otherwise the document the browser opened, when that document was itself the landing
 *     (`browserLandingPath`: the signup page, opened directly or from another origin);
 *  3. otherwise unknown: the visit began on another page of ours that did not say which.
 *
 * Without a campaign parameter there is no landing page to report, whatever the address says: no
 * first touch starts here then, and `lp` on its own describes a touch with no source behind it
 * (the same rule the links and the signup request follow, lib/auth/signup-carry.ts).
 *
 * Both inputs are sanitized here, so a caller may pass what it read as it is.
 */
export function signupLandingPath(input: {
  /** The signup URL's own campaign parameters. */
  url: Attribution;
  /** The raw `lp` of the address, when it appears exactly once. */
  linkLandingPath: string | null | undefined;
  /** The document's own path when it was the landing, else undefined. */
  documentLandingPath: string | null | undefined;
}): string | undefined {
  if (!hasCampaignParams(input.url)) return undefined;
  return sanitizeLandingPath(input.linkLandingPath) ?? sanitizeLandingPath(input.documentLandingPath);
}

/**
 * The first touch a signup-page beacon reports:
 *
 *  1. the stored first touch (`rb_ft`) when there is one, whatever it says. A visitor who first
 *     arrived from a search engine and now follows a campaign link is still that first visit;
 *  2. otherwise the campaign parameters of the signup URL itself, with the landing page a first
 *     touch started here has (`signupLandingPath`): exactly what a fresh cookie says, so the
 *     visitor record and the account's record agree even when the cookie could not be written;
 *  3. otherwise nothing, which the API labels `direct`.
 */
export function beaconFirstTouch(input: {
  stored: StoredFirstTouch | null;
  /** The signup URL's own campaign parameters (`parseAttribution` of the current address). */
  url: Attribution;
  ownHost?: string;
  /** `signupLandingPath` for this page. Used only when no first touch is stored. */
  landingPath?: string;
}): Attribution {
  if (input.stored) return input.stored.touch;
  const { url } = input;
  const touch: Attribution = {
    source: sanitizeSource(url.source, input.ownHost),
    medium: url.medium,
    campaign: url.campaign,
    content: url.content,
    term: url.term,
    landingPath: hasCampaignParams(url) ? sanitizeLandingPath(input.landingPath) : undefined,
  };
  for (const k of Object.keys(touch) as (keyof Attribution)[]) {
    if (touch[k] === undefined) delete touch[k];
  }
  return touch;
}

/** Builds a beacon body. No `detail`: the API works out the source label itself. */
export function visitorEventBody(event: SignupVisitorEvent, visitorId: string, touch: Attribution): VisitorEventBody {
  const body: VisitorEventBody = {
    event,
    visitorId,
    source: touch.source,
    medium: touch.medium,
    campaign: touch.campaign,
    content: touch.content,
    term: touch.term,
    landingPath: touch.landingPath,
    referrerHost: touch.referrerHost,
  };
  for (const k of Object.keys(body) as (keyof VisitorEventBody)[]) {
    if (body[k] === undefined) delete body[k];
  }
  return body;
}
