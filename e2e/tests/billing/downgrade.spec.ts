/**
 * Downgrade on Plan & billing: Pro → "Switch to Creator" is held until the period ends ("Moves to
 * Creator on {date}"), "Keep Pro" removes it, and the next period starts on Creator with Creator's
 * credits. While a cancel is scheduled no plan change is offered, and the API refuses one.
 * Throwaway Owners only.
 *
 *   cd e2e && npx playwright test tests/billing/downgrade.spec.ts --project=desktop
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, loadStack, longDateIn, mock, subscribeViaApi, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('down');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
});

test('Pro → Switch to Creator → Keep Pro → Switch again → period end → Creator with Creator credits', async ({ page }) => {
  test.setTimeout(120_000);
  const t = await users.create('pro');
  await users.login(page, t);
  const { summary: start } = await subscribeViaApi(page, 'pro');
  const periodEndText = await longDateIn(page, start.subscription.periodEnd);
  await page.goto('/app/settings/billing');
  const card = page.getByTestId('current-plan-card');
  await expect(card).toContainText('Pro');

  const switchBtn = page.getByTestId('plan-creator-change');
  await expect(switchBtn).toHaveText('Switch to Creator');

  await test.step('Switch to Creator (Confirm) → banner "Moves to Creator on {date}", still Pro', async () => {
    await switchBtn.click();
    const dialog = page.getByRole('dialog', { name: 'Switch to Creator?' });
    await expect(dialog).toContainText(`You keep Pro until ${periodEndText}. Creator starts with your next billing period.`);
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/change-plan'));
    await dialog.getByRole('button', { name: 'Confirm' }).click();
    const r = await resp;
    expect(r.status()).toBe(202);
    expect(await r.json()).toMatchObject({ direction: 'downgrade', effective: 'period_end', creditGrantMillis: 0 });
    await expect(page.getByTestId('billing-done-notice')).toContainText(`Creator starts on ${periodEndText}. You keep Pro until then.`, { timeout: 25_000 });
    await expect(page.getByTestId('scheduled-change-banner')).toContainText(`Moves to Creator on ${periodEndText}`);
    await expect(card).toContainText('Pro');
    const s = await billingSummary(page);
    expect(s.subscription.planKey).toBe('pro');
    expect(s.subscription.scheduledChange).toMatchObject({ planKey: 'creator' });
  });

  await test.step('Keep Pro → banner gone', async () => {
    const keep = page.getByTestId('scheduled-change-keep');
    await expect(keep).toHaveText('Keep Pro');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/change-plan'));
    await keep.click();
    const r = await resp;
    expect(JSON.parse(r.request().postData()!)).toEqual({ plan: 'pro' });
    expect(await r.json()).toMatchObject({ direction: 'keep' });
    await expect(page.getByTestId('billing-done-notice')).toContainText('You keep Pro. The scheduled change is removed.', { timeout: 25_000 });
    await expect(page.getByTestId('scheduled-change-banner')).toHaveCount(0);
    expect((await billingSummary(page)).subscription.scheduledChange).toBeNull();
    // A full_next_billing_period downgrade and a scheduled_change: null charge nothing.
    const sub = await mock.liveSubscriptionOf(t.orgId!);
    expect(await mock.prorationTransactions(sub!.id)).toEqual([]);
  });

  await test.step('Switch again → period end → Creator, refilled with Creator credits', async () => {
    await switchBtn.click();
    await page.getByRole('dialog', { name: 'Switch to Creator?' }).getByRole('button', { name: 'Confirm' }).click();
    await expect(page.getByTestId('scheduled-change-banner')).toBeVisible({ timeout: 25_000 });
    const before = (await billingSummary(page)).credits.balance;
    const sub = await mock.liveSubscriptionOf(t.orgId!);
    await mock.advancePeriod(sub!.id);
    const after = await expectSummary(page, (s) => `${s.subscription.planKey}/${s.subscription.scheduledChange === null}`, 'creator/true');
    const creator = after.plans.find((p: any) => p.key === 'creator');
    expect(after.credits.balance - before, "new period's refill is Creator's amount").toBe(creator.includedCredits);
    await page.reload();
    await expect(card).toContainText('Creator');
    await expect(page.getByTestId('scheduled-change-banner')).toHaveCount(0);
    await expect(page.getByTestId('plan-pro-change')).toHaveText('Upgrade to Pro');
  });
});

test('a cancel scheduled: no plan change offered, and the API answers 409 cancel_scheduled', async ({ page }) => {
  test.setTimeout(90_000);
  const t = await users.create('cancel');
  await users.login(page, t);
  await subscribeViaApi(page, 'pro');
  await page.goto('/app/settings/billing');
  await page.getByTestId('cancel-plan').click();
  await page.getByRole('dialog', { name: 'Cancel your plan?' }).getByRole('button', { name: 'Cancel plan' }).click();
  await expect(page.getByTestId('cancel-banner')).toBeVisible({ timeout: 25_000 });
  await expect(page.locator('[data-testid^="plan-"][data-testid$="-change"]')).toHaveCount(0);
  await expect(page.locator('[data-testid^="plan-"][data-testid$="-choose"]')).toHaveCount(0);
  await expect(page.getByTestId('plans-card').getByText('Keep your plan first to change it.')).toHaveCount(2);

  const r = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/subscription/change-plan`, { data: { plan: 'creator' } });
  expect(r.status()).toBe(409);
  expect((await r.json()).error).toBe('cancel_scheduled');
  expect((await billingSummary(page)).subscription.planKey).toBe('pro');
});
