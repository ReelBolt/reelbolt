import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  EVENT_DETAIL_MAX_LENGTH,
  VISIT_ENDPOINT,
  createSiteEventSender,
  pageKeyForPath,
  postSiteEvent,
  sanitizeEventDetail,
  sendCtaClick,
  sendPageView,
  siteEventBody,
  type SiteEventBody,
  type SiteEventEnv,
} from './site-events.ts';
import { PERSONA_LANDING_SLUGS } from './attribution-persona.ts';
import type { ConsentValue } from './consent.ts';

const VID = '3f2b8c1e-5d4a-4b7e-9c61-0a1b2c3d4e5f';
const TOKEN = /^[a-z0-9][a-z0-9:._-]{0,63}$/;

test('the fixed pages map to the page keys of the funnel contract', () => {
  const table: Record<string, string> = {
    '/': 'home',
    '/features': 'features',
    '/pricing': 'pricing',
    '/about': 'about',
    '/contact': 'contact',
    '/recipes': 'recipes',
    '/runner': 'runner',
    '/blog': 'blog',
  };
  for (const [path, key] of Object.entries(table)) {
    assert.equal(pageKeyForPath(path), key, path);
    if (path !== '/') assert.equal(pageKeyForPath(`${path}/`), key, `${path}/`);
    assert.equal(pageKeyForPath(`${path}?utm_source=x#top`), key, 'query and fragment are ignored');
  }
});

test('every audience page has its own key, and only the six that exist', () => {
  for (const slug of Object.values(PERSONA_LANDING_SLUGS)) {
    assert.equal(pageKeyForPath(`/for/${slug}`), `for-${slug}`);
  }
  assert.equal(pageKeyForPath('/for/small-business/'), 'for-small-business');
  for (const path of ['/for', '/for/', '/for/everyone', '/for/creators/more']) {
    assert.equal(pageKeyForPath(path), undefined, path);
  }
});

test('recipes and blog families', () => {
  assert.equal(pageKeyForPath('/recipes/3f2b8c1e-5d4a-4b7e-9c61-0a1b2c3d4e5f'), 'recipes');
  assert.equal(pageKeyForPath('/recipes/_'), 'recipes');
  assert.equal(pageKeyForPath('/blog/reelbolt-vs-descript'), 'blog:reelbolt-vs-descript');
  assert.equal(pageKeyForPath('/blog/tag/comparisons'), 'blog');
  for (const path of ['/blog/rss.xml', '/blog/Some_Post', '/blog/a/b', '/blog/tag', '/blog/tag/x/y', '/recipes/a/b']) {
    assert.equal(pageKeyForPath(path), undefined, path);
  }
});

test('a long blog slug is cut to the 64 characters of a detail token and stays a valid one', () => {
  const key = pageKeyForPath(`/blog/${'a-very-long-slug-'.repeat(8)}end`);
  assert.equal(key?.length, EVENT_DETAIL_MAX_LENGTH);
  assert.match(key ?? '', TOKEN);
  assert.ok(key?.startsWith('blog:a-very-long-slug-'));
  assert.equal(sanitizeEventDetail(key), key);
});

test('pages that are not counted have no key', () => {
  for (const path of [
    '/contact/thank-you',
    '/legal/privacy',
    '/legal/terms',
    '/it/pricing', // the caller removes the language prefix first
    '/nope',
    '/Pricing',
    '/app/signup',
    '//pricing',
    '',
    null,
    undefined,
  ]) {
    assert.equal(pageKeyForPath(path), undefined, String(path));
  }
});

test('every key the table can produce is a valid detail token', () => {
  const paths = ['/', '/features', '/pricing', '/about', '/contact', '/recipes', '/runner', '/blog', '/recipes/x', '/blog/tag/guides', '/blog/hello-reelbolt', '/for/agencies'];
  for (const path of paths) assert.match(pageKeyForPath(path) ?? '', TOKEN, path);
});

test('sanitizeEventDetail keeps closed tokens and drops everything else', () => {
  for (const ok of ['hero', 'header-mobile', 'recipe_detail', 'pricing-creator', 'blog:hello-reelbolt', 'for-creators', 'a', 'x'.repeat(64)]) {
    assert.equal(sanitizeEventDetail(ok), ok);
  }
  assert.equal(sanitizeEventDetail('  Hero '), 'hero');
  for (const bad of ['', ' ', '-hero', ':x', 'two words', 'a/b', 'a?b', 'x'.repeat(65), 'héro', null, undefined]) {
    assert.equal(sanitizeEventDetail(bad), undefined, String(bad));
  }
});

const landing = {
  source: 'hackernews',
  medium: 'community',
  campaign: 'launch-2026-11',
  content: 'hn-launch',
  term: 'video',
  referrerHost: 'news.ycombinator.com',
  landingPath: '/for/creators',
};

