/**
 * A recurring promo discount across plan changes. A 20% discount for 3 months targets
 * Creator and Pro. The Trial owner buys Creator with the code, so the live Paddle subscription
 * carries the discount.
 *   (a) Creator → Pro (a targeted plan): the modal says nothing about the promo, the
 *       PATCH /subscriptions/{id} has no `discount` key, and the subscription keeps the discount.
 *   (b) Pro → Team (not targeted): the modal says the promo ends, the same PATCH sends
 *       `"discount": null`, the mock's subscription loses it, and the admin redemptions drawer shows
 *       "Ended on plan change".
 * Throwaway Owner only.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing/promo-plan-change.spec.ts --project=desktop
 */
import { expect, test, type Browser, type Page } from '@playwright/test';
import { billingSummary, expectSummary, loadStack, loginAs, mock, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('promoplan');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
});

function randomSuffix(length = 6): string {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  let out = '';
  for (let i = 0; i < length; i++) out += alphabet[Math.floor(Math.random() * alphabet.length)];
  return out;
}

async function adminPage(browser: Browser): Promise<Page> {
  const context = await browser.newContext({ baseURL: loadStack().baseUrl });
  const page = await context.newPage();
  await loginAs(page, 'admin');
  return page;
}

/** The last PATCH /subscriptions/{id} the Inference API sent for this subscription. */
async function lastSubscriptionPatch(subscriptionId: string): Promise<Record<string, unknown>> {
  const calls = (await mock.calls({ path: `/subscriptions/${subscriptionId}`, method: 'PATCH' }))
    .filter((c) => c.path.endsWith(`/subscriptions/${subscriptionId}`));
  const last = calls.at(-1);
  expect(last, `a PATCH /subscriptions/${subscriptionId}`).toBeTruthy();
  expect(last!.status, 'the mock accepted the PATCH').toBe(200);
  return last!.body as Record<string, unknown>;
}

