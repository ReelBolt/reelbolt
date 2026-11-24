/**
 * Reproduction walk (saas-subscription-mgmt T-002, updated by T-005): every interactive element of
 * Plan & billing (`/app/settings/billing`) and Usage (`/app/settings/usage`), per seeded plan state.
 * Each finding of the initiative's repro.md (F-xx) is asserted as the CORRECT behaviour now that it is
 * fixed (F-01..F-07, F-09). A finding still open would be wrapped in `knownFinding()` (`test.fail()`),
 * so it flips red when fixed; none is left. F-08's link cannot render on this stack: links.spec.ts.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && REPRO_DIR=<dir for screenshots/logs> npx playwright test tests/billing/repro-walk.spec.ts --project=desktop
 *
 * Seeded users are only READ here, plus requests that leave no state behind in the database (portal
 * sessions, an unpaid checkout in the mock). Anything that pays, changes seats or creates a customer
 * runs on throwaway users created through the admin API and deleted at the end, so the seeded state
 * the other billing specs rely on is never touched.
 */
import { test, expect, request as pwRequest, type Page, type TestInfo, type APIRequestContext } from '@playwright/test';
import { randomBytes } from 'crypto';
import * as fs from 'fs';
import * as path from 'path';
import { loadStack, loginAs, mock, postToken, signIn, user, type SeededLabel } from '../../fixtures/stack';

/** POST /api/v1/auth/token (429-tolerant, see fixtures/stack.ts `postToken`). */
async function token(api: APIRequestContext, email: string, password: string): Promise<string> {
  const r = await postToken(api, email, password);
  expect(r.status(), `sign in as ${email}`).toBe(200);
  return (await r.json()).accessToken;
}

// One worker for this file in the normal run: it signs in a lot, and the stack's auth rate limit
// (burst 5) would otherwise also trip the other billing specs' sign-ins when they run alongside.
// A known finding that fails as expected does not break a serial chain. REPRO_STRICT runs in
// parallel so that every finding is reported instead of stopping at the first.
test.describe.configure({ mode: process.env.REPRO_STRICT ? 'parallel' : 'serial' });
test.use({ trace: 'retain-on-failure' });

// ------------------------------------------------------------------ evidence helpers

