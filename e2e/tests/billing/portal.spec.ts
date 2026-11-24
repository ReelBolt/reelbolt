/**
 * Paddle's customer portal from Plan & billing: "Manage in Paddle" opens the overview; "Update
 * payment method" and "Cancel in Paddle" open the subscription's deep links, each from a fresh
 * portal session. A canceled → Trial workspace still reaches its invoices; a Trial workspace that
 * never bought anything has nothing to open. Throwaway Owners only.
 *
 *   cd e2e && npx playwright test tests/billing/portal.spec.ts --project=desktop
 */
import { test, expect, type Page } from '@playwright/test';
import { billingSummary, expectSummary, mock, subscribeViaApi, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('portal');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
});

/** Clicks `testId`, follows the portal URL, checks the mock page, and comes back through "Back to ReelBolt". */
async function openAndReturn(page: Page, testId: string, landing: RegExp, customerId: string) {
  const before = (await mock.calls({ path: `/customers/${customerId}/portal-sessions` })).length;
  const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/portal'));
  await page.getByTestId(testId).click();
  expect((await resp).status()).toBe(200);
  await page.waitForURL(landing);
  await expect(page.getByTestId('mock-portal-heading')).toBeVisible();
  // A fresh session for every click: portal URLs are short-lived and never cached.
  expect((await mock.calls({ path: `/customers/${customerId}/portal-sessions` })).length).toBe(before + 1);
  await page.getByTestId('mock-portal-back').click();
  await expect(page).toHaveURL(/\/app\/settings\/billing$/);
  await expect(page.getByTestId('current-plan-card')).toBeVisible();
}

test('subscribed: Manage in Paddle, Update payment method, Cancel in Paddle', async ({ page }) => {
  test.setTimeout(90_000);
  const t = await users.create('creator');
  await users.login(page, t);
  const { subscriptionId } = await subscribeViaApi(page, 'creator');
  const customerId = (await mock.liveSubscriptionOf(t.orgId!))!.customer_id as string;
  await page.goto('/app/settings/billing');
  await expect(page.getByTestId('manage-in-paddle')).toHaveText('Manage in Paddle');
  await openAndReturn(page, 'manage-in-paddle', /\/portal\/[^/]+\/overview$/, customerId);
  await openAndReturn(page, 'update-payment-method', new RegExp(`/portal/[^/]+/subscriptions/${subscriptionId}/update-payment-method$`), customerId);
  await openAndReturn(page, 'cancel-in-paddle', new RegExp(`/portal/[^/]+/subscriptions/${subscriptionId}/cancel$`), customerId);
  // Opening the cancel page is not canceling: nothing changed.
  expect((await billingSummary(page)).subscription.cancelAtPeriodEnd).toBe(false);
});

test('canceled → Trial: Manage in Paddle still opens the portal (invoices), no subscription links', async ({ page }) => {
  test.setTimeout(90_000);
  const t = await users.create('canceled');
  await users.login(page, t);
  const { subscriptionId } = await subscribeViaApi(page, 'creator');
  const customerId = (await mock.liveSubscriptionOf(t.orgId!))!.customer_id as string;
  await mock.cancelSubscription(subscriptionId, 'immediately');
  await expectSummary(page, (s) => s.subscription.planKey, 'trial');
  await page.goto('/app/settings/billing');
  await expect(page.getByTestId('manage-in-paddle')).toBeEnabled();
  await expect(page.getByTestId('update-payment-method')).toHaveCount(0);
  await expect(page.getByTestId('cancel-in-paddle')).toHaveCount(0);
  await openAndReturn(page, 'manage-in-paddle', /\/portal\/[^/]+\/overview$/, customerId);
});

test('trial without a Paddle customer: nothing to open', async ({ page }) => {
  const t = await users.create('trial');
  await users.login(page, t);
  await page.goto('/app/settings/billing');
  await expect(page.getByTestId('current-plan-card')).toContainText('This workspace has not been purchased yet');
  // Rendered disabled (CurrentPlanCard.tsx:83 `disabled={… || !hasBillingCustomer}`), not hidden.
  await expect(page.getByTestId('manage-in-paddle')).toBeDisabled();
  await expect(page.getByTestId('update-payment-method')).toHaveCount(0);
  await expect(page.getByTestId('cancel-in-paddle')).toHaveCount(0);
  expect((await billingSummary(page)).subscription.hasBillingCustomer).toBe(false);
});
