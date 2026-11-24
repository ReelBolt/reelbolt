import { expect, test, type Page } from '@playwright/test';
import { JOURNEY, RAW_TEXT, block, setupJourney, type Failure, type JourneyPage } from './fixtures';

// Failure-state matrix: every journey page x every failure. Each case asserts a plain sentence on
// screen, a Retry or a way back, none of the server's raw English, and that a 404 says "not found"
// while a 500 does not. Retry after un-blocking the route recovers the page.

const FAILURES: { name: string; mode: Failure; retry: boolean; notFound: boolean }[] = [
  { name: '500 ProblemDetails', mode: 'json500', retry: true, notFound: false },
  { name: '502 HTML body', mode: 'html502', retry: true, notFound: false },
  { name: 'network abort', mode: 'network', retry: true, notFound: false },
  { name: '403', mode: 'e403', retry: false, notFound: false },
  { name: '404', mode: 'e404', retry: false, notFound: true },
];

const seen = (page: Page, re: RegExp) => page.getByText(re).locator('visible=true').first();

async function open(page: Page, baseURL: string, p: JourneyPage, mode: Failure | 'ok') {
  await setupJourney(page, baseURL);
  const blocker = await block(page, p.target, mode);
  await page.goto(p.path);
  return blocker;
}

async function expectPlainFailure(page: Page, { retry, notFound }: { retry: boolean; notFound: boolean }) {
  const card = page.getByTestId('failure-card').first();
  await expect(card).toBeVisible({ timeout: 20_000 });
  const text = (await card.innerText()).trim();
  // A title plus a plain sentence, not one raw word.
  expect.soft(text.split('\n').filter((l) => l.trim().length > 25).length, `a sentence in: ${text}`).toBeGreaterThan(0);
  // A Retry, or at least a way back.
  const retryBtn = card.getByRole('button', { name: /^(Retry|Try again)$/ });
  if (retry) await expect(retryBtn).toBeVisible();
  else await expect(retryBtn).toHaveCount(0);
  await expect(card.getByRole('link').or(retryBtn).first()).toBeVisible();
  const body = await page.locator('body').innerText();
  for (const raw of RAW_TEXT) expect.soft(body, `raw server text "${raw}" on screen`).not.toContain(raw);
  if (notFound) expect.soft(text).toMatch(/not found/i);
  else expect.soft(text).not.toMatch(/not found/i);
}

for (const p of JOURNEY) {
  test.describe(`${p.name} page`, () => {
    for (const f of FAILURES) {
      test(`${f.name}: plain sentence, ${f.retry ? 'Retry' : 'a way back'}, no raw text`, async ({ page, baseURL }) => {
        await open(page, baseURL!, p, f.mode);
        await expectPlainFailure(page, f);
      });
    }

    test('500 then Retry after un-blocking recovers the page', async ({ page, baseURL }) => {
      const blocker = await open(page, baseURL!, p, 'json500');
      await expectPlainFailure(page, { retry: true, notFound: false });
      blocker.mode = 'ok';
      await page.getByTestId('failure-card').first().getByRole('button', { name: /^(Retry|Try again)$/ }).click();
      await expect(page.getByTestId('failure-card')).toHaveCount(0, { timeout: 15_000 });
      await expect(seen(page, p.ready)).toBeVisible({ timeout: 15_000 });
    });

    test('network abort then Retry recovers the page', async ({ page, baseURL }) => {
      const blocker = await open(page, baseURL!, p, 'network');
      await expectPlainFailure(page, { retry: true, notFound: false });
      blocker.mode = 'ok';
      await page.getByTestId('failure-card').first().getByRole('button', { name: /^(Retry|Try again)$/ }).click();
      await expect(page.getByTestId('failure-card')).toHaveCount(0, { timeout: 15_000 });
    });

    test('slow response (14 s): "taking longer than usual" appears, then the page loads', async ({ page, baseURL }) => {
      await open(page, baseURL!, p, 'slow');
      await expect(page.getByText(/taking longer than usual/i).first()).toBeVisible({ timeout: 13_000 });
      expect(await page.locator('body').innerText()).not.toContain('Internal Server Error');
      await expect(seen(page, p.ready)).toBeVisible({ timeout: 15_000 });
    });

    test('401 sends the person to the sign-in page', async ({ page, baseURL }) => {
      await open(page, baseURL!, p, 'e401');
      await expect(page).toHaveURL(/\/login/, { timeout: 20_000 });
    });

    test('offline: the banner appears, and goes away when back online', async ({ page, context, baseURL }) => {
      await open(page, baseURL!, p, 'ok');
      await expect(seen(page, p.ready)).toBeVisible({ timeout: 20_000 });
      await context.setOffline(true);
      await expect(page.getByTestId('offline-banner')).toBeVisible({ timeout: 10_000 });
      expect(await page.getByTestId('offline-banner').innerText()).toMatch(/offline|connection|internet/i);
      await context.setOffline(false);
      await expect(page.getByTestId('offline-banner')).toHaveCount(0, { timeout: 10_000 });
    });
  });
}

