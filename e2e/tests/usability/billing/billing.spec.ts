import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * T-007 billing. Runs on the desktop (1400), tablet (820), pixel7 and iphone14 (webkit) projects of
 * playwright.usability.config.ts. Findings: A12, A13, A14, A18, A15 (A28 is covered by vitest: it needs a
 * refused run).
 *
 * Every billing answer is stubbed at the network (summary, checkout preview), so the specs do not
 * depend on engine tables or on a payment provider and never start a checkout.
 */
const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');
const ORG_ID = '11111111-1111-1111-1111-111111111111';

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

const PLANS = (current: string | null) => [
  { key: 'creator', name: 'Creator', kind: 'Subscription', priceMonthlyUsd: 29, minSeats: null, includedCredits: 1000, priceId: 'pri_creator', current: current === 'creator' },
  { key: 'pro', name: 'Pro', kind: 'Subscription', priceMonthlyUsd: 79, minSeats: null, includedCredits: 2500, priceId: 'pri_pro', current: current === 'pro' },
  { key: 'team', name: 'Team', kind: 'PerSeat', priceMonthlyUsd: 49, minSeats: 3, includedCredits: 1500, priceId: 'pri_team', current: current === 'team' },
];

const GRANT = { credits: 100, remaining: 100, expiresAt: '2027-03-04T00:00:00Z' };

function trialSummary() {
  return {
    billingProvider: 'paddle', paddle: null, canManageBilling: true,
    subscription: { planKey: 'trial', planName: 'Trial', status: 'Active', seats: 1, periodStart: null, periodEnd: null, cancelAtPeriodEnd: false, hasBillingCustomer: false },
    credits: { balance: 850, reserved: 0, available: 850, grants: [
      { source: 'promo', remaining: 750, expiresAt: '2027-01-10T00:00:00Z' },
      { source: 'trial', remaining: 100, expiresAt: null },
    ] },
    firstVideoGrant: GRANT,
    topUp: { available: false, packs: [] },
    plans: PLANS(null),
  };
}

function paidSummary() {
  return {
    billingProvider: 'paddle', paddle: null, canManageBilling: true,
    subscription: { planKey: 'creator', planName: 'Creator', status: 'Active', seats: 1, periodStart: '2026-10-01T00:00:00Z', periodEnd: '2026-11-01T00:00:00Z', cancelAtPeriodEnd: false, hasBillingCustomer: true },
    credits: { balance: 1850, reserved: 0, available: 1850, grants: [
      { source: 'trial', remaining: 850, expiresAt: null },
      { source: 'plan', remaining: 1000, expiresAt: '2026-11-01T00:00:00Z' },
    ] },
    firstVideoGrant: GRANT,
    topUp: { available: true, packs: [500, 1000, 3000] },
    plans: PLANS('creator'),
  };
}

const PACK_PRICES: Record<string, string> = { 500: '$18.00', 1000: '$35.00', 3000: '$105.00' };

/**
 * Route-stubbed, login-free: the session is a fabricated reelbolt_user + reelbolt_token cookie pair, GETs
 * are answered by page.route, and the one POST (checkout preview) by a fetch wrapper in the page, because
 * Playwright's WebKit does not route POST/PATCH requests to page.route handlers.
 */
