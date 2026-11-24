/**
 * (d) Cancel and resume on Stripe, and the customer portal. The dashboard cancels at the period end
 * (`cancel_at_period_end=true`) and resumes (`=false`); both are webhook-authoritative, so the page follows
 * `customer.subscription.updated`. The portal links ("Manage in Stripe", "Update payment method", "Cancel in
 * Stripe") each mint a fresh Billing Portal session on the dedicated configuration; a cancel made inside the
 * portal (a change the platform did not start) reaches the dashboard the same way. At the period end the
 * subscription is deleted and the workspace falls back to Trial.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/cancel-resume.spec.ts --project=billing-stripe
 */
import { test, expect, type Page } from '@playwright/test';
import { billingSummary, expectSummary, isStripeStack, stripeMock, subscribeViaStripeApi, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-cancel');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

const PORTAL_URL = /\/portal\/bps_[A-Za-z0-9]+$/;

/** Clicks `testId`, follows the portal URL and checks the fake's page and the session it was minted from. */
async function openPortal(page: Page, testId: string, section: string, customerId: string) {
  const before = (await stripeMock.calls({ path: '/v1/billing_portal/sessions', method: 'POST' })).length;
  const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/portal'));
  await page.getByTestId(testId).click();
  expect((await resp).status()).toBe(200);
  await page.waitForURL(PORTAL_URL);
  await expect(page.getByTestId('mock-portal-heading')).toBeVisible();
  await expect(page.getByTestId('mock-portal-section')).toHaveText(section);
  // Every click mints sessions of its own: portal URLs are short-lived and never cached. The overview is
  // always one of them; the deep links add theirs.
  const minted = (await stripeMock.calls({ path: '/v1/billing_portal/sessions', method: 'POST' })).slice(before);
  expect(minted.length).toBeGreaterThanOrEqual(1);
  for (const call of minted) {
    expect(call.body).toMatchObject({ customer: customerId, configuration: 'bpc_e2e_portal' });
    expect(call.body.return_url).toMatch(/\/app\/settings\/billing$/);
  }
}

test('cancel → Cancels on … → keep; the portal links; a cancel made in the portal; period end → Trial', async ({ page }) => {
  test.setTimeout(180_000);
  const t = await users.create('owner');
  await users.login(page, t);
  const { subscriptionId } = await subscribeViaStripeApi(page, 'creator');
  const customerId = (await stripeMock.liveSubscriptionOf(t.orgId!))!.customer as string;
  const fakeSub = async () => (await stripeMock.state()).subscriptions.find((s) => s.id === subscriptionId)!;
  await page.goto('/app/settings/billing');

  await test.step('Cancel plan (dashboard) → the page says when, Stripe has cancel_at_period_end', async () => {
    await page.getByTestId('cancel-plan').click();
    const dialog = page.getByRole('dialog', { name: 'Cancel your plan?' });
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/cancel'));
    await dialog.getByRole('button', { name: 'Cancel plan' }).click();
    expect((await resp).status()).toBe(202);
    await expectSummary(page, (s) => s.subscription.cancelAtPeriodEnd, true, 'the cancel webhook landed');
    await expect(page.getByTestId('cancel-banner')).toContainText('Cancels on', { timeout: 25_000 });
    await expect(page.getByTestId('cancel-plan')).toHaveCount(0);

    const sub = await fakeSub();
    expect(sub.cancel_at_period_end).toBe(true);
    expect(sub.cancel_at).toBe(sub.items.data[0].current_period_end);
    const call = (await stripeMock.calls({ path: `/v1/subscriptions/${subscriptionId}`, method: 'POST' })).at(-1)!;
    expect(call.body).toEqual({ cancel_at_period_end: 'true' });
  });

  await test.step('banner "Keep my plan" → resumed, the banner is gone', async () => {
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/resume'));
    await page.getByTestId('cancel-banner-keep').click();
    expect((await resp).status()).toBe(202);
    await expectSummary(page, (s) => s.subscription.cancelAtPeriodEnd, false, 'the resume webhook landed');
    await expect(page.getByTestId('cancel-banner')).toHaveCount(0, { timeout: 25_000 });
    await expect(page.getByTestId('cancel-plan')).toBeVisible();
    const sub = await fakeSub();
    expect(sub.cancel_at_period_end).toBe(false);
    expect(sub.cancel_at).toBeNull();
    const call = (await stripeMock.calls({ path: `/v1/subscriptions/${subscriptionId}`, method: 'POST' })).at(-1)!;
    expect(call.body).toEqual({ cancel_at_period_end: 'false' });
  });

  await test.step('Manage in Stripe and Update payment method open portal sessions on the dedicated configuration', async () => {
    await expect(page.getByTestId('manage-in-stripe')).toBeEnabled();
    await openPortal(page, 'manage-in-stripe', 'overview', customerId);
    await page.getByTestId('mock-portal-back').click();
    await expect(page).toHaveURL(/\/app\/settings\/billing$/);
    await openPortal(page, 'update-payment-method', 'payment_method_update', customerId);
    await page.getByTestId('mock-portal-back').click();
    await expect(page.getByTestId('current-plan-card')).toBeVisible();
  });

  await test.step('Cancel in Stripe: opening the page cancels nothing; confirming it there reaches the dashboard', async () => {
    await openPortal(page, 'cancel-in-stripe', 'subscription_cancel', customerId);
    expect((await billingSummary(page)).subscription.cancelAtPeriodEnd, 'opening the cancel page is not canceling').toBe(false);
    const flow = (await stripeMock.calls({ path: '/v1/billing_portal/sessions', method: 'POST' })).filter((c) => c.body?.flow_data?.type === 'subscription_cancel').at(-1)!;
    expect(flow.body.flow_data.subscription_cancel.subscription).toBe(subscriptionId);

    await page.getByTestId('mock-portal-cancel').click();
    await expect(page).toHaveURL(/\/app\/settings\/billing$/);
    await expectSummary(page, (s) => s.subscription.cancelAtPeriodEnd, true, 'a cancel made in the portal arrives as a webhook');
    // The page was loaded when the browser came back from the portal, before the webhook landed: the next visit shows it.
    await page.reload();
    await expect(page.getByTestId('cancel-banner')).toContainText('Cancels on', { timeout: 25_000 });
    const keep = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/resume'));
    await page.getByTestId('cancel-banner-keep').click();
    expect((await keep).status()).toBe(202);
    await expectSummary(page, (s) => s.subscription.cancelAtPeriodEnd, false, 'resumed again');
  });

  await test.step('cancel once more → the period ends → customer.subscription.deleted → Trial', async () => {
    await page.reload();
    await page.getByTestId('cancel-plan').click();
    await page.getByRole('dialog', { name: 'Cancel your plan?' }).getByRole('button', { name: 'Cancel plan' }).click();
    await expect(page.getByTestId('cancel-banner')).toBeVisible({ timeout: 25_000 });
    await stripeMock.advancePeriod(subscriptionId);
    await expectSummary(page, (s) => s.subscription.planKey, 'trial', 'falls back to Trial after the subscription is deleted');
    expect((await fakeSub()).status).toBe('canceled');
    await page.reload();
    await expect(page.getByTestId('current-plan-card')).toContainText('Trial');
    await expect(page.getByTestId('cancel-banner')).toHaveCount(0);
    await expect(page.getByTestId('manage-in-stripe'), 'invoices of the canceled plan stay reachable').toBeEnabled();
  });
});