test('the visit body carries every campaign field, content and term included, and no detail', () => {
  assert.deepEqual(siteEventBody({ event: 'site.visit', visitorId: VID, landing }), {
    event: 'site.visit',
    visitorId: VID,
    source: 'hackernews',
    medium: 'community',
    campaign: 'launch-2026-11',
    content: 'hn-launch',
    term: 'video',
    landingPath: '/for/creators',
    referrerHost: 'news.ycombinator.com',
  });
});

test('absent fields are omitted, and an own-host utm_source is not sent', () => {
  assert.deepEqual(siteEventBody({ event: 'site.visit', visitorId: VID, landing: null }), { event: 'site.visit', visitorId: VID });
  assert.deepEqual(
    siteEventBody({ event: 'site.page_view', visitorId: VID, landing: { source: 'site.test', landingPath: '/' }, ownHost: 'site.test', detail: 'home' }),
    { event: 'site.page_view', visitorId: VID, landingPath: '/', detail: 'home' },
  );
  // A referrer-only landing sends no source: the API works the label out itself.
  const body = siteEventBody({ event: 'site.visit', visitorId: VID, landing: { referrerHost: 'example.org' } });
  assert.deepEqual(body, { event: 'site.visit', visitorId: VID, referrerHost: 'example.org' });
  assert.ok(JSON.stringify(siteEventBody({ event: 'site.cta_click', visitorId: VID, landing, detail: 'x'.repeat(64) })).length < 2048, 'under the 2 KB body cap');
});

function harness(overrides: Partial<SiteEventEnv> & { consentValue?: ConsentValue; vid?: string | null } = {}) {
  const posted: SiteEventBody[] = [];
  const state = { consent: 'consentValue' in overrides ? overrides.consentValue! : ('granted' as ConsentValue), vid: 'vid' in overrides ? overrides.vid! : VID };
  const env: SiteEventEnv = {
    consent: () => state.consent,
    visitorId: () => state.vid,
    firstTouch: () => null,
    landing: () => landing,
    ownHost: () => 'reelbolt.ai',
    post: (body) => void posted.push(body),
    ...overrides,
  };
  return { send: createSiteEventSender(env), posted, state };
}

test('a page view and a CTA click go out with the visitor id, the landing and their detail', () => {
  const { send, posted } = harness();
  assert.equal(send('site.page_view', 'pricing'), true);
  assert.equal(send('site.cta_click', 'hero'), true);
  assert.deepEqual(posted.map((b) => [b.event, b.detail, b.visitorId, b.content]), [
    ['site.page_view', 'pricing', VID, 'hn-launch'],
    ['site.cta_click', 'hero', VID, 'hn-launch'],
  ]);
});

test('a stored first touch is what every beacon reports, not the landing of this document', () => {
  // The visitor first came through link-a and accepted (the cookie says so). Today they are back
  // through link-b. The account they may create is filed under link-a, so the visitor row must be
  // too, even when this is the first beacon the API ever receives for the id.
  const stored = {
    visitorId: VID,
    touch: { source: 'newsletter', medium: 'email', campaign: 'october', content: 'link-a', landingPath: '/pricing' },
  };
  const { send, posted } = harness({ firstTouch: () => stored });
  assert.equal(send('site.page_view', 'home'), true);
  assert.equal(send('site.cta_click', 'hero'), true);
  for (const body of posted) {
    assert.equal(body.visitorId, VID);
    assert.deepEqual(
      [body.source, body.medium, body.campaign, body.content, body.term, body.landingPath, body.referrerHost],
      ['newsletter', 'email', 'october', 'link-a', undefined, '/pricing', undefined],
      'nothing of the link-b landing is mixed in',
    );
  }
});

test('the first-touch cookie names the visitor; without one the stored id and the landing are used', () => {
  // The cookie is the fact (`planIdentity`): an id left in localStorage by an earlier visit is not
  // sent with another visitor's first touch.
  const other = '11111111-2222-4333-8444-555555555555';
  const cookie = { visitorId: other, touch: { content: 'link-a' } };
  const withCookie = harness({ firstTouch: () => cookie });
  assert.equal(withCookie.send('site.page_view', 'home'), true);
  assert.deepEqual([withCookie.posted[0].visitorId, withCookie.posted[0].content], [other, 'link-a']);
  // No readable cookie (blocked, or cleared at signup in another tab): the id this tab stored and
  // the landing it holds, which is what a fresh cookie would have said.
  const without = harness();
  assert.equal(without.send('site.page_view', 'home'), true);
  assert.deepEqual([without.posted[0].visitorId, without.posted[0].content], [VID, 'hn-launch']);
  // A stored id that is not a visitor id is not sent at all.
  assert.equal(harness({ vid: 'not-an-id' }).send('site.page_view', 'home'), false);
});

test('nothing is sent before consent is granted, and the visitor id is not even read', () => {
  for (const consentValue of [null, 'denied'] as const) {
    let idReads = 0;
    const { send, posted } = harness({
      consentValue,
      firstTouch: () => {
        idReads++;
        return null;
      },
      visitorId: () => {
        idReads++;
        return VID;
      },
    });
    assert.equal(send('site.page_view', 'home'), false);
    assert.equal(send('site.cta_click', 'hero'), false);
    assert.deepEqual(posted, []);
    assert.equal(idReads, 0);
  }
});

