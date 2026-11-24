import { defineConfig, devices } from '@playwright/test';

/**
 * T-QA-03 — the assistant's live-browser suite.
 *
 * Deliberately separate from `playwright.config.ts`: the shared config points at
 * `http://localhost` (the graded stack behind nginx), while this suite's subject is a server
 * started by hand from this worktree on a spare port. `BASE_URL` still wins if it is set, so the
 * suite can be re-pointed without editing anything.
 *
 * `ASSISTANT_BASE_URL` is the knob the spec itself reads; `use.baseURL` is set to the same value
 * so both agree whichever config grabs the file.
 */

const PORT = process.env.ASSISTANT_WEB_PORT ?? '3210';
const BASE_URL = process.env.ASSISTANT_BASE_URL ?? process.env.BASE_URL ?? `http://127.0.0.1:${PORT}`;

export default defineConfig({
  testDir: './tests',
  /* Only this ticket's spec: the shared config's other specs need the real stack behind nginx. */
  testMatch: /assistant-global\.spec\.ts/,
  /* Serial, single worker: the subject is a single `next dev` server that compiles each route on
     first hit, so parallel workers would only contend on that compile rather than measure
     anything real. */
  fullyParallel: false,
  workers: 1,
  /* No retries: a retry can turn a real, reproducible failure into a green tick. */
  retries: 0,
  timeout: 60_000,
  expect: { timeout: 15_000 },
  /* Line reporter: the runner summary this ticket must attach verbatim. */
  reporter: [['list']],
  outputDir: 'test-results/assistant-global',
  use: {
    baseURL: BASE_URL,
    trace: 'off',
    video: 'off',
    screenshot: 'off',
  },
  projects: [
    {
      name: 'desktop',
      use: { ...devices['Desktop Chrome'], viewport: { width: 1280, height: 800 } },
    },
    {
      name: 'mobile',
      use: { ...devices['Pixel 5'], viewport: { width: 390, height: 844 } },
    },
  ],
});
