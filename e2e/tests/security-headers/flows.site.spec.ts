/**
 * Real-flow replay of the browser security headers, THROUGH nginx, in a real browser (initiative
 * security-headers, T-002). Each test is one flow; it asserts that the flow produced no CSP-Report-Only
 * violation, no COEP-Report-Only issue and no response blocked by CORP/COEP/XFO beyond
 * allowlist.json. All findings (allowed or not) are appended to $SECHDR_OUT/findings.json, and every
 * third-party origin a flow reached to $SECHDR_OUT/externals.json, for the report.
 *
 * Stack: see playwright.security-headers.config.ts. Needs the marketing site (profile e2e-site).
 */
import { test } from '@playwright/test';
import { flow, settle, acceptCookies } from './harness';

test.describe('marketing site', () => {
  flow('site: home, consent accept (analytics)', async (page) => {
    await page.goto('/');
    await settle(page);
    await acceptCookies(page);
    await page.waitForTimeout(3000);
    await page.mouse.wheel(0, 3000);
    await page.waitForTimeout(1500);
  });
  flow('site: features, pricing, about, examples', async (page) => {
    for (const p of ['/features', '/pricing', '/about', '/examples']) {
      await page.goto(p);
      await settle(page);
    }
  });
  flow('site: blog index and a post', async (page) => {
    await page.goto('/blog');
    await settle(page);
    await page.locator('a[href^="/blog/"]:not([href*="tag"])').first().click();
    await settle(page);
  });
  flow('site: /for/creators', async (page) => {
    await page.goto('/for/creators');
    await settle(page);
  });
  flow('site: recipes gallery and a recipe', async (page) => {
    await page.goto('/recipes');
    await settle(page);
    // The gallery is empty on a fresh stack: open a recipe only when one is listed.
    const recipe = page.locator('a[href^="/recipes/"]').first();
    if (await recipe.isVisible({ timeout: 3000 }).catch(() => false)) {
      await recipe.click();
      await settle(page);
    }
  });
  flow('site: contact form submit', async (page) => {
    await page.goto('/contact');
    await settle(page);
    await page.locator('#name').fill('Header Probe');
    await page.locator('#email').fill('sechdr@example.com');
    await page.locator('#message').fill('Security header probe, please ignore.');
    await page.getByRole('button', { name: /send|submit/i }).first().click();
    await page.waitForTimeout(3000);
  });
  flow('site: newsletter footer signup', async (page) => {
    await page.goto('/');
    await settle(page);
    const form = page.locator('footer form').first();
    await form.locator('input[name="email"]').fill('sechdr-news@example.com');
    await form.getByRole('button').first().click();
    await page.waitForTimeout(3000);
  });
  flow('site: /it/ page', async (page) => {
    await page.goto('/it');
    await settle(page);
    await page.goto('/it/features');
    await settle(page);
  });
  flow('site: legal pages', async (page) => {
    await page.goto('/legal/privacy');
    await settle(page);
  });
  // Needs the docs site: it is off in the isolated stack. Build it from docs-site/ (see the report) and attach it to the stack
  // network with the alias `docs`; the flow skips itself when /docs/ answers 502.
  flow('docs: landing, a guide page, search box', async (page) => {
    const res = await page.goto('/docs/');
    test.skip(res?.status() === 502, 'docs service not running in this stack');
    await settle(page);
    await page.locator('a[href^="/docs/"]').nth(3).click().catch(() => undefined);
    await settle(page);
    const search = page.getByRole('button', { name: /search/i }).first();
    if (await search.isVisible().catch(() => false)) await search.click();
    await page.waitForTimeout(1500);
  });
});
