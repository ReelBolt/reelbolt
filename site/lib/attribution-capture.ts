// Pure decision logic for AttributionTracker, so it can be unit tested without a DOM.
import {
  FIRST_TOUCH_COOKIE,
  decodeFirstTouch,
  sanitizeHost,
  sanitizeLandingPath,
  sanitizeUtm,
  type Attribution,
} from './attribution.ts';
import type { ConsentValue } from './consent.ts';

export interface CapturePlan {
  clear: boolean;
  writeFirstTouch: boolean;
  beacon: boolean;
  /** `utm_source` for the beacon; undefined when the landing has none or it is an own host. */
  source?: string;
}

function isOwnHost(label: string, ownHost?: string): boolean {
  const l = label.toLowerCase();
  return (
    l === 'reelbolt.ai' ||
    l.endsWith('.reelbolt.ai') ||
    (!!ownHost && l === ownHost.trim().toLowerCase())
  );
}

/**
 * The `source` a beacon carries: the landing's `utm_source`, unless it names one of our own hosts.
 * It is the UTM value only, never the source label: the API works the label out itself
 * (`utm_source`, else the referrer host, else `direct`), and it keeps `utm_source` as its own
 * column, so sending a label here would file a referrer host or `direct` as a campaign source.
 */
export function beaconSource(landing: Attribution, ownHost?: string): string | undefined {
  return landing.source && !isOwnHost(landing.source, ownHost) ? landing.source : undefined;
}

/**
 * The choice the tracker acts on.
 *
 * `consent` comes from `CookieConsentProvider`, whose first render is the server snapshot, `null`,
 * for every visitor, including one who accepted last week. So `null` alone says nothing: the stored
 * value is the truth (`GoogleAnalytics` resolves it the same way). What is left is a real "no
 * choice", and that has two meanings:
 *
 *  - nothing was decided in this page life either: a first-time visitor. Nothing is stored for
 *    them, so there is nothing to do, and above all nothing to clear.
 *  - a choice was seen in this page life and is now gone: the visitor reopened the banner from the
 *    footer's "Cookie preferences". That withdraws the choice, and the privacy policy promises the
 *    cookie and the visitor id are removed on withdrawal, so it counts as `denied`.
 */
export function resolveConsent(input: {
  consent: ConsentValue;
  stored: ConsentValue;
  decidedThisPage: boolean;
}): ConsentValue {
  const choice = input.consent ?? input.stored;
  if (choice !== null) return choice;
  return input.decidedThisPage ? 'denied' : null;
}

export function planCapture(input: {
  consent: ConsentValue;
  landing: Attribution;
  /** True when a READABLE first-touch cookie exists (it decodes and names a visitor id). */
  hasFirstTouch: boolean;
  visitSent: boolean;
  ownHost?: string;
}): CapturePlan {
  if (input.consent !== 'granted') {
    // Only an explicit rejection (or a withdrawal, which `resolveConsent` turns into one) removes
    // what was stored. An undecided `null` must not: on every full page load it is what a returning
    // accepted visitor's first render looks like, and clearing there wiped the first-touch cookie
    // and the visitor id each time.
    return { clear: input.consent === 'denied', writeFirstTouch: false, beacon: false };
  }
  return {
    clear: false,
    writeFirstTouch: !input.hasFirstTouch,
    beacon: !input.visitSent,
    source: beaconSource(input.landing, input.ownHost),
  };
}

const VISITOR_ID_RE = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;

/** A visitor id as the site mints it (`crypto.randomUUID()`), lowercased; anything else is null. */
export function sanitizeVisitorId(value: string | null | undefined): string | null {
  if (!value) return null;
  const id = value.trim().toLowerCase();
  return VISITOR_ID_RE.test(id) ? id : null;
}

export interface IdentityPlan {
  /** The visitor id every beacon of this page carries, and the one localStorage must hold. */
  visitorId: string;
  /**
   * True when an earlier visitor id was replaced by a new one. The new visitor has not been counted
   * yet, so its `site.visit` goes out whatever the once-per-session marker says.
   */
  renewed: boolean;
}

