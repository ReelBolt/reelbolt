// The GA4 events the marketing site can send, pinned. docs/release/soft-open-funnel.md lists exactly
// these, and says what does NOT fire (no sign_up, begin_checkout or purchase: the site has no checkout
// and no account, and the app's GA code sends page views only). Adding a GA event means changing this
// list and that page on purpose, with the privacy page's description of what Google receives in view.

import assert from 'node:assert/strict';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import { test } from 'node:test';

const siteRoot = new URL('..', import.meta.url).pathname;

function sources(dir: string, out: string[] = []): string[] {
  for (const name of readdirSync(dir)) {
    if (name === 'node_modules' || name === '.next' || name === 'out' || name.startsWith('.')) continue;
    const path = join(dir, name);
    if (statSync(path).isDirectory()) sources(path, out);
    else if (/\.(ts|tsx)$/.test(name) && !/\.test\.(ts|tsx)$/.test(name) && !name.endsWith('.d.ts')) out.push(path);
  }
  return out;
}

const files = sources(siteRoot).map((path) => ({ path, text: readFileSync(path, 'utf8') }));

function literalCalls(call: RegExp): string[] {
  const names = new Set<string>();
  for (const { text } of files) for (const m of text.matchAll(call)) names.add(m[1]);
  return [...names].sort();
}

test('the custom events the site sends through trackEvent are the three it has always sent', () => {
  // trackEvent('name', ...) literals, plus the call-to-action event names declared in site-config.
  const direct = literalCalls(/trackEvent\(\s*'([a-z_]+)'/g);
  const ctaNames = literalCalls(/event:\s*'(cta_[a-z_]+)'/g);
  assert.deepEqual([...direct, ...ctaNames].sort(), ['cta_start_free', 'cta_talk_to_us', 'generate_lead', 'page_view']);
});

test('trackEvent is only ever called with a literal name or the primary CTA event', () => {
  for (const { path, text } of files) {
    if (path.endsWith('lib/gtag.ts')) continue;
    for (const m of text.matchAll(/trackEvent\(\s*([^'\s][^,)]*)/g)) {
      assert.ok(['event', 'siteConfig.primaryCta.event'].includes(m[1].trim()), `${path}: trackEvent(${m[1]})`);
    }
  }
});

test('nothing in the site sends a GA4 commerce or sign-up event, or calls gtag directly for one', () => {
  for (const { path, text } of files) {
    assert.doesNotMatch(text, /['"](purchase|begin_checkout|add_payment_info|sign_up|login|generate_lead_value)['"]/, path);
  }
  // The only direct gtag('event', ...) call site is trackEvent itself, which checks the stored consent first.
  const direct = files.filter(({ text }) => /gtag(\?\.)?\(\s*'event'/.test(text)).map(({ path }) => path);
  assert.deepEqual(direct.map((p) => p.replace(siteRoot, '')), ['lib/gtag.ts']);
});
