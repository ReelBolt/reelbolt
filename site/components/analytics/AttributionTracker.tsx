'use client';

import { useEffect, useRef } from 'react';
import { useCookieConsent } from '@/components/consent/CookieConsentProvider';
import { useBasePath } from '@/i18n/use-base-path';
import {
  FIRST_TOUCH_COOKIE,
  FIRST_TOUCH_MAX_AGE_SECONDS,
  VISITOR_ID_KEY,
  encodeFirstTouch,
  type Attribution,
} from '@/lib/attribution';
import {
  beaconTouch,
  planCapture,
  planIdentity,
  readFirstTouchCookie,
  resolveConsent,
  sanitizeVisitorId,
  type StoredFirstTouch,
} from '@/lib/attribution-capture';
import { landingMemory } from '@/lib/attribution-landing';
import { readStoredConsent } from '@/lib/consent';
import { pageKeyForPath, postSiteEvent, sendPageView, siteEventBody } from '@/lib/site-events';
import { browserLanding } from './landing';

const SESSION_FLAG = 'rf-visit-sent';

function clearStored() {
  try {
    window.localStorage.removeItem(VISITOR_ID_KEY);
  } catch {
    // storage unavailable
  }
  try {
    // The once-per-session marker goes too: an Accept later in this tab is a new visitor id, and
    // its visit should be counted.
    window.sessionStorage.removeItem(SESSION_FLAG);
  } catch {
    // storage unavailable
  }
  document.cookie = `${FIRST_TOUCH_COOKIE}=; Path=/; Max-Age=0; SameSite=Lax`;
}

/** The first-touch cookie as stored (its touch and the visitor it names), or null when there is none or it cannot be read. */
function storedFirstTouch(): StoredFirstTouch | null {
  return readFirstTouchCookie(document.cookie, window.location.hostname);
}

function storedVisitorId(): string | null {
  try {
    return sanitizeVisitorId(window.localStorage.getItem(VISITOR_ID_KEY));
  } catch {
    return null;
  }
}

/** Writes a fresh first touch naming `visitorId`. True only when the cookie is really in place afterwards. */
function writeFirstTouch(landing: Attribution, visitorId: string): boolean {
  const value = encodeFirstTouch({ ...landing, visitorId, ts: Math.floor(Date.now() / 1000) });
  if (!value) return false;
  const secure = window.location.protocol === 'https:' ? '; Secure' : '';
  document.cookie = `${FIRST_TOUCH_COOKIE}=${value}; Path=/; Max-Age=${FIRST_TOUCH_MAX_AGE_SECONDS}; SameSite=Lax${secure}`;
  return storedFirstTouch()?.visitorId === visitorId;
}

/** Runs only with consent granted: this is the one place that reads or writes the device. */
function capture(landing: Attribution) {
  let visitSent = false;
  try {
    visitSent = !!window.sessionStorage.getItem(SESSION_FLAG);
  } catch {
    visitSent = true;
  }
  const ownHost = window.location.hostname;
  // Read before anything is written: a first touch that was already here is the one every beacon
  // of this visitor reports (`beaconTouch`), not the landing of this document.
  const firstTouch = storedFirstTouch();
  const inCookie = firstTouch?.visitorId ?? null;
  const stored = storedVisitorId();
  const plan = planCapture({
    consent: 'granted',
    landing,
    hasFirstTouch: inCookie !== null,
    visitSent,
    ownHost,
  });
  // The cookie and the visitor id are one fact (`planIdentity`): a fresh first touch is a fresh
  // visitor, and an existing first touch names the visitor.
  const candidate = crypto.randomUUID();
  const identity = planIdentity({
    firstTouchVisitorId: inCookie,
    storedVisitorId: stored,
    candidate,
    freshFirstTouchWritten: plan.writeFirstTouch && writeFirstTouch(landing, candidate),
  });
  const vid = identity.visitorId;
  if (vid !== stored) {
    try {
      window.localStorage.setItem(VISITOR_ID_KEY, vid);
    } catch {
      // storage unavailable: the cookie still names the visitor
    }
  }
  if (!plan.beacon && !identity.renewed) return;
  try {
    window.sessionStorage.setItem(SESSION_FLAG, '1');
  } catch {
    return;
  }
  const touch = beaconTouch({ stored: firstTouch, visitorId: vid, landing });
  postSiteEvent(siteEventBody({ event: 'site.visit', visitorId: vid, landing: touch, ownHost }));
}

/**
 * First-party attribution and visitor events, mounted once in the root shell.
 *
 *  - The landing (campaign parameters, referrer, landing path) is read once per document into
 *    MEMORY (`browserLanding`). Nothing is stored before Accept, so a visitor who accepts on their
 *    third page still gets the real landing recorded, as long as they got there by client-side
 *    navigation.
 *  - With consent granted: the first-touch cookie (only if absent, and then together with a new
 *    visitor id: see `planIdentity`), one `site.visit` per visitor per tab session, and one
 *    `site.page_view` per page key per page life, on client navigation too. The landing page of
 *    the document gets its page view as well, even when Accept came pages later: the visitor did
 *    view it. These are first-party beacons; Google Analytics is `PageViewTracker`'s job.
 *  - Every beacon reports the visitor's FIRST touch: the one stored in `rb_ft` when the cookie was
 *    already there, the landing of this document only when this page started the first touch
 *    (`beaconTouch`). The visitor row and the account then name the same link.
 *  - With consent denied or withdrawn: the cookie and the visitor id are removed.
 *  - Undecided: nothing is read, written, sent or removed.
 */
export function AttributionTracker() {
  const { consent } = useCookieConsent();
  const basePath = useBasePath();
  // True once this page life has seen a stored choice. A `null` after that is the footer's "Cookie
  // preferences" (the choice withdrawn); a `null` before it is a first render or a new visitor.
  const decided = useRef(false);

  useEffect(() => {
    const landing = browserLanding();
    if (!landing) return;
    // An audience page tells every signup link on it (header, sticky bar, footer) whom it is for,
    // unless the link the visitor arrived through already said so (`LandingMemory.notePage`).
    landingMemory.notePage(basePath);

    // `consent` is the server snapshot (`null`) on the first render; localStorage is the truth.
    // Reading it here, not trusting that `null`, is what keeps a returning accepted visitor's
    // cookie and visitor id from being wiped on every full page load.
    const stored = readStoredConsent();
    const choice = resolveConsent({ consent, stored, decidedThisPage: decided.current });
    if ((consent ?? stored) !== null) decided.current = true;

    if (choice === 'granted') {
      capture(landing);
      // The page the document opened on, then the page open now. They are the same page until the
      // visitor navigates; the sender drops a repeat, so each is counted once per page life. Without
      // the first line a visitor who accepts on their second page would be recorded as having seen
      // one page only.
      sendPageView(pageKeyForPath(landingMemory.landingPage()));
      sendPageView(pageKeyForPath(basePath));
    } else if (planCapture({ consent: choice, landing, hasFirstTouch: false, visitSent: true }).clear) {
      // Rejected or withdrawn: remove what we stored. Harmless when nothing exists.
      clearStored();
    }
  }, [consent, basePath]);

  return null;
}
