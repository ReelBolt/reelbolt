import { expect, test, type BrowserContext, type Page } from '@playwright/test';

/**
 * T-002 shell-a11y. Runs on desktop (1400), tablet (820), pixel7 and iphone14 (webkit) projects of
 * playwright.usability.config.ts. Findings: A17, C12, C2, A19, C10, A08.
 */
const EMAIL = process.env.LOCAL_STAGING_EMAIL ?? '';
const PASSWORD = process.env.LOCAL_STAGING_PASSWORD ?? '';
test.skip(!EMAIL || !PASSWORD, 'LOCAL_STAGING_* not set');

/** Signs in once per project (rate limits and webkit stalls make 12 logins per project unreliable), then replays the session. */
const sessions = new Map<string, { cookies: Parameters<BrowserContext['addCookies']>[0]; local: Record<string, string> }>();

async function login(page: Page): Promise<void> {
  const key = test.info().project.name;
  const saved = sessions.get(key);
  if (saved) {
    await page.context().addCookies(saved.cookies);
    await page.addInitScript((kv) => {
      for (const [k, v] of Object.entries(kv)) if (!window.localStorage.getItem(k)) window.localStorage.setItem(k, v);
    }, saved.local);
    await page.goto('/app/dashboard');
    await page.waitForLoadState('networkidle');
    if (!/\/app\/(dashboard|projects)/.test(page.url())) throw new Error(`session replay landed on ${page.url()}`);
    return;
  }
  await page.goto('/app/login');
  await page.waitForLoadState('networkidle'); // submit before hydration is a native GET on webkit
  await page.locator('input[type="email"], input[autocomplete="username"], input[autocomplete="email"]').first().fill(EMAIL);
  await page.locator('input[type="password"]').first().fill(PASSWORD);
  const resp = page.waitForResponse((r) => r.request().method() === 'POST' && /auth|login/.test(r.url()), { timeout: 30_000 }).catch(() => null);
  await page.getByRole('button', { name: /log in|sign in/i }).click();
  const r = await resp;
  try {
    await page.waitForURL(/\/app\/(dashboard|projects)/, { timeout: 45_000, waitUntil: 'commit' });
  } catch (e) {
    throw new Error(`login did not leave /app/login (POST ${r?.url()} -> ${r?.status()}); at ${page.url()}`);
  }
  await page.waitForLoadState('networkidle');
  const st = await page.context().storageState();
  const local: Record<string, string> = {};
  for (const o of st.origins) for (const i of o.localStorage) local[i.name] = i.value;
  sessions.set(key, { cookies: st.cookies, local });
}

const vw = (page: Page): number => page.viewportSize()!.width;
const touchWidth = (page: Page): boolean => vw(page) <= 1024;

/** True when the element's centre hits the element (or a descendant) rather than something on top. */
const hitsItself = (el: Element): string => {
  const r = el.getBoundingClientRect();
  const top = document.elementFromPoint(r.x + r.width / 2, r.y + r.height / 2);
  if (top && (top === el || el.contains(top))) return 'ok';
  return `covered by ${top?.tagName}.${String(top?.className).slice(0, 60)}[${top?.getAttribute('data-testid') ?? ''}] at ${Math.round(r.x)},${Math.round(r.y)}`;
};

test.describe('signed out', () => {
  for (const path of ['/app/login', '/app/signup', '/app/forgot-password']) {
    test(`A08: exactly one h1 on ${path}`, async ({ page }) => {
      await page.goto(path);
      await page.waitForLoadState('networkidle');
      await expect(page.getByRole('heading', { level: 1 })).toHaveCount(1);
    });
  }

  test('A17: marketing opt-in label text is not overlapped by the checkbox or its row', async ({ page }) => {
    await page.goto('/app/signup');
    await page.waitForLoadState('networkidle');
    const box = page.getByTestId('marketing-optin-checkbox');
    await expect(box).toBeAttached();
    await box.scrollIntoViewIfNeeded();
    const res = await box.evaluate((input) => {
      const body = input.closest('.mantine-Checkbox-body') ?? input.parentElement!;
      const labelEl = body.querySelector('label') as HTMLElement | null;
      if (!labelEl) return { error: 'no label' };
      const lr = labelEl.getBoundingClientRect();
      // Rendered text box of the label (Range excludes padding the label itself may add).
      const range = document.createRange();
      range.selectNodeContents(labelEl);
      const tr = range.getBoundingClientRect();
      const control = (body.querySelector('.mantine-Checkbox-inner') ?? input) as HTMLElement;
      const cr = control.getBoundingClientRect();
      const overlapsText = cr.right > tr.left + 1 && cr.left < tr.right && cr.bottom > tr.top && cr.top < tr.bottom;
      const top = document.elementFromPoint(tr.left + 2, tr.top + tr.height / 2);
      const okHit = !!top && (top === labelEl || labelEl.contains(top) || top === input || top.contains(labelEl));
      return { overlapsText, okHit, hit: top?.tagName + '.' + (top?.className ?? ''), labelH: lr.height };
    });
    expect(res, JSON.stringify(res)).toMatchObject({ overlapsText: false, okHit: true });
  });
});

