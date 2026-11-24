import { expect, test, type BrowserContext, type Page } from '@playwright/test';
import { cookieUser, respond } from './fixtures/api.mjs';

/**
 * Onboarding tour + "Getting started" card on desktop (1400), tablet (820), pixel7 and iphone14 (webkit).
 * `/api/v1/onboarding*` is faked in the browser: the account's real state is never touched.
 */
const EMAIL = process.env.LOCAL_STAGING_EMAIL ?? '';
const PASSWORD = process.env.LOCAL_STAGING_PASSWORD ?? '';
const HARNESS = process.env.ONBOARDING_E2E_HARNESS === '1';
test.skip(!HARNESS && (!EMAIL || !PASSWORD), 'LOCAL_STAGING_* not set');

// The nine slugs the docs publish for the tour (plan.md contract), in step order.
const STEP_GUIDES = [
  'sign-up-and-first-look',
  'create-a-project-and-upload',
  'pick-an-app-and-run',
  'follow-progress-and-share',
  'assistant-basics',
  'editor-basics',
  'pictures-and-thumbnails',
  'plan-credits-and-billing',
];

const sessions = new Map<string, { cookies: Parameters<BrowserContext['addCookies']>[0]; local: Record<string, string> }>();

/** Harness mode: dummy session cookies and every /api/** call answered from the fixtures. */
async function fakeApi(page: Page): Promise<void> {
  const base = test.info().project.use.baseURL!;
  await page.context().addCookies([
    { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(cookieUser)), url: base },
    { name: 'reelbolt_token', value: 'harness-fixture-token', url: base },
  ]);
  const held = new Set<string>();
  await page.route('**/api/**', async (route) => {
    const url = new URL(route.request().url());
    if (url.pathname.startsWith('/app/_next')) return route.continue();
    const result = respond(route.request().method(), url.pathname, url.search) as
      | { status: number; json: unknown; sse?: undefined; media?: undefined }
      | { sse: { event: string; data: unknown }[]; media?: undefined }
      | { media: number }
      | null;
    if (!result || 'media' in result && result.media !== undefined) return route.fulfill({ status: 404, contentType: 'application/json', body: '{}' });
    if ('sse' in result && result.sse) {
      if (held.has(url.pathname)) return;
      held.add(url.pathname);
      const body = result.sse.map((e) => `event: ${e.event}\ndata: ${JSON.stringify(e.data)}\n\n`).join('');
      return route.fulfill({ status: 200, headers: { 'content-type': 'text/event-stream' }, body });
    }
    const r = result as { status: number; json: unknown };
    return route.fulfill({ status: r.status, contentType: 'application/json', body: JSON.stringify(r.json) });
  });
}

async function signIn(page: Page): Promise<void> {
  if (HARNESS) return fakeApi(page);
  const key = test.info().project.name;
  const saved = sessions.get(key);
  if (saved) {
    await page.context().addCookies(saved.cookies);
    return;
  }
  await page.goto('/app/login');
  await page.waitForLoadState('networkidle');
  await page.locator('input[type="email"], input[autocomplete="username"], input[autocomplete="email"]').first().fill(EMAIL);
  await page.locator('input[type="password"]').first().fill(PASSWORD);
  await page.getByRole('button', { name: /log in|sign in/i }).click();
  await page.waitForURL(/\/app\/(dashboard|projects)/, { timeout: 45_000, waitUntil: 'commit' });
  await page.waitForLoadState('networkidle');
  const st = await page.context().storageState();
  sessions.set(key, { cookies: st.cookies, local: {} });
}

interface Faked {
  puts: unknown[];
  events: unknown[];
}

/** Fakes the onboarding API as "tour pending" and records what the page sends. */
async function fakeOnboarding(page: Page, status: 'pending' | 'skipped' = 'pending'): Promise<Faked> {
  const seen: Faked = { puts: [], events: [] };
  const body = (s: string) =>
    JSON.stringify({
      tour: { status: s, version: s === 'pending' ? null : 1, updatedAt: s === 'pending' ? null : '2026-11-01T00:00:00Z' },
      checklist: { dismissed: false, items: ['create_project', 'upload_file', 'run_video', 'open_result', 'try_assistant', 'open_editor'].map((key) => ({ key, done: false })) },
    });
  // Recorded from the request event rather than the route handler: WebKit does not reliably run route
  // handlers for fetch PUTs. A body it cannot expose is recorded as `null` (body assertions then skip).
  page.on('request', (r) => {
    if (r.method() !== 'PUT' || !/\/api\/v1\/onboarding$/.test(r.url())) return;
    let sent: unknown = null;
    try {
      sent = r.postDataJSON();
    } catch {
      sent = null;
    }
    seen.puts.push(sent);
  });
  await page.route('**/api/v1/onboarding', async (route) => {
    const req = route.request();
    if (req.method() === 'PUT') {
      return route.fulfill({ status: 200, contentType: 'application/json', body: body('skipped') });
    }
    return route.fulfill({ status: 200, contentType: 'application/json', body: body(status) });
  });
  await page.route('**/api/v1/onboarding/events', async (route) => {
    seen.events.push(route.request().postDataJSON());
    return route.fulfill({ status: 204 });
  });
  // An answered consent card must never be in the tour's way.
  await page.addInitScript(() => {
    window.localStorage.setItem('rf-app-telemetry-consent', 'denied');
  });
  return seen;
}

