/**
 * Google Consent Mode v2 on the marketing site. Run with playwright.site-consent.config.ts against
 * a build made with NEXT_PUBLIC_GA_MEASUREMENT_ID=G-TESTCONSENT1 (see the config for the commands).
 *
 * What it proves, against the real gtag.js:
 *  - the tag is on the page before any choice, so Google's tag checker can see it;
 *  - the denied default is the first thing in the data layer, ahead of `config`;
 *  - before a choice no cookie is written and the tag sends only cookieless pings (`gcs=G100`,
 *    analytics storage denied);
 *  - Reject ends it (operator decision, option B): a stored rejection loads no Google script and
 *    makes no Google request at all, and a rejection made on the page stops every later request;
 *  - Accept grants analytics storage only (`gcs=G101`), writes the `_ga` cookies, and ads stay denied;
 *  - Accept then Cookie preferences then Reject stops everything, including the trailing
 *    engagement hit gtag.js holds for a granted visit.
 */
import { expect, test, type BrowserContext, type Page } from '@playwright/test';

const GA_ID = 'G-TESTCONSENT1';
const CONSENT_KEY = 'rf-cookie-consent';
// Every Google endpoint gtag.js can send a measurement hit to. Matched by shape, not host, because the
// tag also uses regional hosts (region1.google-analytics.com) and analytics.google.com.
const COLLECT = /\/(g\/)?collect(\?|$)/;

test.skip(!process.env.GA_CONSENT_BASE_URL, 'set GA_CONSENT_BASE_URL to a site build made with a test measurement ID');

interface Ping {
  url: URL;
  gcs: string | null;
}

// Any request to a Google measurement or tag host, whatever the path (gtag.js, collect, regional).
const GOOGLE_HOST = /(^|\.)(googletagmanager\.com|google-analytics\.com|analytics\.google\.com|doubleclick\.net)$/;

/** Every request the context makes to a Google tag or measurement host, as `host/path`. */
function recordGoogleRequests(context: BrowserContext): string[] {
  const seen: string[] = [];
  context.on('request', (request) => {
    const url = new URL(request.url());
    if (GOOGLE_HOST.test(url.hostname)) seen.push(`${url.hostname}${url.pathname}`);
  });
  return seen;
}

/** Answers every Google collection request locally (204) and records it. */
async function recordPings(context: BrowserContext): Promise<Ping[]> {
  const pings: Ping[] = [];
  await context.route(COLLECT, async (route) => {
    const url = new URL(route.request().url());
    if (/google|doubleclick/.test(url.host)) {
      pings.push({ url, gcs: url.searchParams.get('gcs') });
      await route.fulfill({ status: 204, body: '' });
      return;
    }
    await route.continue();
  });
  return pings;
}

async function open(context: BrowserContext, path = '/') {
  const pings = await recordPings(context);
  const page = await context.newPage();
  await page.goto(path);
  return { page, pings };
}

async function waitForPing(pings: Ping[], atLeast = 1) {
  await expect.poll(() => pings.length, { timeout: 15_000 }).toBeGreaterThanOrEqual(atLeast);
}

async function googleCookies(context: BrowserContext) {
  return (await context.cookies()).filter((c) => /^(_ga|_gid|_gcl|_gac|FPLC)/.test(c.name));
}

async function dataLayerHead(page: Page): Promise<unknown[][]> {
  return page.evaluate(() =>
    ((window as unknown as { dataLayer: ArrayLike<unknown>[] }).dataLayer ?? []).map((e) =>
      JSON.parse(JSON.stringify(Array.from(e))),
    ),
  );
}

test('before any choice the tag is on the page, denied by default, and sets no cookie', async ({ context }) => {
  const { page, pings } = await open(context);
  await expect(page.locator(`script[src*="googletagmanager.com/gtag/js?id=${GA_ID}"]`)).toHaveCount(1);
  await waitForPing(pings);

  const layer = await dataLayerHead(page);
  expect(layer[0][0]).toBe('consent');
  expect(layer[0][1]).toBe('default');
  expect(layer[0][2]).toEqual({
    ad_storage: 'denied',
    ad_user_data: 'denied',
    ad_personalization: 'denied',
    analytics_storage: 'denied',
    wait_for_update: 500,
  });
  const configAt = layer.findIndex((e) => e[0] === 'config');
  expect(configAt).toBeGreaterThan(0);
  expect(layer.slice(0, configAt).some((e) => e[0] === 'consent' && e[1] === 'update')).toBe(false);

  expect(pings.every((p) => p.gcs === 'G100')).toBe(true);
  expect(await googleCookies(context)).toEqual([]);
  expect(await context.cookies()).toEqual([]);
  // Nothing of Google's in local or session storage either (only our own consent key may exist).
  const storage = await page.evaluate(() => [...Object.keys(localStorage), ...Object.keys(sessionStorage)]);
  expect(storage.filter((k) => k !== CONSENT_KEY)).toEqual([]);
});

test('a stored rejection loads no Google script and makes no Google request', async ({ context }) => {
  await context.addInitScript((k) => window.localStorage.setItem(k, 'denied'), CONSENT_KEY);
  const google = recordGoogleRequests(context);
  const { page } = await open(context);
  await page.waitForTimeout(4000);
  await page.reload();
  await page.waitForTimeout(3000);

  expect(google).toEqual([]);
  await expect(page.locator('script[src*="googletagmanager.com"]')).toHaveCount(0);
  expect(await page.evaluate(() => typeof (window as unknown as { gtag?: unknown }).gtag)).toBe('undefined');
  expect(await context.cookies()).toEqual([]);
});

