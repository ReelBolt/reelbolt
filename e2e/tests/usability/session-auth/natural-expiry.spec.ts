import { expect, test } from '@playwright/test';
import { loginAs, user } from '../../../fixtures/stack';
import { signInViaForm } from './helpers';

// A21 natural expiry: the session cookies are simply gone (24h lifetime), so the proxy, not the 401 handler, redirects.
test.describe('A21 natural expiry via proxy', () => {
  test('cleared cookies on a protected page: login carries next (no reason), sign-in returns there', async ({ page, context }) => {
    await loginAs(page, 'creator');
    await context.clearCookies();
    await page.goto('/app/settings/billing');
    await page.waitForURL(/\/app\/login\?/, { timeout: 30_000 });
    const url = new URL(page.url());
    expect(url.pathname).toBe('/app/login');
    expect(url.searchParams.get('next')).toBe('/app/settings/billing');
    expect(url.searchParams.has('reason')).toBe(false);
    expect(url.search).toBe('?next=%2Fapp%2Fsettings%2Fbilling');

    const u = user('creator');
    await signInViaForm(page, u.email, u.password);
    await page.waitForURL(/\/app\/settings\/billing/, { timeout: 30_000 });
  });

  test('a hostile next in the requested URL never leaves the site', async ({ page, context }) => {
    await loginAs(page, 'creator');
    await context.clearCookies();
    await page.goto('/app/projects?next=//evil.example/x');
    await page.waitForURL(/\/app\/login/, { timeout: 30_000 });
    const url = new URL(page.url());
    expect(url.origin).toBe(new URL(page.url()).origin);
    expect(url.hostname).not.toBe('evil.example');
    const next = url.searchParams.get('next');
    expect(next === null || (next.startsWith('/app/') && !next.startsWith('//'))).toBe(true);

    const u = user('creator');
    await signInViaForm(page, u.email, u.password);
    await page.waitForLoadState('load');
    await expect.poll(() => new URL(page.url()).hostname, { timeout: 30_000 }).not.toBe('evil.example');
    expect(new URL(page.url()).pathname.startsWith('/app')).toBe(true);
  });
});

// A06: the theme default makes every PasswordInput's eye a tab stop, including signup's.
test.describe('A06 signup password eye', () => {
  test('both eye toggles on /app/signup are tab stops with a name', async ({ page }) => {
    await page.goto('/app/signup');
    const toggles = page.getByRole('button', { name: 'Show password' });
    const n = await toggles.count();
    test.skip(n === 0, 'signup disabled on this stack');
    expect(n).toBeGreaterThanOrEqual(2);
    for (let i = 0; i < n; i++) {
      await expect(toggles.nth(i)).toHaveAttribute('tabindex', '0');
    }
  });
});
