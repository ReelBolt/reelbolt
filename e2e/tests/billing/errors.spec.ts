/**
 * The "Retry" button of Plan & billing and of Usage: shown when the summary (or usage) request fails,
 * and loads the page once the API answers again. The failure is injected in the browser (the request
 * is answered 503 by a route handler) because a healthy stack never fails it; the recovery is real.
 * Read-only, seeded `creator`.
 *
 *   cd e2e && npx playwright test tests/billing/errors.spec.ts --project=desktop
 */
import { test, expect } from '@playwright/test';
import { loginAs } from '../../fixtures/stack';

test.use({ trace: 'retain-on-failure' });

test.beforeEach(async ({}, testInfo) => {
  test.skip(testInfo.project.name !== 'desktop', 'flows run on the desktop project');
});

for (const [path, failing, title, loaded] of [
  ['/app/settings/billing', '**/api/v1/billing/summary', 'Could not load your plan', 'current-plan-card'],
  ['/app/settings/usage', '**/api/v1/billing/usage?**', 'Could not load your usage', 'credits-card'],
] as const) {
  test(`Retry on ${path}`, async ({ page }) => {
    await loginAs(page, 'creator');
    let fail = true;
    await page.route(failing, (route) =>
      fail ? route.fulfill({ status: 503, contentType: 'application/json', body: '{"error":"e2e_injected"}' }) : route.fallback());
    await page.goto(path);
    const alert = page.getByRole('alert').filter({ hasText: title });
    await expect(alert).toBeVisible({ timeout: 20_000 });
    fail = false;
    const retried = page.waitForResponse((r) => r.url().includes(failing.replace(/\*\*|\?/g, '').replace(/\*$/, '')) && r.status() === 200);
    await alert.getByRole('button', { name: 'Retry' }).click();
    await retried;
    await expect(page.getByTestId(loaded)).toBeVisible();
    await expect(alert).toHaveCount(0);
  });
}
