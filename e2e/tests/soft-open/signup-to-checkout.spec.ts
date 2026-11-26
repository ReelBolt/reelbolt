/**
 * Soft-open rehearsal: the "signup switch" end to end on an ISOLATED stack (initiative launch-soft-open).
 *
 * The thing the operator flips in production is `SIGNUP_ENABLED=true` on go-api AND `NEXT_PUBLIC_SIGNUP_ENABLED=true`
 * at SITE BUILD time. This spec proves, in order:
 *   phase off  0. negative control: switches off -> POST /api/v1/auth/signup is 404 and the site CTA is "Talk to us"
 *   phase on   0. switches on -> CTA "Start free" -> /app/signup
 *              1. sign up through the real /app/signup page (Turnstile is OFF in the stack)
 *              2. verify the email from the link in the go-api console log; resend-verification; the per-IP cap
 *              3. the Terms gate: a stale acceptance cannot reach the app until /app/accept-terms is accepted
 *              4. the free first video: grant, balance, a run starts (queues) and costs nothing
 *              5. upgrade through the Stripe fake: hosted checkout -> /app/checkout?session_id=... -> plan + credits,
 *                 no double grant on a reload or a webhook redelivery
 *
 * No paid provider is involved: the stack has no LLM/video/voice keys and the Stripe side is the local fake.
 * Run it with `e2e/scripts/soft-open-switches.sh` for the off/on flip; see e2e/README.md "Soft-open rehearsal".
 *
 *   source e2e/.stack/<project>/env.sh
 *   SOFT_OPEN_PHASE=off npx playwright test -c playwright.soft-open.config.ts   # after `soft-open-switches.sh off`
 *   SOFT_OPEN_PHASE=on  npx playwright test -c playwright.soft-open.config.ts   # after `soft-open-switches.sh on`
 */
import { test, expect, request as pwRequest, type Page } from '@playwright/test';
import { randomBytes } from 'crypto';
import * as fs from 'fs';
import {
  billingSummary, captureCheckout, composeStack, expectSummary, isStripeStack, loadStack, planIncludedCredits,
  planPriceMinor, postToken, psql, signIn, sqlStr, stripeMock, user,
} from '../../fixtures/stack';

const PHASE = process.env.SOFT_OPEN_PHASE || 'on';
const ARTIFACTS = process.env.SOFT_OPEN_ARTIFACTS || '/tmp/launch-soft-open-rehearsal';
const SHOTS = `${ARTIFACTS}/shots`;
/** The marketing site built with NEXT_PUBLIC_SIGNUP_ENABLED=false (soft-open-switches.sh off). */
const SITE_OFF_URL = process.env.SITE_OFF_URL || 'http://127.0.0.1:8131';
/**
 * Email domain of the main account. The Inference API refuses the free first video to an address whose domain has no
 * mail server (a live DNS lookup, FirstVideoAbuse.cs), and `.test` has none, so the account that is meant to GET the
 * grant needs a domain that resolves. Nothing is sent to it: the stack's mailer is the console logger.
 */
const MX_DOMAIN = process.env.SOFT_OPEN_MX_DOMAIN || 'gmail.com';
const PASSWORD = 'Soft-Open-Passw0rd-12';

fs.mkdirSync(SHOTS, { recursive: true });

const consoleErrors: string[] = [];
async function watch(page: Page, who: string) {
  // The rehearsal must not reach anything outside the stack (the site's hero streams a video from the production CDN,
  // which also keeps `networkidle` from ever settling): every non-local request is aborted.
  await page.route((url) => !['localhost', '127.0.0.1'].includes(url.hostname), (route) => route.abort());
  page.on('console', (m) => { if (m.type() === 'error') consoleErrors.push(`[${who}] ${m.text()}`); });
  page.on('pageerror', (e) => consoleErrors.push(`[${who}] pageerror ${e.message}`));
}
test.afterAll(() => {
  if (consoleErrors.length) console.log(`soft-open console errors (${consoleErrors.length}):\n${consoleErrors.slice(0, 20).join('\n')}`);
});

const unique = (tag: string) => `${tag}-${Date.now()}-${randomBytes(2).toString('hex')}`;

