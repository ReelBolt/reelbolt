import { defineConfig, devices } from '@playwright/test';

/**
 * Soft-open "signup switch" rehearsal (initiative launch-soft-open). Runs against an ISOLATED stack started with
 * `up-isolated.sh --provider stripe` and E2E_PROFILES=e2e-site,e2e-engine; see e2e/README.md "Soft-open rehearsal".
 *
 *   SOFT_OPEN_PHASE=off  negative control (signup switches off: API 404, site CTA "Talk to us")
 *   SOFT_OPEN_PHASE=on   the flow (default): CTA, signup, verify, terms, first video, Stripe upgrade
 *
 * One worker, serial: the flow shares one account and the per-IP signup cap (3/hour) is counted on the stack.
 * Traces, screenshots and the report go to /tmp/launch-soft-open-rehearsal/ (SOFT_OPEN_ARTIFACTS to move them).
 */
const artifacts = process.env.SOFT_OPEN_ARTIFACTS || '/tmp/launch-soft-open-rehearsal';

export default defineConfig({
  testDir: './tests/soft-open',
  outputDir: `${artifacts}/pw-output`,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: !!process.env.CI,
  timeout: 180_000,
  reporter: [['list'], ['json', { outputFile: `${artifacts}/report-${process.env.SOFT_OPEN_PHASE || 'on'}.json` }]],
  use: {
    baseURL: process.env.BASE_URL,
    ...devices['Desktop Chrome'],
    viewport: { width: 1280, height: 800 },
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
});
