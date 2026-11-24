import { expect, test, type Page, type Route } from '@playwright/test';
import fs from 'node:fs';

/**
 * Phone assistant sheet (initiative assistant-drawer-mobile). Route-stubbed: no real API, no assistant message
 * ever reaches a provider. A fabricated `reelbolt_user` + `reelbolt_token` cookie pair, every /api call answered
 * here: ordinary reads via page.route, writes (and the drawer's own reads, which mobile WebKit sometimes lets past
 * the route handler) via an in-page fetch wrapper - WebKit does not route POST/PATCH.
 *
 * Serve THIS worktree's web on 127.0.0.1:3780 first (never the shared :18080/:28080 stacks):
 *   e2e/scripts/assistant-phone-web.sh
 *   cd e2e && LOCAL_STAGING_URL=http://127.0.0.1:3780 npx playwright test -c playwright.usability.config.ts \
 *     tests/usability/assistant --workers=1        (inside the repository test governor, e2e class)
 *   A bare name filter (e.g. `assistant-phone-sheet`) is ignored by Playwright 1.63: it runs every spec.
 * Screenshots (not asserted): SHOTS=1 -> /tmp/assistant-drawer-mobile/<SHOT_DIR|repro>/<project>-<scheme>-<locale>.png
 * Phone only: skipped at width >= 600.
 */
const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://127.0.0.1:3780').replace(/\/+$/, '');
const ORG_ID = '11111111-1111-1111-1111-111111111111';
const THREAD_ID = '22222222-2222-2222-2222-222222222222';
const SHOT_DIR = `/tmp/assistant-drawer-mobile/${process.env.SHOT_DIR ?? 'repro'}`;

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

const PARA =
  'The export pipeline renders each scene to a temporary clip, normalises the audio loudness, then stitches the clips together with the transitions you picked. If a scene fails the whole run stops and nothing is charged for the stages after it.';
const LIST = '- Check the aspect ratio of every clip\n- Re-run only the failed scene\n- Compare the loudness of the intro and the outro';
const CODE = '```json\n{ "scene": 3, "status": "Failed", "reason": "source clip is shorter than the requested duration" }\n```';

/** 36 messages (18 user + 18 assistant): long paragraphs, a list and a code block, so the thread must scroll. */
function longThread() {
  const messages = [];
  for (let i = 0; i < 36; i++) {
    const user = i % 2 === 0;
    const content = user
      ? `Question ${i / 2 + 1}: why did scene ${(i % 7) + 1} of my teaser fail, and what should I change before I run it again?`
      : `${PARA}\n\n${i % 6 === 1 ? LIST : PARA}\n\n${i % 6 === 3 ? CODE : 'Let me know if you want me to walk through it step by step.'}`;
    messages.push({
      id: `m${i}`, role: user ? 'user' : 'assistant', content, sequence: i + 1,
      createdAt: new Date(Date.UTC(2026, 9, 1, 10, i)).toISOString(),
    });
  }
  return { id: THREAD_ID, title: 'Why did my teaser fail?', projectId: null, messages };
}

