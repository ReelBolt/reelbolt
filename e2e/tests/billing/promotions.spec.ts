/**
 * Promotions on the isolated stack (spec §E.5, flows 1 and 2):
 *
 *   source e2e/.stack/<project>/env.sh
 *   npx playwright test tests/billing/promotions.spec.ts --project=desktop
 *
 * The specs never reset the Paddle mock (the seeded subscriptions live there); they look only at the
 * discounts and transactions created after they start.
 */
import { expect, test, type APIRequestContext, type Browser, type Page } from '@playwright/test';
import { loadStack, loginAs, mock, Throwaways, user, type MockTransaction, type SeededLabel } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });

// The checkout gives its owner a Paddle customer, so it runs as a throwaway Trial owner: the seeded
// `trial` user must stay customer-less for repro-walk's and portal's Trial assertions.
const users = new Throwaways('promo');
test.afterAll(async () => users.cleanup());

async function signedInPage(browser: Browser, label: SeededLabel): Promise<Page> {
  const context = await browser.newContext({ baseURL: loadStack().baseUrl });
  const page = await context.newPage();
  await loginAs(page, label);
  return page;
}

function randomSuffix(length = 6): string {
  const alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZ23456789';
  let out = '';
  for (let i = 0; i < length; i++) out += alphabet[Math.floor(Math.random() * alphabet.length)];
  return out;
}

async function billingSummary(request: APIRequestContext): Promise<any> {
  const res = await request.get(`${loadStack().baseUrl}/api/v1/billing/summary`);
  expect(res.status(), 'GET /api/v1/billing/summary').toBe(200);
  return res.json();
}

/** The seeded user's id, from the admin user list (users.json carries no ids). */
async function userIdOf(adminRequest: APIRequestContext, email: string): Promise<string> {
  const res = await adminRequest.get(`${loadStack().baseUrl}/api/v1/admin/users`);
  expect(res.status(), 'GET /api/v1/admin/users').toBe(200);
  const body = await res.json();
  const list: any[] = Array.isArray(body) ? body : body?.users ?? body?.items ?? [];
  const found = list.find((u) => String(u.email ?? '').toLowerCase() === email.toLowerCase());
  expect(found, `${email} in the admin user list`).toBeTruthy();
  return String(found.id ?? found.userId);
}

