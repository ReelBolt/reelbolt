/**
 * Promo plan prices on /pricing (saas-marketing-refresh, plan-promo-prices.md) on the isolated stack:
 *
 *   source e2e/.stack/<project>/env.sh
 *   npx playwright test tests/site/promo-prices.spec.ts --project=desktop --workers=1
 *
 * The API caches plan prices for 60 s, so every check that follows a change waits with expect.poll
 * (75 s). Money strings are compared against the API's Paddle strings, never recomputed.
 */
import { expect, request as pwRequest, test, type APIRequestContext, type Browser, type BrowserContext, type Locator, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { loadStack, loginAs } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.setTimeout(300_000);

// ------------------------------------------------------------------ fixtures

interface CatalogPlan {
  id: string;
  name: string;
  kind: string;
  priceMonthly?: number;
}
const catalogPlans: CatalogPlan[] = JSON.parse(
  fs.readFileSync(path.resolve(__dirname, '../../../site/lib/pricing/plans.catalog.json'), 'utf8'),
).plans;
/** Catalog prices, in major units, as the Offer markup carries them. The trial is free: 0 (PlanCard shows 0). */
const catalogPrices = [0, ...catalogPlans.filter((p) => typeof p.priceMonthly === 'number').map((p) => p.priceMonthly as number)];

const CARD_NAME: Record<string, string> = { creator: 'Creator', pro: 'Pro', team: 'Team' };
const CARDS = ['Trial', 'Creator', 'Pro', 'Team', 'Enterprise'] as const;
const PAID = ['Creator', 'Pro', 'Team'] as const;
const VIEWPORTS = { desktop: { width: 1280, height: 800 }, mobile: { width: 390, height: 844 } } as const;

function randomSuffix(length = 6): string {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  let out = '';
  for (let i = 0; i < length; i++) out += alphabet[Math.floor(Math.random() * alphabet.length)];
  return out;
}

const suffix = randomSuffix();
const created: string[] = [];
let admin: BrowserContext;
let anon: APIRequestContext;
let promotionA = '';
let promotionB = '';
/** Block heights per viewport, measured with the promotion live and with none. */
const measured: { live: Record<string, Record<string, CardSnap>>; none: Record<string, Record<string, CardSnap>> } = { live: {}, none: {} };

test.beforeAll(async ({ browser }) => {
  const { baseUrl } = loadStack();
  admin = await browser.newContext({ baseURL: baseUrl });
  await loginAs(admin, 'admin');
  anon = await pwRequest.newContext({ baseURL: baseUrl });
});

test.afterAll(async () => {
  for (const id of created) {
    // Archiving is idempotent; a promotion already archived by its test answers an error we do not need.
    await admin.request.patch(`${loadStack().baseUrl}/api/v1/billing/admin/promotions/${id}`, { data: { status: 'archived' } });
  }
  // Only the fault knob is reset. A full POST /__mock/reset drops the seeded subscriptions other specs use.
  await setPreviewMode('ok');
  await admin?.close();
  await anon?.dispose();
});

// ------------------------------------------------------------------ helpers

async function setPreviewMode(mode: 'ok' | 'error' | 'tax'): Promise<void> {
  const res = await fetch(`${loadStack().mockUrl}/__mock/pricing-preview/mode`, {
    method: 'POST',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ mode }),
  });
  expect(res.status, `set pricing-preview mode ${mode}`).toBe(200);
}

/** The anonymous public endpoint, as the site's same-origin proxy sees it. */
async function planPrices(): Promise<any[]> {
  const res = await anon.get('/api/v1/billing/promotions/plan-prices');
  expect(res.status(), 'GET /api/v1/billing/promotions/plan-prices').toBe(200);
  return ((await res.json()).items as any[]) ?? [];
}

function keysOf(items: any[]): string[] {
  return items.map((i) => i.planKey).sort();
}

function window(): { startsAt: string; endsAt: string } {
  const now = Date.now();
  return { startsAt: new Date(now - 60_000).toISOString(), endsAt: new Date(now + 60 * 60_000).toISOString() };
}

async function createPromotion(data: Record<string, unknown>): Promise<string> {
  const res = await admin.request.post(`${loadStack().baseUrl}/api/v1/billing/admin/promotions`, { data });
  expect(res.status(), await res.text()).toBeLessThan(300);
  const id = (await res.json()).id as string;
  expect(id, 'created promotion id').toBeTruthy();
  created.push(id);
  return id;
}

async function archive(id: string): Promise<void> {
  const res = await admin.request.patch(`${loadStack().baseUrl}/api/v1/billing/admin/promotions/${id}`, { data: { status: 'archived' } });
  expect(res.status(), await res.text()).toBeLessThan(300);
}

