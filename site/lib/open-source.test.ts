import assert from 'node:assert/strict';
import { test } from 'node:test';
import { siteConfig } from './site-config.ts';
import { buildLlmsTxt } from './llms.ts';
import { alternateLanguages, isTranslatedPath, localizePath } from '../i18n/config.ts';

test('the nav lists Open source and keeps Contact in the mobile menu only', () => {
  const labels = siteConfig.nav.map((item) => item.label);
  assert.ok(labels.includes('Open source'));
  const contact = siteConfig.nav.find((item) => item.label === 'Contact');
  assert.ok(contact && 'desktop' in contact && contact.desktop === false);
});

test('the repository link is the public GitHub repo', () => {
  assert.equal(siteConfig.repoUrl, 'https://github.com/ReelBolt/reelbolt');
});

test('llms.txt no longer sells self-hosting as Enterprise-only', () => {
  const text = buildLlmsTxt({
    siteUrl: 'https://reelbolt.ai',
    name: 'ReelBolt',
    shortDescription: 'x',
    cta: { label: 'Talk to us', href: '/contact' },
    plans: [],
    posts: [],
    faqs: { home: [], pricing: [] },
    runnerAvailable: false,
    runnerPath: '/runner',
  } as unknown as Parameters<typeof buildLlmsTxt>[0]);
  assert.doesNotMatch(text, /Enterprise customers can self-host/);
  assert.match(text, /## Open source/);
  assert.match(text, /AGPL-3\.0/);
});

test('the open-source page is translated: both languages are listed with hreflang alternates', () => {
  assert.equal(isTranslatedPath('/open-source'), true);
  assert.deepEqual(Object.keys(alternateLanguages('/open-source')).sort(), ['en', 'it', 'x-default']);
  assert.equal(localizePath('it', '/open-source'), '/it/open-source');
});
