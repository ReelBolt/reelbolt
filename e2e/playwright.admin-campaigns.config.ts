import { defineConfig, devices } from '@playwright/test';

/**
 * QA run of /app/admin/campaigns against an isolated stack (E2E_PROJECT=reelbolt-brevo-qa) with a local fake Brevo.
 *   e2e/brevo-mock/run-phase.sh <A|B|C|D|F|R> (see e2e/README.md, Brevo campaigns)
 * Loopback only. Artifacts go to QA_OUT.
 */
const BASE_URL = (process.env.BASE_URL ?? 'http://localhost:8731').replace(/\/+$/, '');
const host = new URL(BASE_URL).hostname;
if (host !== 'localhost' && host !== '127.0.0.1') throw new Error('admin-campaigns: loopback targets only');
const OUT = process.env.QA_OUT ?? '/tmp/admin-campaigns-qa';

export default defineConfig({
  testDir: './tests/admin-campaigns',
  outputDir: `${OUT}/test-results`,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120_000,
  reporter: 'list',
  use: { baseURL: BASE_URL, trace: 'off', screenshot: 'off' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } },
    { name: 'phone', use: { ...devices['Desktop Chrome'], viewport: { width: 390, height: 844 }, isMobile: true, hasTouch: true } },
  ],
});
