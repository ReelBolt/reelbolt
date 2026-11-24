import { devices, expect, test, type BrowserContext, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { dismissTour, phoneContext } from './mobile-nav-target';
import { logsDir, saveAuthState } from './logs-dir';

/**
 * Initiative mobile-nav-stuck, T-001, session / per-user-state matrix. Owner facts: a reload does not fix the stuck
 * navbar, only logging out and in does; it is NOT session expiry. So this spec mutates, one at a time, what logout/login
 * replaces and a reload keeps (cookie contents, duplicate cookies, storage), freezes and resumes the tab, reloads, taps the burger and asserts
 * the VISIBLE navbar with real content (on-screen rect, elementFromPoint inside it, nav links visible).
 * A redirect to /login counts as "not stuck" (the session-expiry path worked).
 *
 *   set -a; source ~/.config/reelbolt/local-staging.env; set +a
 *   cd e2e && npx playwright test -c playwright.mobile-nav.config.ts mobile-nav-session
 */
const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');
const EMAIL = process.env.LOCAL_STAGING_EMAIL ?? '';
const PASSWORD = process.env.LOCAL_STAGING_PASSWORD ?? '';
const LOGS = logsDir();
const STATE = `${LOGS}/auth-state-session.json`;
mkdirSync(`${LOGS}/shots`, { recursive: true });
const results: Record<string, unknown> = {};
// A failed test restarts the worker and loses an afterAll write: persist per test.
test.afterEach(async ({}, info) => {
  if (Object.keys(results).length) writeFileSync(`${LOGS}/session-${info.title.replace(/\W+/g, '_').slice(0, 60)}.json`, JSON.stringify(results, null, 1));
  for (const k of Object.keys(results)) delete results[k];
});

test.beforeAll(async ({ browser }) => {
  test.skip(!EMAIL || !PASSWORD, 'LOCAL_STAGING_EMAIL/PASSWORD not set');
  const ctx = await phoneContext(browser);
  const page = await ctx.newPage();
  await page.goto('/app/login');
  await page.getByPlaceholder('you@example.com').fill(EMAIL);
  await page.getByPlaceholder('Your password').fill(PASSWORD);
  await page.getByRole('button', { name: 'Sign in' }).click();
  await page.waitForURL(/\/app\/(dashboard|projects)/);
  await saveAuthState(ctx, STATE);
  await ctx.close();
});

const burger = (p: Page) => p.getByRole('button', { name: 'Toggle navigation' });

async function snap(page: Page) {
  return page.evaluate(() => {
    const nav = document.querySelector<HTMLElement>('nav.mantine-AppShell-navbar');
    const vw = window.innerWidth;
    const r = nav?.getBoundingClientRect();
    const hit = r ? document.elementFromPoint(Math.min(Math.max(r.left + r.width / 2, 0), vw - 1), Math.min(Math.max(r.top + r.height / 2, 0), innerHeight - 1)) : null;
    const links = Array.from(nav?.querySelectorAll<HTMLElement>('a[href]') ?? []).filter((a) => {
      const q = a.getBoundingClientRect();
      return q.width > 0 && q.height > 0 && q.right > 0 && q.left < vw;
    });
    const b = document.querySelector('button[aria-label="Toggle navigation"]');
    return {
      url: location.pathname,
      burgerOpened: b?.querySelector('.mantine-Burger-burger')?.hasAttribute('data-opened') ?? null,
      navDataOpened: nav?.hasAttribute('data-opened') ?? null,
      rect: r ? [r.left, r.top, r.width, r.height].map(Math.round) : null,
      onScreen: !!r && r.width > 0 && r.height > 100 && r.right > 1 && r.left >= -1 && r.left < vw - 1,
      hitInside: !!hit && !!nav?.contains(hit),
      visibleLinks: links.length,
      navTextLen: (nav?.innerText ?? '').length,
      switcher: !!nav?.querySelector('[class*="OrgSwitcher"], button[aria-haspopup]'),
      navVar: getComputedStyle(document.documentElement).getPropertyValue('--app-shell-navbar-transform').trim(),
      inert: nav?.hasAttribute('inert') ?? null,
      navHtmlLen: nav?.innerHTML.length ?? 0,
    };
  });
}

async function background(page: Page, ctx: BrowserContext) {
  const cdp = await ctx.newCDPSession(page);
  await cdp.send('Page.setWebLifecycleState', { state: 'frozen' });
  await page.waitForTimeout(300);
  await cdp.send('Page.setWebLifecycleState', { state: 'active' });
}

type Setup = (ctx: BrowserContext, page: Page) => Promise<void>;

async function scenario(browser: import('@playwright/test').Browser, name: string, setup: Setup, opts: { reload?: boolean } = {}) {
  const ctx = await phoneContext(browser, STATE);
  const page = await ctx.newPage();
  const errors: string[] = [];
  page.on('console', (m) => m.type() === 'error' && errors.push(m.text().slice(0, 160)));
  await page.goto('/app/dashboard');
  await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => undefined);
  await burger(page).waitFor();
  await dismissTour(page);
  await setup(ctx, page);
  await background(page, ctx);
  if (opts.reload !== false) await page.reload();
  await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => undefined);
  await page.waitForTimeout(1200);
  await dismissTour(page);
  let out: Record<string, unknown>;
  if (/\/login/.test(new URL(page.url()).pathname)) out = { redirectedToLogin: true, url: page.url() };
  else {
    const has = await burger(page).count();
    if (!has) out = { noBurger: true, url: page.url(), snap: await snap(page) };
    else {
      await burger(page).tap();
      await page.waitForTimeout(500);
      const s = await snap(page);
      out = { snap: s };
      expect.soft(s.onScreen, `${name}: navbar on screen ${JSON.stringify(s.rect)}`).toBe(true);
      expect.soft(s.hitInside, `${name}: elementFromPoint inside navbar`).toBe(true);
      expect.soft(s.visibleLinks, `${name}: nav links visible`).toBeGreaterThan(3);
    }
  }
  out.errors = errors.slice(0, 6);
  results[name] = out;
  await page.screenshot({ path: `${LOGS}/shots/session-${name.replace(/\W+/g, '_')}.png` }).catch(() => undefined);
  await ctx.close();
}

