import { expect, test, type Page } from '@playwright/test';
import { mkdirSync, writeFileSync } from 'node:fs';
import { LOCAL_WEB, TARGET, dismissTour, phoneContext } from './mobile-nav-target';
import { logsDir, saveAuthState } from './logs-dir';

/**
 * Initiative mobile-nav-stuck: deterministic regression for the burger/navbar desync (burger shows X, navbar
 * stays off screen). Each case forces one mechanism the AppShell hardening guards against, then asserts the
 * VISIBLE navbar: on-screen rect, elementFromPoint at its centre inside it, nav links visible.
 *
 *   set -a; source ~/.config/reelbolt/local-staging.env; set +a
 *   cd e2e && npx playwright test -c playwright.mobile-nav.config.ts mobile-nav-desync               # BEFORE (staging web)
 *   MOBILE_NAV_LOCAL_WEB=http://localhost:3918 npx playwright test -c playwright.mobile-nav.config.ts mobile-nav-desync  # AFTER
 */
const EMAIL = process.env.LOCAL_STAGING_EMAIL ?? '';
const PASSWORD = process.env.LOCAL_STAGING_PASSWORD ?? '';
const LOGS = logsDir();
const STATE = `${LOGS}/auth-state-desync.json`;
mkdirSync(`${LOGS}/shots`, { recursive: true });
const results: Record<string, unknown> = {};
// A failed test restarts the worker, so persist per test rather than once in afterAll.
test.afterEach(async ({}, info) => {
  writeFileSync(`${LOGS}/desync-${TARGET}-${info.title.replace(/\W+/g, '_').slice(0, 60)}.json`, JSON.stringify(results, null, 1));
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
    const vw = innerWidth;
    const r = nav?.getBoundingClientRect();
    const cx = r ? Math.min(Math.max(r.left + r.width / 2, 0), vw - 1) : 0;
    const cy = r ? Math.min(Math.max(r.top + r.height / 2, 0), innerHeight - 1) : 0;
    const hit = document.elementFromPoint(cx, cy);
    const links = Array.from(nav?.querySelectorAll<HTMLElement>('a[href]') ?? []).filter((a) => {
      const q = a.getBoundingClientRect();
      return q.width > 0 && q.height > 0 && q.right > 0 && q.left < vw && getComputedStyle(a).visibility !== 'hidden';
    });
    const cs = nav ? getComputedStyle(nav) : null;
    return {
      burgerOpened: document.querySelector('button[aria-label="Toggle navigation"] .mantine-Burger-burger')?.hasAttribute('data-opened') ?? null,
      navDataOpened: nav?.hasAttribute('data-opened') ?? null,
      rect: r ? [r.left, r.top, r.width, r.height].map(Math.round) : null,
      onScreen: !!r && r.width > 0 && r.height > 100 && r.right > 1 && r.left >= -1 && r.left < vw - 1,
      hit: hit ? `${hit.tagName}.${String(hit.className).slice(0, 50)}` : null,
      hitInside: !!hit && !!nav?.contains(hit),
      visibleLinks: links.length,
      transform: cs?.transform,
      visibility: cs?.visibility,
      zIndex: cs?.zIndex,
      navVar: getComputedStyle(document.documentElement).getPropertyValue('--app-shell-navbar-transform').trim(),
    };
  });
}
type Snap = Awaited<ReturnType<typeof snap>>;

function expectVisible(name: string, s: Snap) {
  results[name] = s;
  expect.soft(s.onScreen, `${name}: navbar on screen, rect ${JSON.stringify(s.rect)}`).toBe(true);
  expect.soft(s.hitInside, `${name}: elementFromPoint inside navbar, got ${s.hit}`).toBe(true);
  expect.soft(s.visibleLinks, `${name}: nav links visible`).toBeGreaterThan(3);
}

async function withPhone(browser: import('@playwright/test').Browser, name: string, body: (page: Page) => Promise<void>) {
  const ctx = await phoneContext(browser, STATE);
  const page = await ctx.newPage();
  await page.goto('/app/dashboard');
  // The worktree build keeps a request open behind the route (networkidle never fires), so bound the wait.
  await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => undefined);
  await burger(page).waitFor();
  await dismissTour(page);
  await page.waitForTimeout(800);
  try {
    await body(page);
  } finally {
    await page.screenshot({ path: `${LOGS}/shots/desync-${TARGET}-${name}.png` }).catch(() => undefined);
    await ctx.close();
  }
}

