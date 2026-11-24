import { expect, type Locator, type Page } from '@playwright/test';

/** Fill and confirm the value stuck (webkit can fill before React hydrates and lose the value). */
export async function fillStable(field: Locator, value: string): Promise<void> {
  await expect(async () => {
    await field.fill(value);
    await expect(field).toHaveValue(value, { timeout: 1000 });
  }).toPass({ timeout: 15_000 });
}

/** Every authenticated API call answers 401 (the auth endpoints keep working). Simulates an expired session. */
export async function expireSession(page: Page): Promise<void> {
  await page.route('**/api/v1/**', async (route) => {
    const path = new URL(route.request().url()).pathname;
    if (/^\/api\/v1\/auth\//.test(path)) return route.continue();
    await route.fulfill({
      status: 401,
      contentType: 'application/json',
      body: JSON.stringify({ error: 'Unauthorized' }),
    });
  });
}

export async function signInViaForm(page: Page, email: string, password: string): Promise<void> {
  await fillStable(page.getByLabel('Email'), email);
  await fillStable(page.getByLabel('Password', { exact: true }), password);
  await page.getByRole('button', { name: /^sign in$/i }).click();
}
