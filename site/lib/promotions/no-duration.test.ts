// Owner ruling: the founder discount's duration is never stated publicly. The site says "50% off, offer
// open until 31 January 2027" and nothing about how long the discount lasts, whatever the billing API
// returns for the coupon's duration ("forever", "repeating"/N months, "once").
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';

import { offerOpenUntil, parseActivePromotions, promoBadgeLabel, promoLabel } from './active.ts';
import * as planPrices from './plan-prices.ts';
import { FOUNDER_PRICE_ENDS_ON } from '../launch-offer.ts';

const NOW = new Date('2026-10-26T12:00:00.000Z');
const AFTER = new Date('2027-02-01T12:00:00.000Z');
const DURATION_WORDS = /forever|for life|lifetime|recurring|every (month|year)|first (payment|month|year|\d)|for \d+ months?|for the first|months?\b|years?\b/i;
const EXPECTED = `50% off, offer open until ${FOUNDER_PRICE_ENDS_ON}`;

const DURATIONS = [
  { durationKind: 'forever', durationMonths: null },
  { durationKind: 'months', durationMonths: 3 },
  { durationKind: 'months', durationMonths: 1 },
  { durationKind: 'once', durationMonths: null },
];

function raw(duration: Record<string, unknown>, headline: string | null): Record<string, unknown> {
  return {
    kind: 'planDiscount',
    headline,
    endsAt: null,
    discountType: 'percentage',
    percentOff: 50,
    bonusPercent: null,
    planKeys: [],
    packCredits: [],
    ...duration,
  };
}

test('the plan-discount label and badge never carry a duration, whatever the API returns', () => {
  for (const duration of DURATIONS) {
    for (const headline of [null, 'Launch offer: 50% off every plan, forever', '50% off for 3 months', 'Half price for the first year']) {
      const [p] = parseActivePromotions({ items: [raw(duration, headline)] }, NOW);
      assert.ok(p);
      assert.equal(promoLabel(p, NOW), EXPECTED, JSON.stringify({ duration, headline }));
      assert.equal(promoLabel(p, NOW).match(DURATION_WORDS), null);
      assert.equal(promoBadgeLabel(p, NOW), '50% off');
    }
  }
});

test('after the end date the label is the percentage alone, still without a duration', () => {
  const [p] = parseActivePromotions({ items: [raw({ durationKind: 'forever', durationMonths: null }, 'forever')] }, NOW);
  assert.equal(promoLabel(p, AFTER), '50% off');
  assert.equal(offerOpenUntil(AFTER), '');
});

test('the card line is the end date from the one launch-offer constant', () => {
  assert.equal(offerOpenUntil(NOW), 'Offer open until 31 January 2027');
});

test('nothing in the promo copy or the plan-price module can render a duration', () => {
  assert.equal('durationLabel' in planPrices, false);
  const copy = readFileSync(new URL('../../components/promo/copy.ts', import.meta.url), 'utf8');
  assert.doesNotMatch(copy, /duration|forever|for \$\{months\}/i);
  const price = readFileSync(new URL('../../components/promo/PlanPromoPrice.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(price, /durationLabel|durationKind|durationMonths/);
  const badge = readFileSync(new URL('../../components/promo/PlanPromoBadge.tsx', import.meta.url), 'utf8');
  assert.doesNotMatch(badge, /\bpromoLabel\(|\.headline/);
});