test('target sanity: AFTER serves the worktree build (navbar carries data-opened)', async ({ browser }) => {
  await withPhone(browser, 'sanity', async (page) => {
    await burger(page).tap();
    await page.waitForTimeout(500);
    const s = await snap(page);
    results.sanity = { target: TARGET, localWeb: LOCAL_WEB ?? null, ...s };
    if (LOCAL_WEB) expect(s.navDataOpened, 'AFTER must serve the hardened build').toBe(true);
    expect(s.burgerOpened).toBe(true);
  });
});

test('(a) stale inline Mantine app-shell <style> after the tap', async ({ browser }) => {
  await withPhone(browser, 'a_stale_style', async (page) => {
    const frozen = await page.evaluate(() => {
      const pick = () =>
        Array.from(document.querySelectorAll<HTMLStyleElement>('style[data-mantine-styles="inline"]')).filter((el) =>
          (el.textContent ?? '').includes('app-shell'),
        );
      const stale = new Map(pick().map((el) => [el, el.textContent ?? '']));
      let budget = 200; // never ping-pong forever with a renderer that rewrites the style
      const revert = () => {
        if (budget-- <= 0) return;
        for (const el of pick()) {
          const old = stale.get(el) ?? Array.from(stale.values())[0];
          if (old !== undefined && el.textContent !== old) el.textContent = old;
        }
      };
      new MutationObserver(revert).observe(document.head, { subtree: true, childList: true, characterData: true });
      new MutationObserver(revert).observe(document.body, { subtree: true, childList: true, characterData: true });
      return stale.size;
    });
    expect(frozen, 'an inline app-shell style exists to freeze').toBeGreaterThan(0);
    await burger(page).tap();
    await page.waitForTimeout(600);
    expectVisible('a_stale_style', await snap(page));
  });
});

test('(b1) stuck data-resizing on the AppShell root', async ({ browser }) => {
  await withPhone(browser, 'b1_resizing', async (page) => {
    await page.evaluate(() => {
      document.documentElement.setAttribute('data-resizing', 'true');
      document.querySelector('.mantine-AppShell-root')?.setAttribute('data-resizing', 'true');
    });
    await burger(page).tap();
    await page.waitForTimeout(600);
    expectVisible('b1_resizing', await snap(page));
  });
});

// KNOWN GAP: fails on BEFORE *and* on AFTER. With transitions frozen the navbar keeps translateX(-100%) although
// burger and nav[data-opened] both say open (transform:none !important still transitions). Marked test.fail so it
// flips red the day the shell stops depending on the transform transition finishing.
test('(b2) transitions never run (CDP Animation.setPlaybackRate 0)', async ({ browser }) => {
  test.fail(true, 'known gap: open state still relies on the transform transition completing');
  await withPhone(browser, 'b2_paused', async (page) => {
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Animation.enable');
    await cdp.send('Animation.setPlaybackRate', { playbackRate: 0 });
    await burger(page).tap();
    await page.waitForTimeout(800);
    expectVisible('b2_paused', await snap(page));
  });
});

// By design the open phone menu (z-index 190) stays BELOW Mantine modals/drawers (200): a dialog opened from the
// header must never hide under the menu (QA finding). So a stray z-200 overlay still covers it; opening the menu closes
// the assistant sheet, the only z-200 layer the shell owns. Marked test.fail so it flips red if that ordering changes.
test('(c) leftover z-index 200 overlay below the header', async ({ browser }) => {
  test.fail(true, 'by design: modals (200) stay above the open phone menu (190)');
  await withPhone(browser, 'c_overlay', async (page) => {
    await page.evaluate(() => {
      const top = document.querySelector('header.mantine-AppShell-header')?.getBoundingClientRect().bottom ?? 60;
      const o = document.createElement('div');
      o.className = 'mantine-Drawer-overlay test-leftover-overlay';
      Object.assign(o.style, { position: 'fixed', left: '0', right: '0', bottom: '0', top: `${top}px`, zIndex: '200', background: 'rgba(0,0,0,.4)' });
      document.body.appendChild(o);
    });
    await burger(page).tap();
    await page.waitForTimeout(600);
    expectVisible('c_overlay', await snap(page));
  });
});

