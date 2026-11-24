import { defineConfig, devices } from '@playwright/test';

/**
 * Specs that need a stack started with `e2e/scripts/up-isolated.sh --provider stripe` (the Stripe fake as
 * the active billing provider). They have projects of their own and every other project leaves them out;
 * each of them also skips itself when the stack under test is not a Stripe one.
 */
const STRIPE_SPECS = /billing-stripe\/.*\.spec\.ts$/;

/**
 * Read environment variables from file.
 * https://github.com/motdotla/dotenv
 */
// require('dotenv').config();

/**
 * See https://playwright.dev/docs/test-configuration.
 */
// The soft-open rehearsal is serial and flips switches on one stack: only playwright.soft-open.config.ts runs it.
const SOFT_OPEN_SPECS = /[\\/]tests[\\/]soft-open[\\/]/;
// Own config (playwright.mobile-nav.config.ts): loopback guard and the bfcache launch flag live there.
const MOBILE_NAV_SPECS = /[\\/]tests[\\/]mobile-nav[\\/]/;
// Needs the fake Brevo and a phase-specific stack: only playwright.admin-campaigns.config.ts runs it.
const ADMIN_CAMPAIGNS_SPECS = /[\\/]tests[\\/]admin-campaigns[\\/]/;

export default defineConfig({
  testDir: './tests',
  /* Run tests in files in parallel */
  fullyParallel: true,
  /* Fail the build on CI if you accidentally left test.only in the source code. */
  forbidOnly: !!process.env.CI,
  /* Retry on CI only */
  retries: process.env.CI ? 2 : 0,
  /* Opt out of parallel tests on CI. */
  workers: process.env.CI ? 1 : undefined,
  /* Reporter to use. See https://playwright.dev/docs/test-reporters */
  reporter: 'html',
  /* Shared settings for all the projects below. See https://playwright.dev/docs/api/class-testoptions. */
  use: {
    /* Base URL to use in actions like `await page.goto('/')`. */
    baseURL: process.env.BASE_URL || 'http://localhost',
    /* Collect trace when retrying the failed test. See https://playwright.dev/docs/trace-viewer */
    trace: 'on-first-retry',
  },

  /* Configure projects for major browsers */
  projects: [
    {
      name: 'desktop',
      testIgnore: [STRIPE_SPECS, SOFT_OPEN_SPECS, MOBILE_NAV_SPECS, ADMIN_CAMPAIGNS_SPECS],
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
    },

    {
      name: 'mobile',
      testIgnore: [STRIPE_SPECS, SOFT_OPEN_SPECS, MOBILE_NAV_SPECS, ADMIN_CAMPAIGNS_SPECS],
      use: { ...devices['Pixel 5'], viewport: { width: 390, height: 844 } },
    },

    // Stripe billing flows (tests/billing-stripe/): `npx playwright test --project=billing-stripe --workers=2`
    // on a `--provider stripe` stack. Files run in parallel; each makes its own throwaway users.
    {
      name: 'billing-stripe',
      testMatch: STRIPE_SPECS,
      testIgnore: /webhook-lost-lazy\.spec\.ts$/,
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
    },

    // The one Stripe spec that restarts the Inference API container (API answers 502 meanwhile), so it
    // runs alone, after billing-stripe: `npx playwright test --project=billing-stripe-restart --no-deps --workers=1`.
    {
      name: 'billing-stripe-restart',
      testMatch: /billing-stripe\/webhook-lost-lazy\.spec\.ts$/,
      dependencies: ['billing-stripe'],
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
    },
  ],

  /* Run your local dev server before starting the tests */
  // webServer: {
  //   command: 'npm run dev',
  //   url: 'http://127.0.0.1:5173',
  //   reuseExistingServer: !process.env.CI,
  // },
});