/** The plan card on /pricing: the smallest flex column holding the plan's H3 (as marketing.spec). */
function planCard(page: Page, name: string): Locator {
  return page
    .locator('div[class*="h-full"][class*="flex-col"]')
    .filter({ has: page.getByRole('heading', { level: 3, name, exact: true }) })
    .last();
}

/**
 * The price block of a card. PlanPromoPrice has no test hook, so the block is found from its figure
 * (the span with the font-display text-4xl classes), whose parent is the flex row and grandparent the
 * block itself (`div.mt-4`). The same locator is used with and without a promotion.
 */
function priceBlock(card: Locator): Locator {
  return card.locator('span.font-display.text-4xl').first().locator('xpath=../..');
}

interface CardSnap {
  strikes: string[];
  figure: string | null;
  duration: string;
  height: number | null;
  text: string;
}

async function readCard(page: Page, name: string): Promise<CardSnap> {
  const card = planCard(page, name);
  await card.waitFor({ state: 'visible', timeout: 15_000 });
  // The visible struck price only: the sr-only "Was" prefix is for screen readers.
  const strikes = await card.locator('s').evaluateAll((els) =>
    els.map((el) => {
      const copy = el.cloneNode(true) as HTMLElement;
      copy.querySelectorAll('.sr-only').forEach((n) => n.remove());
      return (copy.textContent ?? '').trim();
    }),
  );
  // ...and screen readers hear "Was" before it.
  for (const text of await card.locator('s .sr-only').allTextContents()) expect(text.trim()).toBe('Was');
  const text = (await card.innerText()).trim();
  const figure = card.locator('span.font-display.text-4xl');
  if ((await figure.count()) === 0) return { strikes, figure: null, duration: '', height: null, text };
  const block = priceBlock(card);
  const figureText = ((await figure.first().textContent()) ?? '').trim().replace(/^Now\s+/, '');
  const duration = ((await block.locator('p').last().textContent()) ?? '').trim();
  const box = await block.boundingBox();
  return { strikes, figure: figureText, duration, height: box?.height ?? null, text };
}

interface PricingSnap {
  snap: Record<string, CardSnap>;
  alerts: number;
  promoStatus: number[];
  consoleErrors: string[];
}

/** /pricing in a fresh anonymous context at the given viewport. */
async function snapshotPricing(browser: Browser, viewport: { width: number; height: number } = VIEWPORTS.desktop): Promise<PricingSnap> {
  const ctx = await browser.newContext({ baseURL: loadStack().baseUrl, viewport });
  const page = await ctx.newPage();
  const promoStatus: number[] = [];
  const consoleErrors: string[] = [];
  page.on('response', (r) => {
    if (r.url().includes('/api/promotions')) promoStatus.push(r.status());
  });
  page.on('console', (m) => {
    if (m.type() === 'error') consoleErrors.push(m.text());
  });
  try {
    await page.goto('/pricing', { waitUntil: 'networkidle' });
    const snap: Record<string, CardSnap> = {};
    for (const name of CARDS) snap[name] = await readCard(page, name);
    // Next's route announcer is always an empty role=alert; any other alert is a real error message.
    const alerts = await page.locator('[role="alert"]:not(#__next-route-announcer__)').count();
    return { snap, alerts, promoStatus, consoleErrors };
  } finally {
    await ctx.close();
  }
}

// ------------------------------------------------------------------ a. live discount

