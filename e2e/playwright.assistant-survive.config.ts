import { defineConfig, devices } from '@playwright/test';

/**
 * The assistant survives a screen change on a phone.
 *
 * Like `playwright.assistant.config.ts` this points at a production Next.js server started by hand from the worktree
 * (`cd web && npx next build && npx next start -p 3210`; see the spec header), not at the graded nginx stack. The spec mocks the assistant API with a
 * small in-process server (see the spec), so it needs no inference backend, no provider and no database.
 */
const PORT = process.env.ASSISTANT_WEB_PORT ?? '3210';
const BASE_URL = process.env.ASSISTANT_BASE_URL ?? `http://127.0.0.1:${PORT}`;

export default defineConfig({
  testDir: './tests',
  testMatch: /assistant-survive\.spec\.ts/,
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 90_000,
  expect: { timeout: 15_000 },
  reporter: [['list']],
  outputDir: 'test-results/assistant-survive',
  use: { baseURL: BASE_URL, trace: 'off', video: 'off', screenshot: 'off' },
  projects: [{ name: 'mobile', use: { ...devices['Pixel 5'], viewport: { width: 390, height: 844 } } }],
});