const editCookie = async (ctx: BrowserContext, name: string, fn: (c: any) => any | null) => {
  const all = await ctx.cookies();
  const c = all.find((x) => x.name === name)!;
  await ctx.clearCookies({ name });
  const n = fn(c);
  if (n) await ctx.addCookies([n]);
};
const jwtWith = (tok: string, patch: Record<string, unknown>) => {
  const [h, p] = tok.split('.');
  const body = { ...JSON.parse(Buffer.from(p, 'base64url').toString()), ...patch };
  return `${h}.${Buffer.from(JSON.stringify(body)).toString('base64url')}.${'x'.repeat(43)}`;
};
const apiAnswer = (status: number, body: unknown, pattern: RegExp): Setup => async (ctx) => {
  await ctx.route(pattern, (r) => r.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) }));
};


const userCookie = (c: any) => JSON.parse(decodeURIComponent(c.value));
const withUser = (patch: Record<string, unknown>): Setup => (ctx) =>
  editCookie(ctx, 'reelbolt_user', (c) => ({ ...c, value: encodeURIComponent(JSON.stringify({ ...userCookie(c), ...patch })) }));

test.describe('reelbolt_user cookie contents', () => {
  for (const [name, patch] of Object.entries({
    odd_orgName: { orgName: "100% +a+b %41 é中文 😀 <b>", orgKind: 'Team' },
    long_orgName: { orgName: 'x'.repeat(3000), orgKind: 'Team' },
    locale_odd: { locale: 'zz-ZZ' },
    role_viewer: { orgRole: 'Viewer' },
    other_org: { org: '00000000-0000-4000-8000-0000000000aa', orgKind: 'Team', orgName: 'Ghost', orgRole: 'Viewer' },
  })) {
    test(name, async ({ browser }) => { await scenario(browser, `cookie_${name}`, withUser(patch)); });
  }
  test('user cookie corrupted', async ({ browser }) => {
    await scenario(browser, 'cookie_corrupt', (ctx) => editCookie(ctx, 'reelbolt_user', (c) => ({ ...c, value: '%7Bnot-json' })));
  });
  test('duplicate reelbolt_user cookies (path=/ and path=/app, old and new)', async ({ browser }) => {
    await scenario(browser, 'cookie_duplicate', async (ctx, page) => {
      const c = (await ctx.cookies()).find((x) => x.name === 'reelbolt_user')!;
      const u = userCookie(c);
      await ctx.addCookies([{ ...c, path: '/app', value: encodeURIComponent(JSON.stringify({ ...u, orgName: 'STALE', orgRole: 'Viewer' })) }]);
      await page.evaluate(() => (document.cookie = 'reelbolt_user=%7B%7D; path=/; SameSite=Lax'));
      results.duplicate_cookie_header = await page.evaluate(() => document.cookie.split('; ').filter((x) => x.startsWith('reelbolt_user')).length);
    });
  });
  test('switch-org to the same org then reload', async ({ browser }) => {
    await scenario(browser, 'switch_org_same', async (_c, page) => {
      const org = await page.evaluate(() => JSON.parse(decodeURIComponent(document.cookie.split('; ').find((c) => c.startsWith('reelbolt_user='))!.slice(14))).org);
      results.switch_org_status = await page.evaluate(async (o) => (await fetch('/api/v1/auth/switch-org', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ orgId: o }) })).status, org);
      results.cookies_after_switch = (await _c.cookies()).filter((x) => x.name === 'reelbolt_user').map((x) => `${x.path} exp=${x.expires}`);
    });
  });
});