test('nothing is sent without a stored visitor id, and none is invented', () => {
  const { send, posted } = harness({ vid: null });
  assert.equal(send('site.page_view', 'home'), false);
  assert.deepEqual(posted, []);
});

test('an invalid or missing detail sends nothing', () => {
  const { send, posted } = harness();
  for (const detail of [undefined, null, '', 'two words', 'x'.repeat(65)]) assert.equal(send('site.page_view', detail), false);
  assert.deepEqual(posted, []);
});

test('one beacon per event and detail per page life; a new detail or a new visitor id sends again', () => {
  const { send, posted, state } = harness();
  assert.equal(send('site.page_view', 'home'), true);
  assert.equal(send('site.page_view', 'home'), false);
  assert.equal(send('site.page_view', ' HOME '), false, 'the same token after normalising');
  assert.equal(send('site.page_view', 'pricing'), true);
  assert.equal(send('site.cta_click', 'home'), true, 'a different event with the same detail');
  // Rejected, then accepted again: a new visitor id, so the page is counted for it.
  state.vid = '11111111-2222-4333-8444-555555555555';
  assert.equal(send('site.page_view', 'home'), true);
  assert.equal(posted.length, 4);
});

test('a refusal is not remembered: the page is sent once consent arrives', () => {
  const { send, posted, state } = harness({ consentValue: null });
  assert.equal(send('site.page_view', 'home'), false);
  state.consent = 'granted';
  assert.equal(send('site.page_view', 'home'), true);
  assert.equal(posted.length, 1);
});

test('the sender never throws, whatever the page does', () => {
  const boom = () => {
    throw new Error('storage is gone');
  };
  assert.equal(harness({ consent: boom }).send('site.page_view', 'home'), false);
  assert.equal(harness({ visitorId: boom }).send('site.page_view', 'home'), false);
  assert.equal(harness({ firstTouch: boom }).send('site.page_view', 'home'), false);
  assert.equal(harness({ landing: boom }).send('site.page_view', 'home'), false);
  assert.equal(harness({ post: boom }).send('site.cta_click', 'hero'), false);
});

test('postSiteEvent: same-origin POST with keepalive, and a failing or missing fetch is swallowed', async () => {
  const real = globalThis.fetch;
  const calls: { url: string; init: RequestInit }[] = [];
  try {
    globalThis.fetch = (async (url: string | URL | Request, init?: RequestInit) => {
      calls.push({ url: String(url), init: init ?? {} });
      throw new Error('offline');
    }) as typeof fetch;
    postSiteEvent({ event: 'site.page_view', visitorId: VID, detail: 'home' });
    await new Promise((resolve) => setImmediate(resolve));
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, VISIT_ENDPOINT);
    assert.equal(VISIT_ENDPOINT, '/api/v1/telemetry/visit');
    assert.equal(calls[0].init.method, 'POST');
    assert.equal(calls[0].init.keepalive, true);
    assert.deepEqual(JSON.parse(String(calls[0].init.body)), { event: 'site.page_view', visitorId: VID, detail: 'home' });
    // No credentials option, no extra header: a plain same-origin JSON POST.
    assert.deepEqual(calls[0].init.headers, { 'Content-Type': 'application/json' });

    globalThis.fetch = (() => {
      throw new Error('fetch refused synchronously');
    }) as typeof fetch;
    assert.doesNotThrow(() => postSiteEvent({ event: 'site.cta_click', visitorId: VID, detail: 'hero' }));
  } finally {
    globalThis.fetch = real;
  }
});

test('outside a browser the module-level senders are silent no-ops', () => {
  // No window, so no stored consent: nothing may go out, and nothing may throw.
  const real = globalThis.fetch;
  let called = 0;
  globalThis.fetch = (async () => {
    called++;
    return new Response(null);
  }) as typeof fetch;
  try {
    assert.equal(sendPageView('home'), false);
    assert.equal(sendCtaClick('hero'), false);
    assert.equal(called, 0);
  } finally {
    globalThis.fetch = real;
  }
});

test('first-party events never go through Google Analytics', () => {
  const source = readFileSync(new URL('./site-events.ts', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /gtag\s*\(|trackEvent\s*\(|from '.*gtag/);
  // And the CTA components keep their GA event next to the first-party one.
  for (const file of ['../components/ui/PrimaryCta.tsx', '../components/pricing/PlanCtaButton.tsx']) {
    const text = readFileSync(new URL(file, import.meta.url), 'utf8');
    assert.match(text, /trackEvent\((event|siteConfig\.primaryCta\.event), \{ location \}\);\s*sendCtaClick\(location\);/, file);
    assert.match(text, /useSignupHref\(/, file);
  }
});