test('a: a live 50% discount on every plan shows on the API and on the pricing cards', async ({ browser }) => {
  const { baseUrl } = loadStack();
  promotionA = await createPromotion({
    kind: 'planDiscount',
    name: `E2E half off ${suffix}`,
    publicHeadline: 'E2E half off',
    discountType: 'percentage',
    discountAmount: '50',
    duration: 'forever',
    planKeys: [],
    ...window(),
  });

  // The API may still serve a cached answer for up to 60 s: wait until it lists the three paid plans.
  await expect
    .poll(async () => keysOf(await planPrices()), { timeout: 75_000, intervals: [5_000] })
    .toEqual(['creator', 'pro', 'team']);

  const items = await planPrices();
  const byKey = Object.fromEntries(items.map((i) => [i.planKey, i]));
  for (const item of items) {
    expect(item.original, `${item.planKey} original`).toBeTruthy();
    expect(item.discounted, `${item.planKey} discounted`).toBeTruthy();
    expect(item.discounted, `${item.planKey} discounted differs from original`).not.toBe(item.original);
    expect(item.durationKind, `${item.planKey} durationKind`).toBe('forever');
  }
  expect(byKey.team.perSeat, 'team is per seat').toBe(true);
  // Team is sold from 3 seats (the mock refuses fewer, as Paddle does), so the API previews 3 seats and
  // shows Paddle's per-seat figures (formatted_unit_totals): the mock's $49.00 seat at 50% off.
  expect(byKey.team.original, 'team per-seat original').toBe('$49.00');
  expect(byKey.team.discounted, 'team per-seat discounted').toBe('$24.50');
  expect(byKey.creator.perSeat).toBe(false);
  expect(byKey.pro.perSeat).toBe(false);
  expect(byKey.pro.headline).toBe('E2E half off');

  const res = await anon.get('/api/v1/billing/promotions/plan-prices');
  expect(res.headers()['cache-control'], 'Cache-Control on plan-prices').toContain('max-age=60');

  // The pricing cards, in a fresh context: the struck original, Paddle's discounted string and "forever".
  await expect
    .poll(async () => (await snapshotPricing(browser)).snap.Pro.strikes, { timeout: 75_000, intervals: [5_000] })
    .toEqual([byKey.pro.original]);

  for (const viewport of ['desktop', 'mobile'] as const) {
    const { snap, alerts } = await snapshotPricing(browser, VIEWPORTS[viewport]);
    expect(alerts, `${viewport}: no alert on /pricing`).toBe(0);
    for (const [key, name] of Object.entries(CARD_NAME)) {
      expect(snap[name].strikes, `${viewport} ${name} struck price`).toEqual([byKey[key].original]);
      expect(snap[name].figure, `${viewport} ${name} figure`).toBe(byKey[key].discounted);
      expect(snap[name].duration, `${viewport} ${name} duration`).toBe('forever');
    }
    for (const name of ['Trial', 'Enterprise']) expect(snap[name].strikes, `${viewport} ${name} has no struck price`).toEqual([]);
    expect(snap.Team.text.toLowerCase(), `${viewport} Team keeps its per-seat unit`).toContain('/seat/month');
    measured.live[viewport] = snap;
  }
});

// ------------------------------------------------------------------ e. structured data (live state)

test('e: the /pricing JSON-LD Offers keep the catalog prices while a promotion is live', async ({ browser }) => {
  const items = await planPrices();
  expect(keysOf(items), 'a promotion is live').toEqual(['creator', 'pro', 'team']);
  const discountedNumbers = items.map((i) => Number(String(i.discounted).replace(/[^\d.]/g, '')));

  const ctx = await browser.newContext({ baseURL: loadStack().baseUrl });
  const page = await ctx.newPage();
  try {
    await page.goto('/pricing', { waitUntil: 'networkidle' });
    const nodes = (await page.locator('script[type="application/ld+json"]').allTextContents()).flatMap((t) => {
      try {
        return [JSON.parse(t)];
      } catch {
        return [];
      }
    });
    const offers: any[] = [];
    const walk = (v: unknown): void => {
      if (Array.isArray(v)) return v.forEach(walk);
      if (!v || typeof v !== 'object') return;
      const types = ([] as unknown[]).concat((v as any)['@type']);
      if (types.includes('Offer') || types.includes('AggregateOffer')) offers.push(v);
      Object.values(v).forEach(walk);
    };
    nodes.forEach(walk);
    expect(offers.length, 'Offer nodes in JSON-LD').toBeGreaterThan(0);

    const prices = offers.flatMap((o) => [o.price, o.lowPrice, o.highPrice]).filter((x) => x !== undefined && x !== null).map(Number);
    expect(prices.length, 'priced Offer values').toBeGreaterThan(0);
    for (const p of prices) expect(catalogPrices, `JSON-LD price ${p} is a catalog price`).toContain(p);
    for (const d of discountedNumbers) expect(prices, `JSON-LD never carries the discounted ${d}`).not.toContain(d);
  } finally {
    await ctx.close();
  }
});

// ------------------------------------------------------------------ b. months variant

test('b: a 3-month Pro promotion replaces the forever one, and the Pro card says so', async ({ browser }) => {
  await archive(promotionA);
  promotionB = await createPromotion({
    kind: 'planDiscount',
    name: `E2E Pro 3 months ${suffix}`,
    publicHeadline: 'E2E Pro for 3 months',
    discountType: 'percentage',
    discountAmount: '50',
    duration: 'months',
    durationMonths: 3,
    planKeys: ['pro'],
    ...window(),
  });

  await expect
    .poll(async () => (await planPrices()).map((i) => `${i.planKey}:${i.durationKind}:${i.durationMonths}`), { timeout: 75_000, intervals: [5_000] })
    .toEqual(['pro:months:3']);
  const [pro] = await planPrices();
  expect(pro.perSeat).toBe(false);
  expect(pro.original).not.toBe(pro.discounted);

  await expect
    .poll(async () => (await snapshotPricing(browser)).snap.Pro.duration, { timeout: 75_000, intervals: [5_000] })
    .toBe('for 3 months');
  const { snap } = await snapshotPricing(browser);
  expect(snap.Pro.strikes).toEqual([pro.original]);
  expect(snap.Pro.figure).toBe(pro.discounted);
  expect(snap.Creator.strikes, 'Creator is not in the promotion').toEqual([]);
  expect(snap.Team.strikes, 'Team is not in the promotion').toEqual([]);
});

