// First-party visitor events of the marketing site (docs/design/new-user-funnel.md section 4):
// `site.visit`, `site.page_view` and `site.cta_click`, all POSTed to the anonymous visit endpoint.
// They are NOT Google Analytics events: nothing here touches gtag, and lib/gtag.ts is unchanged.
//
// Every one of them is consent-gated. The sender re-reads the stored choice at the moment of the
// call and needs the visitor id that only exists after Accept; without either it sends nothing.
// It never throws and never reports a failure to the page: a lost beacon is a lost count, not an
// error the visitor should see.
//
// The pure parts (page keys, the detail token, the body) are tested in site-events.test.ts.
import { VISITOR_ID_KEY, type Attribution } from './attribution.ts';
import { beaconSource, beaconTouch, readFirstTouchCookie, sanitizeVisitorId, type StoredFirstTouch } from './attribution-capture.ts';
import { landingMemory } from './attribution-landing.ts';
import { PERSONA_LANDING_SLUGS } from './attribution-persona.ts';
import { readStoredConsent, type ConsentValue } from './consent.ts';

export const VISIT_ENDPOINT = '/api/v1/telemetry/visit';

export type SiteEventName = 'site.visit' | 'site.page_view' | 'site.cta_click';

/** `detail` is a closed token; the API stores it in a 64-character column. */
export const EVENT_DETAIL_MAX_LENGTH = 64;
const DETAIL_RE = /^[a-z0-9][a-z0-9:._-]{0,63}$/;

/** Trimmed and lowercased, then it must match the token pattern. An invalid value is dropped. */
export function sanitizeEventDetail(value: string | null | undefined): string | undefined {
  if (!value) return undefined;
  const token = value.trim().toLowerCase();
  return DETAIL_RE.test(token) ? token : undefined;
}

/** The fixed pages. Keys are paths WITHOUT the language prefix: `/it/pricing` is the same page as `/pricing`. */
const STATIC_PAGE_KEYS: ReadonlyMap<string, string> = new Map([
  ['/', 'home'],
  ['/features', 'features'],
  ['/pricing', 'pricing'],
  ['/about', 'about'],
  ['/contact', 'contact'],
  ['/recipes', 'recipes'],
  ['/runner', 'runner'],
  ['/blog', 'blog'],
]);

const AUDIENCE_SLUGS: readonly string[] = Object.values(PERSONA_LANDING_SLUGS);
const BLOG_SLUG_RE = /^[a-z0-9][a-z0-9-]*$/;

/**
 * The page key of a path (language prefix already removed), or undefined for a page that is not
 * counted: legal pages, the contact thank-you page, a 404, anything unknown.
 *
 *   /                 home          /recipes, /recipes/<id>   recipes
 *   /features         features      /runner                   runner
 *   /pricing          pricing       /for/<audience slug>      for-<slug>
 *   /about            about         /blog, /blog/tag/<tag>    blog
 *   /contact          contact       /blog/<slug>              blog:<slug>
 *
 * A key never exceeds the 64 characters of a detail token; a long blog slug is cut to fit.
 */
