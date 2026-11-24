'use client';

import { useEffect } from 'react';
import {
  FIRST_TOUCH_COOKIE,
  VISITOR_ID_KEY,
  encodeFirstTouch,
  parseAttribution,
  type Attribution,
} from '@/lib/attribution';
import {
  VISIT_ENDPOINT,
  beaconFirstTouch,
  hasCampaignParams,
  planSignupIdentity,
  readStoredFirstTouch,
  sanitizeVisitorId,
  signupLandingPath,
  visitorEventBody,
  type SignupVisitorEvent,
} from '@/lib/attribution/visitor-events';
import { LANDING_PARAM, browserLandingPath } from '@/lib/auth/signup-carry';
import { readStoredConsent } from '@/lib/analytics/consent';
import { newGuid } from '@/lib/editing/ids';

function firstTouchCookie(): string | null {
  const prefix = `${FIRST_TOUCH_COOKIE}=`;
  const found = document.cookie.split('; ').find((c) => c.startsWith(prefix));
  return found ? found.slice(prefix.length) : null;
}

/** The visitor id localStorage holds, or null (nothing stored, not an id, or storage unavailable). */
function storedVisitorId(): string | null {
  try {
    return sanitizeVisitorId(window.localStorage.getItem(VISITOR_ID_KEY));
  } catch {
    return null;
  }
}

/**
 * The first-touch cookie as this page found it (or wrote it) when it opened. Memory only: it lives
 * as long as the page and is never stored. It exists for one case, see {@link restoreFirstTouch}.
 */
let rememberedFirstTouch: string | null = null;

function writeSessionFirstTouch(value: string): void {
  const secure = window.location.protocol === 'https:' ? '; Secure' : '';
  // No Max-Age: a session cookie.
  document.cookie = `${FIRST_TOUCH_COOKIE}=${value}; Path=/; SameSite=Lax${secure}`;
}

/**
 * Writes a fresh session first touch holding the address's own campaign parameters and the landing
 * page of the visit (`signupLandingPath`), and naming `visitorId`. True only when the cookie is
 * really in place afterwards (cookies can be blocked).
 */
function writeFreshFirstTouch(landing: Attribution, landingPath: string | undefined, visitorId: string): boolean {
  const value = encodeFirstTouch({
    source: landing.source,
    medium: landing.medium,
    campaign: landing.campaign,
    content: landing.content,
    term: landing.term,
    landingPath,
    visitorId,
    ts: Math.floor(Date.now() / 1000),
  });
  if (!value) return false;
  writeSessionFirstTouch(value);
  return firstTouchCookie() === value;
}

/**
 * The `lp` of the address: the page of ours the visit began on, named by the page that sent the
 * visitor here. A repeated parameter is never legitimate and reads as absent, as it does for the
 * form (`readSignupCarry`), so the cookie and the signup request never name different pages.
 */
function linkLandingPath(): string | undefined {
  const values = new URLSearchParams(window.location.search).getAll(LANDING_PARAM);
  return values.length === 1 ? values[0] : undefined;
}

/**
 * What both beacons need from the page: who the visitor is and their first touch. Reads the
 * device, so: only with consent granted.
 *
 * `mayStartFirstTouch` is true only when the page opens. Then, and only then, a signup address that
 * carries campaign parameters while no readable `rb_ft` exists starts a first touch: a session
 * cookie, and with it a NEW visitor id (`planSignupIdentity`). The id kept in localStorage follows
 * whatever this decides, so a later beacon from the same page names the same visitor even after
 * the API has used and cleared the cookie.
 */
