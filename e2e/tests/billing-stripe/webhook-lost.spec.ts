/**
 * (e) A Stripe webhook lost for good. Stripe retries for days in live mode, but the platform must not
 * depend on it:
 *
 *   A (sweep)   Creator → Upgrade to Pro in the UI with `customer.subscription.updated` and the proration
 *               `invoice.paid` dropped. Stripe has both changes; the platform has neither. A second change
 *               → 409 change_pending. The background reconciler (every 4 s, after 8 s, docker-compose.e2e.yml)
 *               reads the subscription and the paid proration invoice from Stripe and heals the row to Pro
 *               with exactly one prorated grant. Redelivering both lost webhooks changes nothing.
 *   C (top-up)  The `checkout.session.completed` of a paid top-up is dropped: the balance does not move
 *               (top-ups are not reconciled). Redelivering that same event grants the pack once; a second
 *               redelivery grants nothing.
 *
 * Stripe's subscription carries no `updated_at` (the provider declares `ReconcilesWithoutUpdatedAt`), so the reconciler
 * ages a drift from the org's reconcile watch (the change request the API recorded) and stamps the healing event with
 * the moment it read the subscription. "Too recent" (the 409 above, no heal yet) therefore counts from the upgrade
 * request, once the checkout's own watch has been cleared.
 *
 * The lazy path (a change heals the row itself) needs the sweep paused and lives in webhook-lost-lazy.spec.ts.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/webhook-lost.spec.ts --project=billing-stripe
 */
