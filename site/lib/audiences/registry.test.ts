import { test } from 'node:test';
import assert from 'node:assert/strict';
import { existsSync, readFileSync } from 'node:fs';

import {
  AUDIENCES,
  AUDIENCE_PATHS,
  AUDIENCE_SLUGS,
  CAPABILITIES,
  CAPABILITY_IDS,
  HOW_STEPS,
  PAID_GATES,
  audienceForSlug,
  isAudiencePath,
  isPaidGate,
} from './registry.ts';
import { LANDING_PERSONAS, PERSONA_LANDING_SLUGS, personaForPath } from '../attribution-persona.ts';
import { EVENT_DETAIL_MAX_LENGTH, pageKeyForPath, sanitizeEventDetail } from '../site-events.ts';
import { isTranslatedPath, localizePath, prefixedLocales } from '../../i18n/config.ts';

const read = (locale: string) =>
  JSON.parse(readFileSync(new URL(`../../messages/${locale}/audiences.json`, import.meta.url), 'utf8'));
const LOCALES = ['en', ...prefixedLocales];
const keys = (node: unknown) => Object.keys(node as Record<string, unknown>);

test('the audience pages are the closed set of the funnel contract', () => {
  // docs/design/new-user-funnel.md section 2: six personas with a landing page, `other` has none.
  assert.deepEqual(
    AUDIENCES.map(({ persona, slug, path }) => ({ persona, slug, path })),
    [
      { persona: 'creator', slug: 'creators', path: '/for/creators' },
      { persona: 'business', slug: 'small-business', path: '/for/small-business' },
      { persona: 'educator', slug: 'educators', path: '/for/educators' },
      { persona: 'nonprofit', slug: 'nonprofits', path: '/for/nonprofits' },
      { persona: 'software', slug: 'software-teams', path: '/for/software-teams' },
      { persona: 'agency', slug: 'agencies', path: '/for/agencies' },
    ],
  );
  assert.deepEqual(AUDIENCE_SLUGS, AUDIENCES.map((a) => a.slug));
  assert.deepEqual(AUDIENCE_PATHS, AUDIENCES.map((a) => a.path));
});

test('the registry and the tracking side agree on slug, persona and page key', () => {
  assert.deepEqual(AUDIENCES.map((a) => a.persona), LANDING_PERSONAS);
  for (const audience of AUDIENCES) {
    assert.equal(PERSONA_LANDING_SLUGS[audience.persona], audience.slug);
    assert.equal(audience.messageKey, audience.persona);
    assert.equal(audienceForSlug(audience.slug), audience);
    // The tracker derives the persona and the page-view key from the same path.
    assert.equal(personaForPath(audience.path), audience.persona);
    assert.equal(pageKeyForPath(audience.path), `for-${audience.slug}`);
  }
  for (const bad of ['', 'other', 'creator', 'CREATORS', 'creators/', '../creators', undefined, null]) {
    assert.equal(audienceForSlug(bad), undefined, String(bad));
  }
});

test('each page has two distinct CTA locations that the first-party click event accepts', () => {
  const seen = new Set<string>();
  for (const audience of AUDIENCES) {
    assert.equal(audience.cta.hero, `for-${audience.slug}-hero`);
    assert.equal(audience.cta.final, `for-${audience.slug}-final`);
    for (const location of [audience.cta.hero, audience.cta.final]) {
      // A token the sender drops would silently lose the click (lib/site-events).
      assert.equal(sanitizeEventDetail(location), location);
      assert.ok(location.length <= EVENT_DETAIL_MAX_LENGTH);
      assert.ok(!seen.has(location), `duplicate CTA location ${location}`);
      seen.add(location);
    }
  }
});

test('only the six audience paths are audience paths, and they exist in every language', () => {
  for (const path of AUDIENCE_PATHS) {
    assert.equal(isAudiencePath(path), true, path);
    assert.equal(isAudiencePath(`${path}/`), true, `${path}/`);
    assert.equal(isTranslatedPath(path), true, path);
    for (const locale of prefixedLocales) assert.equal(localizePath(locale, path), `/${locale}${path}`);
  }
  for (const path of ['/for', '/for/', '/for/other', '/for/creator', '/for/creators/more', '/features', '/it/for/creators']) {
    assert.equal(isAudiencePath(path), false, path);
  }
  assert.equal(isTranslatedPath('/for/other'), false);
});

test('both root layouts have the route, with static params from the registry', () => {
  for (const file of ['../../app/(en)/for/[audience]/page.tsx', '../../app/[locale]/for/[audience]/page.tsx']) {
    const url = new URL(file, import.meta.url);
    assert.ok(existsSync(url), `${file} is missing`);
    const source = readFileSync(url, 'utf8');
    assert.match(source, /export const dynamicParams = false;/, `${file}: an unknown slug must be a 404`);
    assert.match(source, /export const generateStaticParams = audience(Locale)?StaticParams;/);
  }
});