test('rejecting on the page stops every later Google request', async ({ context }) => {
  const google = recordGoogleRequests(context);
  const { page, pings } = await open(context);
  await waitForPing(pings);
  await page.getByRole('button', { name: 'Reject' }).click();
  const afterReject = google.length;
  const pingsAfterReject = pings.length;
  await page.getByRole('link', { name: /pricing/i }).first().click();
  await page.waitForTimeout(5000);

  expect(google.slice(afterReject)).toEqual([]);
  expect(pings.length).toBe(pingsAfterReject);
  expect(await page.evaluate((k) => localStorage.getItem(k), CONSENT_KEY)).toBe('denied');
  expect(await context.cookies()).toEqual([]);

  // A reload after the rejection loads nothing from Google.
  const mark = google.length;
  await page.reload();
  await page.waitForTimeout(3000);
  expect(google.slice(mark)).toEqual([]);
});

test('accepting grants analytics storage only, and the cookies appear', async ({ context }) => {
  const { page, pings } = await open(context);
  await waitForPing(pings);
  await page.getByRole('button', { name: 'Accept' }).click();

  const layer = await dataLayerHead(page);
  const update = layer.find((e) => e[0] === 'consent' && e[1] === 'update');
  expect(update?.[2]).toEqual({ analytics_storage: 'granted' });
  expect(JSON.stringify(layer)).not.toMatch(/"(ad_storage|ad_user_data|ad_personalization)":"granted/);

  // A returning, already-accepted visitor: the granted update is queued ahead of config, so the very
  // first hit of the visit is a full one and the cookies are written.
  const before = pings.length;
  await page.reload();
  await waitForPing(pings, before + 1);
  expect(pings.slice(before).every((p) => p.gcs === 'G101')).toBe(true);
  const reloaded = (await dataLayerHead(page)).map((e) => `${e[0]}:${typeof e[1] === 'string' ? e[1] : ''}`);
  expect(reloaded.indexOf('consent:default')).toBe(0);
  const updateAt = reloaded.indexOf('consent:update');
  expect(updateAt).toBeGreaterThan(0);
  expect(updateAt).toBeLessThan(reloaded.findIndex((k) => k.startsWith('config:')));
  await expect
    .poll(async () => (await googleCookies(context)).map((c) => c.name).join(','), { timeout: 15_000 })
    .toMatch(/_ga/);
});

test('accept, then Cookie preferences, then Reject stops everything, now and on later pages', async ({ context }) => {
  const google = recordGoogleRequests(context);
  const { page, pings } = await open(context);
  await waitForPing(pings);
  await page.getByRole('button', { name: 'Accept' }).click();
  // Let the page accrue engagement time while granted: gtag.js reports it in a trailing
  // `user_engagement` hit a couple of seconds later, carrying the `_ga` client id.
  await page.waitForTimeout(1200);
  await page.getByRole('button', { name: 'Cookie preferences' }).click();
  await page.getByRole('button', { name: 'Reject' }).click();

  const updates = (await dataLayerHead(page))
    .filter((e) => e[0] === 'consent' && e[1] === 'update')
    .map((e) => (e[2] as { analytics_storage: string }).analytics_storage);
  expect(updates.at(-1)).toBe('denied');

  const afterWithdrawal = pings.length;
  const googleAfterWithdrawal = google.length;
  await page.waitForTimeout(6000);
  expect(pings.slice(afterWithdrawal).map((p) => `${p.url.searchParams.get('en')}:${p.gcs}`)).toEqual([]);
  expect(google.slice(googleAfterWithdrawal)).toEqual([]);

  // A fresh page after the rejection loads nothing from Google.
  const second = await context.newPage();
  const mark = google.length;
  await second.goto('/pricing');
  await second.waitForTimeout(4000);
  expect(google.slice(mark)).toEqual([]);
  await second.close();
});

test('accepting again after a rejection on the same page restores measurement', async ({ context }) => {
  const { page, pings } = await open(context);
  await waitForPing(pings);
  await page.getByRole('button', { name: 'Reject' }).click();
  await page.getByRole('button', { name: 'Cookie preferences' }).click();
  await page.getByRole('button', { name: 'Accept' }).click();
  expect(
    await page.evaluate((id) => (window as unknown as Record<string, unknown>)[`ga-disable-${id}`], GA_ID),
  ).toBe(false);

  const before = pings.length;
  await page.reload();
  await waitForPing(pings, before + 1);
  expect(pings.slice(before).every((p) => p.gcs === 'G101')).toBe(true);
});

test('a stored rejection followed by Accept loads the tag then, granted from the first hit', async ({ context }) => {
  await context.addInitScript((k) => {
    if (!sessionStorage.getItem('seeded')) {
      sessionStorage.setItem('seeded', '1');
      localStorage.setItem(k, 'denied');
    }
  }, CONSENT_KEY);
  const { page, pings } = await open(context);
  await page.waitForTimeout(2000);
  expect(pings).toEqual([]);
  await page.getByRole('button', { name: 'Cookie preferences' }).click();
  await page.waitForTimeout(1500);
  expect(pings).toEqual([]);
  await page.getByRole('button', { name: 'Accept' }).click();
  await waitForPing(pings);
  expect(pings.every((p) => p.gcs === 'G101')).toBe(true);
});
