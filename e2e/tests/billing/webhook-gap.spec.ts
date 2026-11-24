/**
 * F-04, the webhook gap: a Creator checkout is paid but Paddle's webhooks are held back, so the page
 * still shows Trial. A second plan checkout must be refused (409 subscription_pending) with the
 * server's message on screen; once the webhooks land the plan is Creator. Throwaway Owner only.
 *
 * The webhooks are held with the mock's per-transaction delivery delay
 * (`POST /__mock/transactions/{id}/complete {delayMs}`): Paddle has really taken the payment and
 * created the subscription, only the notification is late — exactly the production gap.
 *
 *   cd e2e && npx playwright test tests/billing/webhook-gap.spec.ts --project=desktop
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, loadStack, mock, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('gap');
test.afterAll(async () => users.cleanup());

const HOLD_MS = 12_000;

test('paid, webhooks held: second plan checkout → 409 subscription_pending on screen; webhooks land → Creator', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
  test.setTimeout(90_000);
  const t = await users.create('owner');
  await users.login(page, t);
  await page.goto('/app/settings/billing');

  // Choose Creator in the UI; pay it in the mock with the webhooks held back.
  const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
  await page.getByTestId('plan-creator-choose').click();
  expect((await resp).status()).toBe(200);
  // The page leaves for the checkout at once, so the transaction id is read from the checkout URL.
  await page.waitForURL(/\/checkout\/txn_/);
  const transactionId = /\/checkout\/(txn_[^/?#]+)/.exec(page.url())![1];
  const paidAt = Date.now();
  const paid = await mock.completeTransaction(transactionId, HOLD_MS);
  expect(paid.subscription?.status).toBe('active');

  await page.goto(`/app/checkout?_ptxn=${transactionId}`);
  await page.getByRole('link', { name: 'Check your plan and balance' }).click();
  await expect(page).toHaveURL(/\/app\/settings\/billing$/);
  await expect(page.getByTestId('current-plan-card')).toContainText('Trial');

  // The second plan checkout during the gap, from the UI.
  const second = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
  await page.getByTestId('plan-pro-choose').click();
  const r2 = await second;
  expect(r2.status()).toBe(409);
  const body = await r2.json();
  expect(body.error).toBe('subscription_pending');
  await expect(page).toHaveURL(/\/app\/settings\/billing$/); // no checkout opened
  await expect(page.getByText('That did not go through')).toBeVisible();
  await expect(page.getByText(body.message)).toBeVisible();

  // Same refusal for the API directly.
  const api = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: { plan: 'team', seats: 3 } });
  expect(api.status()).toBe(409);
  expect((await api.json()).error).toBe('subscription_pending');
  expect(Date.now() - paidAt, 'the checks ran inside the gap').toBeLessThan(HOLD_MS);
  expect((await billingSummary(page)).subscription.planKey).toBe('trial');

  // Webhooks land → Creator; the page (reloaded) offers Upgrade/Switch, not Choose.
  await expectSummary(page, (s) => `${s.subscription.planKey}/${s.subscription.status}`, 'creator/Active', 'Creator once the held webhooks land', 30_000);
  await page.reload();
  await expect(page.getByTestId('current-plan-card')).toContainText('Creator');
  await expect(page.getByTestId('plan-pro-change')).toHaveText('Upgrade to Pro');
  // Only the first checkout ever became a subscription.
  const subs = (await mock.state()).subscriptions.filter((s) => s.custom_data?.organization_id === t.orgId);
  expect(subs).toHaveLength(1);
});
