import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import {
  CAMPAIGN_PARAMS,
  LANDING_PARAM,
  decorateCarryHref,
  decorateEntryHref,
  isEntryHref,
  isSignupHref,
  isSitePageHref,
} from './attribution-signup-link.ts';

const landing = {
  source: 'hackernews',
  medium: 'community',
  campaign: 'launch-2026-11',
  content: 'hn-launch',
  term: 'video',
  referrerHost: 'news.ycombinator.com',
  landingPath: '/for/creators',
};

test('only the signup page is a signup href', () => {
  for (const href of ['/app/signup', '/app/signup/', '/app/signup?plan=pro', '/app/signup#top']) {
    assert.equal(isSignupHref(href), true, href);
  }
  for (const href of ['/contact', '/app/login', '/app/signup-now', '/app', 'https://reelbolt.ai/app/signup', '/it/app/signup', '']) {
    assert.equal(isSignupHref(href), false, href);
  }
});

test('the persona, the five campaign parameters and the landing path are appended, in a fixed order', () => {
  assert.equal(
    decorateEntryHref('/app/signup', { persona: 'creator', landing }),
    '/app/signup?for=creator&utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&utm_term=video&lp=%2Ffor%2Fcreators',
  );
  assert.deepEqual(CAMPAIGN_PARAMS.map(([param]) => param), ['utm_source', 'utm_medium', 'utm_campaign', 'utm_content', 'utm_term']);
});

test('the dashboard reads the landing path under the same name and the same rule', (t) => {
  // The dashboard's reader (web/lib/auth/signup-carry.ts) is the other half of this contract. It
  // sits outside this package, so a copy of site/ on its own skips the comparison.
  let web: string;
  try {
    web = readFileSync(new URL('../../web/lib/auth/signup-carry.ts', import.meta.url), 'utf8');
  } catch {
    t.skip('web/ is not next to site/ in this checkout');
    return;
  }
  assert.match(web, new RegExp(`export const LANDING_PARAM = '${LANDING_PARAM}';`));
  assert.match(web, /const landingPath = sanitizeLandingPath\(single\(params\[LANDING_PARAM\]\)\);/);
  assert.match(web, /if \(campaign && carry\.landingPath\) pairs\.push\(\[LANDING_PARAM, carry\.landingPath\]\)/);
  // Both sides accept the same paths.
  const pathRule = /const PATH_RE = (\/.+\/);/;
  const own = readFileSync(new URL('./attribution.ts', import.meta.url), 'utf8');
  const theirs = readFileSync(new URL('../../web/lib/attribution/index.ts', import.meta.url), 'utf8');
  assert.equal(pathRule.exec(theirs)?.[1], pathRule.exec(own)?.[1]);
  assert.ok(pathRule.exec(own)?.[1]);
});

test('the landing path travels as lp, next to any campaign parameter', () => {
  assert.equal(LANDING_PARAM, 'lp');

  // One campaign parameter is enough, whichever it is; the value reads back as the plain path.
  for (const field of ['source', 'medium', 'campaign', 'content', 'term'] as const) {
    const href = decorateEntryHref('/app/signup', { landing: { [field]: 'x1', landingPath: '/it/for/creators' } });
    assert.equal(new URLSearchParams(href.split('?')[1]).get('lp'), '/it/for/creators', field);
    assert.ok(href.endsWith('&lp=%2Fit%2Ffor%2Fcreators'), href);
  }
  // The home page is a landing page like any other.
  assert.equal(decorateEntryHref('/app/signup', { landing: { source: 'x', landingPath: '/' } }), '/app/signup?utm_source=x&lp=%2F');
});

test('the landing path never travels without a campaign parameter', () => {
  // On its own it would describe a touch with no source behind it.
  assert.equal(decorateEntryHref('/app/signup', { landing: { referrerHost: 'example.org', landingPath: '/pricing' } }), '/app/signup');
  assert.equal(decorateEntryHref('/app/signup', { persona: 'agency', landing: { landingPath: '/for/agencies' } }), '/app/signup?for=agency');
  assert.equal(decorateCarryHref('/it/pricing', { persona: 'agency', landing: { landingPath: '/pricing' } }), '/it/pricing?for=agency');
  // A campaign value that does not survive sanitizing is no campaign.
  assert.equal(decorateEntryHref('/app/signup', { landing: { source: 'bad value!', landingPath: '/pricing' } }), '/app/signup');
  // A campaign parameter the href came with counts: the link has a source, so it may say where the visit began.
  assert.equal(
    decorateEntryHref('/app/signup?utm_source=newsletter', { landing: { landingPath: '/pricing' } }),
    '/app/signup?utm_source=newsletter&lp=%2Fpricing',
  );
});