/**
 * Which visitor this browser is. ONE RULE: the first-touch cookie and the visitor id are one fact.
 *
 * The server keeps one first touch per visitor id, first write wins, and an account's attribution
 * is whatever its `rb_ft` says. If the cookie could be rewritten from a later landing while the old
 * visitor id lived on, the same person would be filed under one link as a visitor and under another
 * as an account. So:
 *
 *  - a readable `rb_ft` exists: its visitor id IS the visitor. The first touch is not rewritten
 *    (first touch wins) and localStorage follows the cookie, never the other way round;
 *  - no readable `rb_ft` (never set, expired after its 30 days, removed at signup, unreadable): a
 *    new first touch starts, and with it a NEW visitor id. The earlier id and its server row stay
 *    what they were, a finished visit; they are not continued under a different first touch;
 *  - the fresh cookie could not be written (cookies blocked, or the value over the size limit):
 *    there is no first touch to keep in step with, so the stored id is kept. Minting here would
 *    create a new visitor on every page.
 *
 * `candidate` is a new random id; it is used only where the rule says a visitor starts.
 */
export function planIdentity(input: {
  /** The visitor id inside the readable `rb_ft`, or null when there is none. */
  firstTouchVisitorId: string | null;
  /** The id localStorage holds, or null. */
  storedVisitorId: string | null;
  candidate: string;
  /** Whether a fresh `rb_ft` naming `candidate` is now in place. Ignored when a readable cookie existed. */
  freshFirstTouchWritten: boolean;
}): IdentityPlan {
  if (input.firstTouchVisitorId) return { visitorId: input.firstTouchVisitorId, renewed: false };
  if (input.freshFirstTouchWritten) {
    return { visitorId: input.candidate, renewed: input.storedVisitorId !== null };
  }
  return { visitorId: input.storedVisitorId ?? input.candidate, renewed: false };
}

export interface StoredFirstTouch {
  /** The stored touch, every field sanitized again (a cookie is whatever the browser holds). */
  touch: Attribution;
  /** The visitor id the cookie names. */
  visitorId: string;
}

/**
 * The readable first touch in a `document.cookie` string, or null. "Readable" means what
 * `planIdentity` means by it: the `rb_ft` value decodes and names a visitor id. Anything less is
 * treated as no cookie at all.
 */
export function readFirstTouchCookie(cookies: string | null | undefined, ownHost?: string): StoredFirstTouch | null {
  if (!cookies) return null;
  const prefix = `${FIRST_TOUCH_COOKIE}=`;
  const found = cookies.split(/;\s*/).find((c) => c.startsWith(prefix));
  if (!found) return null;
  const ft = decodeFirstTouch(found.slice(prefix.length));
  const visitorId = sanitizeVisitorId(ft?.visitorId);
  if (!ft || !visitorId) return null;
  const touch: Attribution = {
    source: sanitizeUtm(ft.source),
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
  return { touch, visitorId };
}

/**
 * The first touch a beacon reports for `visitorId`. The second half of the one rule above:
 *
 *  - the stored first touch names this visitor: send THAT, whatever it says. The account this
 *    visitor may become is filed under the cookie's link, and the server keeps the first touch of
 *    the first beacon it ever receives for an id. A visitor whose cookie was written while visit
 *    recording was off, or whose earlier beacons were lost, has no server row yet; sending today's
 *    landing would file the visitor under one link and the account under another;
 *  - no stored first touch for this visitor (the fresh cookie could not be written, or it is gone):
 *    the landing this document holds in memory. It is what a fresh cookie would have said.
 */
export function beaconTouch(input: {
  stored: StoredFirstTouch | null;
  visitorId: string;
  landing: Attribution | null;
}): Attribution | null {
  const { stored } = input;
  if (stored && stored.visitorId === sanitizeVisitorId(input.visitorId)) return stored.touch;
  return input.landing;
}
