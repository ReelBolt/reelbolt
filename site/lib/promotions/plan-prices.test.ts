import { test } from 'node:test';
import assert from 'node:assert/strict';

import { parsePlanPrices, planPriceFor, type PlanPrice } from './plan-prices.ts';

const NOW = new Date('2026-10-17T12:00:00.000Z');
const FUTURE = '2026-11-01T00:00:00.000Z';
const PAST = '2026-10-01T00:00:00.000Z';

function rawItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    planKey: 'pro',
    perSeat: false,
    currencyCode: 'USD',
    original: '$79.00',
    discounted: '$39.50',
    headline: 'Launch offer',
    durationKind: 'once',
    durationMonths: null,
    endsAt: null,
    ...overrides,
  };
}

function only(raw: Record<string, unknown>): PlanPrice[] {
  return parsePlanPrices({ items: [raw] }, NOW);
}

function price(overrides: Partial<PlanPrice> = {}): PlanPrice {
  return {
    planKey: 'pro',
    perSeat: false,
    currencyCode: 'USD',
    original: '$79.00',
    discounted: '$39.50',
    headline: null,
    durationKind: 'forever',
    durationMonths: null,
    endsAt: null,
    ...overrides,
  };
}

test('parsePlanPrices keeps a well-formed item and passes its strings through verbatim', () => {
  const [item] = only(rawItem({ endsAt: FUTURE, durationKind: 'months', durationMonths: 3 }));
  assert.deepEqual(item, {
    planKey: 'pro',
    perSeat: false,
    currencyCode: 'USD',
    original: '$79.00',
    discounted: '$39.50',
    headline: 'Launch offer',
    durationKind: 'months',
    durationMonths: 3,
    endsAt: FUTURE,
  });
});

test('parsePlanPrices keeps a per-seat item with a null headline and an open end date', () => {
  const [item] = only(rawItem({ planKey: 'team', perSeat: true, headline: null, durationKind: 'forever' }));
  assert.equal(item?.perSeat, true);
  assert.equal(item?.headline, null);
  assert.equal(item?.endsAt, null);
});

test('parsePlanPrices drops an item whose planKey, currencyCode, original or discounted is missing, empty or blank', () => {
  for (const field of ['planKey', 'currencyCode', 'original', 'discounted']) {
    for (const bad of [undefined, null, '', '   ', 42]) {
      assert.deepEqual(only(rawItem({ [field]: bad })), [], `${field} = ${JSON.stringify(bad)}`);
    }
  }
});

test('parsePlanPrices drops an item whose string is longer than 32 characters and keeps one of exactly 32', () => {
  assert.deepEqual(only(rawItem({ original: 'x'.repeat(33) })), []);
  assert.deepEqual(only(rawItem({ discounted: 'x'.repeat(33) })), []);
  assert.deepEqual(only(rawItem({ planKey: 'p'.repeat(33) })), []);
  const [atLimit] = only(rawItem({ original: '$'.repeat(32) }));
  assert.equal(atLimit?.original.length, 32);
});

test('parsePlanPrices counts code points for the 32-character limit', () => {
  assert.deepEqual(only(rawItem({ discounted: '😀'.repeat(33) })), []);
  assert.equal(only(rawItem({ discounted: '😀'.repeat(32) })).length, 1);
});

test('parsePlanPrices drops an item whose original equals its discounted price', () => {
  assert.deepEqual(only(rawItem({ original: '$79.00', discounted: '$79.00' })), []);
});

test('parsePlanPrices drops an item with a non-boolean perSeat', () => {
  assert.deepEqual(only(rawItem({ perSeat: 'false' })), []);
  assert.deepEqual(only(rawItem({ perSeat: undefined })), []);
});

test('parsePlanPrices drops an item with an unknown durationKind', () => {
  assert.deepEqual(only(rawItem({ durationKind: 'yearly' })), []);
  assert.deepEqual(only(rawItem({ durationKind: null })), []);
});

test('parsePlanPrices drops months without a positive whole durationMonths', () => {
  for (const bad of [undefined, null, 0, -1, 1.5, '3', Number.NaN]) {
    assert.deepEqual(only(rawItem({ durationKind: 'months', durationMonths: bad })), [], `months = ${String(bad)}`);
  }
});

test('parsePlanPrices keeps months with a positive durationMonths and nulls it for once and forever', () => {
  const [months] = only(rawItem({ durationKind: 'months', durationMonths: 1 }));
  assert.equal(months?.durationMonths, 1);
  const [once] = only(rawItem({ durationKind: 'once', durationMonths: 3 }));
  assert.equal(once?.durationMonths, null);
  const [forever] = only(rawItem({ durationKind: 'forever', durationMonths: 3 }));
  assert.equal(forever?.durationMonths, null);
});

test('parsePlanPrices drops an item whose endsAt is at or before now', () => {
  assert.deepEqual(only(rawItem({ endsAt: PAST })), []);
  assert.deepEqual(only(rawItem({ endsAt: NOW.toISOString() })), []);
  assert.deepEqual(only(rawItem({ endsAt: new Date(NOW.getTime() - 1000).toISOString() })), []);
});

test('parsePlanPrices drops an item whose endsAt is malformed, and keeps one still in the future', () => {
  assert.deepEqual(only(rawItem({ endsAt: 'not a date' })), []);
  assert.deepEqual(only(rawItem({ endsAt: 1234 })), []);
  assert.equal(only(rawItem({ endsAt: FUTURE })).length, 1);
});

test('parsePlanPrices keeps the valid items around a malformed one', () => {
  const items = parsePlanPrices(
    { items: [rawItem({ planKey: 'creator' }), rawItem({ original: '$5' , discounted: '$5' }), rawItem({ planKey: 'team', perSeat: true })] },
    NOW,
  );
  assert.deepEqual(items.map((p) => p.planKey), ['creator', 'team']);
});

test('parsePlanPrices never throws and reads anything but { items: [...] } as an empty list', () => {
  for (const junk of [null, undefined, 'items', 42, [], { items: 3 }, { items: 'nope' }, {}, { planPrices: [rawItem()] }]) {
    assert.deepEqual(parsePlanPrices(junk, NOW), [], JSON.stringify(junk));
  }
  assert.deepEqual(parsePlanPrices({ items: [null, 5, 'x', [], rawItem()] }, NOW).length, 1);
  const hostile = {
    get items(): never {
      throw new Error('boom');
    },
  };
  assert.deepEqual(parsePlanPrices(hostile, NOW), []);
});

test('planPriceFor returns the first match for a plan key, or null', () => {
  const first = price({ planKey: 'pro', discounted: '$39.50' });
  const second = price({ planKey: 'pro', discounted: '$1.00' });
  assert.equal(planPriceFor([first, second], 'pro'), first);
  assert.equal(planPriceFor([price({ planKey: 'creator' })], 'pro'), null);
  assert.equal(planPriceFor([], 'pro'), null);
});

test('a long or malformed headline never hides the plan price', async () => {
  const { parsePlanPrices } = await import('./plan-prices.ts');
  const base = { planKey: 'pro', perSeat: false, currencyCode: 'USD', original: '$79.00', discounted: '$39.50', durationKind: 'forever', durationMonths: null, endsAt: null };
  const long = parsePlanPrices({ items: [{ ...base, headline: 'Launch offer: 50% off every plan, forever, for everyone who joins this month' }] }, new Date());
  assert.equal(long.length, 1);
  const bad = parsePlanPrices({ items: [{ ...base, headline: 42 }] }, new Date());
  assert.equal(bad.length, 1);
  assert.equal(bad[0].headline, null);
});