// ------------------------------------------------------------------ c. fail quiet

test('c: a failing preview, then a taxed one, drops the price quietly and the cards show no struck price', async ({ browser }) => {
  // Promotion B stays live here: the error and tax answers are what must hide it.
  await setPreviewMode('error');
  await expect.poll(planPrices, { timeout: 75_000, intervals: [5_000] }).toEqual([]);

  // The site may hold its own copy for up to 60 s more: poll the page, not just the API.
  await expect
    .poll(async () => (await snapshotPricing(browser)).snap.Pro.strikes, { timeout: 75_000, intervals: [5_000] })
    .toEqual([]);

  for (const viewport of ['desktop', 'mobile'] as const) {
    const { snap, alerts, promoStatus, consoleErrors } = await snapshotPricing(browser, VIEWPORTS[viewport]);
    expect(alerts, `${viewport}: no alert on /pricing`).toBe(0);
    expect(promoStatus.every((s) => s < 500), `${viewport}: /api/promotions answered ${promoStatus}`).toBe(true);
    expect(consoleErrors.filter((m) => /promo|plan-prices/i.test(m)), `${viewport}: console errors`).toEqual([]);
    for (const name of CARDS) {
      expect(snap[name].strikes, `${viewport} ${name} struck price`).toEqual([]);
      expect(snap[name].text, `${viewport} ${name} shows no error`).not.toMatch(/error|went wrong|unavailable/i);
    }
  }

  // Taxed: the API must drop the item. The cache must first refill with the live item (mode ok), so the
  // empty answer under tax is a fresh one and not the error-mode cache.
  await setPreviewMode('ok');
  await expect.poll(async () => keysOf(await planPrices()), { timeout: 75_000, intervals: [5_000] }).toEqual(['pro']);
  await setPreviewMode('tax');
  await expect.poll(planPrices, { timeout: 75_000, intervals: [5_000] }).toEqual([]);

  // Now the promotion ends for real, and "none" is measured with no promotion at all (the layout baseline).
  await setPreviewMode('ok');
  await archive(promotionB);
  await expect
    .poll(async () => (await snapshotPricing(browser)).snap.Pro.strikes, { timeout: 75_000, intervals: [5_000] })
    .toEqual([]);
  expect(await planPrices()).toEqual([]);
  for (const viewport of ['desktop', 'mobile'] as const) {
    const { snap, alerts } = await snapshotPricing(browser, VIEWPORTS[viewport]);
    expect(alerts, `${viewport}: no alert on /pricing with no promotion`).toBe(0);
    for (const name of CARDS) expect(snap[name].strikes, `${viewport} ${name} with no promotion`).toEqual([]);
    measured.none[viewport] = snap;
  }
});

// ------------------------------------------------------------------ d. no layout shift

test('d: each paid price block is the same height with the promotion live and with none, at both widths', async () => {
  for (const viewport of ['desktop', 'mobile'] as const) {
    expect(measured.live[viewport]?.Pro?.strikes, `${viewport}: the live measurement had the promotion`).toHaveLength(1);
    expect(measured.none[viewport]?.Pro?.strikes, `${viewport}: the none measurement had no promotion`).toEqual([]);
    for (const name of PAID) {
      const live = measured.live[viewport][name].height;
      const none = measured.none[viewport][name].height;
      expect(live, `${viewport} ${name} live height`).not.toBeNull();
      expect(none, `${viewport} ${name} none height`).toBe(live);
    }
  }
});

// ------------------------------------------------------------------ f. a price with a minimum quantity

test('f: the mock refuses a one-seat Team preview like Paddle, and a three-seat one reports per-seat totals', async () => {
  const preview = async (quantity: number) =>
    fetch(`${loadStack().mockUrl}/pricing-preview`, {
      method: 'POST',
      headers: { 'content-type': 'application/json', authorization: 'Bearer e2e-mock-key' },
      body: JSON.stringify({ items: [{ price_id: 'pri_e2e_team', quantity }] }),
    });

  const one = await preview(1);
  expect(one.status, 'one Team seat is below the price minimum').toBe(400);
  expect((await one.json()).error.code).toBe('transaction_item_quantity_out_of_range');

  const three = await preview(3);
  expect(three.status).toBe(200);
  const line = (await three.json()).data.details.line_items[0];
  expect(line.formatted_totals.subtotal).toBe('$147.00');
  expect(line.formatted_unit_totals.subtotal, 'per-seat figure from the provider').toBe('$49.00');
});
