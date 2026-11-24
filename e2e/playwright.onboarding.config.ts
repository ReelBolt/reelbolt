import { defineConfig, devices } from '@playwright/test';

/**
 * Onboarding tour specs. Runs against the local staging stack (same env as the usability specs):
 *
 *   set -a; source ~/.config/reelbolt/local-staging.env; set +a
 *   cd e2e && npx playwright test -c playwright.onboarding.config.ts --workers=1
 *
 * Needs LOCAL_STAGING_URL, LOCAL_STAGING_EMAIL and LOCAL_STAGING_PASSWORD. Loopback only. The specs fake
 * `/api/v1/onboarding*` in the browser, so the account's real onboarding state is never changed and nothing
 * is spent. Artifacts go under the initiative scratch folder.
 */
// Harness mode (ONBOARDING_E2E_HARNESS=1): Playwright starts this worktree's web app with `next dev` and the
// spec fakes /api/** with fixtures (no stack, no login). The port comes from AIFORGE_PORT_0 (testrun --ports 1).
const HARNESS = process.env.ONBOARDING_E2E_HARNESS === '1';
const HARNESS_PORT = Number(process.env.AIFORGE_PORT_0 ?? 3292);
const BASE_URL = (HARNESS ? `http://localhost:${HARNESS_PORT}` : (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080')).replace(/\/+$/, '');
const host = new URL(BASE_URL).hostname;
if (host !== 'localhost' && host !== '127.0.0.1') throw new Error('onboarding: loopback targets only');

export default defineConfig({
  testDir: './tests/onboarding',
  outputDir: process.env.ONBOARDING_E2E_OUT ?? '../../_scratch/web/e2e-results',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 180_000,
  reporter: 'list',
  webServer: HARNESS
    ? {
        // The production build's standalone server (output: 'standalone'), after `npm run build` in ../web with
        // .next/static and public copied beside it; ONBOARDING_E2E_SERVER=dev uses next dev instead.
        command:
          process.env.ONBOARDING_E2E_SERVER === 'dev'
            ? `npx next dev --turbopack -p ${HARNESS_PORT}`
            : `env PORT=${HARNESS_PORT} HOSTNAME=127.0.0.1 node server.js`,
        cwd: process.env.ONBOARDING_E2E_SERVER === 'dev' ? '../web' : '../web/.next/standalone',
        url: `${BASE_URL}/app/login`,
        reuseExistingServer: false,
        timeout: 240_000,
      }
    : undefined,
  use: { baseURL: BASE_URL, ignoreHTTPSErrors: true, trace: 'off', screenshot: 'off' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1400, height: 900 } } },
    { name: 'tablet', use: { ...devices['Desktop Chrome'], viewport: { width: 820, height: 1180 }, hasTouch: true } },
    { name: 'pixel7', use: { ...devices['Pixel 7'], browserName: 'chromium', hasTouch: true } },
    { name: 'iphone14webkit', use: { ...devices['iPhone 14'], browserName: 'webkit' } },
  ],
});
