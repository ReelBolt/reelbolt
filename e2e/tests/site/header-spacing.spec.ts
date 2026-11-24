/**
 * The marketing header's spacing and its documentation links, per language. Italian labels are longer
 * than English; before this guard the nav ran into the sign-in link ("CONTATTI" touching "ACCEDI", 0px)
 * at every width from 1280px up. Needs only a running site, and judges the signup state that site was
 * built with, so run it against both (NEXT_PUBLIC_SIGNUP_ENABLED=true and false):
 *
 *   BASE_URL=http://localhost:3000 npx playwright test tests/site/header-spacing.spec.ts --project=desktop
 */
import { expect, test } from '@playwright/test';

const BASE_URL = (process.env.BASE_URL || 'http://localhost').replace(/\/+$/, '');
const MIN_GAP_PX = 24;
/** A classic scrollbar narrows the row without moving the 1280px breakpoint, which counts the scrollbar. */
const SCROLLBAR_ROOM_PX = 15;
const LOCALES = [
  { home: '/', docs: '/docs/' },
  { home: '/it', docs: '/docs/it/' },
];

for (const { home, docs } of LOCALES) {
  for (const width of [1280, 1366, 1536, 1920]) {
    test(`${home} at ${width}px keeps the nav clear of the lockup and the sign-in cluster`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.goto(BASE_URL + home);
      const gaps = await page.locator('header').evaluate((header) => {
        const row = header.firstElementChild!;
        const [lockup, nav, cluster] = Array.from(row.children);
        const links = Array.from(nav.children);
        return {
          rightPastPadding:
            cluster.getBoundingClientRect().right -
            (row.getBoundingClientRect().right - parseFloat(getComputedStyle(row).paddingRight)),
          pageOverflow: document.documentElement.scrollWidth - document.documentElement.clientWidth,
          before: links[0].getBoundingClientRect().left - lockup.getBoundingClientRect().right,
          after: cluster.children[0].getBoundingClientRect().left - links[links.length - 1].getBoundingClientRect().right,
        };
      });
      expect(gaps.before).toBeGreaterThanOrEqual(MIN_GAP_PX);
      expect(gaps.after).toBeGreaterThanOrEqual(MIN_GAP_PX);
      expect(gaps.before + gaps.after - 2 * MIN_GAP_PX).toBeGreaterThanOrEqual(SCROLLBAR_ROOM_PX);
      expect(gaps.rightPastPadding).toBeLessThanOrEqual(0.5);
      expect(gaps.pageOverflow).toBeLessThanOrEqual(0);
    });
  }

  for (const width of [320, 768, 1279]) {
    test(`${home} at ${width}px does not overflow the page`, async ({ page }) => {
      await page.setViewportSize({ width, height: 800 });
      await page.goto(BASE_URL + home);
      const overflow = await page.evaluate(() => document.documentElement.scrollWidth - document.documentElement.clientWidth);
      expect(overflow).toBeLessThanOrEqual(0);
    });
  }

  test(`${home} links the guide in its own language from the nav, the mobile menu and the footer`, async ({ page }) => {
    await page.setViewportSize({ width: 1280, height: 800 });
    await page.goto(BASE_URL + home);
    await expect(page.locator(`header nav a[href="${docs}"]`)).toBeVisible();
    await expect(page.locator(`footer a[href="${docs}"]`)).toHaveCount(1);

    await page.setViewportSize({ width: 800, height: 800 });
    await page.locator('header button[aria-controls="mobile-nav"]').click();
    await expect(page.locator(`#mobile-nav a[href="${docs}"]`)).toBeVisible();
  });
}
