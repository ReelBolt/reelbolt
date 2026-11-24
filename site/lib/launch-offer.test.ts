import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import {
  FIRST_VIDEO_LINE,
  FOUNDER_PRICE_ENDS_AT,
  FOUNDER_PRICE_ENDS_ON,
  FOUNDER_PRICE_UNTIL,
  LAUNCH_OFFER,
  MONEY_BACK,
  founderPriceOpen,
  founderStripVisible,
  launchOfferCtaHref,
} from './launch-offer.ts';

// T-017's launch-offer strip used to be covered by `app/pricing/page.test.tsx`, which no runner ever
// executed (`node --test` cannot load `.tsx` — ERR_UNKNOWN_FILE_EXTENSION). The strip's copy and its
// destination now live here, where the site's own runner reaches them, and the page renders this
// module rather than repeating the words, so the copy has one source and cannot drift untested.

test('the strip names the offer and how long it lasts', () => {
  assert.equal(LAUNCH_OFFER.eyebrow, 'Founder price');
  assert.equal(LAUNCH_OFFER.heading, '50% off for new subscribers');
  assert.ok(LAUNCH_OFFER.body.includes(FOUNDER_PRICE_ENDS_ON), 'the body quotes the one date constant');
  assert.equal(FOUNDER_PRICE_UNTIL, 'Open to new subscribers until 31 January 2027');
});

test('the founder price hides after 23:59 on 31 January 2027, Rome time', () => {
  assert.equal(founderPriceOpen(new Date('2027-01-31T22:58:59Z')), true);
  assert.equal(founderPriceOpen(new Date('2027-01-31T23:00:00Z')), false);
  assert.equal(founderPriceOpen(new Date('2026-11-17T09:00:00Z')), true);
  assert.equal(Date.parse(FOUNDER_PRICE_ENDS_AT), Date.UTC(2027, 0, 31, 22, 59));
});

test('the strip hides at the end without a rebuild and never reappears', () => {
  assert.equal(founderStripVisible(true, new Date('2027-01-31T22:58:59Z')), true);
  assert.equal(founderStripVisible(true, new Date('2027-02-01T00:00:00Z')), false);
  assert.equal(founderStripVisible(false, new Date('2026-11-01T00:00:00Z')), false);
});
test('the money-back line links to the refunds clause and the first-video line states the grant', () => {
  assert.equal(MONEY_BACK.href, '/legal/terms#refunds');
  assert.match(MONEY_BACK.text, /14-day money-back guarantee/);
  assert.match(FIRST_VIDEO_LINE, /^Your first video is on us/);
  assert.match(FIRST_VIDEO_LINE, /750 credits/);
  assert.match(FIRST_VIDEO_LINE, /720p.*watermark/);
});

test('the strip says the discount is applied for the visitor, not entered by them', () => {
  assert.match(LAUNCH_OFFER.body, /applied automatically at checkout/i);
  assert.match(LAUNCH_OFFER.body, /no code to enter/i);
});

test('the strip sends the visitor to the signup path once signup is live', () => {
  assert.equal(launchOfferCtaHref(true), '/app/signup');
});

test('with signup off the strip falls back to the contact path', () => {
  assert.equal(launchOfferCtaHref(false), '/contact');
});

test('the strip states the discount and never a price', () => {
  const copy = `${LAUNCH_OFFER.heading} ${LAUNCH_OFFER.body} ${FIRST_VIDEO_LINE}`;
  assert.doesNotMatch(copy, /\$\s?\d/);
});

// The other half of T-017's rule: every price on /pricing comes from the catalog, so the page
// itself must not hand-write one. A figure typed into the page would be the one price that drifts
// from `lib/pricing`; this asserts the page carries none, leaving the catalog as the only source.
test('the pricing page hard-writes no price, so its figures come from the catalog', () => {
  const page = readFileSync(new URL('../components/pages/PricingPage.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(page, /\$\s?\d/);
});