test('(d) freeze and resume with the menu open, then tap opens visibly', async ({ browser }) => {
  await withPhone(browser, 'd_freeze', async (page) => {
    await burger(page).tap();
    await page.waitForTimeout(500);
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Page.setWebLifecycleState', { state: 'frozen' });
    await page.waitForTimeout(500);
    await cdp.send('Page.setWebLifecycleState', { state: 'active' });
    await page.waitForTimeout(500);
    const resumed = await snap(page);
    results.d_resumed = resumed;
    // Burger and navbar must agree after resume, whatever state the shell chose.
    expect.soft(resumed.burgerOpened === resumed.onScreen, `d: burger ${resumed.burgerOpened} vs navbar on screen ${resumed.onScreen}`).toBe(true);
    // If the shell kept the menu open across resume, the first tap legitimately closes it; tap until the burger says open.
    for (let i = 0; i < 2; i++) {
      await burger(page).tap();
      await page.waitForTimeout(600);
      if ((await snap(page)).burgerOpened) break;
    }
    expectVisible('d_freeze', await snap(page));
  });
});

/** (e) Lead from run1 scenarios 3/7: something open before a freeze, then resume and tap. Logs a timestamped event sequence. */
for (const pre of ['menu', 'helpmenu', 'usermenu'] as const) {
  test(`(e) freeze/resume with pre=${pre}, event sequence logged`, async ({ browser }) => {
    test.setTimeout(90_000);
    await withPhone(browser, `e_${pre}`, async (page) => {
      await page.evaluate(() => {
        const w = window as any;
        w.__ev = [];
        const t0 = performance.now();
        const log = (e: string) => w.__ev.push(`${Math.round(performance.now() - t0)} ${e}`);
        w.__log = log;
        for (const n of ['visibilitychange', 'freeze', 'resume', 'pageshow', 'focus', 'blur']) {
          document.addEventListener(n, () => log(`${n}:${document.visibilityState}`), true);
          window.addEventListener(n, () => log(`win.${n}`), true);
        }
        const b = document.querySelector('.mantine-Burger-burger');
        const nav = document.querySelector('nav.mantine-AppShell-navbar');
        new MutationObserver(() => log(`render burger=${b?.hasAttribute('data-opened')} nav=${nav?.hasAttribute('data-opened')} var=${getComputedStyle(document.documentElement).getPropertyValue('--app-shell-navbar-transform').trim()}`))
          .observe(document.body, { subtree: true, attributes: true, attributeFilter: ['data-opened'] });
      });
      let opener = 'burger';
      if (pre === 'menu') await burger(page).tap();
      else {
        // No Help menu exists in the header; the feedback button stands in for it. Usermenu = account menu.
        const sel = pre === 'helpmenu' ? 'button: /help|feedback/i' : 'button: /account menu/i';
        const loc = page.getByRole('button', { name: pre === 'helpmenu' ? /help|feedback/i : /account menu/i }).first();
        opener = (await loc.count()) ? sel : `NOT FOUND: ${sel}`;
        if (await loc.count()) await loc.tap({ timeout: 5000 });
      }
      await page.waitForTimeout(500);
      const cdp = await page.context().newCDPSession(page);
      await page.evaluate(() => (window as any).__log('FREEZE'));
      await cdp.send('Page.setWebLifecycleState', { state: 'frozen' });
      await page.waitForTimeout(3000);
      await cdp.send('Page.setWebLifecycleState', { state: 'active' });
      await page.evaluate(() => (window as any).__log('RESUMED'));
      await page.waitForTimeout(500);
      const leftovers = await page.evaluate(() => ({
        portals: document.querySelectorAll('[data-portal]').length,
        dropdowns: document.querySelectorAll('.mantine-Menu-dropdown, .mantine-Popover-dropdown').length,
        overlays: document.querySelectorAll('.mantine-Overlay-root').length,
        bodyStyle: document.body.getAttribute('style'),
        scrollLocked: document.body.getAttribute('data-scroll-locked'),
        active: `${document.activeElement?.tagName}.${String(document.activeElement?.className).slice(0, 40)}`,
      }));
      const resumed = await snap(page);
      await page.evaluate(() => (window as any).__log('TAP'));
      // A modal opener (feedback dialog) legitimately blocks the burger; record that instead of hanging to the timeout.
      let tapBlocked: string | null = null;
      await burger(page).tap({ timeout: 5000 }).catch((e) => { tapBlocked = String(e).split('\n').find((l) => /intercepts/.test(l))?.trim().slice(0, 200) ?? 'tap timed out'; });
      if (tapBlocked) {
        await page.keyboard.press('Escape');
        await page.waitForTimeout(400);
        await burger(page).tap({ timeout: 5000 }).catch(() => undefined);
      }
      await page.waitForTimeout(600);
      const after1 = await snap(page);
      results[`e_${pre}_tapBlocked`] = tapBlocked;
      let recovered: Snap | null = null;
      if (!(after1.onScreen && after1.hitInside)) {
        await burger(page).tap({ timeout: 5000 }).catch(() => undefined);
        await page.waitForTimeout(600);
        recovered = await snap(page);
      }
      const events = await page.evaluate(() => (window as any).__ev);
      results[`e_${pre}`] = { opener, leftovers, resumed, after1, secondTap: recovered, events };
      expect.soft(resumed.burgerOpened === resumed.onScreen, `e_${pre}: after resume burger ${resumed.burgerOpened} vs navbar on screen ${resumed.onScreen}`).toBe(true);
      expect.soft(after1.burgerOpened === (after1.onScreen && after1.hitInside), `e_${pre}: after tap burger ${after1.burgerOpened} vs navbar visible, rect ${JSON.stringify(after1.rect)} hit ${after1.hit}`).toBe(true);
    });
  });
}

