import { expect, test } from '@playwright/test';
import { PROJECT_ID, mockApi, signIn } from './fixtures';

// Runs with WA_FLAG=on (WORKFLOW_APPS_ENABLED=true). See ../../playwright.workflow-apps.config.ts.
const URL = `/app/projects/${PROJECT_ID}?tab=workflows`;

test.beforeEach(async ({ page, baseURL }) => {
  await signIn(page, baseURL!);
});

test('gallery shows the sections, cost badges as words, and a card opens its workflow route', async ({ page }) => {
  await mockApi(page);
  await page.goto(URL);
  await expect(page.getByRole('tab', { name: /^Apps/ })).toBeVisible();
  await expect(page.getByRole('heading', { level: 2, name: 'Your apps' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 2, name: 'All apps' })).toBeVisible();
  for (const word of ['Uses credits and a voice', 'No credits', 'Uses credits and AI shots', 'Uses credits']) {
    await expect(page.getByText(word, { exact: true }).first()).toBeVisible();
  }
  await expect(page.getByText('Needs:').first()).toBeVisible();
  await page.getByRole('link', { name: 'Open Launch teaser' }).click();
  await expect(page).toHaveURL(new RegExp(`/projects/${PROJECT_ID}/workflows/wf-a$`));
});

test('a recipe provisions its workflow and opens its route', async ({ page }) => {
  let applied = '';
  await mockApi(page, { onApply: (k) => { applied = k; } });
  await page.goto(URL);
  await page.getByRole('button', { name: /^Use this recipe/ }).first().click();
  await expect(page).toHaveURL(new RegExp(`/projects/${PROJECT_ID}/workflows/wf-new$`));
  expect(applied).not.toBe('');
});

test('category chips move with the arrow keys', async ({ page }) => {
  await mockApi(page);
  await page.goto(URL);
  const radios = page.getByRole('radiogroup', { name: 'Category' }).getByRole('radio');
  await expect(radios.first()).toHaveAttribute('aria-checked', 'true');
  await radios.first().focus();
  await page.keyboard.press('ArrowRight');
  await expect(page.getByRole('radio', { name: 'Promo' })).toHaveAttribute('aria-checked', 'true');
  await expect(page.getByRole('radio', { name: 'Promo' })).toBeFocused();
  await page.keyboard.press('ArrowLeft');
  await page.keyboard.press('ArrowLeft');
  await expect(page.getByRole('radio', { name: 'Utility' })).toHaveAttribute('aria-checked', 'true');
  await page.keyboard.press('Home');
  await expect(page.getByRole('radio', { name: 'All' })).toHaveAttribute('aria-checked', 'true');
});

test('WCAG label in name: every recipe button name starts with its visible text', async ({ page }) => {
  await mockApi(page);
  await page.goto(URL);
  const buttons = page.getByRole('button', { name: /^Use this recipe/ });
  await expect(buttons).toHaveCount(3);
  for (const b of await buttons.all()) {
    const name = (await b.getAttribute('aria-label')) ?? (await b.innerText());
    expect(name.trim().startsWith('Use this recipe')).toBe(true);
  }
  await expect(page.getByRole('button', { name: 'Use this recipe for Product promo' })).toBeVisible();
});

test('while one recipe provisions, the others are aria-disabled and the busy card keeps its heading', async ({ page }) => {
  let release!: () => void;
  const hold = new Promise<void>((r) => { release = r; });
  await mockApi(page, { holdApply: hold });
  await page.goto(URL);
  await page.getByRole('button', { name: 'Use this recipe for Product promo' }).click();
  await expect(page.getByRole('status', { name: 'Setting up Product promo' })).toBeVisible();
  await expect(page.getByRole('heading', { level: 3, name: 'Product promo' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Use this recipe for Brand intro' })).toHaveAttribute('aria-disabled', 'true');
  await expect(page.getByRole('button', { name: 'Use this recipe for Highlight reel' })).toHaveAttribute('aria-disabled', 'true');
  // A press on a disabled one is ignored: still on the project page.
  await page.getByRole('button', { name: 'Use this recipe for Brand intro' }).click({ force: true });
  await expect(page).toHaveURL(/tab=workflows/);
  release();
  await expect(page).toHaveURL(new RegExp(`/projects/${PROJECT_ID}/workflows/wf-new$`));
});

test('phone: one card per row, no horizontal scroll, primary buttons >= 44px', async ({ page, isMobile }) => {
  test.skip(!isMobile, 'phone project only');
  await mockApi(page);
  await page.goto(URL);
  await expect(page.getByRole('heading', { level: 2, name: 'All apps' })).toBeVisible();
  const cards = page.locator('article');
  const n = await cards.count();
  expect(n).toBeGreaterThanOrEqual(6);
  const boxes = [];
  for (let i = 0; i < n; i++) boxes.push((await cards.nth(i).boundingBox())!);
  const xs = new Set(boxes.map((b) => Math.round(b.x)));
  expect(xs.size, 'all cards share one left edge').toBe(1);
  for (let i = 1; i < n; i++) expect(boxes[i].y).toBeGreaterThan(boxes[i - 1].y);
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(overflow).toBeLessThanOrEqual(0);
  const targets = page.locator('article').getByRole('button').or(page.locator('article').getByRole('link', { name: /^(Open|Build a custom)/ }));
  const tn = await targets.count();
  expect(tn).toBeGreaterThanOrEqual(6);
  for (let i = 0; i < tn; i++) {
    const b = (await targets.nth(i).boundingBox())!;
    expect(b.height, `target ${i}`).toBeGreaterThanOrEqual(44);
  }
});
