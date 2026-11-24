import { defineConfig, devices } from '@playwright/test';
import { logsDir } from './tests/mobile-nav/logs-dir';

/**
 * Phone navbar across the page lifecycle (initiative mobile-nav-stuck). Asserts the VISIBLE navbar, not the burger.
 *
 *   set -a; source ~/.config/reelbolt/local-staging.env; set +a
 *   cd e2e && npx playwright test -c playwright.mobile-nav.config.ts            # staging's own web build
 *
 * To run this worktree's web/ in front of staging (nginx keeps /api/v1 and the auth cookies):
 *   (cd web && npm ci && npx next build && PORT=3917 npx next start -p 3917)
 *   MOBILE_NAV_LOCAL_WEB=http://localhost:3917 npx playwright test -c playwright.mobile-nav.config.ts
 *
 * Loopback only. Artifacts under a private per-run temp directory (mode 0700; override with MOBILE_NAV_LOGS).
 */
const BASE_URL = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');
const hosts = [BASE_URL, process.env.MOBILE_NAV_LOCAL_WEB ?? BASE_URL].map((u) => new URL(u).hostname);
if (hosts.some((h) => h !== 'localhost' && h !== '127.0.0.1')) throw new Error('mobile-nav: loopback targets only');

const LOGS = logsDir();

export default defineConfig({
  testDir: './tests/mobile-nav',
  outputDir: `${LOGS}/test-results`,
  // Removes the auth-state files (session cookies) of this run once the tests are done.
  globalTeardown: './tests/mobile-nav/global-teardown.ts',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 180_000,
  reporter: 'list',
  use: { baseURL: BASE_URL, ignoreHTTPSErrors: true, trace: 'off', screenshot: 'off' },
  projects: [
    {
      name: 'pixel7',
      use: {
        ...devices['Pixel 7'],
        browserName: 'chromium',
        hasTouch: true,
        isMobile: true,
        // Playwright disables the back/forward cache by default; the bfcache scenarios need it on.
        launchOptions: { ignoreDefaultArgs: ['--disable-back-forward-cache'] },
      },
    },
  ],
});
