import { defineConfig, devices } from '@playwright/test';

/**
 * Project page tab strip (initiative project-tabs-phone-overlap). Needs a seeded stack:
 *
 *   source e2e/.stack/<project>/env.sh   # E2E_STACK_DIR, BASE_URL (loopback only)
 *   cd e2e && npx playwright test -c playwright.project-tabs.config.ts
 *
 * Creates a scratch project through the API and deletes it afterwards. Screenshots: PROJECT_TABS_SHOTS (optional).
 */
const BASE_URL = (process.env.BASE_URL ?? 'http://localhost').replace(/\/+$/, '');
const host = new URL(BASE_URL).hostname;
if (host !== 'localhost' && host !== '127.0.0.1') throw new Error('project-tabs: loopback targets only');

export default defineConfig({
  testDir: './tests/project-tabs',
  outputDir: '/tmp/project-tabs/test-results',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120_000,
  reporter: 'list',
  use: { baseURL: BASE_URL, ignoreHTTPSErrors: true, trace: 'off', screenshot: 'off' },
  projects: [
    { name: 'phone390', use: { ...devices['Pixel 7'], browserName: 'chromium', viewport: { width: 390, height: 844 }, hasTouch: true } },
    { name: 'phone390webkit', use: { ...devices['iPhone 13'], browserName: 'webkit', viewport: { width: 390, height: 844 } } },
    { name: 'tablet820', use: { ...devices['Desktop Chrome'], viewport: { width: 820, height: 1180 } } },
    { name: 'desktop1440', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
  ],
});
