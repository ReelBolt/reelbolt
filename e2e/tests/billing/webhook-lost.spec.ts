/**
 * A Paddle webhook lost for good (docs/billing.md "When a Paddle webhook is lost"): the org recovers
 * from Paddle's live state through each of the reconciler's three triggers, and a late redelivery of
 * the lost webhooks changes nothing. Throwaway Owners only; the seeded admin drives the admin panel.
 *
 *   A (sweep)  Creator → Upgrade to Pro in the UI with subscription.updated and the proration
 *              transaction.completed dropped: a second change → 409 change_pending; the background
 *              sweep heals it to Pro with exactly one prorated grant; redelivering both lost
 *              webhooks leaves summary, plan and balance unchanged.
 *   B (lazy)   Team 3 → 5 seats with subscription.updated dropped; past the threshold the next seat
 *              change heals the row itself and answers 202; the summary shows the new seats.
 *   C (admin)  cancel with subscription.updated dropped; a platform admin finds the workspace under
 *              "Subscriptions waiting on Paddle", Resync from Paddle → confirm; the customer's page
 *              shows "Cancels on …".
 *
 * The isolated stack runs the sweep every 4 s with an 8 s threshold (docker-compose.e2e.yml). B and C
 * must not be healed by the sweep, so before them this spec RESTARTS ONLY THE INFERENCE SERVICE of the
 * stack with E2E_RECONCILE_SWEEP_SECONDS=3600 (same compose project, files and env file as
 * up-isolated.sh; the one startup sweep runs 5 s after start, then none for an hour), and restores the
 * default (4 s) afterwards. That restart makes the API answer 502 for a few seconds, so do not run this
 * file in parallel with other billing specs on the same stack:
 *
 *   E2E_PROJECT=reelbolt-myinit APP_PORT=8112 e2e/scripts/up-isolated.sh
 *   source e2e/.stack/reelbolt-myinit/env.sh
 *   cd e2e && npx playwright test tests/billing/webhook-lost.spec.ts --project=desktop
 *
 * Drop filters match when a webhook is queued, so each is armed before the call that causes it. The
 * only fixed wait is B's wait past the threshold; everything else polls.
 */
import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'child_process';
import * as path from 'path';
import { billingSummary, expectSummary, loadStack, loginAs, longDateIn, mock, subscribeViaApi, Throwaways } from '../../fixtures/stack';

