/**
 * (h) An upgrade whose payment fails, on Stripe. The app sends the upgrade with `proration_behavior=always_invoice`
 * and `payment_behavior=error_if_incomplete`, so Stripe applies it only if the prorated invoice is paid: with a
 * declining card the call answers 402 `card_declined`, the subscription stays on the old plan, no invoice or
 * payment exists, no webhook is sent and no credits are granted. The dashboard shows Stripe's refusal in its own
 * words (409 `billing_provider_refused`), and once the card works the same click upgrades the workspace (every
 * attempt carries a fresh idempotency key, so the retry is a new request and not a replay of the failure).
 *
 * Paddle behaves the other way round (the plan moves and the payment fails afterwards): see
 * tests/billing/upgrade-payment.spec.ts.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/upgrade-payment-failed.spec.ts --project=billing-stripe
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, isStripeStack, stripeMock, subscribeViaStripeApi, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-declined');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

test('a declined upgrade payment keeps Creator, shows the refusal, grants nothing; with a working card the same upgrade goes through', async ({ page }) => {
  test.setTimeout(150_000);
  const t = await users.create('creator');
  await users.login(page, t);
  const { subscriptionId } = await subscribeViaStripeApi(page, 'creator');
  const before = await billingSummary(page);
  const subscriptionUpdates = async () =>
    (await stripeMock.state()).invoices.filter((i) => i.parent.subscription_details.subscription === subscriptionId && i.billing_reason === 'subscription_update');
  const changeCalls = async () => stripeMock.calls({ path: `/v1/subscriptions/${subscriptionId}`, method: 'POST' });

  await stripeMock.setPayments(subscriptionId, 'fail'); // the card is declined
  // The fake is shared by every spec: only this subscription's deliveries count.
  const mySubscriptionWebhooks = async () => (await stripeMock.webhooks()).filter((w) => w.subscription_id === subscriptionId).length;
  const webhooksBefore = await mySubscriptionWebhooks();

  await page.goto('/app/settings/billing');
  const upgrade = async () => {
    await page.getByTestId('plan-pro-change').click();
    const dialog = page.getByRole('dialog', { name: 'Upgrade to Pro?' });
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/change-plan'));
    await dialog.getByRole('button', { name: 'Confirm' }).click();
    return resp;
  };

  const declinedKey = await test.step('Upgrade to Pro with a declining card → 409 billing_provider_refused, shown in the page', async () => {
    const r = await upgrade();
    expect(r.status()).toBe(409);
    const body = await r.json();
    expect(body).toMatchObject({ error: 'billing_provider_refused', providerCode: 'card_declined' });
    expect(body.message).toContain('Stripe could not charge your card');

    const alert = page.getByRole('alert').filter({ hasText: 'That did not go through' });
    await expect(alert).toContainText(body.message);
    await expect(page.getByTestId('billing-done-notice')).toHaveCount(0);
    await expect(page.getByTestId('billing-waiting-notice')).toHaveCount(0);

    const call = (await changeCalls()).at(-1)!;
    expect(call.status).toBe(402);
    expect(call.body).toMatchObject({ proration_behavior: 'always_invoice', payment_behavior: 'error_if_incomplete' });
    expect(call.body.items[0]).toMatchObject({ price: 'price_e2e_pro', quantity: '1' });
    return call.idempotencyKey;
  });

  await test.step('Stripe still has Creator: no item change, no invoice, no webhook', async () => {
    const sub = (await stripeMock.state()).subscriptions.find((s) => s.id === subscriptionId)!;
    expect(sub.status).toBe('active');
    expect(sub.items.data[0].price.id).toBe('price_e2e_creator');
    expect(await subscriptionUpdates(), 'the proration invoice does not exist').toHaveLength(0);
    expect(await mySubscriptionWebhooks(), 'a refused update sends no webhook').toBe(webhooksBefore);
  });

  await test.step('the workspace is still on Creator, with exactly the credits it had', async () => {
    const after = await billingSummary(page);
    expect(after.subscription.planKey).toBe('creator');
    expect(after.subscription.status).toBe(before.subscription.status);
    expect(after.credits.balance, 'no credits for an upgrade that did not happen').toBe(before.credits.balance);
    await page.reload();
    await expect(page.getByTestId('current-plan-card')).toContainText('Creator');
  });

  await test.step('the card works → the same upgrade goes through, as a new request', async () => {
    await stripeMock.setPayments(subscriptionId, 'pay');
    const r = await upgrade();
    expect(r.status(), await r.text()).toBe(202);
    expect(await r.json()).toMatchObject({ direction: 'upgrade', effective: 'now' });
    await expectSummary(page, (s) => s.subscription.planKey, 'pro', 'Pro once the subscription webhook landed');

    const call = (await changeCalls()).at(-1)!;
    expect(call.status).toBe(200);
    expect(call.replayed, 'applied, not replayed from the failed attempt').toBeFalsy();
    expect(call.idempotencyKey).not.toBe(declinedKey);
    const [invoice, ...rest] = await subscriptionUpdates();
    expect(rest).toHaveLength(0);
    expect(invoice).toMatchObject({ status: 'paid' });

    await expect.poll(async () => (await billingSummary(page)).credits.balance, { message: 'the prorated grant after invoice.paid', timeout: 20_000 })
      .toBeGreaterThan(before.credits.balance);
  });
});
