// Google Consent Mode v2 for the marketing site's Google tag.
//
// The tag is on every page whenever a measurement ID is configured, but it starts with every
// storage purpose denied. In that state Google's tag writes no cookie and reads none; it sends
// only cookieless pings (page address and title, referrer, browser and device signals, the
// request's IP address, a client id that is random per page load and not stored). Accepting the
// cookie banner updates `analytics_storage` to granted. Rejecting it ends the tag altogether (see
// `GoogleAnalytics` and `installHitGuard`); only a visitor who has not chosen gets the pings. Ads
// purposes are never granted: the site runs no ads.
//
// Kept in its own dependency-free module, apart from the React component, so a test can run the
// exact script string the page ships and read back the order of the calls it makes.

import { CONSENT_STORAGE_KEY } from './consent.ts';

/** The state set before `config`, and the state every rejection or withdrawal returns to. */
export const GA_CONSENT_DEFAULT = {
  ad_storage: 'denied',
  ad_user_data: 'denied',
  ad_personalization: 'denied',
  analytics_storage: 'denied',
  // Gives the stored-choice update below a window to arrive before the first hit is sent.
  wait_for_update: 500,
} as const;

/**
 * Passed to `config`. The site runs no ads, so Google signals and ad personalization are off even
 * for a visitor who accepts, matching the dashboard's tag.
 */
export const GA_CONFIG = {
  send_page_view: true,
  allow_google_signals: false,
  allow_ad_personalization_signals: false,
} as const;

/** Google's own per-property opt-out flag; set by `GoogleAnalytics` after a same-page withdrawal. */
export function disableFlagName(gaId: string): string {
  return `ga-disable-${gaId}`;
}

// Hosts gtag.js sends measurement hits to (regional hosts included).
const GOOGLE_HIT_HOST = /(^|\.)(google-analytics\.com|analytics\.google\.com)$/;

type HitWindow = {
  location: { href: string };
  fetch?: (input: unknown, init?: unknown) => Promise<unknown>;
  Response?: new (body: null, init: { status: number }) => unknown;
  navigator?: { sendBeacon?: (url: unknown, data?: unknown) => boolean };
  [key: string]: unknown;
};

function isGoogleHit(win: HitWindow, input: unknown): boolean {
  try {
    const raw =
      typeof input === 'string' ? input : (input as { href?: string; url?: string }).href ?? (input as { url?: string }).url;
    return GOOGLE_HIT_HOST.test(new URL(String(raw), win.location.href).hostname);
  } catch {
    return false;
  }
}

/**
 * Makes `ga-disable-<ID>` hold for hits gtag.js has already queued. The flag stops new events, but
 * a hit built while a visitor was granted (the trailing `user_engagement` one, sent a few seconds
 * later with the `_ga` client id) goes out after a withdrawal anyway. This wraps `fetch` and
 * `sendBeacon`, the two transports gtag.js uses, and drops a Google measurement request while the
 * flag is true. It must be installed before gtag.js loads, because the library may keep its own
 * reference to `fetch`. Requests to anything else pass through untouched.
 */
export function installHitGuard(win: HitWindow, gaId: string): void {
  const flag = disableFlagName(gaId);
  const fetchBefore = win.fetch;
  if (typeof fetchBefore === 'function' && win.Response) {
    const ResponseCtor = win.Response;
    win.fetch = function guardedFetch(this: unknown, input: unknown, init?: unknown) {
      if (win[flag] === true && isGoogleHit(win, input)) {
        return Promise.resolve(new ResponseCtor(null, { status: 204 }));
      }
      return fetchBefore.call(this, input, init);
    };
  }
  const beaconBefore = win.navigator?.sendBeacon;
  if (win.navigator && typeof beaconBefore === 'function') {
    const nav = win.navigator;
    nav.sendBeacon = function guardedBeacon(url: unknown, data?: unknown) {
      if (win[flag] === true && isGoogleHit(win, url)) return true;
      return beaconBefore.call(nav, url, data);
    };
  }
}

export const GA_CONSENT_GRANTED = { analytics_storage: 'granted' } as const;
export const GA_CONSENT_REVOKED = { analytics_storage: 'denied' } as const;

// A GA4 measurement ID. The value is a build-time env var and is written into an inline script, so
// anything that is not this exact shape produces no tag at all rather than a script with a
// stranger's text in it.
const MEASUREMENT_ID = /^G-[A-Z0-9]{4,20}$/;

export function isMeasurementId(value: string | undefined): value is string {
  return typeof value === 'string' && MEASUREMENT_ID.test(value);
}

/**
 * The inline script that starts the tag. Order matters and is the point of this module:
 *
 * 1. `consent default` (all denied) goes into the queue first, so `config` and every hit after it
 *    are evaluated under it.
 * 2. A visitor who already accepted on an earlier visit gets `analytics_storage: granted` pushed
 *    right after, still before `config`, so their page view is a full measured one and not a
 *    cookieless one followed by a second hit.
 * 3. `js` and `config` come last.
 *
 * `gtag.js` itself is loaded separately and processes this queue when it arrives.
 */
export function gaInitScript(gaId: string): string {
  if (!isMeasurementId(gaId)) {
    throw new Error('gaInitScript needs a GA4 measurement ID (G-XXXXXXXXXX)');
  }
  return `
window.dataLayer = window.dataLayer || [];
function gtag(){window.dataLayer.push(arguments);}
window.gtag = gtag;
gtag('consent', 'default', ${JSON.stringify(GA_CONSENT_DEFAULT)});
try {
  if (window.localStorage.getItem(${JSON.stringify(CONSENT_STORAGE_KEY)}) === 'granted') {
    gtag('consent', 'update', ${JSON.stringify(GA_CONSENT_GRANTED)});
  }
} catch (e) {}
gtag('js', new Date());
gtag('config', ${JSON.stringify(gaId)}, ${JSON.stringify(GA_CONFIG)});
`;
}
