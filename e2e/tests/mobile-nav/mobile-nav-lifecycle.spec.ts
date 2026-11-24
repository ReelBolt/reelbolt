import { devices, expect, test, type Browser, type BrowserContext, type CDPSession, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dismissTour } from './mobile-nav-target';
import { logsDir, saveAuthState } from './logs-dir';

/**
 * Initiative mobile-nav-stuck, T-001. Owner bug: on Android Chrome, after the dashboard tab sat in the background,
 * tapping the burger turns it into an X but the side navbar never shows. The assertion here is therefore the
 * VISIBLE navbar (on-screen rect + elementFromPoint inside it), never the burger's own state.
 */
const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');
const EMAIL = process.env.LOCAL_STAGING_EMAIL ?? '';
const PASSWORD = process.env.LOCAL_STAGING_PASSWORD ?? '';
const LOCAL_WEB = process.env.MOBILE_NAV_LOCAL_WEB?.replace(/\/+$/, '');
const LOGS = logsDir();
const STATE = `${LOGS}/auth-state.json`;
mkdirSync(LOGS, { recursive: true });

test.beforeAll(async ({ browser }) => {
  test.skip(!EMAIL || !PASSWORD, 'LOCAL_STAGING_EMAIL/PASSWORD not set');
  const ctx = await browser.newContext({ ...devices['Pixel 7'], baseURL: BASE });
  const page = await ctx.newPage();
  await page.goto('/app/login');
  await page.getByPlaceholder('you@example.com').fill(EMAIL);
  await page.getByPlaceholder('Your password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL(/\/app\/(dashboard|projects)/);
  await saveAuthState(ctx, STATE);
  await ctx.close();
});

async function phone(browser: Browser): Promise<{ ctx: BrowserContext; page: Page }> {
  const ctx = await browser.newContext({ ...devices['Pixel 7'], baseURL: BASE, storageState: STATE });
  if (LOCAL_WEB) {
    // Serve /app/** from this worktree's `next start`; nginx still answers /api/v1 and owns the cookies.
    await ctx.route(`${BASE}/app/**`, async (route) => {
      const u = new URL(route.request().url());
      if (route.request().method() !== 'GET') return route.continue();
      const res = await route.fetch({ url: `${LOCAL_WEB}${u.pathname}${u.search}`, maxRedirects: 0 });
      await route.fulfill({ response: res });
    });
  }
  await ctx.addInitScript(() => {
    (window as any).__pageshows = [];
    window.addEventListener('pageshow', (e) => (window as any).__pageshows.push({ persisted: e.persisted }));
  });
  const page = await ctx.newPage();
  return { ctx, page };
}

async function open(page: Page): Promise<void> {
  await page.goto('/app/dashboard');
  await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => undefined);
  await page.getByRole('button', { name: 'Toggle navigation' }).waitFor();
  await dismissTour(page);
  await page.waitForTimeout(800);
}

const burger = (page: Page) => page.getByRole('button', { name: 'Toggle navigation' });

