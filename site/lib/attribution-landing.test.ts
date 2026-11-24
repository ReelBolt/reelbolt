import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createLandingMemory, landingMemory } from './attribution-landing.ts';
import { decorateCarryHref, decorateEntryHref } from './attribution-signup-link.ts';

const first = {
  search: '?utm_source=HackerNews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&utm_term=video',
  referrer: 'https://news.ycombinator.com/item?id=1',
  pathname: '/it/for/creators',
  basePath: '/for/creators',
  ownHost: 'reelbolt.ai',
};

test('nothing is remembered until the first document says where it landed', () => {
  const memory = createLandingMemory();
  assert.equal(memory.landing(), null);
  assert.equal(memory.persona(), undefined);
});

test('the first landing wins: a later page of the same page life never replaces it', () => {
  const memory = createLandingMemory();
  const landing = memory.remember(first);
  assert.deepEqual(landing, {
    source: 'hackernews',
    medium: 'community',
    campaign: 'launch-2026-11',
    content: 'hn-launch',
    term: 'video',
    referrerHost: 'news.ycombinator.com',
    landingPath: '/it/for/creators',
  });
  // After client-side navigation the URL has no parameters and the referrer is unchanged or own.
  const again = memory.remember({ search: '', referrer: 'https://reelbolt.ai/', pathname: '/pricing', ownHost: 'reelbolt.ai' });
  assert.equal(again, landing, 'the very same object');
  assert.deepEqual(memory.landing(), landing);
});

test('invalid campaign values are dropped, never kept raw', () => {
  const memory = createLandingMemory();
  const landing = memory.remember({ search: '?utm_source=bad value!&utm_campaign=ok', referrer: '', pathname: '/' });
  assert.deepEqual(landing, { campaign: 'ok', landingPath: '/' });
});

test('a document opened through one of our own carrying links keeps the landing page of the visit', () => {
  // First document: the visitor lands on /for/creators from a campaign and has stored nothing.
  const firstDoc = createLandingMemory();
  const landed = firstDoc.remember({
    search: '?utm_source=instagram&utm_content=ig-bio&for=creator',
    referrer: 'https://l.instagram.com/',
    pathname: '/for/creators',
    ownHost: 'reelbolt.ai',
  });
  // They switch language: a full load, through a link that carries the visit.
  const href = decorateCarryHref('/it/for/creators', { persona: firstDoc.persona(), landing: landed });
  assert.equal(href, '/it/for/creators?for=creator&utm_source=instagram&utm_content=ig-bio&lp=%2Ffor%2Fcreators');

  // Second document: its own path is /it/for/creators, but the visit began on /for/creators.
  const secondDoc = createLandingMemory();
  const carried = secondDoc.remember({
    search: href.slice(href.indexOf('?')),
    referrer: 'https://reelbolt.ai/for/creators?utm_source=instagram&utm_content=ig-bio&for=creator',
    pathname: '/it/for/creators',
    basePath: '/for/creators',
    ownHost: 'reelbolt.ai',
  });
  assert.deepEqual(carried, { source: 'instagram', content: 'ig-bio', landingPath: '/for/creators' });
  // The page shown first in THIS document is still its own (it decides the page view and the audience).
  assert.equal(secondDoc.landingPage(), '/for/creators');
  assert.equal(secondDoc.persona(), 'creator');
  // So the signup link of the second document names the same landing page as the first one's did.
  assert.equal(
    decorateEntryHref('/app/signup', { persona: secondDoc.persona(), landing: carried }),
    decorateEntryHref('/app/signup', { persona: firstDoc.persona(), landing: landed }),
  );
});

test('lp is believed only from our own page, next to a campaign, and only as a plain path', () => {
  const doc = { pathname: '/pricing', ownHost: 'reelbolt.ai' };
  const own = 'https://reelbolt.ai/it/pricing';
  const path = (search: string, referrer: string, ownHost: string | undefined = doc.ownHost) =>
    createLandingMemory().remember({ ...doc, ownHost, search, referrer }).landingPath;

  assert.equal(path('?utm_source=x&lp=%2Ffor%2Fcreators', own), '/for/creators');
  assert.equal(path('?utm_source=x&lp=/For/Creators%3Fa%3D1', own), '/for/creators', 'sanitized like every landing path');
  // A posted campaign link cannot name another landing page: the referrer is not ours, or absent.
  assert.equal(path('?utm_source=x&lp=%2Ffor%2Fcreators', 'https://news.ycombinator.com/'), '/pricing');
  assert.equal(path('?utm_source=x&lp=%2Ffor%2Fcreators', ''), '/pricing');
  assert.equal(path('?utm_source=x&lp=%2Ffor%2Fcreators', 'https://reelbolt.ai.evil.example/'), '/pricing');
  assert.equal(path('?utm_source=x&lp=%2Ffor%2Fcreators', 'not a url'), '/pricing');
  assert.equal(
    createLandingMemory().remember({ pathname: '/pricing', search: '?utm_source=x&lp=%2Ffor%2Fcreators', referrer: own }).landingPath,
    '/pricing',
    'no own host known, no trust',
  );
  // No campaign parameter: a landing path alone is not carried, so it is not read either.
  assert.equal(path('?lp=%2Ffor%2Fcreators&for=creator', own), '/pricing');
  assert.equal(path('?utm_source=bad value!&lp=%2Ffor%2Fcreators', own), '/pricing');
  // Not a plain path: the document's own path stands.
  for (const bad of ['//evil.example/x', 'https%3A%2F%2Fevil.example%2F', 'for/creators', '/a%20b', '']) {
    assert.equal(path(`?utm_source=x&lp=${bad}`, own), '/pricing', bad);
  }
  // The port of a local or preview host does not matter; the host name does.
  assert.equal(path('?utm_source=x&lp=%2Fblog', 'http://localhost:3100/it', 'localhost'), '/blog');
});

