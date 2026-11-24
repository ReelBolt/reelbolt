import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
// A plain build script (.mjs) with JSDoc types, shared with scripts/build-pages.mjs.
import { pagesRedirects } from '../scripts/pages-redirects.mjs';

import { SIGNUP_PATH, siteCtas } from './site-config.ts';

test('with signup off, a stray /app link on the static bundle goes to the contact page', () => {
  for (const env of [{}, { NEXT_PUBLIC_SIGNUP_ENABLED: 'false' }, { NEXT_PUBLIC_SIGNUP_ENABLED: '' }, { NEXT_PUBLIC_SIGNUP_ENABLED: 'TRUE' }]) {
    assert.equal(pagesRedirects(env), '/app /contact 302\n/app/* /contact 302\n');
    // The same reading of the variable as the CTA switch: these are builds whose button is "Talk to us".
    assert.equal(siteCtas(env).signupEnabled, false);
  }
});

test('with signup on, no rule sends the signup link anywhere else', () => {
  const env = { NEXT_PUBLIC_SIGNUP_ENABLED: 'true' };
  assert.equal(siteCtas(env).primary.href, SIGNUP_PATH);
  const rules = pagesRedirects(env);
  // Not one line may match /app: the "Start free" links carry ?for= and utm_* to /app/signup, and
  // a redirect to /contact would end the funnel there without an error.
  assert.doesNotMatch(rules, /^\/app/m);
  assert.equal(rules, '');
});

test('the Pages build writes the file from that one function, in both states', () => {
  const build = readFileSync(new URL('../scripts/build-pages.mjs', import.meta.url), 'utf8');
  assert.match(build, /writeFileSync\('out\/_redirects', pagesRedirects\(process\.env\)\);/);
  assert.doesNotMatch(build, /\/app \/contact/);
});
