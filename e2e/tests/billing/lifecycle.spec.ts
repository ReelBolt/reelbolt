/**
 * Plan lifecycle on Plan & billing, through the buttons a customer clicks (saas-subscription-mgmt §B):
 * Choose Creator → pay → Upgrade to Pro → Cancel plan → Keep my plan → Cancel plan → period end →
 * Trial, and a new checkout works again (F-01). One throwaway Owner, deleted afterwards.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing/lifecycle.spec.ts --project=desktop
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, longDateIn, mock, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('life');
test.afterAll(async () => users.cleanup());

test('subscribe → upgrade → cancel → keep → cancel → period end → Trial → subscribe again', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
  test.setTimeout(180_000);
  const t = await users.create('owner');
  await users.login(page, t);
  await page.goto('/app/settings/billing');
  const card = page.getByTestId('current-plan-card');
  await expect(card).toContainText('Trial');

  await test.step('Choose Creator → Paddle checkout → Pay now → plan Creator', async () => {
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
    await page.getByTestId('plan-creator-choose').click();
    expect((await resp).status()).toBe(200);
    await page.waitForURL(/\/checkout\/txn_/);
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?_ptxn=txn_/);
    await page.getByRole('link', { name: 'Check your plan and balance' }).click();
    await expect(page).toHaveURL(/\/app\/settings\/billing$/);
    await expectSummary(page, (s) => `${s.subscription.planKey}/${s.subscription.status}`, 'creator/Active');
    await page.reload();
    await expect(card).toContainText('Creator');
    await expect(page.getByTestId('cancel-plan')).toBeVisible();
  });

  await test.step('Upgrade to Pro (Go back first, then Confirm) → Pro now, prorated credits only once paid', async () => {
    const before = (await billingSummary(page)).credits.balance;
    const sub = await mock.liveSubscriptionOf(t.orgId!);
    await mock.setPayments(sub!.id, 'hold'); // Paddle has not charged the proration yet
    const upgrade = page.getByTestId('plan-pro-change');
    await expect(upgrade).toHaveText('Upgrade to Pro');
    await upgrade.click();
    let dialog = page.getByRole('dialog', { name: 'Upgrade to Pro?' });
    await expect(dialog).toContainText('The new plan starts now.');
    await dialog.getByRole('button', { name: 'Go back' }).click();
    await expect(dialog).toBeHidden();

    await upgrade.click();
    dialog = page.getByRole('dialog', { name: 'Upgrade to Pro?' });
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/change-plan'));
    await dialog.getByRole('button', { name: 'Confirm' }).click();
    const r = await resp;
    expect(r.status()).toBe(202);
    expect(JSON.parse(r.request().postData()!)).toEqual({ plan: 'pro' });
    const body = await r.json();
    expect(body).toMatchObject({ direction: 'upgrade', effective: 'now' });
    const notice = page.getByTestId('billing-done-notice');
    await expect(notice).toContainText("You're on Pro now. Extra credits for the rest of this billing period are added as soon as Paddle confirms the payment.", { timeout: 25_000 });
    expect(await notice.textContent()).not.toMatch(/\d/); // never states a credit number
    await expect(card).toContainText('Pro');

    // Plan already Pro, payment held: the balance has not moved.
    const [txn] = await mock.prorationTransactions(sub!.id);
    expect(txn).toMatchObject({ origin: 'subscription_update', status: 'billed', subscription_id: sub!.id, customer_id: sub!.customer_id });
    expect(txn.items[0]).toMatchObject({ price_id: 'pri_e2e_pro', quantity: 1 });
    expect(txn.items[0].proration.billing_period.ends_at).toBe(sub!.current_billing_period.ends_at);
    const held = await billingSummary(page);
    expect(held.subscription.planKey).toBe('pro');
    expect(held.credits.balance, 'no upgrade credits before the payment').toBe(before);

    // Paddle charges it: transaction.completed → the prorated grant lands.
    const paid = await mock.releasePayment(txn.id);
    expect(paid.webhook?.delivered).toBe(true);
    await expect.poll(async () => (await billingSummary(page)).credits.balance, { timeout: 20_000 }).toBeGreaterThan(before);
    const after = await billingSummary(page);
    expect(after.subscription.planKey).toBe('pro');
    const gained = after.credits.balance - before;
    expect(gained, 'prorated upgrade grant').toBeGreaterThan(0);
    expect(gained).toBeLessThan(2500); // Pro 3500 − Creator 1000, prorated to the time left
    expect(Math.abs(gained - body.creditGrantMillis / 1000)).toBeLessThan(1);
  });

  const periodEnd: string = (await billingSummary(page)).subscription.periodEnd;
  const periodEndText = await longDateIn(page, periodEnd);

  await test.step('Cancel plan → modal names the period end → Keep my plan closes it', async () => {
    await page.getByTestId('cancel-plan').click();
    const dialog = page.getByRole('dialog', { name: 'Cancel your plan?' });
    await expect(dialog).toContainText(`Your plan stays active until ${periodEndText}.`);
    await dialog.getByRole('button', { name: 'Keep my plan' }).click();
    await expect(dialog).toBeHidden();
    await expect(page.getByTestId('cancel-banner')).toHaveCount(0);
    expect((await billingSummary(page)).subscription.cancelAtPeriodEnd).toBe(false);
  });

  await test.step('Cancel plan (confirm) → banner "Cancels on {date}", plan changes blocked', async () => {
    await page.getByTestId('cancel-plan').click();
    const dialog = page.getByRole('dialog', { name: 'Cancel your plan?' });
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/cancel'));
    await dialog.getByRole('button', { name: 'Cancel plan' }).click();
    expect((await resp).status()).toBe(202);
    await expect(page.getByTestId('billing-done-notice')).toContainText(`Your plan cancels on ${periodEndText}.`, { timeout: 25_000 });
    await expect(page.getByTestId('cancel-banner')).toContainText(`Cancels on ${periodEndText}`);
    await expect(page.getByTestId('cancel-plan')).toHaveCount(0);
    await expect(page.getByTestId('plans-card').getByText('Keep your plan first to change it.')).toHaveCount(2);
    await expect(page.locator('[data-testid$="-change"]')).toHaveCount(0);
  });

  await test.step('banner Keep my plan → banner gone', async () => {
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/resume'));
    await page.getByTestId('cancel-banner-keep').click();
    expect((await resp).status()).toBe(202);
    await expect(page.getByTestId('billing-done-notice')).toContainText('You keep Pro. It renews as usual.', { timeout: 25_000 });
    await expect(page.getByTestId('cancel-banner')).toHaveCount(0);
    await expect(page.getByTestId('cancel-plan')).toBeVisible();
  });

  await test.step('Cancel plan again → period end → Trial, "Choose {Plan}" offered again (F-01)', async () => {
    await page.getByTestId('cancel-plan').click();
    await page.getByRole('dialog', { name: 'Cancel your plan?' }).getByRole('button', { name: 'Cancel plan' }).click();
    await expect(page.getByTestId('cancel-banner')).toBeVisible({ timeout: 25_000 });
    const sub = await mock.liveSubscriptionOf(t.orgId!);
    expect(sub?.scheduled_change?.action).toBe('cancel');
    await mock.advancePeriod(sub!.id);
    await expectSummary(page, (s) => s.subscription.planKey, 'trial', 'falls back to Trial after the cancel takes effect');
    await page.reload();
    await expect(card).toContainText('Trial');
    for (const name of ['Creator', 'Pro', 'Team']) {
      await expect(page.getByRole('button', { name: `Choose ${name}` })).toBeEnabled();
    }
    await expect(page.getByTestId('cancel-banner')).toHaveCount(0);
    await expect(page.getByTestId('manage-in-paddle')).toBeEnabled(); // invoices of the canceled plan
    await expect(page.getByTestId('topup-card')).toContainText('Top-up credits are available on paid plans only.');
  });

  await test.step('a new checkout works: Choose Pro → pay → Pro', async () => {
    await page.getByTestId('plan-pro-choose').click();
    await page.waitForURL(/\/checkout\/txn_/);
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?_ptxn=txn_/);
    await expectSummary(page, (s) => `${s.subscription.planKey}/${s.subscription.status}`, 'pro/Active');
    await page.goto('/app/settings/billing');
    await expect(card).toContainText('Pro');
    await expect(page.getByTestId('cancel-plan')).toBeVisible();
  });
});