test.describe('promotions', () => {
  test('admin creates a 20% Pro discount code and the checkout carries it', async ({ browser }) => {
    const { baseUrl } = loadStack();
    const code = `E2EPRO20${randomSuffix()}`;
    const discountsBefore = new Set((await mock.discounts()).map((d) => d.id));
    const transactionsBefore = new Set((await mock.transactions()).map((t) => t.id));

    // --- Admin: create the plan discount on the Promotions page.
    const admin = await signedInPage(browser, 'admin');
    await admin.goto('/app/admin/promotions');
    // A fresh stack shows the "No promotions yet" empty state instead of the table.
    await expect(admin.getByTestId('admin-promotions-table').or(admin.getByText('No promotions yet'))).toBeVisible();
    await admin.getByTestId('create-promotion-button').click();

    const dialog = admin.getByRole('dialog', { name: 'New promotion' });
    await expect(dialog).toBeVisible();
    await dialog.getByText('Plan discount', { exact: true }).click();
    await dialog.getByRole('textbox', { name: /^Name/ }).fill(`E2E Pro 20% ${code}`);
    await dialog.getByRole('textbox', { name: /^Public headline/ }).fill('E2E: 20% off Pro');
    await dialog.getByRole('textbox', { name: /^Code/ }).fill(code);
    await dialog.getByText('Percentage', { exact: true }).click();
    await dialog.getByRole('textbox', { name: /^Percent off/ }).fill('20');
    await dialog.getByText('First payment', { exact: true }).click();
    await dialog.getByRole('combobox', { name: 'Plans' }).click({ force: true }); // the pills wrapper sits over the input
    await admin.getByRole('option', { name: 'Pro', exact: true }).click();
    await admin.keyboard.press('Escape');
    await expect(dialog).toBeVisible(); // Escape closed the dropdown, not the modal.
    await dialog.getByTestId('create-promotion-submit').click();
    await expect(dialog).toBeHidden();
    await expect(admin.getByTestId('admin-promotions-table')).toContainText(code);

    // The promotion as the API stores it, and the discount the mock received for it.
    const list = await admin.request.get(`${baseUrl}/api/v1/billing/admin/promotions`);
    expect(list.status()).toBe(200);
    const promotion = ((await list.json()).items as any[]).find((p) => p.code === code);
    expect(promotion, `promotion ${code}`).toBeTruthy();
    expect(promotion.kind).toMatch(/planDiscount/i);
    expect(promotion.discountType).toMatch(/percentage/i);
    expect(promotion.durationKind ?? promotion.duration).toMatch(/once/i);
    expect(promotion.providerDiscountId).toMatch(/^dsc_/);

    // Other specs (promo-plan-change, promo-supersede) create discounts in parallel against the same
    // mock, so look only at the ones made for this promotion (custom_data.promotion_id).
    const created = (await mock.discounts()).filter((d) => !discountsBefore.has(d.id) && d.custom_data?.promotion_id === promotion.id);
    expect(created.map((d) => d.id), 'POST /discounts received exactly one new discount').toEqual([promotion.providerDiscountId]);
    const discount = created[0];
    expect(discount.type).toBe('percentage');
    expect(Number(discount.amount)).toBe(20);
    expect(discount.recur).toBe(false);
    expect(discount.restrict_to).toEqual(['pri_e2e_pro']);
    await admin.context().close();

    // --- Trial owner: apply the code, see the preview, start Pro checkout.
    const t = await users.create('trial');
    const owner = await (await browser.newContext({ baseURL: baseUrl })).newPage();
    await users.login(owner, t);
    await owner.goto('/app/settings/billing');
    await expect(owner.getByRole('heading', { name: 'Plan and billing' })).toBeVisible();
    await owner.getByTestId('promo-code-input').fill(code.toLowerCase());
    await owner.getByTestId('promo-code-apply').click();
    await expect(owner.getByTestId('promo-code-card')).toContainText(code);

    const pro = owner.getByTestId('plan-pro');
    await expect(pro.getByTestId('promo-preview-total')).toContainText('$63.20');
    // The code covers Pro only.
    await expect(owner.getByTestId('plan-creator').getByTestId('promo-preview-total')).toHaveCount(0);

    await pro.getByRole('button', { name: 'Choose Pro' }).click();

    let txn: MockTransaction | undefined;
    await expect
      .poll(async () => {
        // Other specs run in parallel against the same mock, so pick this checkout's own transaction.
        const fresh = (await mock.transactions()).filter(
          (t) => !transactionsBefore.has(t.id) && t.request?.discount_id === discount.id,
        );
        txn = fresh[fresh.length - 1];
        return txn?.id ?? null;
      }, { message: 'the Pro checkout reached the Paddle mock (POST /transactions)', timeout: 20_000 })
      .not.toBeNull();

    expect(txn!.request, 'the body the Inference API sent').toBeTruthy();
    expect(txn!.request!.discount_id).toBe(discount.id);
    expect(txn!.request!.custom_data?.promotion_id).toBe(promotion.id);
    expect((txn!.request!.items as any[]).map((i) => i.price_id)).toEqual(['pri_e2e_pro']);
    expect(txn!.details?.totals.total).toBe('6320');
    await owner.context().close();
  });

  test('admin grants 500 promo credits and the balance shows them with an expiry', async ({ browser }) => {
    const label: SeededLabel = 'canceled';
    const target = user(label);

    const member = await signedInPage(browser, label);
    const before = await billingSummary(member.request);
    const balanceBefore = Number(before.credits.balance);

    // --- Admin: grant 500 credits from the user's admin page.
    const admin = await signedInPage(browser, 'admin');
    const userId = await userIdOf(admin.request, target.email);
    await admin.goto(`/app/admin/users/${userId}`);
    const card = admin.getByTestId('org-credits-card');
    const orgRow = card.getByTestId('org-credits-table').getByRole('row').filter({ hasText: target.orgName });
    await expect(orgRow).toHaveCount(1);
    await orgRow.getByTestId('grant-credits-button').click();

    const expiry = new Date(Date.now() + 90 * 24 * 3600 * 1000);
    const expiryInput = `${expiry.getFullYear()}-${String(expiry.getMonth() + 1).padStart(2, '0')}-${String(expiry.getDate()).padStart(2, '0')}`;
    const reason = `E2E goodwill grant ${randomSuffix()}`;
    await admin.getByTestId('grant-credits-amount').fill('500');
    await admin.getByTestId('grant-credits-expires').fill(expiryInput);
    await admin.getByTestId('grant-credits-reason').fill(reason);
    await admin.getByTestId('grant-credits-submit').click();
    await expect(admin.getByTestId('grant-credits-submit')).toBeHidden();
    await expect(card.getByTestId('org-credit-grants')).toContainText(reason);

    const grants = await admin.request.get(`${loadStack().baseUrl}/api/v1/billing/admin/orgs/${target.orgId}/credit-grants`);
    expect(grants.status()).toBe(200);
    const grantsBody = await grants.json();
    const grantList: any[] = Array.isArray(grantsBody) ? grantsBody : grantsBody.items ?? [];
    const grant = grantList.find((g) => g.reason === reason);
    expect(grant, 'the grant in the admin list').toBeTruthy();
    expect(grant.credits).toBe(500);
    const daysAhead = (new Date(grant.expiresAt).getTime() - Date.now()) / (24 * 3600 * 1000);
    expect(daysAhead).toBeGreaterThan(88);
    expect(daysAhead).toBeLessThan(92);
    await admin.context().close();

    // --- The user: balance up by 500 and a promo grant with an expiry on the billing page.
    const after = await billingSummary(member.request);
    expect(Number(after.credits.balance) - balanceBefore).toBe(500);
    const promo = (after.credits.grants as any[]).find((g) => String(g.source).toLowerCase() === 'promo');
    expect(promo, 'a promo grant in the summary').toBeTruthy();
    expect(promo.expiresAt).toBeTruthy();

    await member.goto('/app/settings/billing');
    const list = member.getByTestId('credit-grants-list');
    await expect(list).toBeVisible();
    const promoRow = list.locator('div').filter({ hasText: 'Promo credits' }).filter({ hasText: /expires / }).last();
    await expect(promoRow).toBeVisible();
    const shown = expiry.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric' });
    await expect(list).toContainText(`expires ${shown}`);
    await expect(member.getByText(`${(balanceBefore + 500).toLocaleString('en-US')} in the account`)).toBeVisible();
    await member.context().close();
  });
});
