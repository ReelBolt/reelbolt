import { expect, test } from '@playwright/test';

// A06: the show/hide eye is a keyboard tab stop, named, aria-pressed, 44px on touch.
test('A06 eye toggle: keyboard reachable and operable, accessible name, aria-pressed', async ({ page }) => {
  await page.goto('/app/login');
  const pw = page.getByLabel('Password', { exact: true });
  const eye = page.getByRole('button', { name: 'Show password' });
  await expect(eye).toBeVisible();
  await expect(eye).not.toHaveAttribute('tabindex', '-1');
  await pw.focus();
  await page.keyboard.press('Tab');
  await expect(eye).toBeFocused();
  await expect(eye).toHaveAttribute('aria-pressed', 'false');
  await expect(pw).toHaveAttribute('type', 'password');
  await page.keyboard.press('Enter');
  await expect(eye).toHaveAttribute('aria-pressed', 'true');
  await expect(pw).toHaveAttribute('type', 'text');
  await page.keyboard.press('Space');
  await expect(eye).toHaveAttribute('aria-pressed', 'false');
  await expect(pw).toHaveAttribute('type', 'password');
});

test('A06 eye toggle is at least 44x44 on touch pointers', async ({ page, isMobile, hasTouch }) => {
  test.skip(!(isMobile || hasTouch), 'touch projects only');
  await page.goto('/app/login');
  const coarse = await page.evaluate(() => matchMedia('(pointer: coarse)').matches);
  test.skip(!coarse, 'pointer is not coarse in this project');
  const box = await page.getByRole('button', { name: 'Show password' }).boundingBox();
  expect(box!.width).toBeGreaterThanOrEqual(44);
  expect(box!.height).toBeGreaterThanOrEqual(44);
});
