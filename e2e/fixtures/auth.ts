import { test as base, expect, Page, BrowserContext } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const STORAGE_STATE_PATH = path.join(__dirname, '../.auth/user.json');

export const test = base.extend<{ authenticatedPage: Page }>({
  authenticatedPage: async ({ page }, use) => {
    const baseURL = process.env.BASE_URL || 'http://localhost';

    // Check if we already have a stored authentication session
    if (fs.existsSync(STORAGE_STATE_PATH)) {
      // Load existing session
      const storageState = JSON.parse(fs.readFileSync(STORAGE_STATE_PATH, 'utf-8'));
      await page.context().addInitScript(() => {
        window.localStorage.clear();
      });
      await page.context().addCookies(storageState.cookies || []);
      // Set localStorage if it exists in storage state
      if (storageState.origins && storageState.origins.length > 0) {
        for (const origin of storageState.origins) {
          if (origin.localStorage) {
            for (const item of origin.localStorage) {
              await page.evaluate(
                ({ key, value }) => {
                  window.localStorage.setItem(key, value);
                },
                { key: item.name, value: item.value }
              );
            }
          }
        }
      }
    } else {
      // Perform login via API
      const loginResponse = await page.context().request.post(
        `${baseURL}/api/v1/auth/token`,
        {
          data: {
            email: process.env.TEST_EMAIL || 'test@example.com',
            password: process.env.TEST_PASSWORD || 'password123',
          },
        }
      );

      expect(loginResponse.ok()).toBeTruthy();

      // Extract and store the session
      const storageState = await page.context().storageState();
      const authDir = path.dirname(STORAGE_STATE_PATH);

      // Create .auth directory if it doesn't exist
      if (!fs.existsSync(authDir)) {
        fs.mkdirSync(authDir, { recursive: true });
      }

      fs.writeFileSync(STORAGE_STATE_PATH, JSON.stringify(storageState, null, 2));
    }

    await use(page);
  },
});

export { expect };
