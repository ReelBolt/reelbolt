import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

const surface = (page: import('@playwright/test').Page) => page.locator('[data-editor-surface]');

test('T026.VF13 density: Compact shrinks the controls, is remembered after a reload, and Comfortable brings them back', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  const controlH = () => surface(page).evaluate((el) => getComputedStyle(el).getPropertyValue('--rb-control-h').trim());
  await expect(surface(page)).toHaveAttribute('data-ed-density', 'comfortable');
  expect(await controlH()).toBe('36px');
  const iconH = () => surface(page).evaluate((el) => getComputedStyle(el).getPropertyValue('--rb-icon-h').trim());
  expect(await iconH()).toBe('32px');

  await ed.tap(ed.testId('display-menu'));
  await page.locator('label', { hasText: /^Compact$/ }).click();
  await expect(surface(page)).toHaveAttribute('data-ed-density', 'compact');
  expect(await controlH()).toBe('28px');
  // A real header icon button follows the token (the keyboard-shortcuts button is a 28 px square now).
  await page.keyboard.press('Escape');
  const shortcuts = await ed.testId('open-keymap').boundingBox();
  expect(Math.round(shortcuts!.height), 'the header icon button is 28 px tall in compact').toBe(28);

  await ed.reload();
  await expect(surface(page), 'compact is remembered after a reload').toHaveAttribute('data-ed-density', 'compact');

  await ed.tap(ed.testId('display-menu'));
  await page.locator('label', { hasText: /^Comfortable$/ }).click();
  await expect(surface(page)).toHaveAttribute('data-ed-density', 'comfortable');
  await page.keyboard.press('Escape');
  const roomier = await ed.testId('open-keymap').boundingBox();
  expect(Math.round(roomier!.height), 'comfortable icon buttons are 32 px tall').toBe(32);
});

test('T026.VF13 a touch screen forces comfortable controls with 44 px targets even when Compact is saved', async ({ page, freshTimeline, openEditor, seed }) => {
  runOn('pixel7');
  const id = await freshTimeline('seeded');
  // Save Compact first (the way the menu does), then open the editor on the phone.
  await page.goto('/app');
  await page.evaluate(() => {
    for (let i = 0; i < localStorage.length; i++) {
      const k = localStorage.key(i);
      if (k?.startsWith('reelbolt:editor:display')) localStorage.removeItem(k);
    }
  });
  void seed;
  const ed = await openEditor(id);
  // T-006: the key carries the hashed scope, never the email. Find the one the app uses, or derive it the same way.
  const key = await page.evaluate(async () => {
    const existing = Object.keys(localStorage).find((k) => k.startsWith('reelbolt:editor:display'));
    if (existing) return existing;
    const raw = /(?:^|;\s*)reelbolt_user=([^;]+)/.exec(document.cookie)?.[1];
    let email = 'anonymous';
    if (raw) {
      try {
        const user = JSON.parse(decodeURIComponent(raw.replace(/\+/g, '%20'))) as { email?: string };
        email = (user.email ?? '').trim().toLowerCase() || 'anonymous';
      } catch {
        email = 'anonymous';
      }
    }
    const digest = await crypto.subtle.digest('SHA-256', new TextEncoder().encode(email));
    const hex = [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
    return `reelbolt:editor:display:${hex.slice(0, 16)}`;
  });
  await page.evaluate((k) => localStorage.setItem(k, JSON.stringify({ density: 'compact', surround: 'match' })), key);
  await ed.reload();
  await expect(surface(page)).toHaveAttribute('data-ed-touch', 'true');
  await expect(surface(page), 'a finger always gets comfortable').toHaveAttribute('data-ed-density', 'comfortable');
  const h = await surface(page).evaluate((el) => getComputedStyle(el).getPropertyValue('--rb-control-h').trim());
  expect(h).toBe('44px');
  const icon = await surface(page).evaluate((el) => getComputedStyle(el).getPropertyValue('--rb-icon-h').trim());
  expect(icon, 'icon buttons are 44 px on a touch screen').toBe('44px');
});

test('T026.VF18 Always dark keeps the editor dark while the app is light, is remembered, and gives the app scheme back', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  const scheme = () => page.locator('html').getAttribute('data-mantine-color-scheme');
  const headerBg = () => page.locator('header').filter({ has: ed.testId('open-keymap') }).evaluate((el) => getComputedStyle(el).backgroundColor);
  // Playwright's browser prefers light: the app starts light unless a scheme was saved.
  expect(await scheme()).not.toBe('dark');
  const lightBg = await headerBg();

  await ed.tap(ed.testId('display-menu'));
  await page.locator('label', { hasText: /^Always dark$/ }).click();
  await expect.poll(scheme, 'the editor goes dark').toBe('dark');
  await expect(surface(page)).toHaveAttribute('data-ed-surround', 'dark');
  await page.keyboard.press('Escape');
  const darkBg = await headerBg();
  expect(darkBg, 'the editor header is recoloured').not.toBe(lightBg);
  // A dark header: its red channel is below 64.
  expect(Number(darkBg.match(/\d+/)![0]), `header ${darkBg} is dark`).toBeLessThan(64);

  await ed.reload();
  await expect.poll(scheme, 'dark surround is remembered after a reload').toBe('dark');
  await expect(surface(page)).toHaveAttribute('data-ed-surround', 'dark');

  await ed.tap(ed.testId('display-menu'));
  await page.locator('label', { hasText: /^Match the app$/ }).click();
  await expect.poll(scheme, 'matching the app gives the light scheme back').not.toBe('dark');
});
