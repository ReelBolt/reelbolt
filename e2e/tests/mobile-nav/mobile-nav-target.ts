import { devices, type Browser, type BrowserContext } from '@playwright/test';

/**
 * Shared target for the mobile-nav specs. BEFORE = staging's own web (LOCAL_STAGING_URL, default :28080).
 * AFTER = set MOBILE_NAV_LOCAL_WEB to a `next start` of this worktree: GET /app/** is served from it while
 * nginx keeps answering /api/v1 and owns the cookies.
 */
export const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');
export const LOCAL_WEB = process.env.MOBILE_NAV_LOCAL_WEB?.replace(/\/+$/, '');
export const TARGET = LOCAL_WEB ? 'after' : 'before';

export async function routeLocalWeb(ctx: BrowserContext): Promise<void> {
  if (!LOCAL_WEB) return;
  await ctx.route(`${BASE}/app/**`, async (route) => {
    const u = new URL(route.request().url());
    if (route.request().method() !== 'GET') return route.continue();
    // The service worker keeps fetching (e.g. /app/offline) while a context closes; swallow those teardown races.
    try {
      const res = await route.fetch({ url: `${LOCAL_WEB}${u.pathname}${u.search}`, maxRedirects: 0 });
      await route.fulfill({ response: res });
    } catch {
      await route.abort().catch(() => undefined);
    }
  });
}

export async function phoneContext(browser: Browser, storageState?: string): Promise<BrowserContext> {
  const ctx = await browser.newContext({ ...devices['Pixel 7'], baseURL: BASE, ...(storageState ? { storageState } : {}) });
  await routeLocalWeb(ctx);
  return ctx;
}

/**
 * The branch build ships a first-run onboarding tour (not on master) whose dim layer swallows burger taps.
 * It is unrelated to the navbar bug, so skip it when it shows up.
 */
export async function dismissTour(page: import('@playwright/test').Page): Promise<void> {
  const tour = page.locator('[data-testid="onboarding-tour"]');
  if (!(await tour.isVisible({ timeout: 2500 }).catch(() => false))) {
    await tour.waitFor({ state: 'visible', timeout: 2500 }).catch(() => undefined);
  }
  if (await tour.isVisible().catch(() => false)) {
    await page.keyboard.press('Escape');
    await tour.waitFor({ state: 'hidden', timeout: 5000 }).catch(() => undefined);
  }
}
