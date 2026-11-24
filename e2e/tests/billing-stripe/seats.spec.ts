/**
 * (c) Team seats on Stripe. More seats start now: the subscription items are updated with
 * `proration_behavior=always_invoice` and `payment_behavior=error_if_incomplete` (Stripe applies the update only
 * if the prorated invoice is paid), and the credits arrive when the `invoice.paid` webhook says it was paid. A
 * seat increase whose payment fails is not applied at all (see upgrade-payment-failed.spec.ts). Fewer seats are
 * confirmed first and sent as
 * `proration_behavior=none` (no invoice, no credit); the workspace keeps its seats until the period ends and
 * the renewal invoice bills the new count.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing-stripe/seats.spec.ts --project=billing-stripe
 */
import { test, expect } from '@playwright/test';
import { billingSummary, expectSummary, isStripeStack, planIncludedCredits, planPriceMinor, stripeMock, subscribeViaStripeApi, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('sx-seats');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe');
  test.skip(testInfo.project.name !== 'billing-stripe', 'flows run on the billing-stripe project');
});

test('Team (3) → 5 seats now (paid proration invoice, credits on invoice.paid) → 4 seats at period end', async ({ page }) => {
  test.setTimeout(180_000);
  const t = await users.create('team');
  await users.login(page, t);
  const { subscriptionId } = await subscribeViaStripeApi(page, 'team', 3);
  await page.goto('/app/settings/billing');
  const seatsInput = page.getByLabel('Seats on the current plan');
  const update = page.getByTestId('update-seats');
  await expect(update).toBeDisabled(); // same count

  const teamSeatInvoices = async () => (await stripeMock.state()).invoices.filter((i) => i.parent.subscription_details.subscription === subscriptionId);

  await test.step('3 → 5: the items move at once, the prorated invoice is paid, the credits follow its invoice.paid', async () => {
    const before = (await billingSummary(page)).credits.balance;
    await seatsInput.fill('5');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/seats'));
    await update.click();
    await expect(page.getByRole('dialog', { name: /^Fewer seats/ })).toHaveCount(0); // no confirm for more seats
    const r = await resp;
    expect(r.status()).toBe(202);
    expect(await r.json()).toMatchObject({ direction: 'upgrade', effective: 'now', seats: 5 });
    await expectSummary(page, (s) => s.subscription.seats, 5, 'five seats once the subscription webhook landed');

    const call = (await stripeMock.calls({ path: `/v1/subscriptions/${subscriptionId}`, method: 'POST' })).at(-1)!;
    expect(call.status).toBe(200);
    expect(call.body.proration_behavior).toBe('always_invoice');
    expect(call.body.payment_behavior, 'an unpaid seat increase must fail instead of being applied').toBe('error_if_incomplete');
    expect(call.body.items[0]).toMatchObject({ price: 'price_e2e_team', quantity: '5' });
    expect(call.body.items[0].id, 'the existing item is updated, not a second one added').toMatch(/^si_/);
    expect(call.idempotencyKey, 'a fresh nonce per call, never derived from the request').toMatch(new RegExp(`^change:${subscriptionId}:[0-9a-f]{32}$`));

    const paid = (await teamSeatInvoices()).find((i) => i.billing_reason === 'subscription_update')!;
    expect(paid).toMatchObject({ status: 'paid' });
    expect(paid.amount_paid).toBeGreaterThan(0);
    const charge = paid.lines.data.find((l: any) => l.amount > 0);
    expect(charge).toMatchObject({ quantity: 5 });
    expect(charge.pricing.price_details.price).toBe('price_e2e_team');
    expect(charge.parent.subscription_item_details.proration).toBe(true);
    expect(paid.lines.data.some((l: any) => l.amount < 0), 'the unused time of the old quantity is credited').toBe(true);

    await expect.poll(async () => (await billingSummary(page)).credits.balance, { message: 'the prorated grant after invoice.paid', timeout: 20_000 })
      .toBeGreaterThan(before);
    const gained = (await billingSummary(page)).credits.balance - before;
    expect(gained, 'less than two full extra seats').toBeLessThan(2 * planIncludedCredits('team'));
  });

  const periodEnd: string = (await billingSummary(page)).subscription.periodEnd;

  await test.step('5 → 4: confirm, the workspace keeps 5 until the period ends, no invoice, no credit', async () => {
    const invoicesBefore = (await teamSeatInvoices()).length;
    const balance = (await billingSummary(page)).credits.balance;
    await seatsInput.fill('4');
    await update.click();
    const dialog = page.getByRole('dialog', { name: /^Fewer seats/ });
    await expect(dialog).toContainText('You keep 5 seats until then.');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/seats'));
    await dialog.getByRole('button', { name: 'Confirm' }).click();
    const r = await resp;
    expect(r.status()).toBe(202);
    expect(await r.json()).toMatchObject({ direction: 'downgrade', effective: 'period_end', seats: 4 });

    const call = (await stripeMock.calls({ path: `/v1/subscriptions/${subscriptionId}`, method: 'POST' })).at(-1)!;
    expect(call.body.proration_behavior).toBe('none');
    expect(call.body.items[0].quantity).toBe('4');

    await expect(page.getByTestId('scheduled-change-banner')).toBeVisible({ timeout: 25_000 });
    const held = await billingSummary(page);
    expect(held.subscription.seats, 'still five until the period ends').toBe(5);
    expect(held.subscription.scheduledChange).not.toBeNull();
    expect(held.credits.balance).toBe(balance);
    expect((await teamSeatInvoices()).length, 'a decrease invoices nothing').toBe(invoicesBefore);
    expect(held.subscription.periodEnd).toBe(periodEnd);
  });

  await test.step('the period ends: the renewal bills four seats and the workspace has four', async () => {
    const { invoice } = await stripeMock.advancePeriod(subscriptionId);
    expect(invoice.billing_reason).toBe('subscription_cycle');
    expect(invoice.amount_paid).toBe(planPriceMinor('team') * 4);
    await expectSummary(page, (s) => `${s.subscription.seats}/${s.subscription.scheduledChange === null}`, '4/true', 'four seats at the new period');
    await page.reload();
    await expect(page.getByTestId('scheduled-change-banner')).toHaveCount(0);
    await expect(seatsInput).toHaveValue('4');
  });
});