test.describe('run page: starting a run', () => {
  test('402 INSUFFICIENT_CREDITS says what is missing and links to plans', async ({ page, baseURL }) => {
    await setupJourney(page, baseURL!, { runStatus: '402' });
    await page.goto(JOURNEY.find((j) => j.name === 'run')!.path);
    await expect(page.getByText(/Launch teaser/).first()).toBeVisible({ timeout: 20_000 });
    const run = page.getByRole('button', { name: /^Run\b/ }).first();
    await expect(run).toBeEnabled({ timeout: 20_000 });
    await run.click();
    const confirm = page.getByRole('dialog').getByRole('button', { name: /run|start|confirm/i });
    if (await confirm.first().isVisible({ timeout: 1500 }).catch(() => false)) await confirm.first().click();
    const cta = page.getByTestId('insufficient-credits-cta');
    await expect(cta).toBeVisible({ timeout: 15_000 });
    await expect(cta).toHaveAttribute('href', /\/settings\/billing/);
    await expect(page.getByText(/12 credits short/)).toBeVisible();
    const body = await page.locator('body').innerText();
    for (const raw of RAW_TEXT) expect(body, `raw "${raw}"`).not.toContain(raw);
  });

  test('a 500 on Run keeps the page and words the failure plainly', async ({ page, baseURL }) => {
    await setupJourney(page, baseURL!);
    await block(page, new RegExp('/workflows/wf-run/execute$'), 'json500');
    await page.goto(JOURNEY.find((j) => j.name === 'run')!.path);
    const run = page.getByRole('button', { name: /^Run\b/ }).first();
    await expect(run).toBeEnabled({ timeout: 20_000 });
    await run.click();
    const confirm = page.getByRole('dialog').getByRole('button', { name: /run|start|confirm/i });
    if (await confirm.first().isVisible({ timeout: 1500 }).catch(() => false)) await confirm.first().click();
    await expect(page.getByText(/something went wrong|try again/i).first()).toBeVisible({ timeout: 15_000 });
    const body = await page.locator('body').innerText();
    for (const raw of RAW_TEXT) expect(body, `raw "${raw}"`).not.toContain(raw);
  });
});

test.describe('Italian pass (rb_locale=it)', () => {
  const pick = (n: string) => JOURNEY.find((j) => j.name === n)!;
  const cases: { page: string; mode: Failure; expect: RegExp; absent: RegExp }[] = [
    { page: 'project', mode: 'json500', expect: /Qualcosa è andato storto da parte nostra/, absent: /Something went wrong|Try again|Retry/ },
    { page: 'dashboard', mode: 'network', expect: /Impossibile raggiungere ReelBolt/, absent: /Could not reach|Retry/ },
    { page: 'run', mode: 'e404', expect: /Non trovato/, absent: /Not found|Back to the dashboard/ },
  ];
  for (const c of cases) {
    test(`${c.page} ${c.mode} is in Italian`, async ({ page, baseURL }) => {
      await page.context().addCookies([{ name: 'rb_locale', value: 'it', url: baseURL! }]);
      await open(page, baseURL!, pick(c.page), c.mode);
      const card = page.getByTestId('failure-card').first();
      await expect(card).toBeVisible({ timeout: 20_000 });
      await expect(card).toContainText(c.expect);
      expect(await card.innerText()).not.toMatch(c.absent);
      if (c.mode !== 'e404') await expect(card.getByRole('button', { name: /Riprova/ })).toBeVisible();
    });
  }

  test('slow notice is in Italian', async ({ page, baseURL }) => {
    await page.context().addCookies([{ name: 'rb_locale', value: 'it', url: baseURL! }]);
    await open(page, baseURL!, pick('project'), 'slow');
    await expect(page.getByText(/richiedendo più tempo del solito/)).toBeVisible({ timeout: 13_000 });
  });
});