function reproDir(testInfo: TestInfo): string {
  const dir = process.env.REPRO_DIR || testInfo.outputPath('repro');
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

async function shot(page: Page, testInfo: TestInfo, name: string): Promise<string> {
  const file = path.join(reproDir(testInfo), `${testInfo.project.name}-${name}.png`);
  await page.screenshot({ path: file, fullPage: true });
  return file;
}

interface ApiEvent { method: string; path: string; status: number; request: string | null; body: string }

/** Every /api/v1 response the page sees, with its body (for the evidence log). */
function recordApi(page: Page): ApiEvent[] {
  const log: ApiEvent[] = [];
  page.on('response', async (r) => {
    const u = new URL(r.url());
    if (!u.pathname.startsWith('/api/v1/')) return;
    let body = '';
    try { body = (await r.text()).slice(0, 600); } catch { /* navigated away */ }
    log.push({ method: r.request().method(), path: u.pathname + u.search, status: r.status(), request: r.request().postData(), body });
  });
  return log;
}

function writeLog(testInfo: TestInfo, name: string, data: unknown): void {
  fs.writeFileSync(path.join(reproDir(testInfo), `${testInfo.project.name}-${name}.json`), `${JSON.stringify(data, null, 2)}\n`);
}

/** Inventory of the billing page's controls: label, visible, enabled. */
async function inventory(page: Page) {
  const out: { element: string; shown: boolean; enabled: boolean | null }[] = [];
  const buttons = page.locator('main button, main a');
  const n = await buttons.count();
  for (let i = 0; i < n; i++) {
    const b = buttons.nth(i);
    const text = (await b.innerText().catch(() => '')).trim();
    if (!text) continue;
    out.push({ element: text, shown: await b.isVisible(), enabled: await b.isEnabled().catch(() => null) });
  }
  const inputs = page.locator('main input');
  for (let i = 0; i < (await inputs.count()); i++) {
    const inp = inputs.nth(i);
    out.push({ element: `input[${await inp.getAttribute('aria-label')}]=${await inp.inputValue()}`, shown: await inp.isVisible(), enabled: await inp.isEnabled() });
  }
  return out;
}

async function summaryOf(page: Page): Promise<any> {
  const r = await page.request.get(`${loadStack().baseUrl}/api/v1/billing/summary`);
  expect(r.status()).toBe(200);
  return r.json();
}

// ------------------------------------------------------------------ throwaway users

interface Throwaway { id: string; email: string; password: string }
const throwaways: Throwaway[] = [];
let adminApi: APIRequestContext | undefined;
let adminToken = '';

async function admin(): Promise<APIRequestContext> {
  if (adminApi) return adminApi;
  adminApi = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
  const a = user('admin');
  adminToken = await token(adminApi, a.email, a.password);
  return adminApi;
}

/** A fresh verified Personal-org Owner on the Trial plan, created as the seed does (admin API). */
async function createThrowaway(tag: string): Promise<Throwaway> {
  const api = await admin();
  const email = `repro-${tag}-${Date.now()}@e2e.test`;
  const created = await api.post('/api/v1/admin/users', { headers: { Authorization: `Bearer ${adminToken}` }, data: { email, displayName: `Repro ${tag}` } });
  expect(created.status(), await created.text()).toBeLessThan(300);
  const { user: u, temporaryPassword } = await created.json();
  const password = `Repro-${randomBytes(9).toString('base64url')}-1a`;
  // The throwaway signs in through its OWN request context: nginx turns the session cookie a
  // sign-in sets into the Authorization header, so sharing the admin's context would make every
  // later admin call run as the throwaway (403 on /admin/users).
  const own = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
  const t1 = await token(own, email, temporaryPassword);
  const cp = await own.post('/api/v1/auth/change-password', { headers: { Authorization: `Bearer ${t1}` }, data: { currentPassword: temporaryPassword, newPassword: password } });
  expect(cp.status()).toBeLessThan(300);
  const t2 = await token(own, email, password);
  const legal = await (await own.get('/api/v1/legal/documents')).json();
  const at = await own.post('/api/v1/auth/accept-terms', {
    headers: { Authorization: `Bearer ${t2}` },
    data: { acceptTerms: true, termsVersion: legal.termsVersion, privacyVersion: legal.privacyVersion },
  });
  expect(at.status()).toBe(200);
  await own.dispose();
  const t: Throwaway = { id: u.id, email, password };
  throwaways.push(t);
  return t;
}

async function loginThrowaway(page: Page, t: Throwaway) {
  await signIn(page, t.email, t.password);
}

test.afterAll(async () => {
  if (!adminApi) return;
  for (const t of throwaways) {
    await adminApi.delete(`/api/v1/admin/users/${t.id}`, { headers: { Authorization: `Bearer ${adminToken}` } });
  }
  await adminApi.dispose();
});

/**
 * Marks the rest of the test as a known finding: expected to fail today, flips when fixed.
 * REPRO_STRICT=1 runs them as ordinary tests, to read the actual failure of each one.
 */
function knownFinding() {
  if (!process.env.REPRO_STRICT) test.fail();
}

function desktopOnly(testInfo: TestInfo) {
  test.skip(testInfo.project.name !== 'desktop', 'walk runs on the desktop project; mobile is layout-only');
}

const STATES: SeededLabel[] = ['trial', 'creator', 'team', 'canceled', 'member'];

// ------------------------------------------------------------------ 1. per-state inventory

test.describe('Plan & billing: what each state is shown', () => {
  for (const label of STATES) {
    test(`inventory: ${label}`, async ({ page }, testInfo) => {
      desktopOnly(testInfo);
      const api = recordApi(page);
      await loginAs(page, label);
      await page.goto('/app/settings/billing');
      await expect(page.getByTestId('current-plan-card')).toBeVisible();
      await page.waitForLoadState('networkidle');
      const inv = await inventory(page);
      await shot(page, testInfo, `billing-${label}`);
      writeLog(testInfo, `billing-${label}`, { inventory: inv, api, summary: await summaryOf(page) });

      const manage = page.getByTestId('manage-in-paddle');
      const choose = page.getByRole('button', { name: /^Choose / });
      const change = page.locator('[data-testid^="plan-"][data-testid$="-change"]');
      const topups = page.getByTestId('topup-card').getByRole('button', { name: /credits$/ });
      const seats = page.getByRole('button', { name: 'Update seats' });

      switch (label) {
        case 'trial':
          await expect(manage).toBeDisabled();
          await expect(choose).toHaveCount(3);
          await expect(change).toHaveCount(0);
          await expect(topups).toHaveCount(0);
          await expect(seats).toHaveCount(0);
          break;
        case 'creator':
          await expect(manage).toBeEnabled();
          await expect(choose).toHaveCount(0);
          await expect(change).toHaveText(['Upgrade to Pro', 'Upgrade to Team']);
          await expect(topups).toHaveCount(3);
          await expect(seats).toHaveCount(0);
          break;
        case 'team':
          await expect(manage).toBeEnabled();
          await expect(change).toHaveText(['Switch to Creator', 'Switch to Pro']);
          await expect(topups).toHaveCount(3);
          await expect(seats).toBeDisabled(); // enabled only once the count differs
          await page.getByLabel('Seats on the current plan').fill('4');
          await expect(seats).toBeEnabled();
          await page.getByLabel('Seats on the current plan').fill('2');
          await expect(seats).toBeDisabled(); // below the Team minimum (3)
          break;
        case 'canceled':
          // F-01 fixed: Trial again, so a plan is chosen through a checkout; invoices stay reachable.
          await expect(manage).toBeEnabled();
          await expect(choose).toHaveCount(3);
          await expect(change).toHaveCount(0);
          await expect(page.getByTestId('topup-card')).toContainText('Choose one of the plans above first');
          break;
        case 'member':
          await expect(manage).toHaveCount(0);
          await expect(choose).toHaveCount(0);
          await expect(change).toHaveCount(0);
          await expect(topups).toHaveCount(0);
          await expect(page.getByText('You can see this page, but not change it')).toBeVisible();
          break;
      }
    });
  }

  test('F-01 canceled → Trial org is offered a way to subscribe again', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    // F-01 (fixed): "subscribed" is a live paid plan, not "has a billing customer".
    await loginAs(page, 'canceled');
    await page.goto('/app/settings/billing');
    await expect(page.getByTestId('plan-creator')).toBeVisible();
    await expect(page.getByTestId('plan-creator').getByRole('button', { name: 'Choose Creator' })).toBeVisible({ timeout: 3000 });
  });

  test('F-01 evidence: the API itself accepts a new checkout for the canceled org', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    await loginAs(page, 'canceled');
    const s = await summaryOf(page);
    expect(s.subscription.planKey).toBe('trial');
    expect(s.subscription.hasBillingCustomer).toBe(true);
    // An unpaid draft transaction in the mock; nothing in the database changes until it is paid.
    const r = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: { plan: 'creator' } });
    const body = await r.json();
    writeLog(testInfo, 'canceled-api-checkout', { status: r.status(), body });
    expect(r.status()).toBe(200);
    expect(body.checkoutUrl).toContain('/checkout/');
  });
});

