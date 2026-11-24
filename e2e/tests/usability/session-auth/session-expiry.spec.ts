import { expect, test } from '@playwright/test';
import { loginAs, user } from '../../../fixtures/stack';
import { expireSession, signInViaForm } from './helpers';

// A21: a 401 on an authenticated call goes to /app/login?next=<path>&reason=expired, once, with a plain message.
test.describe('A21 session expiry', () => {
  test('401 redirects to login with next and reason, shows the message, returns to next after sign-in', async ({ page, context }) => {
    await loginAs(page, 'creator');
    await expireSession(page);
    await page.goto('/app/settings/billing');
    await page.waitForURL(/\/app\/login\?/, { timeout: 30_000 });
    const url = new URL(page.url());
    expect(url.searchParams.get('reason')).toBe('expired');
    expect(url.searchParams.get('next')).toBe('/app/settings/billing');
    await expect(page.getByTestId('session-ended-notice')).toContainText('Your session ended. Sign in to continue.');
    await expect(page.getByText(/^unauthorized$/i)).toHaveCount(0);

    await page.unroute('**/api/v1/**');
    await context.clearCookies();
    const u = user('creator');
    await signInViaForm(page, u.email, u.password);
    await page.waitForURL(/\/app\/settings\/billing/, { timeout: 30_000 });
  });

  test('no redirect loop on the login page itself', async ({ page }) => {
    await expireSession(page);
    await page.goto('/app/login?reason=expired');
    await page.waitForTimeout(1500);
    expect(new URL(page.url()).pathname).toBe('/app/login');
    await expect(page.getByTestId('session-ended-notice')).toBeVisible();
  });

  test('login page without reason shows no expiry notice', async ({ page }) => {
    await page.goto('/app/login');
    await expect(page.getByTestId('session-ended-notice')).toHaveCount(0);
  });

  for (const [label, evil] of [
    ['protocol-relative', '//evil.example/x'],
    ['absolute url', 'https://evil.example/x'],
    ['backslash', '/\\evil.example'],
    ['encoded protocol-relative', '%2F%2Fevil.example'],
    ['encoded backslash', '/%5Cevil.example'],
  ] as const) {
    test(`open redirect stays on-site: ${label}`, async ({ page, context }) => {
      await page.goto(`/app/login?next=${encodeURIComponent(evil).replace(/%25/g, '%')}&reason=expired`);
      await context.clearCookies();
      const u = user('creator');
      await signInViaForm(page, u.email, u.password);
      await page.waitForURL((url) => !url.pathname.endsWith('/login'), { timeout: 30_000 });
      const host = new URL(page.url()).host;
      expect(host).toBe(new URL(test.info().project.use.baseURL ?? process.env.LOCAL_STAGING_URL!).host);
      expect(page.url()).not.toContain('evil');
    });
  }
});
