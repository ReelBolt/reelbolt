/**
 * Team seats on Plan & billing: Choose Team with a seat count; more seats take effect now (after the
 * webhook); fewer seats ask first and take effect at period end; above the self-serve cap the API's
 * refusal is shown. Throwaway Owner only.
 *
 *   cd e2e && npx playwright test tests/billing/seats.spec.ts --project=desktop
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, longDateIn, mock, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('seats');
test.afterAll(async () => users.cleanup());

test('Choose Team (3) → 5 seats now → 4 seats at period end → above the cap refused', async ({ page }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
  test.setTimeout(150_000);
  const t = await users.create('team');
  await users.login(page, t);
  await page.goto('/app/settings/billing');
  const card = page.getByTestId('current-plan-card');
  const seatsInput = page.getByLabel('Seats on the current plan');
  const update = page.getByTestId('update-seats');

  await test.step('Choose Team with 3 seats → pay', async () => {
    await page.getByLabel('Seats on the Team plan').fill('3');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
    await page.getByTestId('plan-team-choose').click();
    expect(JSON.parse((await resp).request().postData()!)).toEqual({ plan: 'team', seats: 3 });
    await page.waitForURL(/\/checkout\/txn_/);
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?_ptxn=txn_/);
    await expectSummary(page, (s) => `${s.subscription.planKey}/${s.subscription.seats}`, 'team/3');
    await page.goto('/app/settings/billing');
    await expect(update).toBeDisabled(); // same count
    await seatsInput.fill('2');
    await expect(update).toBeDisabled(); // below the Team minimum (3)
  });

  await test.step('increase 3 → 5: takes effect now, after the webhook; credits only once paid', async () => {
    const before = (await billingSummary(page)).credits.balance;
    const sub = await mock.liveSubscriptionOf(t.orgId!);
    await mock.setPayments(sub!.id, 'hold');
    await seatsInput.fill('5');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/seats'));
    await update.click();
    await expect(page.getByRole('dialog', { name: /^Fewer seats/ })).toHaveCount(0); // no confirm for more seats
    const r = await resp;
    expect(r.status()).toBe(202);
    expect(await r.json()).toMatchObject({ direction: 'upgrade', effective: 'now', seats: 5 });
    await expect(page.getByTestId('billing-done-notice')).toContainText('You have 5 seats now. Extra credits for the rest of this billing period are added as soon as Paddle confirms the payment.', { timeout: 25_000 });
    const s = await billingSummary(page);
    expect(s.subscription.seats).toBe(5);
    expect(s.credits.balance, 'no seat credits before the payment').toBe(before);
    await expect(card).toContainText('5');

    const [txn] = await mock.prorationTransactions(sub!.id);
    expect(txn).toMatchObject({ origin: 'subscription_update', status: 'billed' });
    expect(txn.items[0]).toMatchObject({ price_id: 'pri_e2e_team', quantity: 5 });
    const paid = await mock.releasePayment(txn.id);
    expect(paid.webhook?.delivered).toBe(true);
    await expect.poll(async () => (await billingSummary(page)).credits.balance, { timeout: 20_000 })
      .toBeGreaterThan(before); // prorated grant for the 2 extra seats
    const patch = (await mock.calls({ path: `/subscriptions/${sub!.id}`, method: 'PATCH' })).at(-1);
    expect((patch!.body as any).items[0].quantity).toBe(5);
  });

  const periodEndText = await longDateIn(page, (await billingSummary(page)).subscription.periodEnd);

  await test.step('decrease 5 → 4: confirm modal (Go back, then Confirm) → takes effect at period end', async () => {
    await seatsInput.fill('4');
    await update.click();
    let dialog = page.getByRole('dialog', { name: `Fewer seats from ${periodEndText}` });
    await expect(dialog).toContainText('You keep 5 seats until then.');
    await dialog.getByRole('button', { name: 'Go back' }).click();
    await expect(dialog).toBeHidden();
    expect((await billingSummary(page)).subscription.scheduledChange).toBeNull();

    await update.click();
    dialog = page.getByRole('dialog', { name: `Fewer seats from ${periodEndText}` });
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/seats'));
    await dialog.getByRole('button', { name: 'Confirm' }).click();
    const r = await resp;
    expect(r.status()).toBe(202);
    expect(await r.json()).toMatchObject({ direction: 'downgrade', effective: 'period_end', seats: 4 });
    await expect(page.getByTestId('billing-done-notice')).toContainText(`The workspace moves to 4 seats on ${periodEndText}. You keep 5 seats until then.`, { timeout: 25_000 });
    await expect(page.getByTestId('scheduled-change-banner')).toContainText(`Moves to Team on ${periodEndText}`);
    expect((await billingSummary(page)).subscription.seats).toBe(5); // unchanged until the period ends

    const sub = await mock.liveSubscriptionOf(t.orgId!);
    await mock.advancePeriod(sub!.id);
    await expectSummary(page, (s) => `${s.subscription.seats}/${s.subscription.scheduledChange === null}`, '4/true', 'seats reduced at the new period');
    await page.reload();
    await expect(page.getByTestId('scheduled-change-banner')).toHaveCount(0);
    await expect(seatsInput).toHaveValue('4');
  });

  await test.step('above the self-serve cap: refused with the cap message', async () => {
    await seatsInput.fill('201');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/seats'));
    await update.click();
    const r = await resp;
    expect(r.status()).toBe(400);
    const body = await r.json();
    expect(body.error).toBe('too_many_seats');
    await expect(page.getByText(`Self-serve plans go up to ${body.maxSeats} seats. Talk to us for more.`)).toBeVisible();
    expect((await billingSummary(page)).subscription.seats).toBe(4);
  });
});