/** Every key a normal session leaves behind, one at a time, plus the whole set at once. */
const SEEDS: Record<string, string> = {
  'reelbolt:editor:full-view': '1',
  'reelbolt:editor:touch-drag': '1',
  'reelbolt.assistant.browserNotify': 'on',
  'reelbolt-install-dismissed': '1',
  'reelbolt.alpha-banner.dismissed': '1',
  'rf-cookie-consent': 'garbage',
  'rf-visitor-id': 'x',
  'mantine-color-scheme-value': 'dark',
};
test.describe('persisted storage mutated one key at a time', () => {
  for (const [k, v] of Object.entries(SEEDS)) {
    test(`seed ${k}`, async ({ browser }) => {
      await scenario(browser, `storage_${k}`, (_c, page) => page.evaluate(([a, b]) => localStorage.setItem(a, b), [k, v]));
    });
  }
  test('every existing app key corrupted and the seeds together', async ({ browser }) => {
    await scenario(browser, 'storage_all_garbage', (_c, page) =>
      page.evaluate((seeds) => {
        for (let i = 0; i < localStorage.length; i++) localStorage.setItem(localStorage.key(i)!, '{"open":true,"docked":true,"step":99,"dismissed":true}');
        for (const [k, v] of Object.entries(seeds)) localStorage.setItem(k, v);
        sessionStorage.setItem('x', '1');
      }, SEEDS));
  });
  test('storage dump after normal use (assistant opened and docked, tour touched)', async ({ browser }) => {
    const ctx = await phoneContext(browser, STATE);
    const page = await ctx.newPage();
    await page.goto('/app/dashboard');
    await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => undefined);
    await dismissTour(page);
    await page.getByRole('button', { name: /assistant/i }).first().tap({ timeout: 5000 }).catch(() => undefined);
    await page.waitForTimeout(1500);
    results.storage_dump = await page.evaluate(async () => ({
      local: Object.fromEntries(Object.keys(localStorage).map((k) => [k, (localStorage.getItem(k) ?? '').slice(0, 120)])),
      session: Object.fromEntries(Object.keys(sessionStorage).map((k) => [k, (sessionStorage.getItem(k) ?? '').slice(0, 120)])),
      idb: (await (indexedDB as any).databases?.())?.map((d: any) => d.name),
      caches: await caches.keys(),
    }));
    results.cookies_dump = (await ctx.cookies()).map((c) => `${c.name} path=${c.path} exp=${c.expires}`);
    await ctx.close();
  });
});

test.describe('installed PWA (display-mode standalone emulated)', () => {
  test('standalone, frozen/resumed, then reload', async ({ browser }) => {
    const ctx = await phoneContext(browser, STATE);
    const page = await ctx.newPage();
    const cdp = await ctx.newCDPSession(page);
    await cdp.send('Emulation.setEmulatedMedia', { features: [{ name: 'display-mode', value: 'standalone' }] });
    await page.goto('/app/dashboard');
    await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => undefined);
    await page.reload();
    await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => undefined);
    await background(page, ctx);
    await dismissTour(page);
    await burger(page).tap();
    await page.waitForTimeout(500);
    const s = await snap(page);
    results.standalone = { standalone: await page.evaluate(() => matchMedia('(display-mode: standalone)').matches), s };
    expect.soft(s.onScreen && s.hitInside && s.visibleLinks > 3, `standalone: navbar visible ${JSON.stringify(s)}`).toBe(true);
    await ctx.close();
  });
});