async function login(page: Page, opts: { locale: string }): Promise<void> {
  const thread = longThread();
  const summaries = [{ id: THREAD_ID, title: thread.title, projectId: null, updatedAt: '2026-10-01T11:00:00Z' }];
  // Reads the drawer needs to render a thread are answered INSIDE the page as well as by page.route: on
  // mobile WebKit a fetch issued while the sheet opens sometimes reaches the server without passing the
  // route handler (observed with two different 16.4.3 builds; chromium never misses), and the run then read
  // a 404 the stub never wrote. The wrapper below is the same data, so both engines get the same thread.
  await page.addInitScript(
    ({ threadId, threadBody, threadSummaries }) => {
      const real = window.fetch.bind(window);
      const ok = (payload: unknown) => new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });
      window.fetch = async (input, init) => {
        const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
        const url = new URL(typeof input === 'string' ? input : input instanceof Request ? input.url : String(input), location.href);
        if (!url.pathname.startsWith('/api/')) return real(input, init);
        const p = url.pathname;
        if (method === 'GET') {
          if (p.endsWith('/assistant/preferences')) return ok({ approvalMode: 'ask' });
          if (p === `/api/v1/assistant/threads/${threadId}`) return ok(threadBody);
          if (p === `/api/v1/assistant/threads/${threadId}/turn`) return ok({ state: 'idle' });
          if (p === '/api/v1/assistant/threads') return ok(threadSummaries);
          if (p.endsWith('/assistant/activity')) return ok({ pendingApprovals: [], unansweredThreads: [], replies: [], working: [] });
          return real(input, init);
        }
        if (p.endsWith('/assistant/preferences')) return ok({ approvalMode: 'ask' });
        if (p.endsWith('/assistant/threads')) return ok({ id: threadId }); // createThread: binds the long thread
        return new Response(JSON.stringify({ error: 'not stubbed' }), { status: 404, headers: { 'Content-Type': 'application/json' } });
      };
      Object.defineProperty(window, 'Notification', { value: Object.assign(function Notification() {}, { permission: 'denied', requestPermission: async () => 'denied' }), configurable: true });
    },
    { threadId: THREAD_ID, threadBody: thread, threadSummaries: summaries },
  );
  const user = {
    email: 'ada@example.com', isPlatformAdmin: false, mustChangePassword: false, emailVerified: true, org: ORG_ID,
    orgName: 'Acme Team', orgKind: 'Team', orgRole: 'Owner', displayName: 'Ada', termsVersion: '2026-10', privacyVersion: '2026-10',
    locale: opts.locale,
  };
  const host = new URL(BASE).hostname;
  await page.context().addCookies([
    { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(user)), domain: host, path: '/' },
    { name: 'reelbolt_token', value: 'stub', domain: host, path: '/' },
    { name: 'rb_locale', value: opts.locale, domain: host, path: '/' },
  ]);
  const org = { id: ORG_ID, name: 'Acme Team', slug: 'acme', kind: 'Team', role: 'Owner', createdAt: '2026-10-01T00:00:00Z' };
  await page.route('**/api/**', async (route) => {
    const p = new URL(route.request().url()).pathname.replace(/^\/app/, '');
    if (p === '/api/v1/orgs') return json(route, [org]);
    if (p === '/api/v1/onboarding') return json(route, { tour: { status: 'skipped', version: 1, updatedAt: null }, checklist: { dismissed: true, items: [] } });
    if (p.endsWith('/assistant/preferences')) return json(route, { approvalMode: 'ask' });
    if (p === `/api/v1/assistant/threads/${THREAD_ID}`) return json(route, thread);
    if (p === `/api/v1/assistant/threads/${THREAD_ID}/turn`) return json(route, { state: 'idle' });
    if (p === '/api/v1/assistant/threads') return json(route, summaries);
    if (p.endsWith('/assistant/activity')) return json(route, { pendingApprovals: [], unansweredThreads: [], replies: [], working: [] });
    if (p.endsWith('/agents')) return json(route, [{ id: 'a1', name: 'code-reader', agentType: 'CodeReader', isBuiltIn: true, description: 'Reads your app', color: 'blue' }]);
    return p.endsWith('s') ? json(route, []) : json(route, { error: 'not stubbed' }, 404);
  });
  page.on('pageerror', (e) => console.log('PAGEERROR', e.message.slice(0, 300)));
  if (process.env.DEBUG_REQ) page.on('request', (r) => r.url().includes('/api/') && console.log('REQ', r.method(), new URL(r.url()).pathname));
  await page.goto('/app/dashboard');
  await page.waitForLoadState('networkidle');
}

async function openSheet(page: Page) {
  await page.locator('[data-assistant-trigger]').first().click(); // header sparkle
  const dialog = page.locator('[data-testid="assistant-drawer"] [role="dialog"]');
  await expect(dialog).toBeVisible();
  await expect(page.locator('[data-role="assistant"]').first()).toBeVisible({ timeout: 15_000 });
  await page.waitForTimeout(600); // drawer transition + scroll-to-bottom settle
  return dialog;
}