import { test, expect } from '@playwright/test';
import { billingSummary, clearReconcileWatch, expectSummary, isStripeStack, loadStack, stripeMock, subscribeViaStripeApi, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-lost');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

async function droppedOf(subscriptionId: string) {
  return (await stripeMock.webhooks()).filter((w) => w.dropped && w.subscription_id === subscriptionId);
}

test('A (sweep): upgrade with its webhooks lost → 409 change_pending → the sweep heals to Pro with one grant → late redelivery is a no-op', async ({ page }) => {
  test.setTimeout(150_000);
  const t = await users.create('sweep');
  await users.login(page, t);
  const { subscriptionId } = await subscribeViaStripeApi(page, 'creator');
  const before = (await billingSummary(page)).credits.balance;
  // Stripe sends no updated_at, so a drift is as old as the watch's oldest due time (BillingReconciler: "anchor").
  // The checkout recorded a watch the sweep has not cleared yet; without this the upgrade could look overdue
  // already and the second change below would be healed instead of refused with 409 change_pending.
  clearReconcileWatch(t.orgId!);

  // Lose the upgrade's customer.subscription.updated and its proration invoice.paid.
  await stripeMock.dropNextWebhook({ event_type: 'customer.subscription.updated', subscription_id: subscriptionId });
  await stripeMock.dropNextWebhook({ event_type: 'invoice.paid', subscription_id: subscriptionId });

  let grant = 0;
  await test.step('Upgrade to Pro in the UI → 202; a second change right away → 409 change_pending', async () => {
    await page.goto('/app/settings/billing');
    await page.getByTestId('plan-pro-change').click();
    const dialog = page.getByRole('dialog', { name: 'Upgrade to Pro?' });
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/change-plan'));
    await dialog.getByRole('button', { name: 'Confirm' }).click();
    const r = await resp;
    expect(r.status()).toBe(202);
    const body = await r.json();
    expect(body).toMatchObject({ direction: 'upgrade', effective: 'now' });
    grant = body.creditGrantMillis / 1000;
    expect(grant).toBeGreaterThan(0);

    // A real change (the row still says Creator, so "back to Creator" would be no_change).
    const second = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/subscription/change-plan`, { data: { plan: 'team' } });
    const secondBody = await second.text();
    expect(second.status(), `second change: ${secondBody}`).toBe(409);
    expect(JSON.parse(secondBody).error).toBe('change_pending');
    expect((await billingSummary(page)).subscription.planKey, 'the row still has the old plan').toBe('creator');
  });

  await test.step('Stripe has both changes; their webhooks were never sent', async () => {
    // The fake records an event a moment after the change that caused it (delivery delay), so wait for both.
    await expect.poll(async () => (await droppedOf(subscriptionId)).length, { message: 'both webhooks were dropped', timeout: 10_000 }).toBe(2);
    const lost = await droppedOf(subscriptionId);
    expect(lost.map((w) => w.event_type).sort()).toEqual(['customer.subscription.updated', 'invoice.paid']);
    for (const w of lost) expect(w).toMatchObject({ delivered: false, attempts: [] });
    const state = await stripeMock.state();
    expect(state.subscriptions.find((s) => s.id === subscriptionId)!.items.data[0].price.id).toBe('price_e2e_pro');
    const paid = state.invoices.filter((i) => i.parent.subscription_details.subscription === subscriptionId && i.billing_reason === 'subscription_update' && i.status === 'paid');
    expect(paid, 'the proration invoice is paid at Stripe').toHaveLength(1);
  });

  const healed = await test.step('the sweep heals it: Pro, balance up by exactly one prorated grant', async () => {
    const s = await expectSummary(page, (x) => `${x.subscription.planKey}/${x.credits.balance > before}`, 'pro/true',
      'healed by the sweep (threshold 8 s, sweep every 4 s)', 60_000);
    const gained = s.credits.balance - before;
    expect(Math.abs(gained - grant), `gained ${gained}, one prorated grant is ${grant}`).toBeLessThan(1);
    expect(gained).toBeLessThan(2500); // Pro 3500 − Creator 1000, prorated
    expect((await droppedOf(subscriptionId)).every((w) => !w.delivered), 'healed without any webhook').toBe(true);
    await page.reload();
    await expect(page.getByTestId('current-plan-card')).toContainText('Pro');
    return s;
  });

  await test.step('redeliver both lost webhooks (original event ids) → summary, plan and balance unchanged', async () => {
    for (const w of await droppedOf(subscriptionId)) {
      const r = await stripeMock.redeliver(w.id);
      expect(r.status, `redelivery of ${w.event_type} ${w.event_id}`).toBe(200);
      expect(r.delivery).toMatchObject({ redelivery_of: w.id, event_id: w.event_id, delivered: true });
    }
    const after = await billingSummary(page);
    expect(after.subscription).toEqual(healed.subscription);
    expect(after.subscription.planKey).toBe('pro');
    expect(after.credits.balance, 'no second grant').toBe(healed.credits.balance);
  });
});

test('C (top-up): the checkout.session.completed is lost → no credits → redelivered once → +pack; redelivered again → nothing more', async ({ page }) => {
  test.setTimeout(120_000);
  const t = await users.create('topup');
  await users.login(page, t);
  await subscribeViaStripeApi(page, 'creator');
  const before = (await billingSummary(page)).credits.balance;

  const start = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: { topUpCredits: 500 } });
  expect(start.status(), await start.text()).toBe(200);
  const { transactionId: sessionId } = await start.json();
  await stripeMock.dropNextWebhook({ event_type: 'checkout.session.completed', object_id: sessionId });
  await stripeMock.completeCheckout(sessionId); // paid at Stripe, webhook lost

  const lost = (await stripeMock.webhooks()).find((w) => w.dropped && w.object_id === sessionId)!;
  expect(lost).toMatchObject({ event_type: 'checkout.session.completed', delivered: false, attempts: [] });
  expect((await stripeMock.state()).paymentIntents.some((p) => p.metadata.topup_credits === '500' && p.status === 'succeeded')).toBe(true);
  // completeCheckout returned only after the fake handled every webhook of the purchase, and the only one
  // there was is the dropped one: nothing can be on its way. A top-up has no reconciler either, only
  // Stripe's own retries and a manual resend.
  expect((await billingSummary(page)).credits.balance, 'the payment is not known yet').toBe(before);

  const first = await stripeMock.redeliver(lost.id);
  expect(first.status).toBe(200);
  await expectSummary(page, (s) => s.credits.balance, before + 500, 'the redelivered event grants the pack');

  const second = await stripeMock.redeliver(lost.id);
  expect(second.status).toBe(200);
  expect((await billingSummary(page)).credits.balance, 'a second redelivery grants nothing').toBe(before + 500);
});