/** Every verify-email link the go-api console mailer has logged so far (it logs, it does not send). */
function verifyLinks(): string[] {
  const out = composeStack(['logs', '--no-color', '--no-log-prefix', '--tail', '400', 'go-api']);
  return [...out.matchAll(/verify_email email to rcpt=\S+ NOT SENT; link \(development only\): (\S+)/g)].map((m) => m[1]);
}

/** Runs `action`, then waits for exactly the verify-email links it caused (new tokens in the log). */
async function newVerifyLinks(action: () => Promise<void>, expectCount = 1): Promise<string[]> {
  const before = new Set(verifyLinks());
  await action();
  let fresh: string[] = [];
  await expect.poll(() => { fresh = verifyLinks().filter((l) => !before.has(l)); return fresh.length; },
    { message: 'verification mail logged by go-api', timeout: 20_000, intervals: [500] }).toBe(expectCount);
  return fresh;
}

/** The per-IP signup cap is counted from application_users.signup_ip; every spec starts from an empty count. */
function resetSignupCap() { psql('UPDATE application_users SET signup_ip = NULL'); }

async function legalVersions(): Promise<{ termsVersion: string; privacyVersion: string }> {
  const api = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
  try { return await (await api.get('/api/v1/legal/documents')).json(); } finally { await api.dispose(); }
}

async function apiSignup(email: string, over: Record<string, unknown> = {}) {
  const api = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
  try {
    const v = await legalVersions();
    const res = await api.post('/api/v1/auth/signup', {
      data: { email, password: PASSWORD, displayName: 'Soft Open', acceptTerms: true, termsVersion: v.termsVersion, privacyVersion: v.privacyVersion, ...over },
    });
    return { status: res.status(), text: await res.text() };
  } finally { await api.dispose(); }
}

async function cleanupAccounts() {
  const ids = psql(`SELECT id FROM application_users WHERE email LIKE 'soft-open-%'`).split('\n').filter(Boolean);
  if (!ids.length) return;
  const api = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
  try {
    const a = user('admin');
    const r = await postToken(api, a.email, a.password);
    const headers = { Authorization: `Bearer ${(await r.json()).accessToken}` };
    for (const id of ids) {
      const d = await api.delete(`/api/v1/admin/users/${id}`, { headers });
      if (d.status() >= 300 && d.status() !== 404) console.warn(`could not delete ${id}: ${d.status()}`);
    }
  } finally { await api.dispose(); }
}

test.use({ trace: 'retain-on-failure' });
test.beforeAll(() => { test.skip(!isStripeStack(), 'needs a stack started with up-isolated.sh --provider stripe'); });

// =============================================================================================== phase off
test.describe('negative control: switches OFF', () => {
  test.skip(PHASE !== 'off', 'SOFT_OPEN_PHASE=off (after soft-open-switches.sh off)');

  test('POST /api/v1/auth/signup and resend-verification answer 404', async () => {
    const r = await apiSignup(`soft-open-off-${unique('a')}@${MX_DOMAIN}`);
    expect(r.status, r.text).toBe(404);
    const api = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
    const resend = await api.post('/api/v1/auth/resend-verification', { data: { email: 'nobody@e2e.test' } });
    expect(resend.status()).toBe(404);
    await api.dispose();
    expect(psql(`SELECT count(*) FROM application_users WHERE email LIKE 'soft-open-off-%'`)).toBe('0');
  });

  test('the site built with NEXT_PUBLIC_SIGNUP_ENABLED=false says "Talk to us" -> /contact and offers no signup', async ({ page }) => {
    await watch(page, 'site-off');
    await page.goto(`${SITE_OFF_URL}/`);
    await page.waitForLoadState('networkidle');
    await page.screenshot({ path: `${SHOTS}/off-site-home.png`, fullPage: false });
    const talk = page.getByRole('link', { name: 'Talk to us' }).first();
    await expect(talk).toBeVisible();
    await expect(talk).toHaveAttribute('href', '/contact');
    await expect(page.getByRole('link', { name: 'Start free' })).toHaveCount(0);
    await expect(page.locator('a[href="/app/signup"]'), 'no link to the signup page on the home page').toHaveCount(0);
  });

  test('the app signup page does not offer a form while go-api has signup off', async ({ page }) => {
    await watch(page, 'web-off');
    await page.goto('/app/signup');
    await page.screenshot({ path: `${SHOTS}/off-app-signup.png` });
    await expect(page.getByRole('button', { name: 'Create account' }), 'no Create account button with signup off').toHaveCount(0);
    await expect(page.getByText('does not take self-serve sign-ups')).toBeVisible();
  });
});