/** All numbers are CSS px, read in the page. */
async function measure(page: Page) {
  return page.evaluate(() => {
    const vv = window.visualViewport!;
    const dlg = document.querySelector('[data-testid="assistant-drawer"] [role="dialog"]') as HTMLElement;
    const r = (e: Element) => e.getBoundingClientRect();
    const dr = r(dlg);
    const thread = document.querySelector('[data-testid="assistant-thread"]') as HTMLElement;
    const viewport = thread.parentElement as HTMLElement;
    const cs = getComputedStyle(dlg);
    const innerW = dr.width - parseFloat(cs.paddingLeft) - parseFloat(cs.paddingRight);
    const assistantBlocks = [...document.querySelectorAll('[data-role="assistant"] > :nth-child(2)')].map((e) => r(e).width);
    const userBlocks = [...document.querySelectorAll('[data-role="user"] > div')].map((e) => r(e).width);
    const scrollable: string[] = [];
    for (let e: Element | null = viewport; e && dlg.contains(e); e = e.parentElement) {
      const o = getComputedStyle(e).overflowY;
      if ((o === 'auto' || o === 'scroll') && e.scrollHeight > e.clientHeight + 1) scrollable.push(`${e.tagName}.${(e as HTMLElement).className.toString().split(' ')[0]}`);
    }
    // Every descendant of the dialog that scrolls in Y, not just the viewport's ancestors (history list, settings, ...).
    const nested = [dlg, ...dlg.querySelectorAll('*')].filter((e) => {
      const o = getComputedStyle(e).overflowY;
      return (o === 'auto' || o === 'scroll') && e.scrollHeight > e.clientHeight + 1 && e !== viewport;
    }).map((e) => `${e.tagName}.${(e as HTMLElement).className.toString().split(' ')[0]}`);
    const ta = document.querySelector('textarea') as HTMLTextAreaElement | null;
    const close = document.querySelector('[aria-label="Close assistant panel"], [aria-label*="pannello"]') as HTMLElement | null;
    const header = dlg.querySelector('header, [class*="header"]') as HTMLElement | null;
    return {
      vv: { h: vv.height, top: vv.offsetTop, innerHeight: window.innerHeight },
      sheet: { top: dr.top, bottom: dr.bottom, h: dr.height, w: dr.width, innerW },
      assistantMin: Math.min(...assistantBlocks), assistantMax: Math.max(...assistantBlocks), userMax: Math.max(...userBlocks),
      userMin: Math.min(...userBlocks),
      viewport: { sh: viewport.scrollHeight, ch: viewport.clientHeight, st: viewport.scrollTop, h: r(viewport).height },
      nested, scrollable,
      doc: { scrollY: window.scrollY, bodyScrolls: document.documentElement.scrollHeight > window.innerHeight + 1, bodyOverflow: getComputedStyle(document.body).overflow },
      composer: ta ? { top: r(ta).top, bottom: r(ta).bottom, h: r(ta).height } : null,
      close: close ? { w: r(close).width, h: r(close).height } : null,
      headerH: header ? r(header).height : null,
    };
  });
}

async function shoot(page: Page, name: string) {
  if (!process.env.SHOTS) return;
  fs.mkdirSync(SHOT_DIR, { recursive: true });
  await page.screenshot({ path: `${SHOT_DIR}/${name}.png` });
}

