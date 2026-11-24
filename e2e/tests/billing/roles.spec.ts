/**
 * Roles on Plan & billing: a Member of a Team workspace sees the plan, the cancel banner and the
 * scheduled-change banner, but no button that acts; every billing action answers 403 to them.
 * A throwaway Owner with its own Team org and a throwaway Member, deleted afterwards.
 *
 *   cd e2e && npx playwright test tests/billing/roles.spec.ts --project=desktop
 */
import { test, expect, type Page } from '@playwright/test';
import { expectSummary, loadStack, subscribeViaApi, Throwaways, type Throwaway } from '../../fixtures/stack';

test.describe.configure({ mode: 'serial' });
test.use({ trace: 'retain-on-failure' });

const users = new Throwaways('roles');
test.afterAll(async () => users.cleanup());

async function expectReadOnly(page: Page) {
  await expect(page.getByText('You can see this page, but not change it')).toBeVisible();
  for (const id of ['manage-in-paddle', 'update-payment-method', 'cancel-in-paddle', 'cancel-plan', 'update-seats', 'cancel-banner-keep', 'scheduled-change-keep']) {
    await expect(page.getByTestId(id), id).toHaveCount(0);
  }
  await expect(page.locator('[data-testid^="plan-"][data-testid$="-change"], [data-testid^="plan-"][data-testid$="-choose"]')).toHaveCount(0);
  await expect(page.getByTestId('topup-card').getByRole('button')).toHaveCount(0);
  await expect(page.getByTestId('plans-card').getByText('An Owner or Billing member can move the workspace to this plan.')).toHaveCount(2);
  await expect(page.locator('main input')).toHaveCount(0); // no seat inputs (F-06)
}

test('member: read-only page, banners without buttons; billing actions → 403', async ({ page, browser }, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
  test.setTimeout(150_000);
  const owner: Throwaway = await users.create('owner');
  const member: Throwaway = await users.create('member');
  await users.createTeamOrg(owner, `E2E Roles ${Date.now()}`);

  const ownerCtx = await browser.newContext({ baseURL: loadStack().baseUrl });
  const ownerPage = await ownerCtx.newPage();
  try {
    await users.login(ownerPage, owner);
    await subscribeViaApi(ownerPage, 'team', 3);
    await users.addMember(owner, member, 'Member');
    await users.login(page, member);

    await test.step('plain read-only view', async () => {
      await page.goto('/app/settings/billing');
      await expect(page.getByTestId('current-plan-card')).toContainText('Team');
      await expectReadOnly(page);
    });

    await test.step('owner cancels → member sees "Cancels on" without "Keep my plan"', async () => {
      const r = await ownerPage.request.post(`${loadStack().baseUrl}/api/v1/billing/subscription/cancel`);
      expect(r.status()).toBe(202);
      await expectSummary(page, (s) => s.subscription.cancelAtPeriodEnd, true);
      await page.reload();
      await expect(page.getByTestId('cancel-banner')).toContainText('Cancels on');
      await expectReadOnly(page);
    });

    await test.step('owner keeps the plan, then schedules fewer seats → member sees "Moves to Team on" without "Keep"', async () => {
      expect((await ownerPage.request.post(`${loadStack().baseUrl}/api/v1/billing/subscription/resume`)).status()).toBe(202);
      await expectSummary(page, (s) => s.subscription.cancelAtPeriodEnd, false);
      expect((await ownerPage.request.post(`${loadStack().baseUrl}/api/v1/billing/seats`, { data: { seats: 4 } })).status()).toBe(202);
      await expectSummary(page, (s) => s.subscription.seats, 4);
      expect((await ownerPage.request.post(`${loadStack().baseUrl}/api/v1/billing/seats`, { data: { seats: 3 } })).status()).toBe(202);
      await expectSummary(page, (s) => s.subscription.scheduledChange?.seats ?? null, 3);
      await page.reload();
      await expect(page.getByTestId('scheduled-change-banner')).toContainText('Moves to Team on');
      await expect(page.getByTestId('cancel-banner')).toHaveCount(0);
      await expectReadOnly(page);
    });

    await test.step('direct POSTs as the member → 403', async () => {
      const base = loadStack().baseUrl;
      const calls: [string, unknown][] = [
        ['/api/v1/billing/subscription/cancel', undefined],
        ['/api/v1/billing/subscription/resume', undefined],
        ['/api/v1/billing/subscription/change-plan', { plan: 'pro' }],
        ['/api/v1/billing/seats', { seats: 5 }],
        ['/api/v1/billing/checkout', { plan: 'pro' }],
        ['/api/v1/billing/checkout', { topUpCredits: 500 }],
        ['/api/v1/billing/portal', undefined],
      ];
      for (const [p, data] of calls) {
        const r = await page.request.post(`${base}${p}`, data === undefined ? {} : { data });
        expect(r.status(), `${p} ${JSON.stringify(data ?? {})}`).toBe(403);
      }
      const s = await expectSummary(page, (x) => `${x.subscription.planKey}/${x.subscription.seats}/${x.subscription.cancelAtPeriodEnd}`, 'team/4/false');
      expect(s.canManageBilling).toBe(false);
    });
  } finally {
    await ownerCtx.close();
  }
});
