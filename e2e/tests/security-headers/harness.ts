/**
 * Real-flow replay of the browser security headers, THROUGH nginx, in a real browser (initiative
 * security-headers, T-002). Each test is one flow; it asserts that the flow produced no CSP-Report-Only
 * violation, no COEP-Report-Only issue and no response blocked by CORP/COEP/XFO beyond
 * allowlist.json. All findings (allowed or not) are appended to $SECHDR_OUT/findings.json, and every
 * third-party origin a flow reached to $SECHDR_OUT/externals.json, for the report.
 *
 * Stack: see playwright.security-headers.config.ts. Needs the marketing site (profile e2e-site).
 */
import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';
import { loadStack, loginAs } from '../../fixtures/stack';
import { Collector, isAllowed, loadAllowlist } from './collector';

const OUT = process.env.SECHDR_OUT ?? '/tmp/sechdr';
const collector = new Collector();
const allow = loadAllowlist();

fs.mkdirSync(OUT, { recursive: true });

/** Runs one flow with a fresh context; asserts nothing un-allowlisted was reported. */
export function flow(name: string, body: (page: Page, base: string) => Promise<void>, opts: { login?: 'admin' | 'creator' | 'trial' | 'team' } = {}) {
  test(name, async ({ browser }) => {
    const { baseUrl } = loadStack();
    const context = await browser.newContext({ baseURL: baseUrl, viewport: { width: 1360, height: 900 } });
    collector.flow = name;
    await collector.attach(context, baseUrl);
    const page = await context.newPage();
    collector.watch(page);
    page.setDefaultTimeout(20_000);
    page.setDefaultNavigationTimeout(45_000);
    const before = collector.findings.length;
    try {
      if (opts.login) await loginAs(context, opts.login);
      await body(page, baseUrl);
      await page.waitForTimeout(1500);
    } finally {
      fs.writeFileSync(path.join(OUT, `findings-${name.replace(/[^a-z0-9]+/gi, '_')}.json`), JSON.stringify(collector.findings.slice(before), null, 1));
      fs.writeFileSync(path.join(OUT, `externals-${name.replace(/[^a-z0-9]+/gi, '_')}.json`), JSON.stringify(collector.externals.filter((x) => x.flow === name), null, 1));
      await context.close();
    }
    const bad = collector.findings.slice(before).filter((f) => !isAllowed(f, allow));
    expect(bad, JSON.stringify(bad, null, 1)).toEqual([]);
  });
}


export const settle = (page: Page) => page.waitForLoadState('networkidle', { timeout: 20_000 }).catch(() => undefined);
export async function acceptCookies(page: Page) {
  const b = page.getByRole('button', { name: /^accept/i }).first();
  if (await b.isVisible().catch(() => false)) await b.click();
}

/** Attaches the collector to a second (guest) context opened inside a flow. */
export async function attachGuest(context: import('@playwright/test').BrowserContext, page: Page, base: string): Promise<void> {
  await collector.attach(context, base);
  collector.watch(page);
}
