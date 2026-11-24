import { test } from 'node:test';
import assert from 'node:assert/strict';

import { ALPHA_NOTICE, SIGNUP_PATH, productVersion, siteCtas, siteConfig } from './site-config.ts';

// The alpha notice renders on every marketing page, and the component test that used to guard it
// (`components/layout/AlphaBanner.test.tsx`) was never executed: the site's runner is
// `node --test "lib/**/*.test.ts"`, and node refuses `.tsx` with ERR_UNKNOWN_FILE_EXTENSION. The
// string is asserted here, where the runner does reach it, so the notice cannot drift untested.
// `AlphaBanner.tsx` renders this same constant rather than a second copy of the sentence.

test('the shipped version is the pinned alpha literal', () => {
  assert.equal(productVersion, '0.4.0-alpha');
});

test('the alpha notice names the version and the early-access state', () => {
  assert.match(ALPHA_NOTICE, /0\.4\.0-alpha/);
  assert.match(ALPHA_NOTICE, /public alpha/);
});

// Decision D25: "Start free" is the primary CTA only once self-serve signup is deployed. These
// tests exist so the flag cannot be quietly faked — an unset/odd value must leave the coming-soon
// state (the contact path) in place, and only the literal string "true" may point at /app/signup.

test('with no flag set, the primary CTA is the contact path', () => {
  const ctas = siteCtas({});
  assert.equal(ctas.signupEnabled, false);
  assert.equal(ctas.primary.href, '/contact');
  assert.equal(ctas.primary.label, ctas.sales.label);
  assert.equal(ctas.primary.event, 'cta_talk_to_us');
});

test('only the literal string "true" enables signup', () => {
  for (const value of ['1', 'TRUE', 'True', 'yes', 'on', 'true ', '']) {
    const ctas = siteCtas({ NEXT_PUBLIC_SIGNUP_ENABLED: value });
    assert.equal(ctas.signupEnabled, false, `"${value}" must not enable signup`);
    assert.equal(ctas.primary.href, '/contact');
  }
});

test('with the flag on, the primary CTA is Start free and points at the signup route', () => {
  const ctas = siteCtas({ NEXT_PUBLIC_SIGNUP_ENABLED: 'true' });
  assert.equal(ctas.signupEnabled, true);
  assert.equal(ctas.primary.href, SIGNUP_PATH);
  assert.equal(ctas.primary.href, '/app/signup');
  assert.equal(ctas.primary.label, 'Start free');
  assert.equal(ctas.primary.event, 'cta_start_free');
});

test('the sales CTA is always the contact path, in both states', () => {
  for (const env of [{}, { NEXT_PUBLIC_SIGNUP_ENABLED: 'true' }]) {
    assert.equal(siteCtas(env).sales.href, '/contact');
    assert.equal(siteCtas(env).sales.label, 'Talk to us');
  }
});

test('siteConfig exposes the same CTA objects it built from the environment', () => {
  assert.deepEqual(siteConfig.primaryCta, siteCtas().primary);
  assert.deepEqual(siteConfig.salesCta, siteCtas().sales);
  assert.equal(siteConfig.signupEnabled, siteCtas().signupEnabled);
  assert.equal(siteConfig.signupPath, SIGNUP_PATH);
});
