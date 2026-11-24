import { test } from 'node:test';
import assert from 'node:assert/strict';

import {
  bannerPromotion,
  loadPromotionsBody,
  PLAN_PRICES_PATH,
  PROMOTIONS_PATH,
  parseActivePromotions,
  promoLabel,
  promotionsForPlan,
  type ActivePromotion,
} from './active.ts';
import { PROMO_COPY } from '../../components/promo/copy.ts';

const NOW = new Date('2026-10-17T12:00:00.000Z');
const FUTURE = '2026-11-01T00:00:00.000Z';

// A raw API item as the billing endpoint returns it (amountOffMinor included, as the API sends it).
function rawItem(overrides: Record<string, unknown> = {}): Record<string, unknown> {
  return {
    kind: 'planDiscount',
    headline: null,
    endsAt: null,
    discountType: 'percentage',
    percentOff: 20,
    amountOffMinor: 1000,
    currencyCode: 'USD',
    bonusPercent: null,
    planKeys: [],
    packCredits: [],
    durationKind: 'once',
    durationMonths: null,
    ...overrides,
  };
}

function promo(overrides: Partial<ActivePromotion> = {}): ActivePromotion {
  return {
    kind: 'planDiscount',
    headline: null,
    endsAt: null,
    discountType: 'percentage',
    percentOff: 20,
    bonusPercent: null,
    planKeys: [],
    packCredits: [],
    durationKind: 'once',
    durationMonths: null,
    ...overrides,
  };
}

test('parseActivePromotions drops a promotion that ended one second ago', () => {
  const endsAt = new Date(NOW.getTime() - 1000).toISOString();
  assert.deepEqual(parseActivePromotions({ items: [rawItem({ endsAt })] }, NOW), []);
});

test('parseActivePromotions drops a promotion whose endsAt is exactly now', () => {
  assert.deepEqual(parseActivePromotions({ items: [rawItem({ endsAt: NOW.toISOString() })] }, NOW), []);
});

test('parseActivePromotions keeps an open-ended promotion and one that has not ended', () => {
  const items = parseActivePromotions(
    { items: [rawItem({ endsAt: null }), rawItem({ endsAt: FUTURE, headline: 'Autumn' })] },
    NOW,
  );
  assert.equal(items.length, 2);
  assert.equal(items[0]?.endsAt, null);
  assert.equal(items[1]?.endsAt, FUTURE);
  assert.equal(items[1]?.headline, 'Autumn');
});

test('parseActivePromotions drops an unknown kind and keeps the valid items around it', () => {
  const items = parseActivePromotions(
    { items: [rawItem({ kind: 'planFreebie' }), rawItem({ kind: 'packBonus', packCredits: [100] })] },
    NOW,
  );
  assert.deepEqual(
    items.map((p) => p.kind),
    ['packBonus'],
  );
  assert.deepEqual(items[0]?.packCredits, [100]);
});

test('parseActivePromotions never includes amountOffMinor in the parsed shape', () => {
  const [item] = parseActivePromotions({ items: [rawItem()] }, NOW);
  assert.ok(item);
  assert.equal('amountOffMinor' in item, false);
});

test('parseActivePromotions survives null, strings, wrong shapes and junk items', () => {
  for (const junk of [null, undefined, 'items', 42, [], { items: 3 }, { items: 'nope' }, {}]) {
    assert.deepEqual(parseActivePromotions(junk, NOW), []);
  }
  const items = parseActivePromotions({ items: [null, 5, 'x', rawItem({ endsAt: 12 }), rawItem()] }, NOW);
  assert.equal(items.length, 1, 'only the well-formed item survives');
});

const CREATOR = { id: 'creator', kind: 'subscription' };
const PRO = { id: 'pro', kind: 'subscription' };
const TEAM = { id: 'team', kind: 'perSeat' };
const TRIAL = { id: 'trial', kind: 'trial' };
const ENTERPRISE = { id: 'enterprise', kind: 'contact' };

test('promotionsForPlan with empty planKeys badges every paid plan on sale (Creator, Pro, Team)', () => {
  const all = promo({ planKeys: [] });
  assert.equal(promotionsForPlan([all], CREATOR).length, 1);
  assert.equal(promotionsForPlan([all], PRO).length, 1);
  assert.equal(promotionsForPlan([all], TEAM).length, 1);
});

test('promotionsForPlan with empty planKeys never badges the trial or Enterprise plan', () => {
  const all = promo({ planKeys: [] });
  assert.equal(promotionsForPlan([all], TRIAL).length, 0);
  assert.equal(promotionsForPlan([all], ENTERPRISE).length, 0);
});

test('promotionsForPlan never badges trial or Enterprise even when they are listed in planKeys', () => {
  const listed = promo({ planKeys: ['trial', 'enterprise'] });
  assert.equal(promotionsForPlan([listed], TRIAL).length, 0);
  assert.equal(promotionsForPlan([listed], ENTERPRISE).length, 0);
});