test('phone assistant sheet: full height, readable width, one scroller, pinned composer, 44px close', async ({ page }, info) => {
  test.setTimeout(90_000);
  test.skip((page.viewportSize()?.width ?? 999) >= 600, 'phone only');
  await login(page, { locale: 'en' });
  const dialog = await openSheet(page);
  const m = await measure(page);
  console.log(`MEASURE ${info.project.name}: ${JSON.stringify(m)}`);
  const w = page.viewportSize()!.width;

  // 1. Full-height sheet.
  expect.soft(m.sheet.h / m.vv.h, `sheet height ${m.sheet.h}px of visualViewport ${m.vv.h}px must be >= 90%`).toBeGreaterThanOrEqual(0.9);

  // 2. Readable width: message blocks >= 85% of the sheet's inner width, and not narrower than ~300px at 360px.
  const minAssistantRatio = m.assistantMin / m.sheet.innerW;
  expect.soft(minAssistantRatio, `assistant text block ${m.assistantMin}px of sheet inner width ${m.sheet.innerW}px must be >= 85%`).toBeGreaterThanOrEqual(0.85);
  expect.soft(m.userMax / m.sheet.innerW, `longest user bubble ${m.userMax}px of sheet inner width ${m.sheet.innerW}px must be >= 85%`).toBeGreaterThanOrEqual(0.85);
  expect.soft(m.assistantMin, `assistant column ${m.assistantMin}px must be >= ${Math.min(300, w - 60)}px`).toBeGreaterThanOrEqual(Math.min(300, w - 60));

  // 3. One scroll container.
  expect.soft(m.viewport.sh, 'messages viewport must overflow (scrollHeight > clientHeight)').toBeGreaterThan(m.viewport.ch);
  expect.soft(m.nested, `no other element inside the dialog may scroll in Y (found ${JSON.stringify(m.nested)})`).toEqual([]);
  const viewport = page.locator('[data-testid="assistant-thread"]').locator('xpath=..');
  const y0 = await page.evaluate(() => window.scrollY);
  await viewport.evaluate((e) => { e.style.scrollBehavior = 'auto'; e.scrollTop = 0; });
  await page.mouse.move(w / 2, m.sheet.top + m.sheet.h / 2);
  let wheelWorked = true;
  try { await page.mouse.wheel(0, 400); } catch { wheelWorked = false; } // mobile WebKit has no mouse wheel
  await page.waitForTimeout(200);
  if (!wheelWorked) await viewport.evaluate((e) => { e.scrollTop = 400; });
  expect.soft(await page.evaluate(() => window.scrollY), 'window.scrollY must not move when scrolling inside the sheet').toBe(y0);
  expect.soft(await viewport.evaluate((e) => e.scrollTop), 'wheel inside the sheet scrolls the messages viewport').toBeGreaterThan(0);
  await viewport.evaluate((e) => { e.scrollTop = 0; });
  expect.soft(await viewport.evaluate((e) => e.scrollTop), 'scroll to top works').toBe(0);
  await viewport.evaluate((e) => { e.scrollTop = e.scrollHeight; });
  expect.soft(await viewport.evaluate((e) => e.scrollHeight - e.clientHeight - e.scrollTop), 'scroll back to bottom works').toBeLessThanOrEqual(2);

  // 4. Composer pinned and visible; still inside the visual viewport with a simulated soft keyboard.
  const input = page.getByRole('textbox').first();
  await expect(input).toBeVisible();
  const c = (await measure(page)).composer!;
  expect.soft(c.bottom, `composer bottom ${c.bottom}px must be <= visualViewport bottom ${m.vv.top + m.vv.h}px`).toBeLessThanOrEqual(m.vv.top + m.vv.h + 0.5);
  expect.soft(c.top, 'composer top must be on screen').toBeGreaterThanOrEqual(0);
  await input.click();
  // Keyboard simulation (the harness has no real IME): shrink the page's visualViewport by overriding
  // `height` / `offsetTop` on the instance and dispatching `resize` + `scroll`, which is exactly what the app's
  // useKeyboardInset listens to. The layout viewport (innerHeight) stays unchanged, as on Android/iOS.
  const KEYBOARD = 300;
  await page.evaluate((kb) => {
    const vv = window.visualViewport!;
    const full = window.innerHeight;
    Object.defineProperty(vv, 'height', { configurable: true, get: () => full - kb });
    Object.defineProperty(vv, 'offsetTop', { configurable: true, get: () => 0 });
    vv.dispatchEvent(new Event('resize'));
    vv.dispatchEvent(new Event('scroll'));
  }, KEYBOARD);
  await page.waitForTimeout(400);
  const k = await measure(page);
  console.log(`MEASURE-KEYBOARD ${info.project.name}: ${JSON.stringify({ composer: k.composer, sheet: k.sheet, vv: k.vv })}`);
  expect.soft(k.composer!.bottom, `with a ${KEYBOARD}px keyboard composer bottom ${k.composer!.bottom}px must stay <= visualViewport bottom ${k.vv.h}px`).toBeLessThanOrEqual(k.vv.h + 0.5);
  expect.soft(k.composer!.top, 'composer top must stay on screen with the keyboard open').toBeGreaterThanOrEqual(0);
  expect.soft(k.sheet.top, `sheet top ${k.sheet.top}px must stay on screen with the keyboard open`).toBeGreaterThanOrEqual(0);
  await input.fill('hello from the phone');
  await expect.soft(input).toHaveValue('hello from the phone');
  await page.evaluate(() => { for (const p of ['height', 'offsetTop']) delete (window.visualViewport as unknown as Record<string, unknown>)[p]; window.visualViewport!.dispatchEvent(new Event('resize')); });

  // 5. Header: Close >= 44x44, header <= 64px.
  const close = page.getByRole('button', { name: /close assistant panel|chiudi/i }).first();
  const cb = (await close.boundingBox())!;
  expect.soft(cb.width, `Close ${cb.width}x${cb.height} must be >= 44x44`).toBeGreaterThanOrEqual(44);
  expect.soft(cb.height, `Close ${cb.width}x${cb.height} must be >= 44x44`).toBeGreaterThanOrEqual(44);
  expect.soft(m.headerH ?? 0, `header height ${m.headerH}px must be <= 64px`).toBeLessThanOrEqual(64);

  // 6. Close: dialog gone, focus back on the trigger.
  await close.click();
  await expect.soft(dialog).toBeHidden();
  const focused = await page.evaluate(() => document.activeElement?.getAttribute('aria-label') ?? document.activeElement?.tagName);
  console.log(`FOCUS-AFTER-CLOSE ${info.project.name}: ${focused}`);
  expect.soft(focused, 'focus must return to the assistant trigger, not <body>').not.toBe('BODY');
});

test('screenshots: long thread, dark+light, en+it', async ({ browser }, info) => {
  test.setTimeout(150_000);
  test.skip(!process.env.SHOTS, 'set SHOTS=1');
  test.skip((info.project.use.viewport?.width ?? 999) >= 600, 'phone only');
  for (const scheme of ['dark', 'light'] as const) {
    for (const locale of ['en', 'it']) {
      const ctx = await browser.newContext({ ...info.project.use, baseURL: BASE, colorScheme: scheme });
      const page = await ctx.newPage();
      await login(page, { locale });
      await openSheet(page);
      await shoot(page, `${info.project.name}-${scheme}-${locale}`);
      await ctx.close();
    }
  }
});