// =============================================================================================== phase on
test.describe('the flow: switches ON', () => {
  test.skip(PHASE !== 'on', 'SOFT_OPEN_PHASE=on (default)');
  test.describe.configure({ mode: 'serial' });

  const acct = { email: `soft-open-main-${unique('m')}@${MX_DOMAIN}`, orgId: '', userId: '' };

  test.beforeAll(async () => { await cleanupAccounts(); });
  test.afterAll(async () => { await cleanupAccounts(); });

  test('0. the site built with NEXT_PUBLIC_SIGNUP_ENABLED=true says "Start free" -> /app/signup', async ({ page }) => {
    await watch(page, 'site-on');
    const api = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
    const empty = await api.post('/api/v1/auth/signup', { data: {} });
    expect(empty.status(), 'signup is no longer a 404 (an empty body is a 400)').toBe(400);
    await api.dispose();

    await page.goto('/');
    await page.waitForLoadState('networkidle');
    await page.screenshot({ path: `${SHOTS}/on-site-home.png` });
    // Hydrated DOM, not just the server HTML: a client component that re-reads the flag in the browser bundle used to
    // flip every CTA back to "Talk to us" after hydration (site/lib/site-config.ts).
    const start = page.getByRole('link', { name: 'Start free' }).first();
    await expect(start).toBeVisible();
    await expect(start).toHaveAttribute('href', '/app/signup');
    await start.click();
    await expect(page).toHaveURL(/\/app\/signup/);
    await expect(page.getByRole('button', { name: 'Create account' })).toBeVisible();
  });

  test('1. sign up through the real /app/signup page (Turnstile is off in the stack)', async ({ page }) => {
    resetSignupCap();
    await watch(page, 'signup');
    await page.goto('/app/signup');
    expect(await page.locator('iframe[src*="turnstile"], .cf-turnstile').count(), 'no Turnstile widget in this stack (prod has one)').toBe(0);
    await page.getByLabel('Email', { exact: true }).fill(acct.email);
    await page.getByLabel('Password', { exact: true }).fill(PASSWORD);
    await page.getByLabel(/I accept the/).check();
    const links = await newVerifyLinks(async () => {
      await page.getByRole('button', { name: 'Create account' }).click();
      await expect(page.getByRole('status')).toBeVisible({ timeout: 20_000 });
    });
    await page.screenshot({ path: `${SHOTS}/signup-submitted.png` });
    expect(links[0]).toMatch(/\/app\/verify-email\?token=[0-9a-f]{64}$/);
    const row = psql(`SELECT id, (email_verified_at IS NULL) FROM application_users WHERE email = ${sqlStr(acct.email.toLowerCase())}`);
    expect(row, 'account created and still unverified').toMatch(/\|t$/);
    acct.userId = row.split('|')[0];
    expect(psql(`SELECT count(*) FROM legal_acceptances WHERE user_id = ${sqlStr(acct.userId)}`), 'Terms + Privacy acceptance recorded at signup').toBe('2');
    // The same address again answers the same 202 (no account enumeration) and mails the owner a fresh link.
    const again = await newVerifyLinks(async () => { expect((await apiSignup(acct.email)).status).toBe(202); });
    expect(again[0]).not.toBe(links[0]);
  });

  test('2. verify email from the console-logged link; resend-verification; old links die', async ({ page }) => {
    await watch(page, 'verify');
    const api = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
    // Resend: 202, a NEW link; the previous links stop working (latest mail wins).
    const previous = verifyLinks().filter(() => true).at(-1)!;
    const [resent] = await newVerifyLinks(async () => {
      const r = await api.post('/api/v1/auth/resend-verification', { data: { email: acct.email } });
      expect(r.status()).toBe(202);
    });
    expect(resent).not.toBe(previous);
    // Unknown address: the same 202, and no mail.
    const before = verifyLinks().length;
    const unknown = await api.post('/api/v1/auth/resend-verification', { data: { email: `soft-open-nobody-${unique('x')}@${MX_DOMAIN}` } });
    expect(unknown.status()).toBe(202);
    await new Promise((r) => setTimeout(r, 1500));
    expect(verifyLinks().length, 'no mail for an unknown address').toBe(before);
    const stale = await api.post('/api/v1/auth/verify-email', { data: { token: new URL(previous).searchParams.get('token') } });
    expect(stale.status(), 'a superseded link is refused').toBe(400);

    // Open the link in the browser: the page verifies, issues the session and lands on the dashboard.
    // A brand-new account with no project is then sent on, once, to the welcome questions
    // (WELCOME_FLOW_ENABLED, default on), so either address proves the session works.
    await page.goto(resent);
    await expect(page).toHaveURL(/\/app\/(dashboard|welcome)/, { timeout: 30_000 });
    await page.screenshot({ path: `${SHOTS}/verified-dashboard.png` });
    expect(psql(`SELECT (email_verified_at IS NOT NULL) FROM application_users WHERE id = ${sqlStr(acct.userId)}`)).toBe('t');
    const used = await api.post('/api/v1/auth/verify-email', { data: { token: new URL(resent).searchParams.get('token') } });
    expect(used.status(), 'a link is single-use').toBe(400);
    // Resend for a verified account: still 202, but nothing is mailed.
    const n = verifyLinks().length;
    expect((await api.post('/api/v1/auth/resend-verification', { data: { email: acct.email } })).status()).toBe(202);
    await new Promise((r) => setTimeout(r, 1500));
    expect(verifyLinks().length, 'no mail once verified').toBe(n);
    await api.dispose();
    const s = await billingSummary(page);
    acct.orgId = psql(`SELECT organization_id FROM organization_memberships WHERE user_id = ${sqlStr(acct.userId)} LIMIT 1`).split('\n')[0] || '';
    expect(s.subscription.planKey).toBe('trial');
  });

  test('2b. the per-IP signup cap throttles a burst (A6): 3 accepted, the 4th is 429 and creates nothing', async () => {
    resetSignupCap();
    const tag = unique('burst');
    const results: number[] = [];
    for (let i = 1; i <= 4; i++) results.push((await apiSignup(`soft-open-${tag}-${i}@${MX_DOMAIN}`)).status);
    expect(results).toEqual([202, 202, 202, 429]);
    expect(psql(`SELECT count(*) FROM application_users WHERE email LIKE ${sqlStr(`soft-open-${tag}-%`)}`), 'the throttled signup created no user').toBe('3');
    // A burst of resends is not throttled by the application; in the stack nginx's public-auth limiter is raised
    // (E2E_AUTH_RATE=100r/s, production is 5r/m burst 5), so this only records that the route stays 202.
    const api = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
    const codes: number[] = [];
    for (let i = 0; i < 8; i++) codes.push((await api.post('/api/v1/auth/resend-verification', { data: { email: `soft-open-${tag}-1@${MX_DOMAIN}` } })).status());
    test.info().annotations.push({ type: 'resend burst x8 (stack nginx 100r/s)', description: codes.join(',') });
    await api.dispose();
    resetSignupCap();
  });

  test('3. Terms gate: with a stale acceptance the app is unreachable until /app/accept-terms is accepted', async ({ page }) => {
    await watch(page, 'terms');
    const v = await legalVersions();
    // The acceptance was recorded at signup. Make it stale, as if the Terms had been republished since.
    psql(`UPDATE legal_acceptances SET version = '2000-01-01' WHERE user_id = ${sqlStr(acct.userId)}`);
    await signIn(page, acct.email, PASSWORD, undefined, 'soft-open account');
    await page.goto('/app/dashboard');
    await expect(page).toHaveURL(/\/app\/accept-terms/);
    await page.screenshot({ path: `${SHOTS}/terms-gate.png` });
    await page.goto('/app/settings/billing');
    await expect(page, 'the gate holds for every app page').toHaveURL(/\/app\/accept-terms/);
    const accept = page.getByRole('checkbox', { name: new RegExp(`version ${v.termsVersion}`) });
    await accept.check();
    await page.getByRole('button', { name: 'Accept and continue' }).click();
    // The account still has no project here, so the dashboard may hand over to the welcome questions.
    await expect(page).toHaveURL(/\/app\/(dashboard|welcome)/, { timeout: 20_000 });
    await page.goto('/app/settings/billing');
    await expect(page, 'after accepting, the app opens').toHaveURL(/\/app\/settings\/billing/);
    expect(psql(`SELECT string_agg(document || ':' || version, ',' ORDER BY document) FROM (SELECT DISTINCT ON (document) document, version FROM legal_acceptances WHERE user_id = ${sqlStr(acct.userId)} ORDER BY document, accepted_at DESC, id DESC) t`))
      .toBe(`Privacy:${v.privacyVersion},Terms:${v.termsVersion}`);
  });

  let balanceAfterGrant = 0;
  test('4. free first video: granted, in the balance, and a run starts (queues) without spending anything', async ({ page }) => {
    await watch(page, 'first-video');
    await signIn(page, acct.email, PASSWORD, undefined, 'soft-open account');
    const s = await billingSummary(page);
    expect(s.firstVideoGrantStatus).toBe('granted');
    expect(s.firstVideoGrant).toMatchObject({ credits: 750, remaining: 750 });
    expect(s.credits.grants.map((g: any) => g.source).sort()).toEqual(['promo', 'trial']);
    expect(s.credits.balance, 'trial credits + the 750 first-video credits').toBe(100 + 750);
    await page.goto('/app/settings/billing');
    await expect(page.getByTestId('first-video-grant-card')).toContainText('Your first video is on us');
    await page.screenshot({ path: `${SHOTS}/first-video-card.png` });

    // A run: create a project (the starter workflow is applied for it), execute it. The engine runs with no
    // provider keys, so nothing paid can happen; the run is queued, fails on its own empty input, and is stopped.
    const proj = await page.request.post('/api/v1/projects', { data: { name: `Soft open ${Date.now()}`, description: 'rehearsal' } });
    expect(proj.status(), await proj.text()).toBeLessThan(300);
    const projectId = (await proj.json()).id as string;
    const wfs = await (await page.request.get(`/api/v1/projects/${projectId}/workflows`)).json();
    expect(wfs.length).toBeGreaterThan(0);
    const run = await page.request.post(`/api/v1/projects/${projectId}/workflows/${wfs[0].id}/execute`, { data: {} });
    expect(run.status(), `run-start gates (verified email, plan, credits): ${await run.text()}`).toBe(201);
    const exec = await run.json();
    expect(['Queued', 'Running', 'Failed']).toContain(exec.status);
    await page.request.post(`/api/v1/projects/${projectId}/workflows/${wfs[0].id}/executions/${exec.id}/stop`);
    await expect.poll(async () => (await billingSummary(page)).credits.reserved, { message: 'nothing stays reserved', timeout: 20_000 }).toBe(0);
    balanceAfterGrant = (await billingSummary(page)).credits.balance;
    expect(balanceAfterGrant, 'no credits were spent by the queued run').toBe(850);
  });

  test('4b. an address whose domain has no mail server gets the Trial but not the free first video', async ({ page }) => {
    resetSignupCap();
    await watch(page, 'first-video-denied');
    const email = `soft-open-nomx-${unique('n')}@e2e.test`;
    const [link] = await newVerifyLinks(async () => { expect((await apiSignup(email)).status).toBe(202); });
    const ver = await page.context().request.post('/api/v1/auth/verify-email', { data: { token: new URL(link).searchParams.get('token') } });
    expect(ver.status()).toBe(200);
    const s = await billingSummary(page);
    expect(s.firstVideoGrant).toBeNull();
    expect(s.firstVideoGrantStatus).toBe('unavailable');
    expect(s.credits.balance, 'the Trial credits are unaffected').toBe(100);
    expect(psql(`SELECT t.first_video_denied_reason FROM trial_claims t JOIN organization_memberships m ON m.organization_id = t.organization_id JOIN application_users u ON u.id = m.user_id WHERE u.email = ${sqlStr(email.toLowerCase())}`),
      'the refusal is recorded with its reason').toBe('disposable_email');
    await page.goto('/app/settings/billing');
    await expect(page.getByTestId('first-video-grant-card-unavailable')).toBeVisible();
    await page.screenshot({ path: `${SHOTS}/first-video-unavailable.png` });
  });

  test('5. upgrade through Stripe checkout (the fake): plan + credits once, confirm page reload grants nothing more', async ({ page }) => {
    test.setTimeout(150_000);
    await watch(page, 'checkout');
    await signIn(page, acct.email, PASSWORD, undefined, 'soft-open account');
    await page.goto('/app/settings/billing');
    const before = (await billingSummary(page)).credits.balance;
    expect(before).toBe(balanceAfterGrant);

    const captured = await captureCheckout(page, () => page.getByTestId('plan-creator-choose').click());
    expect(captured.status).toBe(200);
    const sessionId = captured.body.transactionId as string;
    expect(sessionId).toMatch(/^cs_test_/);
    expect(captured.body.checkoutUrl).toBe(`${loadStack().stripeMockUrl}/pay/${sessionId}`);

    await page.waitForURL(/\/pay\/cs_test_/);
    await expect(page.getByTestId('mock-checkout-total')).toHaveText((planPriceMinor('creator') / 100).toFixed(2));
    await page.screenshot({ path: `${SHOTS}/stripe-hosted-checkout.png` });
    await page.getByTestId('mock-pay').click();
    await page.waitForURL(/\/app\/checkout\?session_id=cs_test_/);
    expect(new URL(page.url()).searchParams.get('session_id')).toBe(sessionId);

    await expect(page.getByTestId('checkout-success-card')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId('checkout-plan')).toContainText('Creator');
    await expect(page.getByTestId('checkout-plan')).toContainText('Active');
    await page.screenshot({ path: `${SHOTS}/checkout-confirmed.png` });

    const settled = await expectSummary(page, (s) => `${s.subscription.planKey}/${s.subscription.status}`, 'creator/Active');
    await expect.poll(async () => (await billingSummary(page)).credits.balance, { message: 'the plan credits arrive', timeout: 20_000 })
      .toBe(before + planIncludedCredits('creator'));
    await expect(page.getByTestId('checkout-credits')).toContainText('credits available now');
    const granted = await billingSummary(page);
    expect(granted.credits.grants.map((g: any) => g.source), 'the first-video promo grant is still there next to the plan credits').toContain('promo');

    // The confirm page again (a reload, then the same URL opened fresh): still a success, no second grant.
    await page.reload();
    await expect(page.getByTestId('checkout-success-card')).toBeVisible({ timeout: 45_000 });
    await page.goto(`/app/checkout?session_id=${sessionId}`);
    await expect(page.getByTestId('checkout-success-card')).toBeVisible({ timeout: 45_000 });
    await new Promise((r) => setTimeout(r, 2000));
    const afterReload = await billingSummary(page);
    expect(afterReload.credits.balance, 'no double grant on reload').toBe(granted.credits.balance);
    expect(afterReload.subscription).toEqual(settled.subscription);

    // Every webhook of the purchase delivered a second time: still nothing more.
    const org = acct.orgId || afterReload.organizationId;
    const sub = (await stripeMock.liveSubscriptionOf(org))!;
    expect(sub, 'the fake holds the subscription for this organization').toBeTruthy();
    await expect.poll(async () => (await stripeMock.webhooks()).filter((w) => w.subscription_id === sub.id && w.delivered).map((w) => w.event_type).sort(),
      { message: 'the three webhooks of the purchase were delivered', timeout: 20_000 })
      .toEqual(['checkout.session.completed', 'customer.subscription.created', 'invoice.paid']);
    for (const w of (await stripeMock.webhooks()).filter((x) => x.subscription_id === sub.id && x.delivered)) {
      expect((await stripeMock.redeliver(w.id)).status).toBe(200);
    }
    const final = await billingSummary(page);
    expect(final.credits.balance, 'no double grant on webhook redelivery').toBe(granted.credits.balance);
  });
});
