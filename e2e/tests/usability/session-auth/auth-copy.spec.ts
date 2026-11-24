import { expect, test } from '@playwright/test';
import { loginAs, user } from '../../../fixtures/stack';
import { fillStable, signInViaForm } from './helpers';

test.describe('A22 plain error sentences', () => {
  test('wrong password: sentence tied to the password field with Forgot password link, cleared on edit', async ({ page }) => {
    await page.goto('/app/login');
    await signInViaForm(page, user('creator').email, 'definitely-wrong-password');
    const pw = page.getByLabel('Password', { exact: true });
    const msg = page.getByText('That email and password do not match. Check them and try again.');
    await expect(msg).toBeVisible({ timeout: 20_000 });
    await expect(page.getByText(/^invalid credentials$/i)).toHaveCount(0);
    await expect(pw).toHaveAttribute('data-invalid', 'true');
    const errId = await pw.getAttribute('aria-describedby');
    expect(errId).toBeTruthy();
    const err = page.locator(`[id="${errId}"]`);
    await expect(err).toContainText('That email and password do not match');
    await expect(err.getByRole('link', { name: 'Forgot password?' })).toBeVisible();
    await pw.fill('x');
    await expect(msg).toHaveCount(0);
  });

  test('forced password change copy: current = temporary, sentence case, length rule up front', async ({ page }) => {
    await loginAs(page, 'creator');
    await page.goto('/app/change-password');
    test.skip(!/change-password/.test(page.url()), 'redirected to ' + page.url());
    await expect(page.getByText('If an administrator gave you a temporary password, enter that.')).toBeVisible();
    await expect(page.getByText('Use at least 10 characters.')).toBeVisible();
    await expect(page.getByLabel('Current password')).toBeVisible();
    await expect(page.getByLabel('New password', { exact: true })).toBeVisible();
    await expect(page.getByLabel('Confirm new password')).toBeVisible();
  });
});

test.describe('A24 reset-password with a bad or missing token', () => {
  for (const [label, qs] of [['missing', ''], ['obviously bad', '?token=abc']] as const) {
    test(`${label} token: expired message up front with a send-new-link action`, async ({ page }) => {
      await page.goto(`/app/reset-password${qs}`);
      await expect(page.getByText('This link has expired or is not complete. Send a new link to set your password.')).toBeVisible();
      await expect(page.getByLabel('New password')).toHaveCount(0);
      await page.getByRole('link', { name: 'Send a new link' }).click();
      await page.waitForURL(/\/app\/forgot-password/);
    });
  }

  test('well-formed but unknown token: form shows, server rejection becomes the plain expired sentence', async ({ page }) => {
    await page.goto('/app/reset-password?token=' + 'A'.repeat(43));
    await expect(page.getByLabel('New password', { exact: true })).toBeVisible();
    await fillStable(page.getByLabel('New password', { exact: true }), 'a-long-enough-password-1');
    await fillStable(page.getByLabel('Confirm new password'), 'a-long-enough-password-1');
    await page.getByRole('button', { name: /reset|update|set|save/i }).click();
    await expect(page.getByText(/expired|already used/i).first()).toBeVisible({ timeout: 20_000 });
    await expect(page.getByText(/^invalid or expired token$/i)).toHaveCount(0);
    await expect(page.getByRole('link', { name: 'Send a new link' })).toBeVisible();
  });
});