// ------------------------------------------------------------------ 2. portal

test.describe('Plan & billing: billing portal', () => {
  for (const label of ['creator', 'team', 'canceled'] as const) {
    test(`Manage in Paddle: ${label}`, async ({ page }, testInfo) => {
      desktopOnly(testInfo);
      await loginAs(page, label);
      await page.goto('/app/settings/billing');
      const before = (await mock.calls({ path: `/customers/${user(label).customerId}/portal-sessions` })).length;
      // Pass-through route: the page navigates away as soon as it has the URL, so the body is read here.
      let status = 0;
      let body: any = null;
      await page.route('**/api/v1/billing/portal', async (route) => {
        const res = await route.fetch();
        status = res.status();
        body = await res.json();
        await route.fulfill({ response: res });
      });
      await page.getByTestId('manage-in-paddle').click();
      await page.waitForURL(/\/portal\/[^/]+\/overview$/);
      await expect(page.getByTestId('mock-portal-heading')).toBeVisible();
      const calls = (await mock.calls({ path: `/customers/${user(label).customerId}/portal-sessions` })).slice(before);
      await shot(page, testInfo, `portal-${label}`);
      writeLog(testInfo, `portal-${label}`, { status, body, landed: page.url(), mockCalls: calls, portal: await page.locator('body').innerText() });
      expect(status).toBe(200);
      expect(Object.keys(body).sort()).toEqual(['cancelUrl', 'updatePaymentMethodUrl', 'url']);
      if (label === 'canceled') {
        expect(body.cancelUrl).toBeNull(); // no live subscription to link to
        expect(body.updatePaymentMethodUrl).toBeNull();
      } else {
        expect(body.cancelUrl).toContain(`/subscriptions/${user(label).subscriptionId}/cancel`);
        expect(body.updatePaymentMethodUrl).toContain(`/subscriptions/${user(label).subscriptionId}/update-payment-method`);
      }
      expect(calls.length).toBeGreaterThanOrEqual(1); // other creator tests may open sessions in parallel
      for (const c of calls) {
        if (label === 'canceled') expect((c.body as any).subscription_ids).toBeUndefined();
        else expect((c.body as any).subscription_ids).toEqual([user(label).subscriptionId]);
      }
      await page.getByTestId('mock-portal-back').click();
      await expect(page).toHaveURL(/\/app\/settings\/billing$/);
    });
  }

  test('F-02 the plan can be changed from Plan & billing (Upgrade to Pro, from Creator)', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    // F-02 (fixed): a change-plan endpoint and "Upgrade to / Switch to {Plan}" with a confirm dialog
    // replace "Change in the billing portal". Seeded user: the dialog is opened and dismissed only
    // (the full change runs on throwaway users in lifecycle.spec.ts / downgrade.spec.ts).
    await loginAs(page, 'creator');
    await page.goto('/app/settings/billing');
    await expect(page.getByText('Change in the billing portal')).toHaveCount(0);
    await expect(page.getByTestId('plans-card')).not.toContainText('happen in the billing portal');
    await page.getByTestId('plan-pro-change').click();
    const dialog = page.getByRole('dialog', { name: 'Upgrade to Pro?' });
    await expect(dialog).toContainText('The new plan starts now.');
    await shot(page, testInfo, 'upgrade-dialog-creator');
    await dialog.getByRole('button', { name: 'Go back' }).click();
    await expect(dialog).toBeHidden();
    expect((await summaryOf(page)).subscription.planKey).toBe('creator');
  });

  test('F-03 the portal hands over the subscription links Paddle returned (cancel, payment method)', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    // F-03 (fixed): the portal response carries the subscription's cancel / payment-method links.
    await loginAs(page, 'creator');
    const r = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/portal`);
    const body = await r.json();
    const sess = (await mock.state()).portalSessions.filter((x) => x.customer_id === user('creator').customerId).at(-1)!;
    writeLog(testInfo, 'portal-api-creator', { status: r.status(), body, paddleSession: sess.urls });
    expect(sess.urls.subscriptions.length).toBe(1); // Paddle did return them
    expect(body.cancelUrl).toContain('/cancel');
    expect(body.updatePaymentMethodUrl).toContain('/update-payment-method');
  });
});

// ------------------------------------------------------------------ 3. checkout on throwaway users

test.describe('Plan & billing: checkout, payment and seats (throwaway users)', () => {
  test('trial: Choose Creator → mock checkout → Pay now → /app/checkout → billing shows Creator; checkout page buttons', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    const t = await createThrowaway('creator');
    const api = recordApi(page);
    await loginThrowaway(page, t);
    await page.goto('/app/settings/billing');
    await shot(page, testInfo, 'throwaway-trial-before');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
    await page.getByTestId('plan-creator').getByRole('button', { name: 'Choose Creator' }).click();
    const r = await resp;
    expect(r.status()).toBe(200);
    expect(JSON.parse(r.request().postData()!)).toEqual({ plan: 'creator' });
    await page.waitForURL(/\/checkout\/txn_/);
    await shot(page, testInfo, 'mock-checkout-creator');
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?_ptxn=txn_/);
    await expect(page.getByTestId('checkout-pending-notice')).toBeVisible();
    await shot(page, testInfo, 'checkout-return');

    await page.getByRole('button', { name: 'Go to the dashboard' }).or(page.getByRole('link', { name: 'Go to the dashboard' })).click();
    await expect(page).toHaveURL(/\/app\/dashboard$/);
    await page.goBack();
    await page.getByRole('link', { name: 'Check your plan and balance' }).click();
    await expect(page).toHaveURL(/\/app\/settings\/billing$/);

    await expect.poll(async () => (await summaryOf(page)).subscription.planKey, { timeout: 15_000 }).toBe('creator');
    await page.reload();
    await expect(page.getByTestId('current-plan-card')).toContainText('Creator');
    await expect(page.getByTestId('manage-in-paddle')).toBeEnabled();
    await shot(page, testInfo, 'throwaway-creator-after');
    const s = await summaryOf(page);

    // Top-up through the browser: 500 credits, Pay now, balance moves once the webhook lands.
    const before = s.credits.balance;
    const tr = page.waitForResponse((x) => x.url().endsWith('/api/v1/billing/checkout'));
    await page.getByTestId('topup-card').getByRole('button', { name: '500 credits' }).click();
    const top = await tr;
    expect(top.status()).toBe(200);
    expect(JSON.parse(top.request().postData()!)).toEqual({ topUpCredits: 500 });
    await page.waitForURL(/\/checkout\/txn_/);
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?_ptxn=txn_/);
    await expect.poll(async () => (await summaryOf(page)).credits.balance, { timeout: 15_000 }).toBe(before + 500);
    writeLog(testInfo, 'throwaway-creator-flow', { api, summaryAfterPlan: s, summaryAfterTopUp: await summaryOf(page) });
  });

  test('trial: Choose Team with 4 seats → paid → Update seats 4→5 applies after the webhook', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    const t = await createThrowaway('team');
    const api = recordApi(page);
    await loginThrowaway(page, t);
    await page.goto('/app/settings/billing');
    await page.getByLabel('Seats on the Team plan').fill('4');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
    await page.getByTestId('plan-team').getByRole('button', { name: 'Choose Team' }).click();
    const r = await resp;
    expect(r.status()).toBe(200);
    expect(JSON.parse(r.request().postData()!)).toEqual({ plan: 'team', seats: 4 });
    await page.waitForURL(/\/checkout\/txn_/);
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?_ptxn=txn_/);
    await expect.poll(async () => { const s = (await summaryOf(page)).subscription; return `${s.planKey}/${s.seats}`; }, { timeout: 15_000 }).toBe('team/4');

    await page.goto('/app/settings/billing');
    await shot(page, testInfo, 'throwaway-team-4');
    await page.getByLabel('Seats on the current plan').fill('5');
    const sr = page.waitForResponse((x) => x.url().endsWith('/api/v1/billing/seats'));
    await page.getByRole('button', { name: 'Update seats' }).click();
    const seats = await sr;
    expect(seats.status()).toBe(202);
    await expect(page.getByTestId('billing-done-notice')).toContainText('You have 5 seats now. Extra credits for the rest of this billing period are added as soon as Paddle confirms the payment.', { timeout: 25_000 });
    await expect.poll(async () => (await summaryOf(page)).subscription.seats, { timeout: 15_000 }).toBe(5);
    await page.reload();
    await expect(page.getByTestId('current-plan-card')).toContainText('5');
    await shot(page, testInfo, 'throwaway-team-5');
    const patch = (await mock.calls({ path: '/subscriptions/', method: 'PATCH' })).at(-1);
    writeLog(testInfo, 'throwaway-team-flow', { api, mockPatch: patch });
    expect((patch!.body as any).items[0].quantity).toBe(5);
  });

  test('F-04 webhook gap: a paid org whose webhook has not landed is refused a second subscription', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    test.setTimeout(60_000);
    const t = await createThrowaway('gap');
    await loginThrowaway(page, t);
    const co = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: { plan: 'creator' } });
    expect(co.status()).toBe(200);
    const { transactionId } = await co.json();
    // Paid, but the webhooks are held back for 20 s (the mock's delayMs).
    await mock.completeTransaction(transactionId, 20_000);
    await page.goto(`/app/checkout?_ptxn=${transactionId}`);
    await page.getByRole('link', { name: 'Check your plan and balance' }).click();
    await expect(page).toHaveURL(/\/app\/settings\/billing$/);
    await expect(page.getByTestId('current-plan-card')).toContainText('Trial');
    // The checkout stored the Paddle customer, so invoices are reachable before the webhook.
    const manageEnabled = await page.getByTestId('manage-in-paddle').isEnabled();
    await shot(page, testInfo, 'webhook-gap-billing');
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
    await page.getByTestId('plan-pro').getByRole('button', { name: 'Choose Pro' }).click();
    const second = await resp;
    const secondBody = await second.json().catch(() => null);
    writeLog(testInfo, 'webhook-gap', { transactionId, manageEnabled, secondCheckout: { status: second.status(), body: secondBody } });
    expect(manageEnabled).toBe(true);
    // F-04 (fixed): BillingController asks Paddle for the org's live subscriptions → 409.
    expect(second.status(), 'a second subscription checkout for a paid org is refused').toBe(409);
    expect(secondBody.error).toBe('subscription_pending');
    await expect(page.getByText(secondBody.message)).toBeVisible();
    // Let the held webhooks land before the throwaway is deleted.
    await expect.poll(async () => (await summaryOf(page)).subscription.planKey, { timeout: 30_000 }).toBe('creator');
  });

  test('F-05 abandoned checkout, then retry: the retry is still linked to the customer created the first time', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    const t = await createThrowaway('retry');
    await loginThrowaway(page, t);
    await page.goto('/app/settings/billing');
    const custBefore = (await mock.calls({ path: '/customers', method: 'POST' })).length;
    for (let i = 0; i < 2; i++) {
      const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/checkout'));
      await page.getByTestId('plan-creator').getByRole('button', { name: 'Choose Creator' }).click();
      expect((await resp).status()).toBe(200);
      await page.waitForURL(/\/checkout\/txn_/);
      await page.goBack(); // abandon
      await expect(page.getByTestId('plan-creator')).toBeVisible();
    }
    // The customer is stored at checkout: invoices/portal are reachable, and a plan can still be chosen.
    await expect(page.getByTestId('manage-in-paddle')).toBeEnabled();
    await expect(page.getByTestId('plan-creator').getByRole('button', { name: 'Choose Creator' })).toBeEnabled();
    const custCalls = (await mock.calls({ path: '/customers', method: 'POST' })).slice(custBefore).filter((c) => (c.body as any)?.email === t.email);
    const txCalls = (await mock.calls({ path: '/transactions', method: 'POST' })).filter((c) => (c.body as any)?.custom_data?.user_id === t.id);
    writeLog(testInfo, 'abandoned-retry', { customers: custCalls, transactions: txCalls.map((c) => ({ status: c.status, customer_id: (c.body as any).customer_id ?? null })) });
    expect(custCalls.filter((c) => c.status === 201)).toHaveLength(1); // one Paddle customer, ever
    expect(txCalls).toHaveLength(2);
    expect((txCalls[0].body as any).customer_id).toBeTruthy();
    // F-05 (fixed): the retry carries the same customer.
    expect((txCalls[1].body as any).customer_id, 'retry carries the same customer').toBe((txCalls[0].body as any).customer_id);
  });
});

// ------------------------------------------------------------------ 4. plan-card seat input on subscribed orgs

test('F-06 Team plan card seat input only where its value is used', async ({ page }, testInfo) => {
  desktopOnly(testInfo);
  // F-06 (fixed): the input is shown for a checkout (Trial/canceled) or a change to Team (another
  // live plan); never on the current plan's card, nor for a read-only member.
  const teamSeats = () => page.getByTestId('plan-team').getByLabel('Seats on the Team plan');
  for (const [label, shown] of [['team', false], ['member', false], ['canceled', true], ['creator', true]] as const) {
    await loginAs(page, label);
    await page.goto('/app/settings/billing');
    await expect(page.getByTestId('plan-team')).toBeVisible();
    await expect(teamSeats(), `${label}: Team card seat input`).toHaveCount(shown ? 1 : 0);
  }
  // Where it is shown, its value is what the button uses (creator: 3 → Upgrade; checked, not clicked).
  await expect(page.getByTestId('plan-team-change')).toHaveText('Upgrade to Team');
});

test('F-09 the merchant of record is named as the brand, not the provider key', async ({ page }, testInfo) => {
  desktopOnly(testInfo);
  await loginAs(page, 'creator');
  await page.goto('/app/settings/billing');
  await expect(page.getByTestId('current-plan-card')).toBeVisible();
  // F-09 (fixed): the provider key "paddle" is shown as the merchant's name, "Paddle".
  await expect(page.getByText(/billed monthly through Paddle,/)).toBeVisible();
  await expect(page.getByText(/billed monthly through paddle,/)).toHaveCount(0);
});

// ------------------------------------------------------------------ 5. usage page

test.describe('Usage page', () => {
  for (const label of STATES) {
    test(`Period and Group by: ${label}`, async ({ page }, testInfo) => {
      desktopOnly(testInfo);
      await loginAs(page, label);
      const seen: { url: string; status: number }[] = [];
      page.on('response', (r) => { if (r.url().includes('/api/v1/billing/usage')) seen.push({ url: new URL(r.url()).search, status: r.status() }); });
      await page.goto('/app/settings/usage');
      await expect(page.getByTestId('credits-card')).toBeVisible();
      const period = page.getByRole('radiogroup', { name: 'Period' });
      const groupBy = page.getByRole('radiogroup', { name: 'Group by' });
      // Each new (period, axis) pair is a new SWR key and must fetch; a pair already fetched may be
      // served from the cache, so the last two clicks (back to 30 days / By day) are checked on screen.
      for (const [p, days] of [['7 days', 7], ['90 days', 90]] as const) {
        const w = page.waitForResponse((r) => r.url().includes('/api/v1/billing/usage'));
        await period.getByText(p, { exact: true }).click();
        expect((await w).status()).toBe(200);
        await expect(page.getByTestId('totals-card')).toContainText(`last ${days} days`);
      }
      for (const [g, key] of [['By project', 'project'], ['By model', 'model'], ['By step kind', 'kind'], ['Who pays', 'mode']] as const) {
        const w = page.waitForResponse((r) => r.url().includes('/api/v1/billing/usage') && r.url().includes(`groupBy=${key}`));
        await groupBy.getByText(g, { exact: true }).click();
        expect((await w).status()).toBe(200);
      }
      await groupBy.getByText('By day', { exact: true }).click();
      await period.getByText('30 days', { exact: true }).click();
      await expect(page.getByTestId('totals-card')).toContainText('last 30 days');
      await expect(groupBy.locator('input[value="day"]')).toBeChecked();
      await shot(page, testInfo, `usage-${label}`);
      writeLog(testInfo, `usage-${label}`, { requests: seen, text: await page.locator('main').innerText() });
      expect(seen.every((s) => s.status === 200)).toBe(true);
    });
  }

  test('breadcrumb Dashboard on both pages', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    await loginAs(page, 'creator');
    for (const p of ['/app/settings/billing', '/app/settings/usage']) {
      await page.goto(p);
      await page.getByRole('link', { name: 'Dashboard', exact: true }).first().click();
      await expect(page).toHaveURL(/\/app\/dashboard$/);
    }
  });
});

// ------------------------------------------------------------------ 6. links into these pages

test.describe('Links into Plan & billing from elsewhere', () => {
  test('F-07 "See plans and credits" (BYO provider refused by plan) opens the page with the plans', async ({ page }, testInfo) => {
    desktopOnly(testInfo);
    await loginAs(page, 'trial');
    await page.goto('/app/org/inference-providers');
    await page.getByRole('button', { name: 'New provider' }).click();
    const dialog = page.getByRole('dialog');
    await dialog.getByRole('textbox', { name: 'Name', exact: true }).fill('Repro BYO');
    await dialog.getByRole('textbox', { name: 'Endpoint' }).fill('https://my-resource.openai.azure.com');
    await dialog.getByRole('combobox', { name: 'Deployment name' }).fill('gpt-4o-mini');
    await dialog.getByRole('textbox', { name: 'API Key' }).fill('sk-repro-not-a-key');
    const resp = page.waitForResponse((r) => r.url().includes('/api/v1/org/inference-providers') && r.request().method() === 'POST');
    await dialog.getByRole('button', { name: 'Create', exact: true }).click();
    const r = await resp;
    writeLog(testInfo, 'byo-plan-limited', { status: r.status(), body: await r.text() });
    expect(r.status()).toBe(402);
    const link = dialog.getByRole('link', { name: 'See plans and credits' });
    await expect(link).toBeVisible();
    await shot(page, testInfo, 'byo-plan-limited');
    await link.click();
    await page.waitForLoadState('networkidle');
    await shot(page, testInfo, 'byo-link-landing');
    writeLog(testInfo, 'byo-link-landing', { landed: page.url() });
    // F-07 (fixed): InferenceProviderForm's PLANS_AND_CREDITS_HREF is Plan & billing.
    await expect(page).toHaveURL(/\/app\/settings\/billing$/);
    await expect(page.getByTestId('plans-card')).toBeVisible();
  });

  test('F-08 assistant attach "See plans and storage" link resolves inside the app', async ({}, testInfo) => {
    desktopOnly(testInfo);
    // F-08 is fixed in the source (AssistantAttachControl.tsx: next/link, so basePath '/app' applies),
    // but the link only renders inside a project's assistant after a storage refusal, and this stack
    // cannot create a project (POST /api/v1/projects → 500, `workflow_definitions` missing without the
    // e2e-engine profile). The raw path it used to point at (/settings/billing at the origin root) is
    // still not a page, which is expected. links.spec.ts checks the class of bug on the pages that do
    // render: no in-app anchor leaves /app.
    test.skip(true, 'not renderable on this stack (no project without e2e-engine); see links.spec.ts');
  });
});

// ------------------------------------------------------------------ 7. mobile layout (once)

test.describe('mobile layout', () => {
  for (const label of ['trial', 'team'] as const) {
    test(`billing and usage fit a phone: ${label}`, async ({ page }, testInfo) => {
      test.skip(testInfo.project.name !== 'mobile', 'mobile project only');
      await loginAs(page, label);
      for (const [p, name] of [['/app/settings/billing', 'billing'], ['/app/settings/usage', 'usage']] as const) {
        await page.goto(p);
        await page.waitForLoadState('networkidle');
        await shot(page, testInfo, `${name}-${label}`);
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
        writeLog(testInfo, `${name}-${label}-layout`, { overflowPx: overflow });
        expect(overflow, `${p} has no horizontal scroll`).toBeLessThanOrEqual(0);
      }
    });
  }
});