/** Everything the brief asks to record, in one evaluate. */
async function snapshot(page: Page) {
  return page.evaluate(() => {
    const nav = document.querySelector<HTMLElement>('nav.mantine-AppShell-navbar');
    const b = document.querySelector<HTMLElement>('button[aria-label="Toggle navigation"]');
    const vw = window.innerWidth;
    const r = nav?.getBoundingClientRect();
    const cs = nav ? getComputedStyle(nav) : null;
    const cx = r ? Math.min(Math.max(r.left + r.width / 2, 0), vw - 1) : 0;
    const cy = r ? Math.min(Math.max(r.top + r.height / 2, 0), window.innerHeight - 1) : 0;
    const hit = document.elementFromPoint(cx, cy);
    const covering: string[] = [];
    if (r && nav) {
      for (const el of Array.from(document.body.querySelectorAll<HTMLElement>('*'))) {
        if (el === nav || nav.contains(el) || el.contains(nav)) continue;
        const s = getComputedStyle(el);
        if (s.position !== 'fixed' || Number(s.zIndex) < 100) continue;
        const q = el.getBoundingClientRect();
        if (q.width > 0 && q.height > 0 && q.left < r.right && q.right > r.left && q.top < r.bottom && q.bottom > r.top) {
          covering.push(`${el.tagName}.${String(el.className).slice(0, 60)} z=${s.zIndex} ${Math.round(q.left)},${Math.round(q.top)} ${Math.round(q.width)}x${Math.round(q.height)}`);
        }
      }
    }
    const styles = Array.from(document.querySelectorAll('style[data-mantine-styles="inline"]'))
      .map((s) => s.textContent ?? '')
      .filter((t) => t.includes('app-shell'));
    const visible = !!r && r.width > 0 && r.height > 0 && r.right > 1 && r.left < vw - 1 && r.left >= -1;
    return {
      burgerOpened: b?.querySelector('.mantine-Burger-burger')?.hasAttribute('data-opened') ?? false,
      navDataOpened: nav?.hasAttribute('data-opened') ?? null,
      rect: r ? { l: r.left, t: r.top, w: r.width, h: r.height } : null,
      vw,
      vh: window.innerHeight,
      transform: cs?.transform,
      visibility: cs?.visibility,
      opacity: cs?.opacity,
      display: cs?.display,
      inert: nav?.hasAttribute('inert') ?? null,
      hitInsideNavbar: !!hit && !!nav && nav.contains(hit),
      hit: hit ? `${hit.tagName}.${String(hit.className).slice(0, 50)}` : null,
      navbarVar: getComputedStyle(document.documentElement).getPropertyValue('--app-shell-navbar-transform').trim(),
      styleTags: styles.length,
      styleTagsText: styles.map((t) => t.slice(0, 400)),
      animations: nav ? nav.getAnimations().map((a) => `${a.playState}:${(a as CSSTransition).transitionProperty ?? ''}`) : [],
      resizingAttr: document.documentElement.hasAttribute('data-resizing') || !!document.querySelector('[data-resizing]'),
      covering,
      visibilityState: document.visibilityState,
      pageshows: (window as any).__pageshows,
      visible,
    };
  });
}

type Snap = Awaited<ReturnType<typeof snapshot>>;
const trace: Record<string, Snap[]> = {};

/** Tap the burger and, once it reports open, require the navbar to be genuinely on screen and hittable. */
async function tapBurger(page: Page, label: string): Promise<void> {
  // A modal left open (feedback dialog for pre=helpmenu) legitimately blocks the burger: record it, Escape, retry,
  // instead of waiting for the 180 s test timeout (run1 scenarios 7/8).
  const ok = await burger(page).tap({ timeout: 5000 }).then(() => true, () => false);
  if (!ok) {
    (trace[`${label}:tapBlocked`] ??= []).push(await snapshot(page));
    await page.keyboard.press('Escape');
    await page.waitForTimeout(400);
    await burger(page).tap({ timeout: 5000 });
  }
}

async function checkOpen(page: Page, label: string, cycle: number, strict = true): Promise<Snap> {
  await tapBurger(page, label);
  await page.waitForTimeout(450);
  const s = await snapshot(page);
  (trace[label] ??= []).push(s);
  await page.screenshot({ path: `${LOGS}/shots/${label.replace(/\W+/g, '_')}-${cycle}.png` }).catch(() => undefined);
  if (strict) expect.soft(s.burgerOpened, `${label}#${cycle}: tap must open the menu`).toBe(true);
  if (s.burgerOpened) {
    expect.soft(s.visible, `${label}#${cycle}: navbar on screen ${JSON.stringify(s.rect)} var=${s.navbarVar}`).toBe(true);
    expect.soft(s.hitInsideNavbar, `${label}#${cycle}: elementFromPoint at navbar centre is ${s.hit}; covering=${s.covering.join('|')}`).toBe(true);
    expect.soft(s.inert, `${label}#${cycle}: navbar inert while open`).toBe(false);
  }
  return s;
}

async function closeMenu(page: Page, label: string): Promise<void> {
  await tapBurger(page, label);
  await page.waitForTimeout(450);
  const s = await snapshot(page);
  (trace[label] ??= []).push(s);
  expect.soft(s.burgerOpened, `${label}: second tap closes`).toBe(false);
}