// In order, in one worker (A before the restart, B and C after it); a failure does not skip the rest.
test.describe.configure({ mode: 'default' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('lost');
test.afterAll(async () => users.cleanup());

test.beforeEach(async ({}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
});

const STUCK_AFTER_MS = Number(process.env.E2E_RECONCILE_STUCK_AFTER_SECONDS || 8) * 1000;
const DEFAULT_SWEEP_SECONDS = '4';

// ------------------------------------------------------------------ helpers

/** `docker compose` on this stack's project, exactly as e2e/scripts/up-isolated.sh calls it. */
function compose(args: string[], env: NodeJS.ProcessEnv = process.env): string {
  const { stackDir } = loadStack();
  const repoRoot = path.resolve(stackDir, '../../..');
  const project = process.env.E2E_PROJECT || path.basename(stackDir);
  return execFileSync('docker', [
    'compose', '-p', project, '--project-directory', repoRoot,
    '--env-file', path.join(stackDir, '.env'),
    '-f', path.join(repoRoot, 'docker-compose.yml'), '-f', path.join(repoRoot, 'docker-compose.e2e.yml'),
    ...args,
  ], { encoding: 'utf8', env, stdio: ['ignore', 'pipe', 'pipe'], timeout: 600_000 }).trim();
}

/**
 * Recreates only the inference container with `Billing__Reconcile__SweepIntervalSeconds` set through
 * E2E_RECONCILE_SWEEP_SECONDS (undefined = the compose default), waits until it is healthy and the API
 * answers through nginx again.
 */
async function restartInference(page: Page, sweepSeconds: string | undefined): Promise<void> {
  const env = { ...process.env };
  if (sweepSeconds === undefined) delete env.E2E_RECONCILE_SWEEP_SECONDS;
  else env.E2E_RECONCILE_SWEEP_SECONDS = sweepSeconds;
  compose(['up', '-d', '--no-deps', '--no-build', '--wait', '--wait-timeout', '300', 'inference'], env);
  const effective = compose(['exec', '-T', 'inference', 'printenv', 'Billing__Reconcile__SweepIntervalSeconds']);
  expect(effective, 'sweep interval of the running inference container').toBe(sweepSeconds ?? DEFAULT_SWEEP_SECONDS);
  await expect.poll(async () => (await page.request.get(`${loadStack().baseUrl}/api/v1/billing/summary`)).status(),
    { message: 'inference answers through nginx again', timeout: 60_000, intervals: [500, 1000] }).toBeLessThan(500);
}

async function post(page: Page, route: string, data: unknown) {
  return page.request.post(`${loadStack().baseUrl}/api/v1/billing/${route}`, { data });
}

async function droppedOf(subscriptionId: string) {
  return (await mock.webhooks()).filter((w) => w.dropped && w.subscription_id === subscriptionId);
}

// ------------------------------------------------------------------ A: sweep

test('A (sweep): upgrade with its webhooks lost → 409 change_pending → sweep heals to Pro with one grant → late redelivery is a no-op', async ({ page }) => {
  test.setTimeout(150_000);
  const t = await users.create('sweep');
  await users.login(page, t);
  const { subscriptionId } = await subscribeViaApi(page, 'creator');
  const before = (await billingSummary(page)).credits.balance;

  // Lose the upgrade's subscription.updated and its proration transaction.completed.
  await mock.dropWebhooks({ event_type: 'subscription.updated', subscription_id: subscriptionId });
  await mock.dropWebhooks({ event_type: 'transaction.completed', subscription_id: subscriptionId });

  let grant = 0;
  await test.step('Upgrade to Pro in the UI → 202; a second change right away → 409 change_pending', async () => {
    await page.goto('/app/settings/billing');
    await page.getByTestId('plan-pro-change').click();
    const dialog = page.getByRole('dialog', { name: 'Upgrade to Pro?' });
    const resp = page.waitForResponse((r) => r.url().endsWith('/api/v1/billing/subscription/change-plan'));
    await dialog.getByRole('button', { name: 'Confirm' }).click();
    const r = await resp;
    expect(r.status()).toBe(202);
    const body = await r.json();
    expect(body).toMatchObject({ direction: 'upgrade', effective: 'now' });
    grant = body.creditGrantMillis / 1000;
    expect(grant).toBeGreaterThan(0);

    // A real change (the row still says Creator, so "back to Creator" would be no_change).
    const second = await post(page, 'subscription/change-plan', { plan: 'team' });
    const secondBody = await second.text();
    expect(second.status(), `second change: ${secondBody}`).toBe(409);
    expect(JSON.parse(secondBody).error).toBe('change_pending');
    const s = await billingSummary(page);
    expect(s.subscription.planKey, 'the row still has the old plan').toBe('creator');
  });

  await test.step('Paddle has both changes; their webhooks were never sent', async () => {
    const lost = await droppedOf(subscriptionId);
    expect(lost.map((w) => w.event_type).sort()).toEqual(['subscription.updated', 'transaction.completed']);
    for (const w of lost) expect(w).toMatchObject({ delivered: false, attempts: [] });
    const paid = await mock.listTransactions(subscriptionId, { origin: 'subscription_update', status: 'completed' });
    expect(paid, 'the proration charge is paid at Paddle').toHaveLength(1);
  });

  const healed = await test.step('the sweep heals it: Pro, balance up by exactly one prorated grant', async () => {
    const s = await expectSummary(page, (x) => `${x.subscription.planKey}/${x.credits.balance > before}`, 'pro/true',
      'healed by the sweep (threshold 8 s, sweep every 4 s)', 60_000);
    const gained = s.credits.balance - before;
    expect(Math.abs(gained - grant), `gained ${gained}, one prorated grant is ${grant}`).toBeLessThan(1);
    expect(gained).toBeLessThan(2500); // Pro 3500 − Creator 1000, prorated
    expect((await droppedOf(subscriptionId)).every((w) => !w.delivered), 'healed without any webhook').toBe(true);
    await page.reload();
    await expect(page.getByTestId('current-plan-card')).toContainText('Pro');
    return s;
  });

  await test.step('redeliver both lost webhooks (original event ids) → summary, plan and balance unchanged', async () => {
    for (const w of await droppedOf(subscriptionId)) {
      const r = await mock.redeliver(w.id);
      expect(r.status, `redelivery of ${w.event_type} ${w.event_id}`).toBe(200);
      expect(r.delivery).toMatchObject({ redelivery_of: w.id, event_id: w.event_id, occurred_at: w.occurred_at, delivered: true });
    }
    const after = await billingSummary(page);
    expect(after.subscription).toEqual(healed.subscription);
    expect(after.subscription.planKey).toBe('pro');
    expect(after.credits.balance, 'no second grant').toBe(healed.credits.balance);
  });
});

// ------------------------------------------------------------------ B and C: sweep paused

test.describe('sweep paused (inference restarted with E2E_RECONCILE_SWEEP_SECONDS=3600)', () => {
  test.beforeAll(async ({ browser }, testInfo) => {
    if (testInfo.project.name !== 'desktop') return;
    test.setTimeout(420_000);
    const page = await browser.newPage();
    try { await restartInference(page, '3600'); } finally { await page.close(); }
  });

  test.afterAll(async ({ browser }, testInfo) => {
    if (testInfo.project.name !== 'desktop') return;
    test.setTimeout(420_000);
    const page = await browser.newPage();
    try { await restartInference(page, undefined); } finally { await page.close(); }
  });

  test('B (lazy): seat change with its webhook lost → past the threshold the next seat change heals and answers 202', async ({ page }) => {
    test.setTimeout(150_000);
    const t = await users.create('lazy');
    await users.login(page, t);
    const { subscriptionId } = await subscribeViaApi(page, 'team', 3);
    await mock.setPayments(subscriptionId, 'hold'); // credits are not under test here

    await mock.dropWebhooks({ event_type: 'subscription.updated', subscription_id: subscriptionId });
    const r1 = await post(page, 'seats', { seats: 5 });
    expect(r1.status(), await r1.text()).toBe(202);
    expect(await r1.json()).toMatchObject({ direction: 'upgrade', effective: 'now', seats: 5 });

    const stuck = await post(page, 'seats', { seats: 6 });
    expect(stuck.status(), 'stuck before the threshold').toBe(409);
    expect((await stuck.json()).error).toBe('change_pending');
    const [lost] = await droppedOf(subscriptionId);
    expect(lost).toMatchObject({ event_type: 'subscription.updated', delivered: false });

    // The one fixed wait: past the threshold, counted from Paddle's change (updated_at).
    const sub = (await mock.state()).subscriptions.find((s) => s.id === subscriptionId)!;
    expect(sub.items[0].quantity).toBe(5);
    const waitMs = Date.parse(sub.updated_at) + STUCK_AFTER_MS + 1_500 - Date.now();
    if (waitMs > 0) await page.waitForTimeout(waitMs);
    expect((await billingSummary(page)).subscription.seats, 'the sweep did not heal it').toBe(3);

    const r2 = await post(page, 'seats', { seats: 6 });
    const r2Body = await r2.text();
    expect(r2.status(), `seat change after the threshold: ${r2Body}`).toBe(202);
    expect(JSON.parse(r2Body)).toMatchObject({ direction: 'upgrade', effective: 'now', seats: 6 });
    await expectSummary(page, (s) => s.subscription.seats, 6, 'the new seats after the lazy heal');
    const patch = (await mock.calls({ path: `/subscriptions/${subscriptionId}`, method: 'PATCH' })).at(-1);
    expect((patch!.body as any).items[0].quantity).toBe(6);
    expect((await droppedOf(subscriptionId)).every((w) => !w.delivered), 'healed without the lost webhook').toBe(true);
  });

  test('C (admin): cancel with its webhook lost → admin Resync from Paddle → customer sees "Cancels on …"', async ({ page, browser }) => {
    test.setTimeout(150_000);
    const t = await users.create('admin');
    await users.login(page, t);
    const { subscriptionId } = await subscribeViaApi(page, 'creator');
    const periodEndText = await longDateIn(page, (await billingSummary(page)).subscription.periodEnd);

    await mock.dropWebhooks({ event_type: 'subscription.updated', subscription_id: subscriptionId });
    const c = await post(page, 'subscription/cancel', {});
    expect(c.status(), await c.text()).toBe(202);
    expect((await mock.state()).subscriptions.find((s) => s.id === subscriptionId)?.scheduled_change?.action).toBe('cancel');
    await page.goto('/app/settings/billing');
    await expect(page.getByTestId('current-plan-card')).toContainText('Creator');
    await expect(page.getByTestId('cancel-banner')).toHaveCount(0);

    const adminContext = await browser.newContext();
    try {
      await loginAs(adminContext, 'admin');
      const admin = await adminContext.newPage();
      const row = admin.getByRole('row').filter({ hasText: t.orgId! });

      await test.step('the workspace is listed under "Subscriptions waiting on Paddle" once past the threshold', async () => {
        await expect(async () => {
          await admin.goto('/app/admin/billing');
          await admin.getByRole('tab', { name: 'Waiting on Paddle' }).click();
          await expect(admin.getByText('Subscriptions waiting on Paddle')).toBeVisible();
          await expect(row).toBeVisible({ timeout: 3_000 });
        }).toPass({ timeout: 60_000, intervals: [1_000, 2_000] });
        expect((await billingSummary(page)).subscription.cancelAtPeriodEnd, 'not healed by the sweep').toBe(false);
      });

      await test.step('Resync from Paddle → confirm → Healed', async () => {
        await row.getByRole('button', { name: /^Resync from Paddle/ }).click();
        const dialog = admin.getByRole('dialog', { name: 'Resync from Paddle' });
        await dialog.getByLabel('Reason (optional)').fill('e2e: cancel webhook lost');
        const resp = admin.waitForResponse((r) => r.url().endsWith(`/organizations/${t.orgId}/resync`) && r.request().method() === 'POST');
        await dialog.getByRole('button', { name: 'Resync from Paddle' }).click();
        const r = await resp;
        expect(r.status()).toBe(200);
        expect(await r.json()).toMatchObject({ outcome: 'Healed' });
        await expect(dialog).toBeHidden();
      });
    } finally {
      await adminContext.close();
    }

    await page.reload();
    await expect(page.getByTestId('cancel-banner')).toContainText(`Cancels on ${periodEndText}`);
    expect((await billingSummary(page)).subscription.cancelAtPeriodEnd).toBe(true);
    expect((await droppedOf(subscriptionId)).every((w) => !w.delivered), 'healed without the lost webhook').toBe(true);
  });
});
