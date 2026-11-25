import { defineConfig, devices } from '@playwright/test';

/**
 * Real-flow harvest of the browser security headers (initiative security-headers). Needs an isolated stack
 * with the marketing site:
 *
 *   E2E_PROJECT=reelbolt-sechdr APP_PORT=8412 HTTPS_PORT=9412 PADDLE_MOCK_PORT=28412 E2E_PROFILES=e2e-site \
 *     NEXT_PUBLIC_GA_MEASUREMENT_ID=G-TEST123 e2e/scripts/up-isolated.sh
 *   source e2e/.stack/reelbolt-sechdr/env.sh
 *   cd e2e && npx playwright test -c playwright.security-headers.config.ts
 *
 * tests/security-headers/reporting.spec.ts drives the stack's HTTPS port (SECHDR_HTTPS_URL, default https://localhost:9412):
 * Chrome only delivers Reporting API reports from an HTTPS origin, about a minute after the violation.
 *
 * Every flow asserts zero CSP / COEP / blocked-response findings that are not in
 * tests/security-headers/allowlist.json; the full list is written to SECHDR_OUT (default /tmp/sechdr).
 */
export default defineConfig({
  testDir: './tests/security-headers',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 180_000,
  reporter: 'list',
  use: { ...devices['Desktop Chrome'], viewport: { width: 1360, height: 900 }, serviceWorkers: 'allow' },
});