test.describe('signed in', () => {
  test.beforeEach(async ({ page }) => {
    await login(page);
  });

  test('C12: skip link is the first tab stop and moves focus to main', async ({ page }) => {
    await page.keyboard.press('Tab');
    const skip = page.getByRole('link', { name: 'Skip to main content' });
    await expect(skip).toBeFocused();
    // Visible while focused (not clipped to nothing).
    const box = await skip.boundingBox();
    expect(box && box.width > 20 && box.height > 10).toBe(true);
    await page.keyboard.press('Enter');
    await expect(page.locator('#main-content')).toBeFocused();
  });

  test('C12: skip link exists on app pages (agents, settings)', async ({ page }) => {
    for (const p of ['/app/agents', '/app/settings']) {
      await page.goto(p);
      await page.waitForLoadState('networkidle');
      await page.keyboard.press('Tab');
      await expect(page.getByRole('link', { name: 'Skip to main content' }), p).toBeFocused();
    }
  });

  test('C12: closed assistant drawer contributes no tab stops', async ({ page }) => {
    const seen: string[] = [];
    for (let i = 0; i < 25; i++) {
      await page.keyboard.press('Tab');
      seen.push(await page.evaluate(() => (document.activeElement?.closest('[data-testid="assistant-drawer"]') ? 'IN-DRAWER' : 'ok')));
    }
    expect(seen).not.toContain('IN-DRAWER');
  });

  test('C12/A19: header and nav controls are at least 44x44 on touch widths', async ({ page }) => {
    test.skip(!touchWidth(page), 'touch sizing applies up to 1024px / pointer:coarse');
    const small = await page.evaluate(() => {
      const out: string[] = [];
      const sel = '[data-testid="app-header"] button, [data-testid="app-header"] a[href]';
      for (const el of Array.from(document.querySelectorAll<HTMLElement>(sel))) {
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0) continue;
        if (r.width < 43.5 || r.height < 43.5) out.push(`${el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 20)} ${Math.round(r.width)}x${Math.round(r.height)}`);
      }
      return out;
    });
    expect(small, 'header controls under 44px').toEqual([]);
  });

  test('C12/A19: agents page controls and badges meet 44px / 12px on touch widths', async ({ page }) => {
    test.skip(!touchWidth(page), 'touch sizing applies up to 1024px / pointer:coarse');
    await page.goto('/app/agents');
    await page.waitForLoadState('networkidle');
    const res = await page.evaluate(() => {
      const small: string[] = [];
      for (const el of Array.from(document.querySelectorAll<HTMLElement>('main button, main .mantine-ActionIcon-root, [id="main-content"] button'))) {
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0 || el.closest('[inert],[hidden]')) continue;
        if (r.width < 43.5 || r.height < 43.5) small.push(`${el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 20)} ${Math.round(r.width)}x${Math.round(r.height)}`);
      }
      const tiny = Array.from(document.querySelectorAll<HTMLElement>('.mantine-Badge-label'))
        .filter((e) => e.getBoundingClientRect().width > 0 && parseFloat(getComputedStyle(e).fontSize) < 12)
        .map((e) => `${e.textContent} ${getComputedStyle(e).fontSize}`);
      return { small, tiny };
    });
    expect(res.small, 'controls under 44px').toEqual([]);
    expect(res.tiny, 'badges under 12px').toEqual([]);
  });

  test('C12/A19: nav links are at least 44px tall on touch widths (burger opened on phones)', async ({ page }) => {
    test.skip(!touchWidth(page), 'touch sizing applies up to 1024px / pointer:coarse');
    const toggle = page.getByRole('button', { name: 'Toggle navigation' });
    if (await toggle.isVisible()) await toggle.click();
    const nav = page.locator('nav, [class*="Navbar"]').first();
    await expect(nav.getByRole('link', { name: 'Projects' })).toBeVisible();
    const small = await nav.evaluate((root) =>
      Array.from(root.querySelectorAll<HTMLElement>('a[href]'))
        .map((a) => ({ t: a.textContent?.trim().slice(0, 16), r: a.getBoundingClientRect() }))
        .filter((x) => x.r.width > 0 && (x.r.height < 43.5 || x.r.width < 43.5))
        .map((x) => `${x.t} ${Math.round(x.r.width)}x${Math.round(x.r.height)}`),
    );
    expect(small, 'nav links under 44px').toEqual([]);
  });

  test('C10: header stays on one row and the nav toggle is reachable on narrow phones', async ({ page }) => {
    test.skip(vw(page) > 500, 'phone widths only');
    const res = await page.evaluate(() => {
      const header = document.querySelector<HTMLElement>('[data-testid="app-header"]')!;
      const hr = header.getBoundingClientRect();
      const els = Array.from(header.querySelectorAll<HTMLElement>('button, a[href]')).filter((e) => {
        const b = e.getBoundingClientRect();
        return b.width > 0 && b.height > 0;
      });
      const tops = els.map((e) => e.getBoundingClientRect());
      const detail = els.map((e) => `${e.getAttribute('aria-label') ?? e.textContent?.trim().slice(0, 12)}@${Math.round(e.getBoundingClientRect().x)},${Math.round(e.getBoundingClientRect().y)} ${Math.round(e.getBoundingClientRect().width)}x${Math.round(e.getBoundingClientRect().height)}`);
      const wordmark = header.querySelector<HTMLElement>('.brand-wordmark');
      const wr = wordmark?.getBoundingClientRect();
      return {
        headerH: hr.height,
        spread: Math.max(...tops.map((r) => r.y + r.height / 2)) - Math.min(...tops.map((r) => r.y + r.height / 2)),
        overflowX: document.documentElement.scrollWidth - window.innerWidth,
        wordmarkVisible: !!wr && wr.width > 2 && wr.height > 2,
        n: tops.length,
        detail,
      };
    });
    expect(res.n).toBeGreaterThan(2);
    expect(res.headerH, JSON.stringify(res)).toBeLessThanOrEqual(61);
    expect(res.spread, `header controls share a row: ${JSON.stringify(res)}`).toBeLessThan(8);
    expect(res.overflowX, 'horizontal overflow').toBeLessThanOrEqual(0);
    if (vw(page) < 400) expect(res.wordmarkVisible, 'wordmark hidden below 400px').toBe(false);
  });

  test('C2: assistant Close and title are hit-testable and Close is the first focus', async ({ page }) => {
    await page.getByRole('button', { name: /assistant/i }).first().click();
    const drawer = page.getByTestId('assistant-drawer');
    const close = page.getByRole('button', { name: 'Close assistant panel' });
    await expect(close).toBeVisible();
    // The drawer slides in: poll until the transition settles, then the answer must be 'ok'.
    await expect.poll(() => close.evaluate(hitsItself), { message: 'Close hit-testable', timeout: 4000 }).toBe('ok');
    const title = drawer.locator('.mantine-Drawer-title').first();
    if (await title.count()) await expect.poll(() => title.evaluate(hitsItself), { message: 'title hit-testable', timeout: 4000 }).toBe('ok');
    await expect(close).toBeFocused();
    // The tooltip of the trigger must not linger over the drawer.
    await expect(page.getByRole('tooltip')).toHaveCount(0);
  });

  test('A08: exactly one h1 on app pages', async ({ page }) => {
    for (const p of ['/app/dashboard', '/app/settings', '/app/settings/billing', '/app/org/members', '/app/checkout']) {
      await page.goto(p);
      await page.waitForLoadState('networkidle');
      await expect(page.getByRole('heading', { level: 1 }), p).toHaveCount(1);
    }
  });
});
