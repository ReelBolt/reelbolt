/**
 * Supersede-cancel through the Paddle mock (Security N-18). A buyer who opens a second discounted
 * checkout with the same code before paying the first supersedes it: the Inference API sends
 * PATCH /transactions/{first} {"status":"canceled"}, the first transaction can no longer be paid, and
 * the second one completes with the discount. Plus the mock's own contract for that route: a
 * completed transaction answers 400 transaction_immutable, an unknown one 404 not_found, any other
 * body 400 invalid_field. Throwaway Owner only.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing/promo-supersede.spec.ts --project=desktop
 */
import { expect, test, type APIRequestContext } from '@playwright/test';
import { expectSummary, loadStack, loginAs, mock, Throwaways } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('promosup');
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

/** The Paddle API of the mock, as the Inference API calls it (docker-compose.e2e.yml's key). */
async function paddlePatchTransaction(id: string, body: unknown): Promise<{ status: number; json: any }> {
  const res = await fetch(`${loadStack().mockUrl}/transactions/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'content-type': 'application/json', authorization: 'Bearer e2e-mock-key', 'paddle-version': '1' },
    body: JSON.stringify(body),
  });
  return { status: res.status, json: await res.json() };
}

async function checkout(request: APIRequestContext, code: string): Promise<string> {
  const r = await request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: { plan: 'pro', promoCode: code } });
  expect(r.status(), `checkout: ${await r.text()}`).toBe(200);
  const body = await r.json();
  expect(body.discount, 'the checkout carries the discount').toBeTruthy();
  return body.transactionId as string;
}

test('a second discounted checkout cancels the first at Paddle; the second pays with the discount', async ({ browser, page }) => {
  test.setTimeout(120_000);
  const { baseUrl, mockUrl } = loadStack();
  const code = `E2ESUP10${randomSuffix()}`;

  // --- Admin: a 10% first-payment discount on Pro (the API the Promotions page uses).
  const adminCtx = await browser.newContext({ baseURL: baseUrl });
  const admin = await adminCtx.newPage();
  await loginAs(admin, 'admin');
  const created = await admin.request.post(`${baseUrl}/api/v1/billing/admin/promotions`, {
    data: {
      kind: 'planDiscount', name: `E2E supersede ${code}`, publicHeadline: 'E2E: 10% off Pro', code,
      discountType: 'percentage', discountAmount: '10', duration: 'once', planKeys: ['pro'],
    },
  });
  expect(created.status(), await created.text()).toBeLessThan(300);
  const promotion = await created.json();
  const discountId: string = promotion.providerDiscountId;
  expect(discountId).toMatch(/^dsc_/);
  await adminCtx.close();

  const t = await users.create('owner');
  await users.login(page, t);

  const first = await checkout(page.request, code);
  const second = await checkout(page.request, code);
  expect(second).not.toBe(first);

  await test.step('the mock received PATCH /transactions/{first} {"status":"canceled"} and canceled it', async () => {
    await expect
      .poll(async () => (await mock.calls({ path: `/transactions/${first}`, method: 'PATCH' })).map((c) => ({ body: c.body, status: c.status })),
        { message: `PATCH /transactions/${first}`, timeout: 20_000 })
      .toEqual([{ body: { status: 'canceled' }, status: 200 }]);
    expect(await mock.calls({ path: `/transactions/${second}`, method: 'PATCH' })).toEqual([]);

    const txns = await mock.transactions();
    const a = txns.find((x) => x.id === first)!;
    const b = txns.find((x) => x.id === second)!;
    expect(a.status).toBe('canceled');
    expect(a.request?.discount_id).toBe(discountId);
    expect(['draft', 'ready']).toContain(b.status);
    expect(b.request?.discount_id).toBe(discountId);
  });

  await test.step('the canceled checkout cannot be paid', async () => {
    const checkoutPage = await (await browser.newContext()).newPage();
    await checkoutPage.goto(`${mockUrl}/checkout/${first}`);
    await expect(checkoutPage.getByTestId('mock-checkout-status')).toHaveText('canceled');
    await expect(checkoutPage.getByTestId('mock-checkout-canceled')).toBeVisible();
    await checkoutPage.context().close();
    await expect(mock.completeTransaction(first)).rejects.toThrow(/400.*transaction_immutable/);
  });

  await test.step('the second checkout pays with the discount', async () => {
    const done = await mock.completeTransaction(second);
    expect(done.transaction).toMatchObject({ id: second, status: 'completed', discount_id: discountId });
    expect(Number(done.transaction.details.totals.discount)).toBeGreaterThan(0);
    await expectSummary(page, (s) => `${s.subscription?.planKey}/${s.subscription?.status}`, 'pro/Active');
  });

  await test.step('mock contract: completed → 400 transaction_immutable; unknown → 404; other body → 400 invalid_field', async () => {
    const immutable = await paddlePatchTransaction(second, { status: 'canceled' });
    expect(immutable.status).toBe(400);
    expect(immutable.json.error).toMatchObject({ type: 'request_error', code: 'transaction_immutable' });
    expect(immutable.json.error.detail).toBeTruthy();
    expect((await mock.transactions()).find((x) => x.id === second)!.status).toBe('completed');

    const unknown = await paddlePatchTransaction('txn_does_not_exist', { status: 'canceled' });
    expect(unknown.status).toBe(404);
    expect(unknown.json.error.code).toBe('not_found');

    const other = await paddlePatchTransaction(second, { status: 'billed' });
    expect(other.status).toBe(400);
    expect(other.json.error.code).toBe('invalid_field');
  });
});
