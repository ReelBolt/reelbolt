import { test, expect } from '@playwright/test';
import { loginAs } from '../../fixtures/stack';

/** S-phase smoke: with campaigns on, the page loads and the panel renders (QA_EXPECT=disabled for off). */
test('campaigns page loads', async ({ page }) => {
  const brevo: string[] = [];
  await page.context().route((u) => /brevo/i.test(u.hostname), (r) => { brevo.push(r.request().url()); return r.abort(); });
  await loginAs(page, 'admin');
  await page.goto('/app/admin/campaigns');
  if (process.env.QA_EXPECT === 'disabled') await expect(page.getByTestId('campaigns-disabled')).toBeVisible();
  else await expect(page.getByTestId('campaigns-panel')).toBeVisible();
  expect(brevo).toEqual([]);
});