/**
 * (f) No simulation: a dark colour scheme on load. On master the SSR markup is light, the client renders dark, React
 * throws #418 (hydration mismatch) and client-renders the tree, leaving the server's app-shell
 * <style data-mantine-styles="inline"> orphaned. Its collapsed --app-shell-navbar-transform then wins on :root forever:
 * burger shows an X, navbar stays at x=-412. A reload keeps the preference; logout/login mounts the shell client-side.
 *   f1: persisted `mantine-color-scheme-value` = 'dark' (the header toggle).
 *   f2: scheme 'auto' on a phone in system dark mode (time-of-day dependent, which is why it looks random).
 * Checks: navbar visible after the tap, exactly one app-shell inline style, no hydration pageerror.
 * The last two are about the root cause and stay red until the hydration mismatch itself is fixed.
 */
for (const [name, scheme, media] of [['f1_dark_persisted', 'dark', null], ['f2_auto_system_dark', 'auto', 'dark']] as const) {
  test(`(${name}) dark colour scheme on load, reload, tap`, async ({ browser }) => {
    await withPhone(browser, name, async (page) => {
      if (media) await page.emulateMedia({ colorScheme: media });
      await page.evaluate((v) => localStorage.setItem('mantine-color-scheme-value', v), scheme);
      const pageErrors: string[] = [];
      page.on('pageerror', (e) => pageErrors.push(String(e.message).slice(0, 200)));
      await page.reload();
      await page.waitForLoadState('networkidle', { timeout: 8000 }).catch(() => undefined);
      await burger(page).waitFor();
      await dismissTour(page);
      await page.waitForTimeout(1200);
      const appShellStyles = await page.evaluate(
        () => Array.from(document.querySelectorAll('style[data-mantine-styles="inline"]')).filter((el) => (el.textContent ?? '').includes('app-shell')).length,
      );
      await burger(page).tap();
      await page.waitForTimeout(600);
      expectVisible(name, await snap(page));
      results[`${name}_root`] = { appShellStyles, pageErrors, scheme: await page.evaluate(() => document.documentElement.getAttribute('data-mantine-color-scheme')) };
      expect.soft(appShellStyles, `${name}: exactly one app-shell inline style (orphaned SSR style = hydration mismatch)`).toBe(1);
      expect.soft(pageErrors.filter((m) => /#418|hydrat/i.test(m)), `${name}: no hydration error on load`).toEqual([]);
    });
  });
}