export function pageKeyForPath(basePath: string | null | undefined): string | undefined {
  if (!basePath) return undefined;
  const raw = basePath.split(/[?#]/)[0];
  const path = raw.length > 1 ? raw.replace(/\/+$/, '') : raw;

  const fixed = STATIC_PAGE_KEYS.get(path);
  if (fixed) return fixed;

  const segments = path.split('/').slice(1);
  if (segments.some((s) => s === '')) return undefined;
  const [first, second, third] = segments;

  if (first === 'recipes' && segments.length === 2) return 'recipes';
  if (first === 'for' && segments.length === 2) {
    return AUDIENCE_SLUGS.includes(second) ? `for-${second}` : undefined;
  }
  if (first === 'blog') {
    // `/blog/tag/<tag>` is a filtered index; `/blog/tag` alone is not a page.
    if (second === 'tag') return segments.length === 3 && BLOG_SLUG_RE.test(third) ? 'blog' : undefined;
    if (segments.length === 2 && BLOG_SLUG_RE.test(second)) {
      return `blog:${second}`.slice(0, EVENT_DETAIL_MAX_LENGTH);
    }
  }
  return undefined;
}

/** The JSON body of `POST /api/v1/telemetry/visit`. Absent fields are omitted. */
export interface SiteEventBody {
  event: SiteEventName;
  visitorId: string;
  /** utm_source (never the source label; the API derives that). */
  source?: string;
  medium?: string;
  campaign?: string;
  content?: string;
  term?: string;
  landingPath?: string;
  referrerHost?: string;
  /** Page key on `site.page_view`, CTA location on `site.cta_click`; not sent on `site.visit`. */
  detail?: string;
}

/**
 * Builds a beacon body. `landing` is the first touch the event reports: the callers pass the
 * visitor's stored first touch when there is one, else the landing held in memory (`beaconTouch`).
 * Every event of a visitor therefore carries the same first touch, and whichever of them reaches
 * the API first records it.
 */
export function siteEventBody(input: {
  event: SiteEventName;
  visitorId: string;
  landing: Attribution | null;
  ownHost?: string;
  detail?: string;
}): SiteEventBody {
  const { landing } = input;
  const body: SiteEventBody = { event: input.event, visitorId: input.visitorId };
  if (landing) {
    body.source = beaconSource(landing, input.ownHost);
    body.medium = landing.medium;
    body.campaign = landing.campaign;
    body.content = landing.content;
    body.term = landing.term;
    body.landingPath = landing.landingPath;
    body.referrerHost = landing.referrerHost;
  }
  body.detail = input.detail;
  for (const k of Object.keys(body) as (keyof SiteEventBody)[]) {
    if (body[k] === undefined) delete body[k];
  }
  return body;
}

/** What the sender needs from the page. Injected so the gate can be tested without a browser. */
export interface SiteEventEnv {
  /** The stored cookie choice, read at the moment of the call. */
  consent(): ConsentValue;
  /** The stored visitor id, or null. Never creates one: only Accept does (AttributionTracker). */
  visitorId(): string | null;
  /** The readable first-touch cookie (its touch and the visitor it names), or null. */
  firstTouch(): StoredFirstTouch | null;
  /** The landing of this document, held in memory. Reported only when no first touch is stored. */
  landing(): Attribution | null;
  ownHost(): string | undefined;
  post(body: SiteEventBody): void;
}

export type SiteEventSender = (
  event: Exclude<SiteEventName, 'site.visit'>,
  detail: string | null | undefined,
) => boolean;

/**
 * A sender that posts each (visitor, event, detail) at most once in its lifetime, which for the
 * module-level sender below is one page life. The API drops repeats for 30 minutes on top of that.
 * Returns whether a beacon went out; never throws.
 */
export function createSiteEventSender(env: SiteEventEnv): SiteEventSender {
  const sent = new Set<string>();
  return (event, detail) => {
    try {
      const token = sanitizeEventDetail(detail);
      if (!token) return false;
      if (env.consent() !== 'granted') return false;
      // A readable first-touch cookie names the visitor (`planIdentity`); the id kept in
      // localStorage follows it and is the fallback when there is no cookie.
      const stored = env.firstTouch();
      const visitorId = stored?.visitorId ?? sanitizeVisitorId(env.visitorId());
      if (!visitorId) return false;
      const key = `${visitorId}|${event}|${token}`;
      if (sent.has(key)) return false;
      sent.add(key);
      const touch = beaconTouch({ stored, visitorId, landing: env.landing() });
      env.post(siteEventBody({ event, visitorId, landing: touch, ownHost: env.ownHost(), detail: token }));
      return true;
    } catch {
      return false;
    }
  };
}

/**
 * POSTs one beacon. `keepalive` lets it finish after the page is left, which is exactly when a
 * CTA click is sent. Failures (the endpoint switched off, a rate limit, no network) are swallowed.
 */
export function postSiteEvent(body: SiteEventBody): void {
  try {
    void fetch(VISIT_ENDPOINT, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      keepalive: true,
      body: JSON.stringify(body),
    }).catch(() => undefined);
  } catch {
    // no fetch, or it refused the request synchronously
  }
}

const browserEnv: SiteEventEnv = {
  consent: readStoredConsent,
  visitorId() {
    try {
      return window.localStorage.getItem(VISITOR_ID_KEY);
    } catch {
      return null;
    }
  },
  firstTouch: () =>
    typeof document === 'undefined' ? null : readFirstTouchCookie(document.cookie, window.location.hostname),
  landing: () => landingMemory.landing(),
  ownHost: () => (typeof window === 'undefined' ? undefined : window.location.hostname),
  post: postSiteEvent,
};

const send = createSiteEventSender(browserEnv);

/** `site.page_view` for a page key (see `pageKeyForPath`). A no-op for an undefined key. */
export function sendPageView(pageKey: string | null | undefined): boolean {
  return send('site.page_view', pageKey);
}

/** `site.cta_click` for a CTA location token (`hero`, `header`, `pricing-creator`, ...). */
export function sendCtaClick(location: string | null | undefined): boolean {
  return send('site.cta_click', location);
}
