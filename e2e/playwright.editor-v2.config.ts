import { defineConfig, devices } from '@playwright/test';
import { ignoreHTTPSErrorsFor } from './fixtures/editor-v2/env';

/**
 * Editor v2 regression suite (initiative editor-v2-remediation, T-001; audit report.md §3, plan §6.3).
 *
 * Separate from `playwright.config.ts`: this suite runs against the isolated stack started by
 * `e2e/scripts/editor-v2-stack.sh up` (compose project `reelbolt-editor-remediation`, nginx on
 * http://localhost:18080) and seeds everything it needs through the REST API.
 *
 *   e2e/scripts/editor-v2-stack.sh up
 *   eval "$(e2e/scripts/editor-v2-stack.sh env)"      # local admin login, never committed
 *   cd e2e && npm run test:editor-v2                   # all projects
 *   cd e2e && npm run test:editor-v2 -- --project=desktop tests/editor-v2/guards
 *
 * Read-only smoke against a deployed box (writes nothing; credentials only from the environment):
 *   EDITOR_E2E_READONLY=1 EDITOR_E2E_BASE_URL=https://staging.example.com \
 *   EDITOR_E2E_EMAIL=... EDITOR_E2E_PASSWORD=... npx playwright test -c playwright.editor-v2.config.ts
 *
 * Every artifact (traces, screenshots, the session storage state) goes under /tmp/editor-remediation/
 * ONLY: traces and storage state carry the session cookie, so they never land in the repository.
 */

/* Marks the run so the suite's fixture can skip itself under the shared playwright.config.ts (testDir ./tests). */
process.env.EDITOR_E2E_SUITE = '1';

const READONLY = process.env.EDITOR_E2E_READONLY === '1';
const BASE_URL = (process.env.EDITOR_E2E_BASE_URL ?? 'http://localhost:18080').replace(/\/+$/, '');
const ARTIFACTS = '/tmp/editor-remediation';

/** Headless Chromium needs SwiftShader for the WebGL2 program monitor. */
const CHROMIUM_ARGS = ['--enable-unsafe-swiftshader', '--ignore-gpu-blocklist', '--autoplay-policy=no-user-gesture-required'];

export default defineConfig({
  testDir: './tests/editor-v2',
  grep: READONLY ? /@smoke-readonly/ : undefined,
  grepInvert: READONLY ? undefined : /@smoke-readonly/,
  globalSetup: './fixtures/editor-v2/global-setup.ts',
  outputDir: `${ARTIFACTS}/test-results`,
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  /* No retries: a retry turns a reproducible failure into a green tick, and expected-fail rows must fail. */
  retries: 0,
  workers: Number(process.env.EDITOR_E2E_WORKERS ?? 2),
  timeout: 120_000,
  expect: { timeout: 15_000 },
  reporter: [['list'], ['json', { outputFile: `${ARTIFACTS}/report-${READONLY ? 'readonly' : 'local'}.json` }]],
  use: {
    baseURL: BASE_URL,
    /* Only for http:// or loopback (security TH-1): the deployed read-only target keeps TLS validation. */
    ignoreHTTPSErrors: ignoreHTTPSErrorsFor(BASE_URL),
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
    actionTimeout: 15_000,
    navigationTimeout: 45_000,
  },
  projects: [
    {
      name: 'desktop',
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1440, height: 900 },
        deviceScaleFactor: 1,
        launchOptions: { args: CHROMIUM_ARGS },
      },
    },
    {
      // Real touch: specs drive `page.touchscreen` and CDP `Input.dispatchTouchEvent`.
      name: 'pixel7',
      use: {
        ...devices['Pixel 7'],
        browserName: 'chromium',
        hasTouch: true,
        launchOptions: { args: CHROMIUM_ARGS },
      },
    },
    {
      // RM.15 (decision D2: "works, not certified"): an iPad-like 1024x768 touch device on the desktop layout with
      // a coarse pointer. Specs switch to 820x1180 (portrait) with page.setViewportSize. Runs 30-phone/tablet and the Wave 2 journeys (20-journeys).
      name: 'tablet',
      testMatch: /(30-phone\/tablet\.spec|20-journeys\/.*\.spec|31-simple\/.*\.spec)\.ts/,
      use: {
        ...devices['Desktop Chrome'],
        viewport: { width: 1024, height: 768 },
        hasTouch: true,
        isMobile: true,
        deviceScaleFactor: 2,
        launchOptions: { args: CHROMIUM_ARGS },
      },
    },
    {
      // Real WebKit with the iPhone 14 profile. Playwright WebKit cannot drag by touch: specs that need a
      // real touch drag are skipped here with the annotation "synthetic pointers" (audit D8: real iOS
      // gesture takeover is an accepted unverified risk).
      name: 'iphone14webkit',
      use: { ...devices['iPhone 14'], browserName: 'webkit' },
    },
  ],
});