function readVisitor(mayStartFirstTouch: boolean): { vid: string; touch: Attribution } {
  const ownHost = window.location.hostname;
  const landing = parseAttribution({
    search: window.location.search,
    referrer: '',
    pathname: window.location.pathname,
    ownHost,
  });
  // Where a first touch that starts on this page began (undefined without a campaign parameter:
  // none starts then). Not the page's own path as such: a visitor sent here by another page of
  // ours landed there, and only the address (`lp`) can say where.
  const landingPath = signupLandingPath({
    url: landing,
    linkLandingPath: linkLandingPath(),
    documentLandingPath: browserLandingPath(),
  });
  let stored = readStoredFirstTouch(firstTouchCookie(), ownHost);
  const inCookie = stored?.visitorId ?? null;
  const local = storedVisitorId();
  const candidate = newGuid();
  let fresh = false;
  if (mayStartFirstTouch && inCookie === null && hasCampaignParams(landing)) {
    fresh = writeFreshFirstTouch(landing, landingPath, candidate);
    if (fresh) stored = readStoredFirstTouch(firstTouchCookie(), ownHost);
  }
  const vid = planSignupIdentity({
    firstTouchVisitorId: inCookie,
    storedVisitorId: local,
    candidate,
    freshFirstTouchWritten: fresh,
  });
  if (vid !== local) {
    try {
      window.localStorage.setItem(VISITOR_ID_KEY, vid);
    } catch {
      // storage unavailable: the cookie still names the visitor
    }
  }
  return { vid, touch: beaconFirstTouch({ stored, url: landing, ownHost, landingPath }) };
}

function post(event: SignupVisitorEvent, vid: string, touch: Attribution): void {
  try {
    void fetch(VISIT_ENDPOINT, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      keepalive: true,
      body: JSON.stringify(visitorEventBody(event, vid, touch)),
    }).catch(() => undefined);
  } catch {
    // no fetch, or it refused the request synchronously: a lost beacon is a lost count, not an error
  }
}

/**
 * Signup-page attribution, run once when the page opens:
 *
 *  - a SESSION `rb_ft` fallback holding the URL's own utm parameters (so they survive the OAuth
 *    redirect) and the landing page of the visit (the `lp` the address carries next to them, else
 *    this page when it was itself the landing) when no readable first touch exists. A readable
 *    cookie is never overwritten,
 *    and the fresh one names a NEW visitor id, never one left in localStorage by an earlier visit:
 *    the visitor events and the account's attribution row then name the same visitor and the same
 *    link (`planSignupIdentity`);
 *  - the `signup.started` visitor event, carrying the real first touch: the stored `rb_ft` when
 *    there is one, else the URL's campaign parameters and that landing page (what the fresh cookie
 *    says, also when it could not be written), else nothing (the API labels that `direct`).
 *
 * Nothing is read, written or sent unless cookie consent is granted. These are first-party
 * beacons; no Google Analytics event is involved.
 */
export function runSignupCapture(): void {
  if (readStoredConsent() !== 'granted') return;
  const { vid, touch } = readVisitor(true);
  rememberedFirstTouch = firstTouchCookie();
  post('signup.started', vid, touch);
}

/**
 * Puts the first touch back for a second submit from the same page.
 *
 * The API uses the `rb_ft` cookie once: the signup request that carries it clears it. A visitor who
 * then presses "Change address" (they mistyped) submits again from this page with the cookie gone,
 * so the account they really use would carry no first touch while the mistyped one holds it. This
 * writes back the value the page opened with, as a session cookie, when it is missing.
 *
 * Consent-gated like everything here, never overwrites a cookie that is present, and never throws.
 */
export function restoreFirstTouch(): void {
  try {
    if (readStoredConsent() !== 'granted') return;
    if (!rememberedFirstTouch || firstTouchCookie() !== null) return;
    writeSessionFirstTouch(rememberedFirstTouch);
  } catch {
    // cookies unavailable: the second submit goes without, as it did before
  }
}

/**
 * The `signup.submitted` visitor event: the visitor asked for an account. Either the signup form
 * passed its own checks and is being sent, or a provider button on the signup page was pressed (a
 * provider sign-up has no form: the press is the submission). It says nothing about the outcome
 * (the API answers the same whether or not an account was created), carries no form value, and is
 * consent-gated exactly like `signup.started`. Sent with `keepalive`, so it survives the navigation
 * a provider button starts. Never throws.
 */
export function sendSignupSubmitted(): void {
  try {
    if (readStoredConsent() !== 'granted') return;
    const { vid, touch } = readVisitor(false);
    post('signup.submitted', vid, touch);
  } catch {
    // a beacon must never get in the way of the signup itself
  }
}

export function SignupTracker() {
  useEffect(() => {
    try {
      runSignupCapture();
    } catch {
      // storage or cookies unavailable: nothing is captured
    }
  }, []);
  return null;
}
