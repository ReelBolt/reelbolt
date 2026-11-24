import { defineConfig, devices } from '@playwright/test';
import { SMOKE_JWT_KEY } from './tests/error-states/fixtures';

/**
 * Failure-state matrix and before/after screenshots. Needs no docker stack: it serves the BUILT web
 * app (`next start`, basePath /app) and mocks every /api/v1/* call with page.route fixtures.
 *
 *   ES_WEB_DIR=../web ES_PORT=3200 npx playwright test -c playwright.error-states.config.ts matrix
 *   ES_WEB_DIR=<base>/web ES_PORT=3201 ES_SHOTS=before npx playwright test -c playwright.error-states.config.ts screenshots
 *   ES_SHOTS=after ... screenshots   (writes under ES_SHOTS_DIR)
 */
const port = Number(process.env.ES_PORT ?? process.env.AIFORGE_PORT_0 ?? 3200);

export default defineConfig({
  testDir: './tests/error-states',
  testMatch: /\.spec\.ts/,
  fullyParallel: true,
  workers: Number(process.env.ES_WORKERS ?? 4),
  retries: 0,
  timeout: 60_000,
  reporter: 'list',
  use: {
    baseURL: `http://localhost:${port}`,
    trace: 'off',
  },
  projects: [{ name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } } }],
  webServer: {
    command: `npx next start -p ${port}`,
    cwd: process.env.ES_WEB_DIR ?? '../web',
    url: `http://localhost:${port}/app/login`,
    reuseExistingServer: false,
    timeout: 120_000,
    env: { WORKFLOW_APPS_ENABLED: 'true', NODE_ENV: 'production', JWT_SIGNING_KEY: SMOKE_JWT_KEY },
  },
});
