import { defineConfig, devices } from '@playwright/test';
/**
 * Run-page "Request changes" flow. Needs no docker stack: it serves the BUILT web app (`next start`, basePath /app)
 * and mocks every /api/v1/* call with page.route fixtures (same pattern as playwright.workflow-apps.config.ts).
 *
 *   npx playwright test -c playwright.change-requests.config.ts --workers=2
 */
const port = Number(process.env.CR_PORT ?? 3101);
export default defineConfig({
  testDir: './tests/change-requests',
  testMatch: /\.spec\.ts$/,
  fullyParallel: true,
  workers: 2,
  retries: 0,
  reporter: 'list',
  use: { baseURL: `http://localhost:${port}`, trace: 'off' },
  projects: [
    { name: 'desktop', use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } } },
    { name: 'pixel7', use: { ...devices['Pixel 7'] } },
  ],
  webServer: {
    command: `npx next start -p ${port}`,
    cwd: process.env.CR_WEB_DIR ?? '../web',
    url: `http://localhost:${port}/app/login`,
    reuseExistingServer: false,
    timeout: 120_000,
    env: { NODE_ENV: 'production' },
  },
});
