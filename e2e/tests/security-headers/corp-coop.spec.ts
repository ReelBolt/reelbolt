/**
 * Per-path CORP / XFO / COOP behaviour in a real browser: a page on ANOTHER origin (example.test, served by
 * route interception, the loopback stack is only the target) embeds or loads each path, and a popup test
 * checks that the dashboard keeps its opener (OAuth, checkout) while the marketing site does not.
 * Asserts what the nginx header maps promise; the expectations ARE the per-path findings.
 */
import { test, expect } from '@playwright/test';
import * as http from 'http';
import type { AddressInfo } from 'net';
import { loadStack, user, loginAs } from '../../fixtures/stack';

// The "foreign" origin is a real HTTP server on another loopback port (a different origin). Route-fulfilled pages do
// not work for this: Chrome then refuses the cross-origin loopback request before any CORP check happens.
let server: http.Server;
let foreignOrigin = '';
test.beforeAll(async () => {
  server = http.createServer((_req, res) => {
    res.setHeader('content-type', 'text/html');
    res.end('<!doctype html><title>foreign</title>');
  });
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r));
  foreignOrigin = `http://127.0.0.1:${(server.address() as AddressInfo).port}`;
});
test.afterAll(() => void server.close());

const foreign = () => foreignOrigin + '/';
const target = (base: string) => base;
const popupUrl = () => foreignOrigin + '/popup';

async function foreignPage(browser: import('@playwright/test').Browser, base: string) {
  const context = await browser.newContext({ baseURL: base });
  return { context, page: await context.newPage() };
}

test('CORP: cross-origin loads of every path class (no-cors, as <img>/<script>/fetch)', async ({ browser }) => {
  const { baseUrl } = loadStack();
  const { context, page } = await foreignPage(browser, baseUrl);
  await page.goto(foreign());
  const home = await (await context.request.get(baseUrl + '/')).text();
  const site = home.match(/\/_next\/static\/[^"']+\.js/)![0];
  const app = (await (await context.request.get(baseUrl + '/app/login')).text()).match(/\/app\/_next\/static\/[^"']+\.js/)![0];
  const cases: Array<{ path: string; expectLoad: boolean; why: string }> = [
    { path: '/favicon.ico', expectLoad: true, why: 'public_asset: cross-origin' },
    { path: '/icon.svg', expectLoad: true, why: 'public_asset: cross-origin' },
    { path: site, expectLoad: true, why: 'site_static: cross-origin (CDN-cacheable chunks)' },
    { path: app, expectLoad: true, why: 'app_static: cross-origin' },
    { path: '/health', expectLoad: false, why: 'machine: same-origin' },
    { path: '/robots.txt', expectLoad: false, why: 'site document: same-origin (crawlers do not enforce CORP)' },
  ];
  for (const c of cases) {
    // fetch(no-cors) is the same CORP check an <img>/<video>/<script> tag gets: it rejects when CORP blocks the response.
    const loaded = await page.evaluate((url) => fetch(url, { mode: 'no-cors' }).then(() => true, () => false), target(baseUrl) + c.path);
    expect(loaded, `${c.path}: ${c.why}`).toBe(c.expectLoad);
  }
  await context.close();
});

test('XFO / frame-ancestors: no path of the stack can be framed by another origin', async ({ browser }) => {
  const { baseUrl } = loadStack();
  const { context, page } = await foreignPage(browser, baseUrl);
  await page.goto(foreign());
  const refusals: string[] = [];
  page.on('console', (m) => /Refused to display .* in a frame/.test(m.text()) && refusals.push(m.text()));
  for (const p of ['/', '/app/login', '/features']) {
    const before = refusals.length;
    await page.evaluate((url) => {
      const f = document.createElement('iframe');
      f.src = url;
      document.body.append(f);
    }, target(baseUrl) + p);
    // Chrome logs "Refused to display '<url>' in a frame because it set 'X-Frame-Options' to 'deny'" for a refused frame.
    await expect.poll(() => refusals.length, { timeout: 10_000, message: `${p} must be refused in a foreign frame (XFO DENY + frame-ancestors 'none')` }).toBeGreaterThan(before);
    await page.evaluate(() => document.querySelector('iframe')!.remove());
  }
  await context.close();
});

test('tracked links stay shareable: navigating from another origin to /l/<code> is not blocked by CORP', async ({ browser }) => {
  const { baseUrl } = loadStack();
  const { context, page } = await foreignPage(browser, baseUrl);
  await page.goto(foreign());
  const res = await page.goto(baseUrl + '/l/abc');
  expect(res?.status(), 'the redirect is followed to a rendered page').toBeLessThan(400);
  expect(page.url()).not.toMatch(/^chrome-error:/);
  await context.close();
});

test('COOP: the dashboard keeps the opener of a popup (OAuth, checkout); the site severs it', async ({ browser }) => {
  const { baseUrl } = loadStack();
  const { context } = await foreignPage(browser, baseUrl);
  await loginAs(context, 'creator');
  void user;
  for (const [start, keeps] of [['/app/dashboard', true], ['/', false]] as const) {
    const page = await context.newPage();
    await page.goto(baseUrl + start);
    const [popup] = await Promise.all([page.waitForEvent('popup'), page.evaluate((u) => void window.open(u, '_blank'), popupUrl())]);
    await popup.waitForLoadState();
    const opener = await popup.evaluate(() => window.opener !== null);
    expect(opener, `${start}: popup opener kept=${keeps}`).toBe(keeps);
    await page.close();
  }
  await context.close();
});
