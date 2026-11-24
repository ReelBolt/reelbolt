/**
 * Upgrade credits follow Paddle's proration payment (F-1): a same-period upgrade is a
 * `prorated_immediately` PATCH, Paddle charges it with a transaction of origin `subscription_update`,
 * and only its `transaction.completed` grants the prorated credits. A failed payment never adds
 * credits; a refund of the upgrade payment claws back what is left of its grant. Throwaway Owners only.
 *
 *   cd e2e && npx playwright test tests/billing/upgrade-payment.spec.ts --project=desktop
 */
import { test, expect, type Page } from '@playwright/test';
import { billingSummary, expectSummary, loadStack, mock, subscribeViaApi, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('upay');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
});

async function upgradeToPro(page: Page): Promise<any> {
  const r = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/subscription/change-plan`, { data: { plan: 'pro' } });
  expect(r.status(), `change-plan: ${await r.text()}`).toBe(202);
  const body = await r.json();
  expect(body).toMatchObject({ direction: 'upgrade', effective: 'now' });
  await expectSummary(page, (s) => s.subscription.planKey, 'pro', 'Pro after subscription.updated');
  return body;
}

async function deliveredOf(eventType: string, subscriptionId: string) {
  return (await mock.webhooks()).filter((w) => w.event_type === eventType && w.subscription_id === subscriptionId);
}

test('a failed proration payment never adds credits', async ({ page }) => {
  test.setTimeout(90_000);
  const t = await users.create('fail');
  await users.login(page, t);
  const { subscriptionId } = await subscribeViaApi(page, 'creator');
  const before = (await billingSummary(page)).credits.balance;

  await mock.setPayments(subscriptionId, 'fail');
  await upgradeToPro(page);

  const [txn] = await mock.prorationTransactions(subscriptionId);
  expect(txn).toMatchObject({ origin: 'subscription_update', status: 'past_due', subscription_id: subscriptionId });
  expect(txn.id).toMatch(/^txn_/);
  expect(txn.items[0]).toMatchObject({ price_id: 'pri_e2e_pro', quantity: 1 });
  // Prorated from the PATCH instant, which is also subscription.updated's occurred_at (the backend
  // pairs the payment with its upgrade by these two).
  const updated = (await deliveredOf('subscription.updated', subscriptionId)).at(-1)!;
  expect(txn.items[0].proration.billing_period.starts_at).toBe(updated.occurred_at);
  expect(Number(txn.items[0].proration.rate)).toBeGreaterThan(0.9); // a fresh period, almost all of it left
  await expect.poll(async () => (await deliveredOf('transaction.payment_failed', subscriptionId)).map((w) => w.delivered), { timeout: 15_000 })
    .toEqual([true]);
  expect(await deliveredOf('transaction.completed', subscriptionId)).toHaveLength(1); // the checkout only

  const after = await billingSummary(page);
  expect(after.subscription.planKey).toBe('pro');
  expect(after.credits.balance, 'no credits for an unpaid upgrade').toBe(before);
});

test('refunding the upgrade payment claws back its unspent grant', async ({ page }) => {
  test.setTimeout(90_000);
  const t = await users.create('refund');
  await users.login(page, t);
  const { subscriptionId } = await subscribeViaApi(page, 'creator');
  const before = (await billingSummary(page)).credits.balance;

  const body = await upgradeToPro(page); // payments default to pay: transaction.completed follows
  await expect.poll(async () => (await billingSummary(page)).credits.balance - before, { timeout: 20_000 }).toBeGreaterThan(0);
  const granted = (await billingSummary(page)).credits.balance - before;
  expect(granted).toBeLessThan(2500); // Pro 3500 − Creator 1000, prorated to the time left
  expect(Math.abs(granted - body.creditGrantMillis / 1000)).toBeLessThan(1);

  const [txn] = await mock.prorationTransactions(subscriptionId);
  expect(txn).toMatchObject({ origin: 'subscription_update', status: 'completed' });
  // F-3: credits follow a charge above 0 (minor units, a string as Paddle sends it).
  expect(Number(txn.details.totals.grand_total)).toBeGreaterThan(0);
  const refunded = await mock.refund(txn.id);
  expect(refunded.adjustment).toMatchObject({ action: 'refund', type: 'full', status: 'approved', transaction_id: txn.id });
  expect(refunded.webhook.delivered).toBe(true);

  await expectSummary(page, (s) => s.credits.balance, before, 'the unspent upgrade grant is clawed back');
  expect((await billingSummary(page)).subscription.planKey).toBe('pro');
});