const noHorizontalOverflow = (page: Page) =>
  page.evaluate(() => document.documentElement.scrollWidth <= document.documentElement.clientWidth + 1);

test.beforeEach(async ({ page }) => {
  await signIn(page);
});

test('the tour opens by itself, walks all eight steps and stays inside the screen', async ({ page }) => {
  const seen = await fakeOnboarding(page);
  await page.goto('/app/dashboard');
  const dialog = page.getByRole('dialog', { name: /Welcome to ReelBolt/ });
  await expect(dialog).toBeVisible({ timeout: 30_000 });
  const phone = (page.viewportSize()?.width ?? 0) < 768;
  if (phone) await expect(dialog).toHaveAttribute('data-placement', 'sheet');

  for (let i = 0; i < STEP_GUIDES.length; i += 1) {
    await expect(page.getByText(`Step ${i + 1} of 8`).first()).toBeVisible();
    expect(await noHorizontalOverflow(page)).toBe(true);
    const vp = page.viewportSize()!;
    // Poll: the phone sheet slides in, so measure once it has settled.
    await expect
      .poll(async () => {
        const box = await page.getByRole('dialog').boundingBox();
        return !!box && box.x >= -1 && box.x + box.width <= vp.width + 1 && box.y + box.height <= vp.height + 1;
      })
      .toBe(true);

    const learn = page.getByRole('link', { name: /Learn more/ });
    await expect(learn).toHaveAttribute('href', `/docs/basics/${STEP_GUIDES[i]}`);
    await expect(learn).toHaveAttribute('target', '_blank');

    // Focus stays inside the tour however many times Tab is pressed.
    for (let t = 0; t < 7; t += 1) {
      await page.keyboard.press('Tab');
      expect(await page.evaluate(() => !!document.activeElement?.closest('[role="dialog"][data-onboarding-tour]'))).toBe(true);
    }
    await page.getByRole('button', { name: i === STEP_GUIDES.length - 1 ? 'Finish' : 'Next' }).click();
  }
  await expect(page.getByTestId('onboarding-tour')).toHaveCount(0);
  await expect.poll(() => seen.puts.length).toBeGreaterThan(0);
  if (seen.puts[0] !== null) expect(seen.puts[0]).toEqual({ tour: { status: 'completed', version: 1 } });
  // Consent was declined, so no event may leave the browser.
  expect(seen.events).toHaveLength(0);
});

test('Escape skips the tour and it does not come back after a reload', async ({ page }) => {
  const seen = await fakeOnboarding(page);
  await page.goto('/app/dashboard');
  await expect(page.getByRole('dialog', { name: /Welcome to ReelBolt/ })).toBeVisible({ timeout: 30_000 });
  await page.keyboard.press('Escape');
  await expect(page.getByTestId('onboarding-tour')).toHaveCount(0);
  await expect.poll(() => seen.puts.length).toBeGreaterThan(0);
  if (seen.puts[0] !== null) expect(seen.puts[0]).toEqual({ tour: { status: 'skipped', version: 1 } });
  // Server still says "pending" here (the fake ignores the PUT); the local copy must win.
  await page.reload();
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(2500);
  await expect(page.getByTestId('onboarding-tour')).toHaveCount(0);
});

test('"Take the tour" in the account menu opens it again on request', async ({ page }) => {
  await fakeOnboarding(page, 'skipped');
  await page.goto('/app/dashboard');
  await page.waitForLoadState('networkidle');
  await page.waitForTimeout(2500);
  await expect(page.getByTestId('onboarding-tour')).toHaveCount(0);
  await page.getByRole('button', { name: /^Account menu/ }).click();
  await page.getByRole('menuitem', { name: 'Take the tour' }).click();
  await expect(page.getByRole('dialog', { name: /Welcome to ReelBolt/ })).toBeVisible();
  expect(await noHorizontalOverflow(page)).toBe(true);
});

test('the Getting started card fits the screen and its How links point at the guides', async ({ page }) => {
  await fakeOnboarding(page, 'skipped');
  await page.goto('/app/dashboard');
  const card = page.getByTestId('getting-started');
  await expect(card).toBeVisible({ timeout: 30_000 });
  expect(await noHorizontalOverflow(page)).toBe(true);
  // Collapsed on a phone or a touch screen: open the list when the toggle is there.
  const toggle = page.getByRole('button', { name: 'Show steps' });
  if (await toggle.isVisible()) await toggle.click();
  const hrefs = await card.getByRole('link', { name: /^How:/ }).evaluateAll((els) => els.map((e) => e.getAttribute('href')));
  expect(hrefs).toEqual([
    '/docs/basics/create-a-project-and-upload',
    '/docs/basics/create-a-project-and-upload',
    '/docs/basics/pick-an-app-and-run',
    '/docs/basics/follow-progress-and-share',
    '/docs/basics/assistant-basics',
    '/docs/basics/editor-basics',
  ]);
  expect(await noHorizontalOverflow(page)).toBe(true);
});
