import { defineConfig, devices } from '@playwright/test';

/**
 * Google Consent Mode proof for the marketing site (initiative ga-consent-mode). Needs no stack: it
 * drives a production build of `/site` made with a throwaway measurement ID.
 *
 *   cd site && NEXT_PUBLIC_GA_MEASUREMENT_ID=G-TESTCONSENT1 npm run build && npx next start -p 3192
 *   cd e2e && GA_CONSENT_BASE_URL=http://127.0.0.1:3192 npx playwright test -c playwright.site-consent.config.ts
 *
 * The real gtag.js is downloaded from Google (no stub), so the cookie assertions describe the real
 * tag. Every request it makes to a Google collection endpoint is answered locally with a 204 and
 * recorded; nothing is sent to Google's property.
 */
export default defineConfig({
  testDir: './tests/site-consent',
  fullyParallel: true,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: process.env.GA_CONSENT_BASE_URL,
    ...devices['Desktop Chrome'],
    viewport: { width: 1280, height: 800 },
  },
});