test('lp is a path only: never a query, a fragment, a host or the referrer', () => {
  const lp = (landingPath: string) =>
    new URLSearchParams(decorateEntryHref('/app/signup', { landing: { source: 'x', landingPath } }).split('?')[1]).get('lp');
  assert.equal(lp('/Pricing?plan=pro&email=a@b.c#faq'), '/pricing');
  assert.equal(lp('/blog/some-post#top'), '/blog/some-post');
  for (const bad of ['//evil.example/x', 'https://evil.example/', 'for/creators', '/a b', '/caf\u00e9', `/${'a'.repeat(120)}`, '']) {
    assert.equal(lp(bad), null, bad);
  }
  // The referrer host is never in the link, with or without a campaign.
  const href = decorateEntryHref('/app/signup', { landing: { source: 'x', referrerHost: 'news.ycombinator.com', landingPath: '/' } });
  assert.doesNotMatch(href, /ycombinator|referr/);
  // An lp the href already has is kept as it is, and decorating twice adds nothing.
  assert.equal(decorateEntryHref('/app/signup?utm_source=x&lp=%2Fpricing', { landing: { landingPath: '/for/creators' } }), '/app/signup?utm_source=x&lp=%2Fpricing');
});

test('an existing query such as ?plan= is kept byte for byte, and so is a fragment', () => {
  assert.equal(
    decorateEntryHref('/app/signup?plan=creator', { persona: 'software', landing: { source: 'x' } }),
    '/app/signup?plan=creator&for=software&utm_source=x',
  );
  assert.equal(
    decorateEntryHref('/app/signup?plan=pro&next=%2Fapp%2Fbilling#top', { landing: { campaign: 'c' } }),
    '/app/signup?plan=pro&next=%2Fapp%2Fbilling&utm_campaign=c#top',
  );
});

test('nothing to carry leaves the href exactly as it was', () => {
  for (const href of ['/app/signup', '/app/signup?plan=team', '/app/signup#x']) {
    assert.equal(decorateEntryHref(href, {}), href);
    assert.equal(decorateEntryHref(href, { persona: null, landing: null }), href);
    assert.equal(decorateEntryHref(href, { persona: undefined, landing: {} }), href);
  }
});

test('invalid values are dropped, valid ones are normalised', () => {
  assert.equal(
    decorateEntryHref('/app/signup', {
      persona: 'other',
      landing: { source: 'bad value!', medium: ' Social ', campaign: '-leading', content: 'a'.repeat(65), term: 'ok' },
    }),
    '/app/signup?utm_medium=social&utm_term=ok',
  );
  assert.equal(decorateEntryHref('/app/signup', { persona: '<script>' }), '/app/signup');
  assert.equal(decorateEntryHref('/app/signup', { persona: ' Agency ' }), '/app/signup?for=agency');
});

test('a plus sign in a campaign value survives the query string', () => {
  const href = decorateEntryHref('/app/signup', { landing: { campaign: 'launch+week' } });
  assert.equal(href, '/app/signup?utm_campaign=launch%2Bweek');
  assert.equal(new URLSearchParams(href.split('?')[1]).get('utm_campaign'), 'launch+week');
});

test('decorating twice changes nothing, and a parameter already on the href is never overwritten', () => {
  const carry = { persona: 'creator', landing };
  const once = decorateEntryHref('/app/signup?plan=creator', carry);
  assert.equal(decorateEntryHref(once, carry), once);
  assert.equal(decorateEntryHref(once, { persona: 'agency', landing: { source: 'other-source' } }), once);
  assert.equal(
    decorateEntryHref('/app/signup?for=educator&utm_source=newsletter', carry),
    '/app/signup?for=educator&utm_source=newsletter&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&utm_term=video&lp=%2Ffor%2Fcreators',
  );
});

test('a link that goes to neither signup nor sign-in is never touched', () => {
  const carry = { persona: 'creator', landing };
  for (const href of ['/contact', '/contact?topic=enterprise', '/app', '/app/dashboard', '/pricing', 'https://example.org/app/signup']) {
    assert.equal(decorateEntryHref(href, carry), href);
  }
});