async function stubBilling(page: Page, summaries: unknown[]): Promise<void> {
  await page.addInitScript(({ prices, bodies }) => {
    let summaryCalls = 0;
    const real = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
      const url = new URL(typeof input === 'string' ? input : input instanceof Request ? input.url : String(input), location.href);
      const reply = (status: number, payload: unknown) =>
        new Response(JSON.stringify(payload), { status, headers: { 'Content-Type': 'application/json' } });
      // The summary is answered here too: in Playwright's WebKit only the first requests of a page reach
      // page.route, later ones (the checkout page polls) fall through to the server and get a 404.
      if (method === 'GET' && url.pathname.endsWith('/api/v1/billing/summary')) {
        return reply(200, bodies[Math.min(summaryCalls++, bodies.length - 1)]);
      }
      if (method === 'GET' || !url.pathname.startsWith('/api/')) return real(input, init);
      if (method === 'POST' && url.pathname.endsWith('/billing/checkout/preview')) {
        const body = JSON.parse(String(init?.body ?? '{}')) as { topUpCredits?: number };
        const price = prices[String(body.topUpCredits ?? 0)] ?? '$0.00';
        return reply(200, { currencyCode: 'USD', subtotal: price, discount: '$0.00', total: price, promotion: null, bonusCredits: null });
      }
      return reply(404, { error: 'not stubbed' });
    };
  }, { prices: PACK_PRICES, bodies: summaries });

  const user = {
    email: 'ada@example.com', isPlatformAdmin: false, mustChangePassword: false, emailVerified: true,
    org: ORG_ID, orgName: 'Acme Team', orgKind: 'Team', orgRole: 'Owner', displayName: 'Ada',
    termsVersion: '2026-10', privacyVersion: '2026-10',
  };
  const host = new URL(BASE).hostname;
  await page.context().addCookies([
    { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(user)), domain: host, path: '/' },
    { name: 'reelbolt_token', value: 'stub', domain: host, path: '/' },
  ]);
  const org = { id: ORG_ID, name: 'Acme Team', slug: 'acme', kind: 'Team', role: 'Owner', createdAt: '2026-10-01T00:00:00Z' };
  await page.route('**/api/**', async (route) => {
    const p = new URL(route.request().url()).pathname.replace(/^\/app/, '');
    if (route.request().method() !== 'GET') return json(route, { error: 'not stubbed' }, 404);
    if (p === '/api/v1/orgs') return json(route, [org]);
    return p.endsWith('s') ? json(route, []) : json(route, { error: 'not stubbed' }, 404);
  });
}

async function openBilling(page: Page, summaries: unknown[]): Promise<void> {
  await stubBilling(page, summaries);
  await page.goto('/app/settings/billing');
  await expect(page.getByTestId('current-plan-card')).toBeVisible({ timeout: 30_000 });
}

