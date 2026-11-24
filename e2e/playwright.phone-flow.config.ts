import { defineConfig, devices } from '@playwright/test';

/**
 * Phone usability pass of the core flow (initiative launch-reliability, T-004). Runs against the local staging stack:
 *
 *   set -a; source ~/.config/reelbolt/local-staging.env; set +a
 *   cd e2e && npx playwright test -c playwright.phone-flow.config.ts --workers=1
 *
 * Needs LOCAL_STAGING_URL (e.g. http://localhost:28080), LOCAL_STAGING_EMAIL, LOCAL_STAGING_PASSWORD and
 * LOCAL_STAGING_CEO_PROJECT (a project that already has a completed run). Loopback only. Never presses Run on
 * anything that spends credits. Artifacts (screenshots, traces) go to /tmp/launch-reliability/phone/ only.
 */
const BASE_URL = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');
const host = new URL(BASE_URL).hostname;
if (host !== 'localhost' && host !== '127.0.0.1') throw new Error('phone-flow: loopback targets only');

export default defineConfig({
  testDir: './tests/phone-flow',
  outputDir: '/tmp/launch-reliability/phone/test-results',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 300_000,
  reporter: 'list',
  use: { baseURL: BASE_URL, ignoreHTTPSErrors: true, trace: 'off', screenshot: 'off' },
  projects: [
    { name: 'pixel7', use: { ...devices['Pixel 7'], browserName: 'chromium', hasTouch: true } },
    { name: 'iphone14webkit', use: { ...devices['iPhone 14'], browserName: 'webkit' } },
  ],
});