test('the sign-in link carries the same values: a provider sign-in can create the account', () => {
  for (const href of ['/app/login', '/app/login/', '/app/login?next=%2Fapp%2Fbilling']) assert.equal(isEntryHref(href), true, href);
  for (const href of ['/app/signup', '/app/signup?plan=pro']) assert.equal(isEntryHref(href), true, href);
  for (const href of ['/app/login-help', '/app', '/login', 'https://reelbolt.ai/app/login']) assert.equal(isEntryHref(href), false, href);
  assert.equal(
    decorateEntryHref('/app/login', { persona: 'educator', landing: { source: 'newsletter', content: 'nl-1' } }),
    '/app/login?for=educator&utm_source=newsletter&utm_content=nl-1',
  );
  // And the landing page of the visit, exactly as the signup link does.
  assert.equal(
    decorateEntryHref('/app/login?next=%2Fapp%2Fbilling', { landing: { source: 'newsletter', content: 'nl-1', landingPath: '/for/educators' } }),
    '/app/login?next=%2Fapp%2Fbilling&utm_source=newsletter&utm_content=nl-1&lp=%2Ffor%2Feducators',
  );
  // The site's own "Sign in" links go through the decorating component, never a bare anchor.
  for (const file of ['../components/layout/SiteHeader.tsx', '../components/layout/SiteFooter.tsx']) {
    const text = readFileSync(new URL(file, import.meta.url), 'utf8');
    assert.doesNotMatch(text, /href=\{siteConfig\.loginPath\}/, file);
    assert.match(text, /<SignInLink/, file);
  }
});

test('a same-site page link that opens a new document carries the audience and the campaign', () => {
  const carry = { persona: 'creator', landing };
  const query = 'for=creator&utm_source=hackernews&utm_medium=community&utm_campaign=launch-2026-11&utm_content=hn-launch&utm_term=video&lp=%2Ffor%2Fcreators';
  assert.equal(decorateCarryHref('/it/for/creators', carry), `/it/for/creators?${query}`);
  assert.equal(decorateCarryHref('/blog', carry), `/blog?${query}`);
  assert.equal(decorateCarryHref('/', carry), `/?${query}`);
  assert.equal(decorateCarryHref('/legal/privacy#cookies', carry), `/legal/privacy?${query}#cookies`);
  // What the next document reads back is exactly what was carried: nothing is added on the way.
  const next = new URL(`https://reelbolt.ai${decorateCarryHref('/it', carry)}`);
  assert.equal(next.searchParams.get('for'), 'creator');
  assert.equal(next.searchParams.get('utm_content'), 'hn-launch');
  // Including where the visit began, which the next document could not know by itself.
  assert.equal(next.searchParams.get('lp'), '/for/creators');
  // A parameter already there is kept, and decorating twice changes nothing.
  const once = decorateCarryHref('/blog?utm_source=own', carry);
  assert.match(once, /^\/blog\?utm_source=own&for=creator&utm_medium=community/);
  assert.equal(decorateCarryHref(once, carry), once);
  // Nothing to carry: the href is the one the server rendered.
  assert.equal(decorateCarryHref('/it/pricing', {}), '/it/pricing');
});

test('only pages of this site are carry targets', () => {
  for (const href of ['/', '/blog', '/it', '/it/pricing?x=1', '/legal/terms#top', '/for/creators']) {
    assert.equal(isSitePageHref(href), true, href);
  }
  for (const href of ['/app', '/app/signup', '/api/contact', '/docs/', '/docs/it/', '/l/hn-launch', '//evil.example/x', 'https://example.org/', 'mailto:a@b.c', '#faq', '']) {
    assert.equal(isSitePageHref(href), false, href);
    assert.equal(decorateCarryHref(href, { persona: 'creator', landing }), href);
  }
  // The two full-load links of the site use it: the language switcher, and a link that leaves a
  // translated page for an English-only one.
  assert.match(readFileSync(new URL('../components/layout/LanguageSwitcher.tsx', import.meta.url), 'utf8'), /useCarryHref\(localizePath\(locale\.code, target\)\)/);
  assert.match(readFileSync(new URL('../i18n/link.tsx', import.meta.url), 'utf8'), /leavesLocaleLayout\(locale, href\)\) return <CarryLink/);
});
