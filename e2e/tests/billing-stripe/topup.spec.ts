/**
 * (b) A credit top-up through Stripe: the 500-credit pack opens a payment-mode Checkout Session; paying it
 * adds 500 to the balance once, and delivering the same `checkout.session.completed` webhook again (Stripe
 * resends, and an operator can redeliver by hand) grants nothing more. A pack that is opened and
 * abandoned charges nothing.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/topup.spec.ts --project=billing-stripe
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, isStripeStack, stripeMock, subscribeViaStripeApi, topUpPriceMinor, Throwaways, captureCheckout } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-topup');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

test('500 credits: abandoned checkout charges nothing; paid → balance +500 once; a redelivered webhook grants nothing more', async ({ page }) => {
  test.setTimeout(120_000);
  const t = await users.create('creator');
  await users.login(page, t);
  await subscribeViaStripeApi(page, 'creator');
  await page.goto('/app/settings/billing');
  const packs = page.getByTestId('topup-card').getByRole('button');
  await expect(packs).toHaveText(['500 credits', '1,000 credits', '3,000 credits']);
  const before = (await billingSummary(page)).credits.balance;

  await test.step('open the 1,000 pack and walk away: nothing is charged', async () => {
    const walkAway = await captureCheckout(page, () => page.getByTestId('topup-card').getByRole('button', { name: '1,000 credits' }).click());
    expect(walkAway.request).toEqual({ topUpCredits: 1000 });
    await page.waitForURL(/\/pay\/cs_test_/);
    await expect(page.getByTestId('mock-checkout-total')).toHaveText((topUpPriceMinor(1000) / 100).toFixed(2));
    await page.goBack();
    await expect(page.getByTestId('topup-card')).toBeVisible();
  });

  const bought = await captureCheckout(page, () => page.getByTestId('topup-card').getByRole('button', { name: '500 credits' }).click());
  expect(bought.request).toEqual({ topUpCredits: 500 });
  const { transactionId: sessionId } = bought.body;
  expect(sessionId).toMatch(/^cs_test_/);

  await test.step('pay the 500 pack in the hosted page', async () => {
    await page.waitForURL(new RegExp(`/pay/${sessionId}$`));
    await expect(page.getByTestId('mock-checkout-total')).toHaveText((topUpPriceMinor(500) / 100).toFixed(2));
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?session_id=cs_test_/);
  });

  await test.step('the balance is +500 and what was sent is a payment-mode session carrying the pack', async () => {
    await expectSummary(page, (s) => s.credits.balance, before + 500, 'balance +500 after the webhook');
    const call = (await stripeMock.calls({ path: '/v1/checkout/sessions', method: 'POST' })).filter((c) => c.body?.metadata?.topup_credits === '500').at(-1);
    expect(call?.body).toMatchObject({ mode: 'payment', line_items: [{ price: 'price_e2e_topup_500', quantity: '1' }] });
    expect(call!.body.payment_intent_data.metadata).toMatchObject({ organization_id: t.orgId, topup_credits: '500' });
    expect(call!.body, 'a payment-mode session has no subscription_data').not.toHaveProperty('subscription_data');
  });

  await test.step('the same webhook delivered again (twice) → still +500', async () => {
    await expect.poll(async () => (await stripeMock.webhooks()).some((w) => w.object_id === sessionId && w.event_type === 'checkout.session.completed' && w.delivered),
      { message: 'checkout.session.completed was delivered', timeout: 20_000 }).toBe(true);
    const completed = (await stripeMock.webhooks()).find((w) => w.object_id === sessionId && w.event_type === 'checkout.session.completed')!;
    for (let i = 0; i < 2; i++) expect((await stripeMock.redeliver(completed.id)).status).toBe(200);
    expect((await billingSummary(page)).credits.balance).toBe(before + 500);
    await page.goto('/app/settings/billing');
    await expect(page.getByTestId('current-plan-card')).toContainText((before + 500).toLocaleString('en-US'));
  });
});
