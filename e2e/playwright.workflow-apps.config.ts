import { defineConfig, devices } from '@playwright/test';
import { SMOKE_JWT_KEY } from './tests/workflow-apps/fixtures';

/**
 * Smoke for the workflow-apps gallery (PR #544). Needs no docker stack: it serves the BUILT web app
 * (`next start`, basePath /app) and mocks every /api/v1/* call with page.route fixtures.
 *
 *   WA_FLAG=on  npx playwright test -c playwright.workflow-apps.config.ts --workers=2
 *   WA_FLAG=off npx playwright test -c playwright.workflow-apps.config.ts --workers=2
 *
 * One server at a time: the flag is chosen per run, never two instances side by side.
 */
const flag = process.env.WA_FLAG === 'off' ? 'off' : 'on';
const port = Number(process.env.WA_PORT ?? 3100);

export default defineConfig({
  testDir: './tests/workflow-apps',
  testMatch: flag === 'on' ? /(gallery|run-page|workflow-page|layout)\.spec\.ts/ : /(flag-off|run-page|workflow-page|layout)\.spec\.ts/,
  fullyParallel: true,
  workers: 2,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: `http://localhost:${port}`,
    trace: 'off',
  },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } } },
    { name: 'pixel7', use: { ...devices['Pixel 7'] } },
  ],
  webServer: {
    command: `npx next start -p ${port}`,
    cwd: process.env.WA_WEB_DIR ?? '../web',
    url: `http://localhost:${port}/app/login`,
    reuseExistingServer: false,
    timeout: 120_000,
    env: { WORKFLOW_APPS_ENABLED: flag === 'on' ? 'true' : 'false', NODE_ENV: 'production', JWT_SIGNING_KEY: SMOKE_JWT_KEY },
  },
});