async function cycles(page: Page, label: string, n = 3): Promise<void> {
  // pre=menu (and anything that leaves it open): the shell may keep the menu open across the event, in which case the
  // first tap legitimately closes it. Require burger and navbar to agree, then close it before counting cycles.
  const start = await snapshot(page);
  (trace[`${label}:start`] ??= []).push(start);
  if (start.burgerOpened) {
    expect.soft(start.visible && start.hitInsideNavbar, `${label}: burger open but navbar not visible ${JSON.stringify(start.rect)}`).toBe(true);
    await closeMenu(page, `${label}:pre-close`);
  }
  for (let i = 1; i <= n; i++) {
    await checkOpen(page, label, i);
    await closeMenu(page, `${label}:close`);
  }
}

const settle = (page: Page, ms = 600) => page.waitForTimeout(ms);
const freeze = async (cdp: CDPSession, ms = 3000, page?: Page) => {
  await cdp.send('Page.setWebLifecycleState', { state: 'frozen' });
  await new Promise((r) => setTimeout(r, ms));
  await cdp.send('Page.setWebLifecycleState', { state: 'active' });
  await page?.waitForTimeout(300);
};
const syntheticHidden = (page: Page) =>
  page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => 'hidden' });
    Object.defineProperty(document, 'hidden', { configurable: true, get: () => true });
    document.dispatchEvent(new Event('visibilitychange'));
  });
const syntheticVisible = (page: Page) =>
  page.evaluate(() => {
    delete (document as any).visibilityState;
    delete (document as any).hidden;
    document.dispatchEvent(new Event('visibilitychange'));
  });
const dispatchViewportResize = (page: Page) =>
  page.evaluate(() => {
    window.visualViewport?.dispatchEvent(new Event('resize'));
    window.dispatchEvent(new Event('resize'));
  });

type Pre = 'closed' | 'menu' | 'assistant' | 'helpmenu' | 'usermenu';
async function prepare(page: Page, pre: Pre): Promise<void> {
  if (pre === 'menu') await burger(page).tap();
  if (pre === 'assistant') await page.locator('[data-testid="assistant-trigger"], button[aria-label*="ssistant" i]').first().tap({ timeout: 5000 });
  // There is no Help menu in the header any more: the old selector made the tap wait until the 180 s timeout (run1).
  // 'helpmenu' now opens the feedback button ("Flag a bug or send feedback"), 'usermenu' the account menu.
  if (pre === 'helpmenu') await page.getByRole('button', { name: /help|feedback/i }).first().tap({ timeout: 5000 });
  if (pre === 'usermenu') await page.getByRole('button', { name: /account menu/i }).first().tap({ timeout: 5000 });
  await settle(page, 500);
}

const PRES: Pre[] = ['closed', 'menu', 'assistant', 'helpmenu'];

test.afterEach(async ({}, info) => {
  writeFileSync(`${LOGS}/trace-${info.title.replace(/\W+/g, '_')}.json`, JSON.stringify(trace, null, 1));
  for (const k of Object.keys(trace)) delete trace[k];
});

test.describe('a. visibility', () => {
  for (const pre of PRES) {
    test(`a. CDP frozen->active, pre=${pre}`, async ({ browser }) => {
      const { ctx, page } = await phone(browser);
      await open(page);
      const cdp = await ctx.newCDPSession(page);
      await prepare(page, pre);
      await freeze(cdp, 3000, page);
      await cycles(page, `a-cdp-${pre}`);
      await ctx.close();
    });

    test(`a. synthetic hidden->visible, pre=${pre}`, async ({ browser }) => {
      const { ctx, page } = await phone(browser);
      await open(page);
      await prepare(page, pre);
      await syntheticHidden(page);
      await settle(page, 2000);
      await syntheticVisible(page);
      await cycles(page, `a-synth-${pre}`);
      await ctx.close();
    });
  }

  test('a. second page brought to front (visibilityState recorded)', async ({ browser }) => {
    const { ctx, page } = await phone(browser);
    await open(page);
    await burger(page).tap();
    await settle(page);
    const other = await ctx.newPage();
    await other.goto(`${BASE}/app/login`);
    await other.bringToFront();
    await settle(page, 2000);
    const during = await page.evaluate(() => document.visibilityState);
    await other.close();
    await page.bringToFront();
    await settle(page);
    (trace['a-bringToFront'] ??= []).push({ visibilityDuringOther: during } as never);
    await cycles(page, 'a-bringToFront');
    await ctx.close();
  });

  test('a. cookies cleared while backgrounded (session expiry)', async ({ browser }) => {
    const { ctx, page } = await phone(browser);
    await open(page);
    const cdp = await ctx.newCDPSession(page);
    await cdp.send('Page.setWebLifecycleState', { state: 'frozen' });
    await ctx.clearCookies();
    await new Promise((r) => setTimeout(r, 2000));
    await cdp.send('Page.setWebLifecycleState', { state: 'active' });
    await settle(page, 2500);
    // The redirect to /login can land a few seconds after resume (it raced the burger tap on the branch build).
    await page.waitForURL(/\/login/, { timeout: 6000 }).catch(() => undefined);
    if (page.url().includes('/login') || !(await burger(page).count())) return test.info().annotations.push({ type: 'note', description: 'redirected to login, no shell' });
    await cycles(page, 'a-cookies');
    await ctx.close();
  });
});

