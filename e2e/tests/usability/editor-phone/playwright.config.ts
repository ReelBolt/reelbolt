import * as path from 'path';
import { defineConfig } from '@playwright/test';
import base from '../../../playwright.editor-v2.config';

/**
 * Runs only this folder with the editor-v2 stack settings (projects, global setup, artifacts under
 * /tmp/editor-remediation). Usage, from e2e/ with the editor-v2 stack up and its login exported:
 *   npx playwright test -c tests/usability/editor-phone/playwright.config.ts --project=pixel7 --project=iphone14webkit
 */
const e2eRoot = path.resolve(__dirname, '../../..');
export default defineConfig({
  ...base,
  testDir: __dirname,
  globalSetup: path.join(e2eRoot, 'fixtures/editor-v2/global-setup.ts'),
});
