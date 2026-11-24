/**
 * A 100% Forever Pro discount must not count as collected money nor add to the committed run rate (Stripe).
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/revenue-discount.spec.ts --project=billing-stripe
 *
 * Other specs share the stack, so every assertion compares the admin overview taken before the checkout
 * with the one taken after it.
 */
import { test, expect, request as pwRequest, type APIRequestContext } from '@playwright/test';
import { expectSummary, isStripeStack, loadStack, planPriceMinor, postToken, stripeMock, Throwaways, user } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-revdisc');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

function randomSuffix(length = 6): string {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  let out = '';
  for (let i = 0; i < length; i++) out += alphabet[Math.floor(Math.random() * alphabet.length)];
  return out;
}

async function overview(request: APIRequestContext, headers: Record<string, string>): Promise<any> {
  const res = await request.get('/api/v1/billing/admin/overview', { headers });
  expect(res.status(), 'GET /api/v1/billing/admin/overview').toBe(200);
  return res.json();
}

/** The USD entry of revenue.collected (zeroed when absent). A missing `collected` is a soft failure. */
function usd(o: any): { payments: number; zeroAmountPayments: number; grossMinor: number } {
  const collected = o.revenue?.collected;
  expect.soft(
    Array.isArray(collected),
    'revenue.collected is missing from the admin overview: no collected-money figure exists (the bug: only committedMonthlyUsd, at list price)',
  ).toBe(true);
  const row = (Array.isArray(collected) ? collected : []).find((c: any) => c.currencyCode === 'USD');
  return { payments: row?.payments ?? 0, zeroAmountPayments: row?.zeroAmountPayments ?? 0, grossMinor: row?.grossMinor ?? 0 };
}

test('a 100% Forever Pro discount is not collected revenue and adds nothing to the run rate', async ({ page }) => {
  test.setTimeout(240_000);
  const proListUsd = planPriceMinor('pro') / 100;
  const code = `E2EFREE${randomSuffix()}`;

  const admin = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
  try {
    const a = user('admin');
    const login = await postToken(admin, a.email, a.password);
    expect(login.status(), 'admin sign-in').toBe(200);
    const headers = { Authorization: `Bearer ${(await login.json()).accessToken}` };

    const created = await admin.post('/api/v1/billing/admin/promotions', {
      headers,
      data: {
        kind: 'planDiscount', name: `E2E Pro 100% forever ${code}`, publicHeadline: 'E2E: Pro free forever', code,
        discountType: 'percentage', discountAmount: '100', duration: 'forever', planKeys: ['pro'],
      },
    });
    expect(created.status(), await created.text()).toBeLessThan(300);

    const before = await overview(admin, headers);
    const usdBefore = usd(before);

    const t = await users.create('owner');
    await users.login(page, t);
    const r = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: { plan: 'pro', promoCode: code } });
    expect(r.status(), `checkout: ${await r.text()}`).toBe(200);
    const { transactionId } = await r.json();
    await stripeMock.completeCheckout(transactionId);
    await expectSummary(page, (s) => `${s.subscription?.planKey}/${s.subscription?.status}`, 'pro/Active', 'pro active after the webhooks');

    await expect
      .poll(async () => (await overview(admin, headers)).revenue.payingSubscriptions, { timeout: 30_000, intervals: [250, 500, 1000] })
      .toBe(before.revenue.payingSubscriptions + 1);
    const after = await overview(admin, headers);
    const usdAfter = usd(after);

    const committedDelta = after.revenue.committedMonthlyUsd - before.revenue.committedMonthlyUsd;
    const listDelta = (after.revenue.listPriceMonthlyUsd ?? NaN) - (before.revenue.listPriceMonthlyUsd ?? NaN);
    console.log(`OVERVIEW committedMonthlyUsd ${before.revenue.committedMonthlyUsd} -> ${after.revenue.committedMonthlyUsd} (delta ${committedDelta}); ` +
      `listPriceMonthlyUsd ${before.revenue.listPriceMonthlyUsd} -> ${after.revenue.listPriceMonthlyUsd}; Pro list ${proListUsd}; ` +
      `USD collected ${JSON.stringify(usdBefore)} -> ${JSON.stringify(usdAfter)}`);

    expect.soft(committedDelta, `committedMonthlyUsd must not grow for a 100% discounted subscription (Pro list is ${proListUsd})`).toBeCloseTo(0, 2);
    expect.soft(listDelta, 'listPriceMonthlyUsd grows by the Pro list price').toBeCloseTo(proListUsd, 2);
    expect.soft(usdAfter.grossMinor - usdBefore.grossMinor, 'USD grossMinor collected grows by 0').toBe(0);
    expect.soft(usdAfter.payments - usdBefore.payments, 'exactly one new payment').toBe(1);
    expect.soft(usdAfter.zeroAmountPayments - usdBefore.zeroAmountPayments, 'the new payment is zero-amount').toBe(1);

    // Renewal under the forever coupon: another zero-amount payment, committed still +0 vs the baseline.
    const sub = (await stripeMock.liveSubscriptionOf(t.orgId!))!;
    const { invoice } = await stripeMock.advancePeriod(sub.id);
    expect(invoice.billing_reason).toBe('subscription_cycle');
    expect(invoice.amount_paid).toBe(0);
    await expect
      .poll(async () => usd(await overview(admin, headers)).payments, { message: 'the renewal payment reached the overview', timeout: 30_000 })
      .toBe(usdBefore.payments + 2);
    const renewed = await overview(admin, headers);
    expect.soft(renewed.revenue.committedMonthlyUsd - before.revenue.committedMonthlyUsd, 'after the renewal committedMonthlyUsd still grows by 0').toBeCloseTo(0, 2);
    expect.soft(usd(renewed).grossMinor - usdBefore.grossMinor, 'after the renewal collected still grows by 0').toBe(0);
  } finally {
    await admin.dispose();
  }
});
