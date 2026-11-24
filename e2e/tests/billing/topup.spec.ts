/**
 * Credit top-ups on Plan & billing: every pack button opens a Paddle checkout for that pack; paying
 * "500 credits" adds 500 to the balance once the webhook lands. A Trial workspace is offered none.
 * Throwaway Owners only.
 *
 *   cd e2e && npx playwright test tests/billing/topup.spec.ts --project=desktop
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, subscribeViaApi, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('topup');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
});

test('subscribed: 1,000 and 3,000 open their checkout; 500 credits → pay → balance +500', async ({ page }) => {
  test.setTimeout(90_000);
  const t = await users.create('creator');
  await users.login(page, t);
  await subscribeViaApi(page, 'creator');
  await page.goto('/app/settings/billing');
  const packs = page.getByTestId('topup-card').getByRole('button');
  await expect(packs).toHaveText(['500 credits', '1,000 credits', '3,000 credits']);

  for (const [label, pack] of [['1,000 credits', 1000], ['3,000 credits', 3000]] as const) {
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
    await page.getByTestId('topup-card').getByRole('button', { name: label }).click();
    const r = await resp;
    expect(r.status()).toBe(200);
    expect(JSON.parse(r.request().postData()!)).toEqual({ topUpCredits: pack });
    await page.waitForURL(/\/checkout\/txn_/);
    await page.goBack(); // abandoned: nothing is charged
    await expect(page.getByTestId('topup-card')).toBeVisible();
  }

  const before = (await billingSummary(page)).credits.balance;
  const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
  await page.getByTestId('topup-card').getByRole('button', { name: '500 credits' }).click();
  expect(JSON.parse((await resp).request().postData()!)).toEqual({ topUpCredits: 500 });
  await page.waitForURL(/\/checkout\/txn_/);
  await page.getByTestId('mock-pay').click();
  await page.waitForURL(/\/app\/checkout\?_ptxn=txn_/);
  await expectSummary(page, (s) => s.credits.balance, before + 500, 'balance +500 after the webhook');
  await page.goto('/app/settings/billing');
  await expect(page.getByTestId('current-plan-card')).toContainText((before + 500).toLocaleString('en-US'));
  // Only the paid packs moved the balance: the two abandoned checkouts charged nothing.
  expect((await billingSummary(page)).credits.balance).toBe(before + 500);
});

test('trial: no top-up buttons', async ({ page }) => {
  const t = await users.create('trial');
  await users.login(page, t);
  await page.goto('/app/settings/billing');
  const topup = page.getByTestId('topup-card');
  await expect(topup).toContainText('Top-up credits are available on paid plans only.');
  await expect(topup.getByRole('button')).toHaveCount(0);
});
