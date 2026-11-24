/**
 * (i) A lost chargeback on a top-up, on Stripe. `charge.dispute.created` only alerts the operator (a warning in the
 * log, nothing changes for the customer); `charge.dispute.closed` with status `lost` is a full reversal of the
 * payment, so the pack's credits are clawed back, exactly once, keyed by the dispute id. A lost dispute leaves the
 * charge's `amount_refunded` at 0, so the Inference API reads the payment intent and decides "full" from the
 * disputed amount. Delivering the closed event again, or a `won` dispute on another payment, changes nothing.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/chargeback.spec.ts --project=billing-stripe
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, isStripeStack, loadStack, stripeMock, subscribeViaStripeApi, topUpPriceMinor, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-dispute');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

test('a 500-credit top-up whose chargeback is lost → the credits go back once; the same event again, and a won dispute, change nothing', async ({ page }) => {
  test.setTimeout(150_000);
  const t = await users.create('creator');
  await users.login(page, t);
  await subscribeViaStripeApi(page, 'creator');
  const before = (await billingSummary(page)).credits.balance;

  const buy = async (credits: number) => {
    const start = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: { topUpCredits: credits } });
    expect(start.status(), await start.text()).toBe(200);
    const { transactionId } = await start.json();
    const done = await stripeMock.completeCheckout(transactionId);
    const paymentIntent: string = done.session.payment_intent;
    expect(paymentIntent).toMatch(/^pi_/);
    return paymentIntent;
  };

  const paymentIntent = await buy(500);
  await expectSummary(page, (s) => s.credits.balance, before + 500, 'the pack is granted');

  // First deliveries only: a redelivery is recorded as a delivery of its own (`redelivery_of`).
  // The fake is shared by every spec: only the disputes this test opened count.
  const mine = new Set<string>();
  const disputeEvents = async (type: string) => (await stripeMock.webhooks()).filter((w) => w.event_type === type && w.delivered && !w.redelivery_of && w.object_id !== null && mine.has(w.object_id));

  const disputeId = await test.step('the cardholder disputes the payment → charge.dispute.created changes nothing', async () => {
    const { dispute, charge } = await stripeMock.dispute(paymentIntent); // status needs_response: created only
    expect(dispute).toMatchObject({ status: 'needs_response', payment_intent: paymentIntent, amount: topUpPriceMinor(500), currency: expect.any(String) });
    expect(dispute.id).toMatch(/^dp_/);
    mine.add(dispute.id);
    expect(charge).toMatchObject({ disputed: true, dispute: dispute.id, amount_refunded: 0 });
    expect(await disputeEvents('charge.dispute.created')).toHaveLength(1);
    // Nothing happens on created, and the event was acknowledged: the fake got a 2xx for it.
    expect((await disputeEvents('charge.dispute.created'))[0].attempts.at(-1)!.status).toBe(200);
    expect((await billingSummary(page)).credits.balance).toBe(before + 500);
    return dispute.id as string;
  });

  await test.step('the dispute is lost → charge.dispute.closed (lost) claws the credits back', async () => {
    const { dispute, charge } = await stripeMock.closeDispute(disputeId, 'lost');
    expect(dispute.status).toBe('lost');
    expect(charge.amount_refunded, 'a lost dispute is not a refund').toBe(0);
    await expectSummary(page, (s) => s.credits.balance, before, 'the pack is clawed back');
  });

  await test.step('the Inference API resolved the dispute through Stripe before acting', async () => {
    const lookup = (await stripeMock.calls({ path: '/v1/invoice_payments', method: 'GET' })).find((c) => c.body?.payment?.payment_intent === paymentIntent);
    expect(lookup?.body, 'looked the payment intent up among invoice payments (none: a top-up has no invoice)').toMatchObject({ payment: { type: 'payment_intent' }, status: 'paid' });
    const read = (await stripeMock.calls({ path: `/v1/payment_intents/${paymentIntent}`, method: 'GET' })).at(-1);
    expect(read?.body.expand, 'read the charge to compare its amount with the disputed amount').toContain('latest_charge');
  });

  await test.step('the closed event again, and a dispute that is closed twice → still clawed back once', async () => {
    const closed = (await disputeEvents('charge.dispute.closed'))[0];
    expect(closed, 'charge.dispute.closed was delivered').toBeTruthy();
    for (let i = 0; i < 2; i++) expect((await stripeMock.redeliver(closed.id)).status).toBe(200);
    await expect(stripeMock.closeDispute(disputeId, 'lost')).rejects.toThrow(/already closed/);
    expect((await billingSummary(page)).credits.balance, 'clawed back once, not twice').toBe(before);
  });

  await test.step('a won dispute on another top-up keeps its credits', async () => {
    const second = await buy(500);
    await expectSummary(page, (s) => s.credits.balance, before + 500, 'the second pack is granted');
    const won = await stripeMock.dispute(second, { status: 'won' }); // created, then closed as won
    mine.add(won.dispute.id);
    expect((await disputeEvents('charge.dispute.closed')).length).toBe(2);
    await expect.poll(async () => (await billingSummary(page)).credits.balance, { message: 'a won dispute takes nothing back', timeout: 5_000 }).toBe(before + 500);
  });
});
