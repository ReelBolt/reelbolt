/**
 * Smoke check of an isolated e2e stack: every seeded user can sign in and open the billing page, and
 * the seeded billing state is what users.json says it is.
 *
 *   source e2e/.stack/<project>/env.sh
 *   cd e2e && npx playwright test tests/billing/stack-smoke.spec.ts --project=desktop
 */
import { test, expect } from '@playwright/test';
import { loadStack, loginAs, mock, user, type SeededLabel } from '../../fixtures/stack';

const expected: Record<SeededLabel, { planKey: string; seats?: number; paid: boolean }> = {
  admin: { planKey: 'trial', paid: false },
  trial: { planKey: 'trial', paid: false },
  creator: { planKey: 'creator', paid: true },
  team: { planKey: 'team', seats: 3, paid: true },
  canceled: { planKey: 'trial', paid: false },
  member: { planKey: 'team', seats: 3, paid: true },
};

test.describe('isolated stack smoke', () => {
  test('the Paddle mock is reachable and knows the seeded subscriptions', async () => {
    const state = await mock.state();
    for (const label of ['creator', 'team'] as const) {
      const sub = state.subscriptions.find((s) => s.id === user(label).subscriptionId);
      expect(sub, `${label}'s subscription in the mock`).toBeTruthy();
      expect(sub!.status).toBe('active');
    }
    const canceled = state.subscriptions.find((s) => s.id === user('canceled').subscriptionId);
    expect(canceled?.status).toBe('canceled');
  });

  for (const label of Object.keys(expected) as SeededLabel[]) {
    test(`${label} signs in and sees the billing page`, async ({ page }) => {
      const { baseUrl } = loadStack();
      await loginAs(page, label);

      const summary = await page.request.get(`${baseUrl}/api/v1/billing/summary`);
      expect(summary.status()).toBe(200);
      const body = await summary.json();
      expect(body.subscription.planKey).toBe(expected[label].planKey);
      if (expected[label].seats) expect(body.subscription.seats).toBe(expected[label].seats);
      if (expected[label].paid) {
        expect(body.subscription.status).toBe('Active');
        expect(body.subscription.hasBillingCustomer).toBe(true);
      }

      await page.goto(`${baseUrl}/app/settings/billing`);
      await expect(page).toHaveURL(/\/app\/settings\/billing/);
      await expect(page.getByRole('heading', { name: 'Plan and billing' })).toBeVisible();
    });
  }
});