test('a 3-month 20% promo on Creator + Pro: kept on Creator → Pro, removed on Pro → Team', async ({ browser, page }) => {
  test.setTimeout(180_000);
  const { baseUrl } = loadStack();
  const code = `E2EPLAN20${randomSuffix()}`;
  const name = `E2E Plan change 20% ${code}`;
  const discountsBefore = new Set((await mock.discounts()).map((d) => d.id));

  // --- Admin: a 20% plan discount for 3 months on Creator and Pro.
  const admin = await adminPage(browser);
  let promotion: any;
  await test.step('admin creates the promotion', async () => {
    await admin.goto('/app/admin/promotions');
    await expect(admin.getByTestId('admin-promotions-table').or(admin.getByText('No promotions yet'))).toBeVisible();
    await admin.getByTestId('create-promotion-button').click();
    const dialog = admin.getByRole('dialog', { name: 'New promotion' });
    await expect(dialog).toBeVisible();
    await dialog.getByText('Plan discount', { exact: true }).click();
    await dialog.getByRole('textbox', { name: /^Name/ }).fill(name);
    await dialog.getByRole('textbox', { name: /^Public headline/ }).fill('E2E: 20% off Creator and Pro');
    await dialog.getByRole('textbox', { name: /^Code/ }).fill(code);
    await dialog.getByText('Percentage', { exact: true }).click();
    await dialog.getByRole('textbox', { name: /^Percent off/ }).fill('20');
    await dialog.getByText('Several months', { exact: true }).click();
    await dialog.getByRole('textbox', { name: /^Months/ }).fill('3');
    await dialog.getByRole('combobox', { name: 'Plans' }).click({ force: true });
    await admin.getByRole('option', { name: 'Creator', exact: true }).click();
    await admin.getByRole('option', { name: 'Pro', exact: true }).click();
    await admin.keyboard.press('Escape');
    await expect(dialog).toBeVisible();
    await dialog.getByTestId('create-promotion-submit').click();
    await expect(dialog).toBeHidden();
    await expect(admin.getByTestId('admin-promotions-table')).toContainText(code);

    const list = await admin.request.get(`${baseUrl}/api/v1/billing/admin/promotions`);
    expect(list.status()).toBe(200);
    promotion = ((await list.json()).items as any[]).find((p) => p.code === code);
    expect(promotion, `promotion ${code}`).toBeTruthy();
    expect(String(promotion.durationKind)).toMatch(/months/i);
    expect(promotion.durationMonths).toBe(3);
    // Other specs create discounts in parallel against the same mock: look at this promotion's only.
    const created = (await mock.discounts()).filter((d) => !discountsBefore.has(d.id) && d.id === promotion.providerDiscountId);
    expect(created, `POST /discounts received ${promotion.providerDiscountId}`).toHaveLength(1);
    expect(created[0].recur, 'a 3-month discount recurs').toBe(true);
    expect([...(created[0].restrict_to ?? [])].sort()).toEqual(['pri_e2e_creator', 'pri_e2e_pro']);
  });
  const discountId: string = promotion.providerDiscountId;

  // --- Owner: buy Creator with the code.
  const t = await users.create('owner');
  await users.login(page, t);
  let subscriptionId = '';
  await test.step('owner buys Creator with the code; the live subscription carries the discount', async () => {
    const r = await page.request.post(`${baseUrl}/api/v1/billing/checkout`, { data: { plan: 'creator', promoCode: code } });
    expect(r.status(), `checkout: ${await r.text()}`).toBe(200);
    const { transactionId } = await r.json();
    const txn = (await mock.transactions()).find((x) => x.id === transactionId);
    expect(txn?.request?.discount_id).toBe(discountId);
    const done = await mock.completeTransaction(transactionId);
    subscriptionId = done.subscription.id;
    expect(done.subscription.discount).toMatchObject({ id: discountId, ends_at: null });
    await expectSummary(page, (x) => `${x.subscription?.planKey}/${x.subscription?.status}`, 'creator/Active');
    await expect
      .poll(async () => (await billingSummary(page)).subscription.promoDiscount ?? null, { message: 'summary promoDiscount after the webhook', timeout: 20_000 })
      .toMatchObject({ discountType: 'Percentage', amount: '20' });
    const promo = (await billingSummary(page)).subscription.promoDiscount;
    expect([...promo.targetPriceIds].sort()).toEqual(['pri_e2e_creator', 'pri_e2e_pro']);
    // Customer-facing: no promotion name or internal id.
    expect(JSON.stringify(promo)).not.toContain(promotion.id);
    expect(JSON.stringify(promo)).not.toContain(code);
  });

  await page.goto('/app/settings/billing');
  await expect(page.getByTestId('current-plan-card')).toContainText('Creator');

  await test.step('(a) Creator → Pro (targeted): no promo line, no discount key, the discount stays', async () => {
    await page.getByTestId('plan-pro-change').click();
    const dialog = page.getByRole('dialog', { name: 'Upgrade to Pro?' });
    await expect(dialog).toBeVisible();
    await expect(page.getByTestId('change-plan-promo-ends')).toHaveCount(0);
    await expect(dialog).not.toContainText('promo ends with this change');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/change-plan'));
    await page.getByTestId('change-plan-modal-confirm').click();
    const r = await resp;
    expect(r.status()).toBe(202);
    expect(await r.json()).toMatchObject({ direction: 'upgrade', effective: 'now', promoDiscountRemoved: false });

    const body = await lastSubscriptionPatch(subscriptionId);
    expect(Object.prototype.hasOwnProperty.call(body, 'discount'), `PATCH body ${JSON.stringify(body)} has no discount key`).toBe(false);
    expect((body.items as any[]).map((i) => i.price_id)).toEqual(['pri_e2e_pro']);
    expect(body.proration_billing_mode).toBe('prorated_immediately');

    const sub = (await mock.state()).subscriptions.find((s) => s.id === subscriptionId)!;
    expect(sub.discount).toMatchObject({ id: discountId });

    // The proration payment settles (payments default to pay).
    await expect.poll(async () => (await mock.prorationTransactions(subscriptionId)).map((x) => x.status), { timeout: 20_000 })
      .toEqual(['completed']);
    const s = await expectSummary(page, (x) => x.subscription.planKey, 'pro', 'Pro after subscription.updated');
    expect(s.subscription.promoDiscount).toMatchObject({ discountType: 'Percentage', amount: '20' });
  });

  await test.step('(b) Pro → Team (not targeted): the modal says so, "discount": null, the discount is gone', async () => {
    await page.reload();
    await expect(page.getByTestId('current-plan-card')).toContainText('Pro');
    await page.getByTestId('plan-team-change').click();
    const dialog = page.getByRole('dialog', { name: 'Upgrade to Team?' });
    await expect(dialog).toBeVisible();
    await expect(page.getByTestId('change-plan-promo-ends')).toHaveText('Your 20% promo ends with this change.');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/change-plan'));
    await page.getByTestId('change-plan-modal-confirm').click();
    const r = await resp;
    expect(r.status(), await r.text()).toBe(202);
    expect(await r.json()).toMatchObject({ direction: 'upgrade', effective: 'now', promoDiscountRemoved: true });

    const body = await lastSubscriptionPatch(subscriptionId);
    expect(Object.prototype.hasOwnProperty.call(body, 'discount'), `PATCH body ${JSON.stringify(body)} carries discount`).toBe(true);
    expect(body.discount).toBeNull();
    expect((body.items as any[]).map((i) => i.price_id)).toEqual(['pri_e2e_team']);
    // One PATCH per change: Creator → Pro, then Pro → Team.
    const patches = (await mock.calls({ path: `/subscriptions/${subscriptionId}`, method: 'PATCH' }))
      .filter((c) => c.path.endsWith(`/subscriptions/${subscriptionId}`));
    expect(patches).toHaveLength(2);

    const sub = (await mock.state()).subscriptions.find((s) => s.id === subscriptionId)!;
    expect(sub.discount).toBeNull();
    const s = await expectSummary(page, (x) => x.subscription.planKey, 'team', 'Team after subscription.updated');
    expect(s.subscription.promoDiscount ?? null).toBeNull();
  });

  await test.step('admin: the redemption shows "Ended on plan change"', async () => {
    const rr = await admin.request.get(`${baseUrl}/api/v1/billing/admin/promotions/${promotion.id}/redemptions`);
    expect(rr.status()).toBe(200);
    const rb = await rr.json();
    const items: any[] = Array.isArray(rb) ? rb : rb.items ?? [];
    const removed = items.filter((i) => i.discountRemovedAt);
    expect(removed, `redemptions ${JSON.stringify(items)}`).toHaveLength(1);

    await admin.goto('/app/admin/promotions');
    const row = admin.getByTestId(`promotion-row-${promotion.id}`);
    await expect(row).toBeVisible();
    await row.getByRole('button', { name: `Actions for ${name}` }).click();
    await admin.getByRole('menuitem', { name: 'Redemptions' }).click();
    const drawer = admin.getByRole('dialog', { name: `Redemptions: ${name}` });
    await expect(drawer.getByTestId('promotion-redemptions-table')).toBeVisible();
    await expect(drawer.getByText('Ended on plan change')).toBeVisible();
  });
  await admin.context().close();
});
