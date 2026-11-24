import { defineConfig } from '@playwright/test';

/**
 * Real-WebGL2 smoke test for the compositor's camera-profile input stage and viewing-LUT pass.
 *
 * Standalone: no webServer, no docker stack, no network. One worker, one headless Chromium; the
 * harness is bundled from `web/lib/editing/**` by `tests/gl-compositor/build-bundle.mjs` into /tmp
 * and injected into a blank page. SwiftShader gives a deterministic software GL.
 *
 *   node e2e/node_modules/@playwright/test/cli.js test -c e2e/playwright.gl-compositor.config.ts
 */
export default defineConfig({
  testDir: './tests/gl-compositor',
  outputDir: '/tmp/gl-compositor-results',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout: 120_000,
  expect: { timeout: 10_000 },
  reporter: [['list']],
  projects: [
    {
      name: 'chromium-gl',
      use: {
        browserName: 'chromium',
        headless: true,
        launchOptions: {
          args: [
            '--use-angle=swiftshader',
            '--use-gl=angle',
            '--enable-unsafe-swiftshader',
            '--ignore-gpu-blocklist',
          ],
        },
      },
    },
  ],
});