test.describe('b. bfcache', () => {
  for (const pre of ['closed', 'menu'] as Pre[]) {
    test(`b. navigate away and goBack, pre=${pre}`, async ({ browser }) => {
      const { ctx, page } = await phone(browser);
      await open(page);
      await prepare(page, pre);
      await page.goto(BASE.replace('localhost', '127.0.0.1') + '/app/login');
      await settle(page, 1500);
      await page.goBack();
      await settle(page, 1500);
      const nrr = await page.evaluate(() => {
        const n = performance.getEntriesByType('navigation')[0] as PerformanceNavigationTiming & { notRestoredReasons?: unknown };
        return JSON.stringify(n.notRestoredReasons ?? null);
      });
      (trace[`b-goback-${pre}`] ??= []).push({ notRestoredReasons: nrr } as never);
      await page.getByRole('button', { name: 'Toggle navigation' }).waitFor();
      await cycles(page, `b-goback-${pre}`);
      await ctx.close();
    });

    test(`b. synthetic pagehide+pageshow persisted, pre=${pre}`, async ({ browser }) => {
      const { ctx, page } = await phone(browser);
      await open(page);
      await prepare(page, pre);
      await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pagehide', { persisted: true })));
      await settle(page, 1000);
      await page.evaluate(() => window.dispatchEvent(new PageTransitionEvent('pageshow', { persisted: true })));
      await cycles(page, `b-synth-${pre}`);
      await ctx.close();
    });
  }
});

test.describe('c. long idle', () => {
  for (const [name, ms] of [['10min', 10 * 60_000], ['1h', 3_600_000], ['25h', 25 * 3_600_000]] as const) {
    for (const pre of ['closed', 'menu'] as Pre[]) {
      test(`c. clock +${name} frozen, pre=${pre}`, async ({ browser }) => {
        const { ctx, page } = await phone(browser);
        await page.clock.install();
        await open(page);
        const cdp = await ctx.newCDPSession(page);
        await prepare(page, pre);
        await cdp.send('Page.setWebLifecycleState', { state: 'frozen' });
        await new Promise((r) => setTimeout(r, 1500));
        await cdp.send('Page.setWebLifecycleState', { state: 'active' });
        await page.clock.fastForward(ms);
        await settle(page, 1500);
        if (page.url().includes('/login')) return test.info().annotations.push({ type: 'note', description: 'redirected to login' });
        await syntheticHidden(page).then(() => syntheticVisible(page));
        await cycles(page, `c-${name}-${pre}`);
        await ctx.close();
      });
    }
  }
});