test('the landing memory and the link decoration agree on the name of the landing parameter', async () => {
  const { LANDING_PARAM } = await import('./attribution-signup-link.ts');
  const { readFileSync } = await import('node:fs');
  assert.match(readFileSync(new URL('./attribution-landing.ts', import.meta.url), 'utf8'), new RegExp(`const LANDING_PARAM = '${LANDING_PARAM}';`));
});

test('the audience comes from the link first, then from the audience page landed on', () => {
  const fromLink = createLandingMemory();
  fromLink.remember({ ...first, search: '?for=agency' });
  assert.equal(fromLink.persona(), 'agency', '?for= beats the page');

  const fromPage = createLandingMemory();
  fromPage.remember(first);
  assert.equal(fromPage.persona(), 'creator');

  const invalid = createLandingMemory();
  invalid.remember({ search: '?for=other', referrer: '', pathname: '/', basePath: '/' });
  assert.equal(invalid.persona(), undefined);

  const prefixedOnly = createLandingMemory();
  prefixedOnly.remember({ search: '', referrer: '', pathname: '/for/educators' });
  assert.equal(prefixedOnly.persona(), 'educator', 'basePath defaults to pathname');
});

test('a later audience page replaces the remembered audience and tells subscribers once', () => {
  const memory = createLandingMemory();
  memory.remember(first);
  let calls = 0;
  const unsubscribe = memory.subscribe(() => calls++);

  memory.notePage(undefined);
  memory.notePage('/pricing'); // a page with no audience keeps the remembered one
  memory.notePage('/for/creators'); // unchanged
  memory.notePage('/for/nope'); // not an audience page
  assert.equal(calls, 0);
  assert.equal(memory.persona(), 'creator');

  memory.notePage('/for/agencies/');
  assert.equal(memory.persona(), 'agency');
  assert.equal(calls, 1);

  unsubscribe();
  memory.notePage('/for/educators');
  assert.equal(memory.persona(), 'educator');
  assert.equal(calls, 1);
  // The landing itself is untouched by any of it.
  assert.equal(memory.landing()?.source, 'hackernews');
  assert.equal(memory.landingPage(), '/for/creators');
});

test('the landing page never replaces the audience its own link named', () => {
  // A tracked link may point at one audience page and carry another audience: the admin sets the
  // two independently, and the link's is the one stored with the account.
  const memory = createLandingMemory();
  memory.remember({ ...first, search: '?utm_content=rd-agency&for=agency' });
  let calls = 0;
  memory.subscribe(() => calls++);

  // What the tracker does when the landing page mounts, and again when the visitor comes back to it.
  memory.notePage('/for/creators');
  assert.equal(memory.persona(), 'agency');
  memory.notePage('/pricing');
  memory.notePage('/for/creators');
  assert.equal(memory.persona(), 'agency');
  assert.equal(calls, 0);
  // The page's own calls to action carry the link's audience too, not the page's.
  assert.equal(memory.personaFor('creator'), 'agency');
  assert.equal(memory.personaFor(), 'agency');

  // Opening a DIFFERENT audience page is the visitor's own choice: from here the pages decide.
  memory.notePage('/for/educators');
  assert.equal(memory.persona(), 'educator');
  assert.equal(calls, 1);
  assert.equal(memory.personaFor('educator'), 'educator');
  memory.notePage('/for/creators');
  assert.equal(memory.persona(), 'creator');
  assert.equal(memory.personaFor('creator'), 'creator');
});

test('the link stops holding on another audience page even when that page names the same audience', () => {
  const memory = createLandingMemory();
  memory.remember({ search: '?for=agency', referrer: '', pathname: '/', basePath: '/' });
  assert.equal(memory.personaFor('creator'), 'agency');
  let calls = 0;
  memory.subscribe(() => calls++);
  memory.notePage('/for/agencies');
  assert.equal(memory.persona(), 'agency');
  assert.equal(calls, 1, 'what personaFor answers changed, so subscribers are told');
  // A section that speaks to one audience now carries its own.
  assert.equal(memory.personaFor('creator'), 'creator');
});

test('without a link audience, a link carries its own section\u2019s audience, else the remembered one', () => {
  const memory = createLandingMemory();
  memory.remember(first); // landed on /for/creators with no ?for=
  assert.equal(memory.personaFor(), 'creator');
  assert.equal(memory.personaFor('educator'), 'educator');
  const none = createLandingMemory();
  none.remember({ search: '', referrer: '', pathname: '/', basePath: '/' });
  assert.equal(none.personaFor(), undefined);
  assert.equal(none.personaFor('business'), 'business');
  assert.equal(none.landingPage(), '/');
});

test('the module keeps exactly one memory per page life', () => {
  assert.equal(typeof landingMemory.remember, 'function');
  assert.equal(landingMemory.landing(), null, 'empty until a document remembers its landing');
});
