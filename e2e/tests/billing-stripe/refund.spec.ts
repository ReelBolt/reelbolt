/**
 * (f) A full refund of a top-up on Stripe: `refund.created` (status succeeded, carrying only the payment
 * intent) claws the pack's credits back exactly once. The webhook cannot say which purchase it belongs to,
 * so the Inference API reads Stripe (invoice payments, the payment intent and its charge) before it
 * acts; the spec checks those reads happened. Delivering the same refund event again, or a second refund
 * attempt, claws back nothing more.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/refund.spec.ts --project=billing-stripe
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, isStripeStack, loadStack, stripeMock, subscribeViaStripeApi, topUpPriceMinor, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-refund');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

test('a 1,000-credit top-up is refunded in full → the credits go back once; the same event again changes nothing', async ({ page }) => {
  test.setTimeout(120_000);
  const t = await users.create('creator');
  await users.login(page, t);
  await subscribeViaStripeApi(page, 'creator');
  const before = (await billingSummary(page)).credits.balance;

  const start = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: { topUpCredits: 1000 } });
  expect(start.status(), await start.text()).toBe(200);
  const { transactionId: sessionId } = await start.json();
  const done = await stripeMock.completeCheckout(sessionId);
  const paymentIntent: string = done.session.payment_intent;
  expect(paymentIntent).toMatch(/^pi_/);
  await expectSummary(page, (s) => s.credits.balance, before + 1000, 'the pack is granted');

  await test.step('refund the whole payment in Stripe → refund.created, then the credits are clawed back', async () => {
    const { refund, charge } = await stripeMock.refund({ paymentIntent });
    expect(refund).toMatchObject({ status: 'succeeded', amount: topUpPriceMinor(1000), payment_intent: paymentIntent });
    expect(charge).toMatchObject({ refunded: true, amount_refunded: topUpPriceMinor(1000) });
    await expectSummary(page, (s) => s.credits.balance, before, 'the pack is clawed back');
  });

  await test.step('the Inference API resolved the refund through Stripe before acting', async () => {
    const reads = (await stripeMock.calls({ method: 'GET' })).map((c) => `${c.path}`);
    expect(reads).toContain('/v1/invoice_payments');
    expect(reads).toContain(`/v1/payment_intents/${paymentIntent}`);
    const lookup = (await stripeMock.calls({ path: '/v1/invoice_payments', method: 'GET' })).find((c) => c.body?.payment?.payment_intent === paymentIntent);
    expect(lookup?.body, 'looked the payment intent up among invoice payments (none: a top-up has no invoice)').toMatchObject({ payment: { type: 'payment_intent' }, status: 'paid' });
  });

  await test.step('the refund event again, and a second refund attempt → no second claw-back', async () => {
    const created = (await stripeMock.webhooks()).find((w) => w.event_type === 'refund.created' && w.delivered);
    expect(created, 'refund.created was delivered').toBeTruthy();
    for (let i = 0; i < 2; i++) expect((await stripeMock.redeliver(created!.id)).status).toBe(200);
    await expect(stripeMock.refund({ paymentIntent })).rejects.toThrow(/already been refunded/);
    expect((await billingSummary(page)).credits.balance, 'clawed back once, not twice').toBe(before);
  });
});
