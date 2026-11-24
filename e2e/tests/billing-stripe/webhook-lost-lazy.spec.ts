/**
 * (e) The lazy path of lost-webhook recovery on Stripe: a seat change whose `customer.subscription.updated`
 * is lost leaves the row unconfirmed; past the stuck threshold the NEXT seat change heals the row itself
 * (it reads the subscription from Stripe first) and answers 202 instead of 409 change_pending.
 *
 * The background sweep would heal it first, so this spec RESTARTS ONLY THE INFERENCE SERVICE with
 * E2E_RECONCILE_SWEEP_SECONDS=3600 (same compose project, files and env file as up-isolated.sh) and
 * restores the default afterwards. The API answers 502 for a few seconds while that happens, so it runs in
 * its own project, alone, after the others:
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test --project=billing-stripe-restart --no-deps --workers=1
 *
 * (Without --no-deps Playwright first runs the billing-stripe project, which is what you want for a full run.)
 */
import { test, expect, type Page } from '@playwright/test';
import { billingSummary, clearReconcileWatch, expectSummary, isStripeStack, loadStack, restartInference, stripeMock, subscribeViaStripeApi, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'default' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-lazy');
test.afterAll(async () => users.cleanup());

const STUCK_AFTER_MS = Number(process.env.E2E_RECONCILE_STUCK_AFTER_SECONDS || 8) * 1000;

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe-restart', 'runs on the billing-stripe-restart project, alone');
});

test.describe('sweep paused (inference restarted with E2E_RECONCILE_SWEEP_SECONDS=3600)', () => {
  test.beforeAll(async ({ browser }, testInfo) => {
    if (testInfo.project.name !== 'billing-stripe-restart' || !isStripeStack()) return;
    test.setTimeout(420_000);
    const page = await browser.newPage();
    try { await restartInference(page, '3600'); } finally { await page.close(); }
  });

  test.afterAll(async ({ browser }, testInfo) => {
    if (testInfo.project.name !== 'billing-stripe-restart' || !isStripeStack()) return;
    test.setTimeout(420_000);
    const page = await browser.newPage();
    try { await restartInference(page, undefined); } finally { await page.close(); }
  });

  const post = (page: Page, route: string, data: unknown) => page.request.post(`${loadStack().baseUrl}/api/v1/billing/${route}`, { data });

  test('B (lazy): seat change with its webhook lost → past the threshold the next seat change heals and answers 202', async ({ page }) => {
    test.setTimeout(150_000);
    const t = await users.create('team');
    await users.login(page, t);
    const { subscriptionId } = await subscribeViaStripeApi(page, 'team', 3);
    // The checkout left a reconcile watch behind and the sweep is paused, so nothing would ever clear it. Stripe
    // sends no updated_at: a drift's age comes from the watch's OLDEST due time. Start from a clean watch so the
    // seat change below is the only thing the threshold counts from.
    clearReconcileWatch(t.orgId!);

    await stripeMock.dropNextWebhook({ event_type: 'customer.subscription.updated', subscription_id: subscriptionId });
    const r1 = await post(page, 'seats', { seats: 5 });
    expect(r1.status(), await r1.text()).toBe(202);
    expect(await r1.json()).toMatchObject({ direction: 'upgrade', effective: 'now', seats: 5 });
    const requestedAt = Date.now();

    const stuck = await post(page, 'seats', { seats: 6 });
    expect(stuck.status(), 'stuck before the threshold').toBe(409);
    expect((await stuck.json()).error).toBe('change_pending');
    const [lost] = (await stripeMock.webhooks()).filter((w) => w.dropped && w.subscription_id === subscriptionId);
    expect(lost).toMatchObject({ event_type: 'customer.subscription.updated', delivered: false });
    const sub = (await stripeMock.state()).subscriptions.find((s) => s.id === subscriptionId)!;
    expect(sub.items.data[0].quantity, 'Stripe has the new seats').toBe(5);

    // The one fixed wait: past the stuck threshold, counted from the change.
    const waitMs = requestedAt + STUCK_AFTER_MS + 1_500 - Date.now();
    if (waitMs > 0) await page.waitForTimeout(waitMs);
    expect((await billingSummary(page)).subscription.seats, 'the sweep did not heal it').toBe(3);

    const r2 = await post(page, 'seats', { seats: 6 });
    const r2Body = await r2.text();
    expect(r2.status(), `seat change after the threshold: ${r2Body}`).toBe(202);
    expect(JSON.parse(r2Body)).toMatchObject({ direction: 'upgrade', effective: 'now', seats: 6 });
    await expectSummary(page, (s) => s.subscription.seats, 6, 'the new seats after the lazy heal');
    const call = (await stripeMock.calls({ path: `/v1/subscriptions/${subscriptionId}`, method: 'POST' })).at(-1)!;
    expect(call.body.items[0].quantity).toBe('6');
    expect((await stripeMock.webhooks()).filter((w) => w.dropped && w.subscription_id === subscriptionId).every((w) => !w.delivered), 'healed without the lost webhook').toBe(true);
  });
});