test('promotionsForPlan with planKeys ["pro"] matches only pro', () => {
  const proOnly = promo({ planKeys: ['pro'] });
  assert.equal(promotionsForPlan([proOnly], PRO).length, 1);
  assert.equal(promotionsForPlan([proOnly], TEAM).length, 0);
});

test('parseActivePromotions trims the headline and keeps one of 120 characters or fewer as is', () => {
  const [spaced] = parseActivePromotions({ items: [rawItem({ headline: '  Autumn sale  ' })] }, NOW);
  assert.equal(spaced?.headline, 'Autumn sale');
  const exact = 'a'.repeat(120);
  const [atLimit] = parseActivePromotions({ items: [rawItem({ headline: exact })] }, NOW);
  assert.equal(atLimit?.headline, exact);
});

test('parseActivePromotions caps a long headline at 120 characters and ends the cut with an ellipsis', () => {
  const long = `  ${'Launch offer '.repeat(20)}  `;
  const [item] = parseActivePromotions({ items: [rawItem({ headline: long })] }, NOW);
  assert.ok(item?.headline);
  assert.equal(Array.from(item.headline).length, 120);
  assert.ok(item.headline.endsWith('…'));
  assert.equal(item.headline.startsWith('Launch offer Launch offer'), true);
});

test('parseActivePromotions counts code points, so an emoji is never split in half', () => {
  const [item] = parseActivePromotions({ items: [rawItem({ headline: '😀'.repeat(200) })] }, NOW);
  assert.equal(Array.from(item?.headline ?? '').length, 120);
  assert.ok(item?.headline?.endsWith('…'));
});

test('parseActivePromotions reads a blank headline as absent', () => {
  const [item] = parseActivePromotions({ items: [rawItem({ headline: '   ' })] }, NOW);
  assert.equal(item?.headline, null);
});

test('promotionsForPlan never matches pack promotions to a plan', () => {
  const packs = [promo({ kind: 'packDiscount' }), promo({ kind: 'packBonus', planKeys: [] })];
  assert.equal(promotionsForPlan(packs, PRO).length, 0);
});

test('bannerPromotion ignores a list that holds only packBonus promotions', () => {
  assert.equal(bannerPromotion([promo({ kind: 'packBonus', bonusPercent: 10 })]), null);
});

test('bannerPromotion returns the first planDiscount or packDiscount', () => {
  const bonus = promo({ kind: 'packBonus', bonusPercent: 10 });
  const pack = promo({ kind: 'packDiscount', headline: 'Packs' });
  const plan = promo({ kind: 'planDiscount', headline: 'Plans' });
  assert.equal(bannerPromotion([bonus, pack, plan]), pack);
  assert.equal(bannerPromotion([bonus, plan]), plan);
  assert.equal(bannerPromotion([]), null);
});

test('promoLabel uses the headline for a non-plan promotion when one is set', () => {
  assert.equal(promoLabel(promo({ kind: 'packDiscount', headline: 'Autumn sale' })), 'Autumn sale');
});

test('promoLabel falls back to "{percentOff}% off" for a pack percentage with no headline', () => {
  assert.equal(promoLabel(promo({ kind: 'packDiscount', discountType: 'percentage', percentOff: 20, headline: null })), '20% off');
});

test('promoLabel for a plan discount is the percentage and the end date, never the headline', () => {
  assert.equal(promoLabel(promo({ headline: 'Autumn sale', percentOff: 50 }), NOW), '50% off, offer open until 31 January 2027');
});

test('promoLabel never contains a dollar sign for a flat discount with no headline', () => {
  const label = promoLabel(promo({ discountType: 'flat', percentOff: null, headline: null }));
  assert.equal(label, PROMO_COPY.fallbackOffer);
  assert.equal(label.includes('$'), false);
});

test('promoLabel treats a blank headline as absent', () => {
  assert.equal(promoLabel(promo({ kind: 'packDiscount', headline: '   ', percentOff: 15 })), '15% off');
});

const EMPTY_BODY = { items: [], planPrices: [] };