test('every capability a page lists is in the closed set, with no repeats', () => {
  for (const audience of AUDIENCES) {
    assert.ok(audience.capabilities.length >= 6, `${audience.slug} lists too little to be useful`);
    assert.equal(new Set(audience.capabilities).size, audience.capabilities.length, `${audience.slug} repeats a capability`);
    for (const id of audience.capabilities) assert.ok(CAPABILITY_IDS.includes(id), `${audience.slug}: ${id}`);
    // Free-trial capabilities lead; what needs a paid plan comes last, so nobody reads a paid
    // feature as the first thing they can do.
    const firstPaid = audience.capabilities.findIndex((id) => isPaidGate(CAPABILITIES[id].gate));
    if (firstPaid !== -1) {
      for (const id of audience.capabilities.slice(firstPaid)) {
        assert.ok(isPaidGate(CAPABILITIES[id].gate), `${audience.slug}: free capability ${id} listed after a paid one`);
      }
    }
  }
});

test('every capability is used by a page, so the catalogue holds no orphan claim', () => {
  const usedCapabilities = new Set(AUDIENCES.flatMap((a) => a.capabilities));
  assert.deepEqual(CAPABILITY_IDS.filter((id) => !usedCapabilities.has(id)), []);
});

test('the paid gates are exactly the gates that are not the free trial', () => {
  const gates = new Set(Object.values(CAPABILITIES).map((c) => c.gate));
  for (const gate of gates) {
    assert.equal(isPaidGate(gate), gate !== 'trial', gate);
  }
  for (const gate of PAID_GATES) assert.ok(gates.has(gate), `no capability uses the paid gate ${gate}`);
});

for (const locale of LOCALES) {
  test(`${locale}: the catalogue has words for every registry entry, and nothing else`, () => {
    const messages = read(locale);
    assert.deepEqual(keys(messages.capabilities).sort(), [...CAPABILITY_IDS].sort());
    for (const id of CAPABILITY_IDS) assert.deepEqual(keys(messages.capabilities[id]).sort(), ['description', 'title']);
    assert.deepEqual(keys(messages.items).sort(), AUDIENCES.map((a) => a.messageKey).sort());
    assert.deepEqual(keys(messages.steps).sort(), [...HOW_STEPS].sort());
    // Only the paid and trial gate labels the registry can produce.
    assert.deepEqual(keys(messages.page.make.gate).sort(), ['paid', 'trial']);

    for (const audience of AUDIENCES) {
      const item = messages.items[audience.messageKey];
      const where = `${locale} items.${audience.messageKey}`;
      assert.deepEqual(
        keys(item).sort(),
        ['after', 'audience', 'before', 'faq', 'final', 'hero', 'name', 'outcome', 'pain', 'seo', 'steps', 'who'],
        where,
      );
      // The counts the page reads by index are the counts the catalogue holds.
      assert.deepEqual(keys(item.before.points), Array.from({ length: audience.beforePoints }, (_, i) => String(i)), where);
      assert.deepEqual(keys(item.after.points), Array.from({ length: audience.afterPoints }, (_, i) => String(i)), where);
      assert.deepEqual(keys(item.faq), Array.from({ length: audience.faqs }, (_, i) => String(i)), where);
      assert.ok(audience.faqs >= 3 && audience.faqs <= 4, `${where}: three or four questions of its own`);
      for (const faq of Object.values(item.faq) as { q: string; a: string }[]) {
        assert.ok(faq.q.trim().endsWith('?'), `${where}: not a question: ${faq.q}`);
        assert.doesNotMatch(faq.a, /<[a-z/]/, `${where}: markup in an answer`);
      }
      // The first two steps are the audience's own; the third is shared.
      assert.deepEqual(keys(item.steps).sort(), ['drop', 'say'], where);
      assert.deepEqual(keys(item.seo).sort(), ['description', 'title'], where);
      assert.ok(item.seo.description.length <= 200, `${where}: meta description is too long`);
    }
  });
}

test('the home page counts match its catalogue', () => {
  const source = readFileSync(new URL('../../components/pages/HomePage.tsx', import.meta.url), 'utf8');
  const before = Number(/const BEFORE_POINTS = (\d+);/.exec(source)?.[1]);
  const after = Number(/const AFTER_POINTS = (\d+);/.exec(source)?.[1]);
  for (const locale of LOCALES) {
    const home = JSON.parse(readFileSync(new URL(`../../messages/${locale}/home.json`, import.meta.url), 'utf8'));
    assert.equal(keys(home.page.beforeAfter.before.points).length, before, locale);
    assert.equal(keys(home.page.beforeAfter.after.points).length, after, locale);
    assert.deepEqual(keys(home.page.steps).sort(), [...HOW_STEPS].sort(), locale);
  }
});
