/**
 * (g) The public-alpha founder price on Stripe: a 50% coupon that lasts forever (`rb_founder_alpha`), made
 * once by the catalog sync tool and linked to the platform's plan-discount promotion through
 * `provider_discount_id`. A customer who applies the promotion's code pays half at checkout, and the renewal
 * invoice is discounted the same way.
 *
 * The spec plays the sync tool's part: it creates the coupon in the Stripe fake and links the promotion to it
 * with the one SQL statement the tool prints. The promotion itself is made through the admin API, as the
 * Promotions page does.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/founder-price.spec.ts --project=billing-stripe
 */
import { test, expect, request as pwRequest } from '@playwright/test';
import { billingSummary, expectSummary, isStripeStack, loadStack, planIncludedCredits, planPriceMinor, postToken, psql, sqlStr, stripeMock, Throwaways, user, captureCheckout } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-founder');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

const COUPON = 'rb_founder_alpha';

function randomSuffix(length = 6): string {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  let out = '';
  for (let i = 0; i < length; i++) out += alphabet[Math.floor(Math.random() * alphabet.length)];
  return out;
}

test('founder coupon: checkout pays half, and so does every renewal', async ({ page }) => {
  test.setTimeout(180_000);
  const half = planPriceMinor('creator') / 2;
  const code = `FOUNDER${randomSuffix()}`;
  let promotionId = '';

  await test.step('the sync tool\'s part: the coupon exists in Stripe and the promotion points at it', async () => {
    try {
      await stripeMock.createCoupon({ id: COUPON, name: 'Founder alpha', percent_off: '50', duration: 'forever', metadata: { reelbolt_catalog: '1' } });
    } catch (err) {
      if (!/already exists/.test(String(err))) throw err; // a previous run on this stack made it
    }

    const admin = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
    try {
      const a = user('admin');
      const login = await postToken(admin, a.email, a.password);
      expect(login.status(), 'admin sign-in').toBe(200);
      const headers = { Authorization: `Bearer ${(await login.json()).accessToken}` };
      const created = await admin.post('/api/v1/billing/admin/promotions', {
        headers,
        data: {
          kind: 'planDiscount', name: `E2E founder 50% ${code}`, publicHeadline: 'E2E: founders pay half', code,
          discountType: 'percentage', discountAmount: '50', duration: 'forever', planKeys: ['creator'],
        },
      });
      expect(created.status(), await created.text()).toBeLessThan(300);
      const promotion = await created.json();
      promotionId = promotion.id;
      expect(promotion.providerDiscountId, 'the platform made a Stripe coupon of its own for the promotion').toMatch(/^rb_promo_/);
    } finally {
      await admin.dispose();
    }
    // What the catalog sync prints: link the promotion to the founder coupon.
    psql(`UPDATE promotions SET provider_discount_id = ${sqlStr(COUPON)} WHERE id = ${sqlStr(promotionId)}`);
  });

  const t = await users.create('owner');
  await users.login(page, t);
  await page.goto('/app/settings/billing');
  const before = (await billingSummary(page)).credits.balance;

  await test.step('apply the code → Creator shows half the list price', async () => {
    await page.getByTestId('promo-code-input').fill(code.toLowerCase());
    await page.getByTestId('promo-code-apply').click();
    await expect(page.getByTestId('promo-code-card')).toContainText(code);
    await expect(page.getByTestId('plan-creator').getByTestId('promo-preview-total')).toContainText((half / 100).toFixed(2));
  });

  const sessionId = await test.step('Choose Creator → the hosted page carries the coupon and the half price', async () => {
    const captured = await captureCheckout(page, () => page.getByTestId('plan-creator-choose').click());
    expect(captured.status).toBe(200);
    const id: string = captured.body.transactionId;
    await page.waitForURL(new RegExp(`/pay/${id}$`));
    await expect(page.getByTestId('mock-checkout-coupon')).toContainText(COUPON);
    await expect(page.getByTestId('mock-checkout-total')).toHaveText((half / 100).toFixed(2));

    const call = (await stripeMock.calls({ path: '/v1/checkout/sessions', method: 'POST' })).filter((c) => c.body?.client_reference_id === t.orgId).at(-1)!;
    expect(call.body.discounts).toEqual([{ coupon: COUPON }]);
    expect(call.body.metadata.promotion_id).toBe(promotionId);
    expect(call.body.subscription_data.metadata.promotion_id, 'the promotion rides on the subscription too').toBe(promotionId);
    return id;
  });

  await test.step('pay → Creator, the first invoice is half price, the platform resolved the discount', async () => {
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?session_id=cs_test_/);
    await expectSummary(page, (s) => `${s.subscription.planKey}/${s.subscription.status}`, 'creator/Active');
    const sub = (await stripeMock.liveSubscriptionOf(t.orgId!))!;
    const state = await stripeMock.state();
    const first = state.invoices.find((i) => i.parent.subscription_details.subscription === sub.id && i.billing_reason === 'subscription_create')!;
    expect(first).toMatchObject({ amount_paid: half, status: 'paid' });
    expect(first.total_discount_amounts[0].amount).toBe(half);
    expect(state.sessions.find((s) => s.id === sessionId)).toMatchObject({ amount_total: half, discounts: [{ coupon: COUPON }] });

    // invoice.paid carries a discount id only; the coupon behind it is read from the invoice.
    await expect.poll(async () => (await stripeMock.calls({ path: `/v1/invoices/${first.id}`, method: 'GET' })).some((c) => c.body?.expand?.includes('discounts')),
      { message: 'the discount was resolved to its coupon', timeout: 20_000 }).toBe(true);
    await expect.poll(async () => (await billingSummary(page)).credits.balance, { timeout: 20_000 }).toBe(before + planIncludedCredits('creator'));
  });

  await test.step('the renewal invoice stays half price', async () => {
    const sub = (await stripeMock.liveSubscriptionOf(t.orgId!))!;
    expect(sub.discounts, 'the discount stays on the subscription').toHaveLength(1);
    const afterFirst = (await billingSummary(page)).credits.balance;
    const { invoice } = await stripeMock.advancePeriod(sub.id);
    expect(invoice.billing_reason).toBe('subscription_cycle');
    expect(invoice.amount_paid).toBe(half);
    expect(invoice.total_discount_amounts[0].amount).toBe(half);
    await expect.poll(async () => (await stripeMock.webhooks()).filter((w) => w.subscription_id === sub.id && !w.delivered && !w.dropped).length,
      { message: 'every webhook of the renewal was accepted', timeout: 20_000 }).toBe(0);
    await expect.poll(async () => (await billingSummary(page)).credits.balance, { message: 'the new period\'s credits', timeout: 20_000 }).toBeGreaterThan(afterFirst);
    expect((await billingSummary(page)).subscription.status).toBe('Active');
  });
});