test.describe('d. viewport', () => {
  for (const pre of ['closed', 'menu'] as Pre[]) {
    test(`d. address bar show/hide, pre=${pre}`, async ({ browser }) => {
      const { ctx, page } = await phone(browser);
      await open(page);
      await prepare(page, pre);
      for (const h of [915, 839, 915, 839]) {
        await page.setViewportSize({ width: 412, height: h });
        await dispatchViewportResize(page);
        await settle(page, 300);
      }
      await cycles(page, `d-addrbar-${pre}`);
      await ctx.close();
    });

    test(`d. landscape and back, pre=${pre}`, async ({ browser }) => {
      const { ctx, page } = await phone(browser);
      await open(page);
      await prepare(page, pre);
      await page.setViewportSize({ width: 915, height: 412 });
      await settle(page, 600);
      await page.setViewportSize({ width: 412, height: 839 });
      await settle(page, 600);
      await cycles(page, `d-orient-${pre}`);
      await ctx.close();
    });

    test(`d. landscape while frozen, pre=${pre}`, async ({ browser }) => {
      const { ctx, page } = await phone(browser);
      await open(page);
      const cdp = await ctx.newCDPSession(page);
      await prepare(page, pre);
      await cdp.send('Page.setWebLifecycleState', { state: 'frozen' });
      await page.setViewportSize({ width: 915, height: 412 }).catch(() => undefined);
      await new Promise((r) => setTimeout(r, 1500));
      await cdp.send('Page.setWebLifecycleState', { state: 'active' });
      await page.setViewportSize({ width: 412, height: 839 });
      await settle(page, 600);
      await cycles(page, `d-frozen-orient-${pre}`);
      await ctx.close();
    });
  }
});

test.describe('e. combos', () => {
  test('e. menu open -> freeze -> resize -> resume -> visible', async ({ browser }) => {
    const { ctx, page } = await phone(browser);
    await open(page);
    const cdp = await ctx.newCDPSession(page);
    await burger(page).tap();
    await settle(page);
    await cdp.send('Page.setWebLifecycleState', { state: 'frozen' });
    await page.setViewportSize({ width: 412, height: 915 }).catch(() => undefined);
    await new Promise((r) => setTimeout(r, 1500));
    await cdp.send('Page.setWebLifecycleState', { state: 'active' });
    await dispatchViewportResize(page);
    await syntheticHidden(page);
    await syntheticVisible(page);
    await cycles(page, 'e-combo');
    await ctx.close();
  });

  test('e. rapid double tap, tap during transition, tap right after resume', async ({ browser }) => {
    const { ctx, page } = await phone(browser);
    await open(page);
    const cdp = await ctx.newCDPSession(page);
    for (let i = 0; i < 3; i++) {
      await burger(page).tap();
      await burger(page).tap();
      await settle(page, 500);
      const s = await snapshot(page);
      (trace['e-double'] ??= []).push(s);
      if (s.burgerOpened) expect.soft(s.visible && s.hitInsideNavbar, `double tap #${i}: burger open but navbar not visible`).toBe(true);
    }
    for (let i = 0; i < 3; i++) {
      await burger(page).tap();
      await page.waitForTimeout(100);
      await burger(page).tap();
      await page.waitForTimeout(100);
      await burger(page).tap();
      await settle(page, 500);
      const s = await snapshot(page);
      (trace['e-midtransition'] ??= []).push(s);
      if (s.burgerOpened) expect.soft(s.visible && s.hitInsideNavbar, `mid-transition #${i}: burger open but navbar not visible`).toBe(true);
      else await burger(page).tap().then(() => settle(page, 500));
    }
    // tap in the same task as resume, before any rAF
    await page.evaluate(() => undefined);
    await cdp.send('Page.setWebLifecycleState', { state: 'frozen' });
    await new Promise((r) => setTimeout(r, 1500));
    await cdp.send('Page.setWebLifecycleState', { state: 'active' });
    await burger(page).tap();
    await settle(page, 500);
    const s = await snapshot(page);
    (trace['e-after-resume'] ??= []).push(s);
    if (s.burgerOpened) expect.soft(s.visible && s.hitInsideNavbar, 'tap right after resume: burger open but navbar not visible').toBe(true);
    await ctx.close();
  });

  test('e. assistant drawer open, background, then burger', async ({ browser }) => {
    const { ctx, page } = await phone(browser);
    await open(page);
    const cdp = await ctx.newCDPSession(page);
    await prepare(page, 'assistant');
    await freeze(cdp, 2000, page);
    await syntheticHidden(page).then(() => syntheticVisible(page));
    await cycles(page, 'e-assistant');
    await ctx.close();
  });
});