test.describe('billing page', () => {
  test('A12: no first-video banner once a plan is bought; no "Credits by expiry" jargon', async ({ page }) => {
    await openBilling(page, [paidSummary()]);
    await expect(page.getByTestId('first-video-grant-card')).toHaveCount(0);
    await expect(page.getByText('Credits by expiry')).toHaveCount(0);
    await expect(page.getByTestId('credit-grants-list')).toContainText('Where your credits come from');
  });

  test('A12: the first-video banner stays for a Trial workspace', async ({ page }) => {
    await openBilling(page, [trialSummary()]);
    await expect(page.getByTestId('first-video-grant-card')).toBeVisible();
  });

  test('A13: Team shows a per-seat total, plan cards list what they include, one code field', async ({ page }) => {
    await openBilling(page, [trialSummary()]);
    await expect(page.getByTestId('plan-team-seat-total')).toContainText('3 seats = $147');
    await page.getByRole('button', { name: 'More seats on the Team plan' }).click();
    await expect(page.getByTestId('plan-team-seat-total')).toContainText('4 seats = $196');
    await expect(page.getByTestId('plan-creator-features')).toContainText('1,000 credits every month');
    await expect(page.getByRole('textbox', { name: /code/i })).toHaveCount(1);
    await expect(page.getByText('Redeem a code')).toHaveCount(0);
  });

  test('A13: top-up packs show a price', async ({ page }) => {
    await openBilling(page, [paidSummary()]);
    const card = page.getByTestId('topup-card');
    await expect(card.getByTestId('pack-price')).toHaveCount(3);
    await expect(card).toContainText('$35.00');
  });

  test('A13/A18: nothing in the plans card is under 12px', async ({ page }) => {
    await openBilling(page, [trialSummary()]);
    const small = await page.getByTestId('plans-card').evaluate((root) => {
      const bad: string[] = [];
      for (const el of root.querySelectorAll<HTMLElement>('*')) {
        const own = Array.from(el.childNodes).some((n) => n.nodeType === 3 && (n.textContent ?? '').trim() !== '');
        if (own && parseFloat(getComputedStyle(el).fontSize) < 12) bad.push(`${el.tagName}:${(el.textContent ?? '').slice(0, 30)}`);
      }
      return bad;
    });
    expect(small).toEqual([]);
  });

  test('A18: seat stepper buttons are named and at least 44px; the grant badge is not truncated', async ({ page }) => {
    await openBilling(page, [trialSummary()]);
    for (const name of ['Fewer seats on the Team plan', 'More seats on the Team plan']) {
      const box = await page.getByRole('button', { name }).boundingBox();
      expect(box, name).not.toBeNull();
      expect(box!.width, `${name} width`).toBeGreaterThanOrEqual(44);
      expect(box!.height, `${name} height`).toBeGreaterThanOrEqual(44);
    }
    const clipped = await page.getByTestId('credit-grants-list').evaluate((root) =>
      Array.from(root.querySelectorAll<HTMLElement>('.mantine-Badge-label')).filter((l) => l.scrollWidth > l.clientWidth + 1).map((l) => l.textContent));
    expect(clipped).toEqual([]);
  });

  test('A14: cancel confirm says what it does; Manage is explained when disabled', async ({ page }) => {
    await openBilling(page, [paidSummary()]);
    await page.getByTestId('cancel-plan').click();
    const dialog = page.getByTestId('cancel-plan-modal');
    await expect(dialog.getByRole('button', { name: 'Yes, cancel at period end' })).toBeVisible();
    await expect(dialog).toContainText('free trial');
    await expect(dialog).not.toContainText('free plan');
    await page.keyboard.press('Escape');
    await expect(page.getByTestId('portal-cancel-note')).toBeVisible();
  });

  test('A14: Manage in Paddle on Trial has a reason on focus', async ({ page }) => {
    await openBilling(page, [trialSummary()]);
    await expect(page.getByTestId('manage-in-paddle')).toBeDisabled();
    await page.getByTestId('manage-in-paddle').locator('xpath=ancestor::span[1]').focus();
    await expect(page.getByRole('tooltip')).toContainText('Available after your first purchase');
  });
});

/** What the billing page saves when it opens a checkout; the return page compares against it. */
async function seedSnapshot(page: Page): Promise<void> {
  await page.addInitScript(() => {
    window.sessionStorage.setItem('reelbolt.checkout-snapshot', JSON.stringify({
      planKey: 'trial', balance: 850, livePaid: false, at: Date.now(), target: { kind: 'plan', planKey: 'creator' },
    }));
  });
}

test.describe('checkout return (Paddle _ptxn)', () => {
  test('A15: says "Payment received" once the summary shows the new plan', async ({ page }) => {
    await seedSnapshot(page);
    await stubBilling(page, [trialSummary(), trialSummary(), paidSummary()]);
    await page.goto('/app/checkout?_ptxn=txn_01abc');
    await expect(page.getByTestId('checkout-pending-notice')).toBeVisible();
    await expect(page.getByTestId('checkout-success-card')).toContainText('Payment received', { timeout: 20_000 });
    await expect(page.getByTestId('checkout-plan')).toContainText('Creator');
  });

  test('A15: with no saved checkout, a paid-looking summary still claims nothing', async ({ page }) => {
    await stubBilling(page, [paidSummary()]);
    await page.goto('/app/checkout?_ptxn=txn_01abc');
    await expect(page.getByTestId('checkout-pending-notice')).toContainText(/cannot tell/i);
    await page.waitForTimeout(8_000);
    await expect(page.getByText('Payment received')).toHaveCount(0);
  });

  test('A15: when nothing changes, it ends in a calm message and claims nothing', async ({ page }) => {
    await seedSnapshot(page);
    await stubBilling(page, [trialSummary()]);
    await page.goto('/app/checkout?_ptxn=txn_01abc');
    await expect(page.getByTestId('checkout-pending-notice')).toContainText(/can.t see a change yet/i, { timeout: 45_000 });
    await expect(page.getByText('Payment received')).toHaveCount(0);
  });
});