function planPrice(overrides: Record<string, unknown> = {}): Record<string, unknown> {
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

/** A fake fetch that answers each path from `routes`; a route value of 'throw' rejects. */
function routedFetch(routes: Record<string, Response | 'throw'>, calls: string[] = []): typeof fetch {
  return (async (url: string | URL | Request) => {
    const path = new URL(String(url)).pathname;
    calls.push(String(url));
    const route = routes[path];
    if (route === undefined) return new Response('{}', { status: 404 });
    if (route === 'throw') throw new TypeError('fetch failed');
    return route;
  }) as typeof fetch;
}

const ok = (body: unknown) => new Response(JSON.stringify(body), { status: 200 });

test('loadPromotionsBody with no API URL answers empty lists without fetching', async () => {
  let called = false;
  const fakeFetch = (async () => {
    called = true;
    return new Response('{}');
  }) as typeof fetch;
  assert.deepEqual(await loadPromotionsBody(undefined, { fetch: fakeFetch }), EMPTY_BODY);
  assert.deepEqual(await loadPromotionsBody('   ', { fetch: fakeFetch }), EMPTY_BODY);
  assert.equal(called, false);
});

test('loadPromotionsBody maps a 404 (billing off) and a network error to empty lists on both sides', async () => {
  const notFound = (async () => new Response('', { status: 404 })) as typeof fetch;
  const broken = (async () => {
    throw new TypeError('fetch failed');
  }) as typeof fetch;
  assert.deepEqual(await loadPromotionsBody('http://api', { fetch: notFound }), EMPTY_BODY);
  assert.deepEqual(await loadPromotionsBody('http://api', { fetch: broken }), EMPTY_BODY);
});

test('loadPromotionsBody maps a 200 with junk on both sides to empty lists', async () => {
  const junk = (async () => new Response('<html>oops</html>', { status: 200 })) as typeof fetch;
  assert.deepEqual(await loadPromotionsBody('http://api', { fetch: junk }), EMPTY_BODY);
});

test('loadPromotionsBody fetches both endpoints in parallel and merges them into one body', async () => {
  const calls: string[] = [];
  const fake = routedFetch(
    {
      [PROMOTIONS_PATH]: ok({ items: [rawItem({ endsAt: FUTURE, headline: 'Live' }), rawItem({ endsAt: '2026-01-01T00:00:00Z' })] }),
      [PLAN_PRICES_PATH]: ok({ items: [planPrice(), planPrice({ planKey: 'team', perSeat: true, original: '$49.00', discounted: '$24.50' })] }),
    },
    calls,
  );
  const result = await loadPromotionsBody('http://api:8080/', { fetch: fake, now: () => NOW });
  assert.deepEqual(calls.sort(), [`http://api:8080${PLAN_PRICES_PATH}`, `http://api:8080${PROMOTIONS_PATH}`].sort());
  assert.equal(result.items.length, 1);
  assert.equal(result.items[0]?.headline, 'Live');
  assert.deepEqual(result.planPrices.map((p) => p.planKey), ['pro', 'team']);
  assert.equal(result.planPrices[1]?.perSeat, true);
});

test('loadPromotionsBody gives the promotions side empty when only its own endpoint fails', async () => {
  const fake = routedFetch({
    [PROMOTIONS_PATH]: 'throw',
    [PLAN_PRICES_PATH]: ok({ items: [planPrice()] }),
  });
  const result = await loadPromotionsBody('http://api', { fetch: fake, now: () => NOW });
  assert.deepEqual(result.items, []);
  assert.equal(result.planPrices.length, 1);
});

test('loadPromotionsBody gives the plan prices side empty when only its own endpoint fails', async () => {
  const fake = routedFetch({
    [PROMOTIONS_PATH]: ok({ items: [rawItem()] }),
    [PLAN_PRICES_PATH]: new Response('boom', { status: 500 }),
  });
  const result = await loadPromotionsBody('http://api', { fetch: fake, now: () => NOW });
  assert.equal(result.items.length, 1);
  assert.deepEqual(result.planPrices, []);
});

test('loadPromotionsBody drops ended plan prices and malformed ones from a 200', async () => {
  const fake = routedFetch({
    [PROMOTIONS_PATH]: ok({ items: [] }),
    [PLAN_PRICES_PATH]: ok({
      items: [planPrice({ endsAt: '2026-01-01T00:00:00Z' }), planPrice({ original: '$39.50' }), planPrice({ planKey: 'creator' })],
    }),
  });
  const result = await loadPromotionsBody('http://api', { fetch: fake, now: () => NOW });
  assert.deepEqual(result.planPrices.map((p) => p.planKey), ['creator']);
});

test('loadPromotionsBody attaches a timeout signal to both requests', async () => {
  const signals: unknown[] = [];
  const fake = (async (_url: string | URL | Request, init?: RequestInit) => {
    signals.push(init?.signal);
    return ok({ items: [] });
  }) as typeof fetch;
  await loadPromotionsBody('http://api', { fetch: fake, now: () => NOW });
  assert.equal(signals.length, 2);
  for (const signal of signals) assert.ok(signal instanceof AbortSignal, 'a timeout signal is attached');
});

test('the edge-cache key drops the query string, so ?x= cannot bypass the cache', async () => {
  const { promotionsCacheKey } = await import('./active.ts');
  assert.equal(promotionsCacheKey('https://reelbolt.ai/api/promotions?x=123'), 'https://reelbolt.ai/api/promotions');
  assert.equal(promotionsCacheKey('https://reelbolt.ai/api/promotions'), 'https://reelbolt.ai/api/promotions');
});

test('a partial upstream answer is reported as incomplete so it is never cached', async () => {
  const { loadPromotionsResult } = await import('./active.ts');
  const fetchOk = (async (url: string) =>
    new Response(JSON.stringify({ items: [] }), { status: url.includes('plan-prices') ? 500 : 200 })) as unknown as typeof fetch;
  const partial = await loadPromotionsResult('http://api', { fetch: fetchOk });
  assert.equal(partial.complete, false);
  const fetchBoth = (async () => new Response(JSON.stringify({ items: [] }), { status: 200 })) as unknown as typeof fetch;
  assert.equal((await loadPromotionsResult('http://api', { fetch: fetchBoth })).complete, true);
});
