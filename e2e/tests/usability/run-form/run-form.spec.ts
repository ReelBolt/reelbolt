import { expect, test, type Locator, type Page } from '@playwright/test';

// T-003: B2 (a fresh run form is not in error; the sticky Run bar must not cover the brief on a phone) and
// B1 (estimate beside Run; confirm before a costly run; Trial gets "Choose a plan"). The stack has no
// engine, so a real project is created through the API and the workflow/app/estimate/execute answers are
// stubbed in the browser. An execute POST is ALWAYS fulfilled by a stub and never reaches the backend.

const json = (body: unknown, status = 200) => ({ status, contentType: 'application/json', body: JSON.stringify(body) });
const now = new Date().toISOString();
const WID = 'wf-uf-run';

const estimateOf = (over: Record<string, unknown> = {}) => ({
  billingEnabled: true, hasHistory: true, lowCredits: 60, highCredits: 80, typicalCredits: 70, fixedMaxCredits: 0,
  requiredCredits: 80, balanceCredits: 500, affordable: true, steps: [], ...over,
});

interface Scenario {
  costClass?: string;
  estimate?: Record<string, unknown>;
  /** Answers to successive execute POSTs; the last repeats. */
  execute?: Array<{ status: number; body: unknown }>;
}

const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');
const ORG_ID = '11111111-1111-1111-1111-111111111111';

const started = {
  status: 201,
  body: { id: 'ex-stub', workflowDefinitionId: WID, status: 'Queued', startedAt: now, completedAt: null,
    iterationCount: 0, resultJson: null, stepResults: [], reviewScores: [] },
};

async function setup(page: Page, who: 'creator' | 'trial', sc: Scenario = {}) {
  // Login-free: a fabricated session pair, every /api GET answered by page.route, and every write
  // answered inside the page by a fetch wrapper (Playwright's WebKit never routes POST/PATCH to page.route).
  const answers = sc.execute ?? [started];
  await page.addInitScript((canned) => {
    const w = window as unknown as { __calls: Array<Record<string, unknown>> };
    w.__calls = [];
    const real = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
      const url = new URL(typeof input === 'string' ? input : input instanceof Request ? input.url : String(input), location.href);
      if (method === 'GET' || !url.pathname.startsWith('/app/api/') && !url.pathname.startsWith('/api/')) return real(input, init);
      let body: Record<string, unknown> = {};
      try {
        body = JSON.parse(String(init?.body ?? '{}')) as Record<string, unknown>;
      } catch {
        // leave empty
      }
      const reply = (status: number, payload: unknown) =>
        new Response(JSON.stringify(payload), { status, headers: { 'Content-Type': 'application/json' } });
      if (method === 'POST' && url.pathname.endsWith('/execute')) {
        w.__calls.push(body);
        const a = canned[Math.min(w.__calls.length - 1, canned.length - 1)]!;
        return reply(a.status, a.body);
      }
      return reply(404, { error: 'not stubbed' });
    };
  }, answers);
  const user = {
    email: 'ada@example.com', isPlatformAdmin: false, mustChangePassword: false, emailVerified: true, org: ORG_ID,
    orgName: 'Acme Team', orgKind: 'Team', orgRole: 'Owner', displayName: 'Ada', termsVersion: '2026-10', privacyVersion: '2026-10',
  };
  const host = new URL(BASE).hostname;
  await page.context().addCookies([
    { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(user)), domain: host, path: '/' },
    { name: 'reelbolt_token', value: 'stub', domain: host, path: '/' },
  ]);
  const org = { id: ORG_ID, name: 'Acme Team', slug: 'acme', kind: 'Team', role: 'Owner', createdAt: '2026-10-01T00:00:00Z' };
  // Lists answer empty and unknown non-lists 404 (registered first; later routes win).
  await page.route('**/api/**', (r) => {
    const p = new URL(r.request().url()).pathname.replace(/^\/app/, '');
    if (r.request().method() !== 'GET') return r.fallback();
    return p.endsWith('s') ? r.fulfill(json([])) : r.fulfill(json({ error: 'not stubbed' }, 404));
  });
  await page.route(/\/api\/v1\/orgs(\?.*)?$/, (r) => r.fulfill(json([org])));
  await page.route(/\/api\/v1\/billing\/summary(\?.*)?$/, (r) => r.fulfill(json({
    billingProvider: null, paddle: null, canManageBilling: true, subscription: null,
    credits: { balance: 500, reserved: 0, available: 500, grants: [] },
    topUp: { available: who !== 'trial', packs: who === 'trial' ? [] : [500, 1000] },
    plans: [{ key: who, name: who === 'trial' ? 'Trial' : 'Creator', kind: 'Subscription', priceMonthlyUsd: 0, minSeats: null, includedCredits: 500, priceId: 'pri_x', current: true }],
  })));
  const pid = '5f0c1e2a-0000-4000-8000-00000000a003';
  const base = `/api/v1/projects/${pid}`;
  const re = (tail: string) => new RegExp(`${base}/${tail}(\\?.*)?$`);
  // Fallback first (later routes win): any other project-scoped GET is an empty list.
  await page.route(new RegExp(`${base}/.*`), (r) => (r.request().method() === 'GET' ? r.fulfill(json([])) : r.fallback()));
  await page.route(new RegExp(`${base}(\\?.*)?$`), (r) => r.fulfill(json({
    id: pid, name: 'Run form project', description: '', status: 'Active', userId: 'u1', createdAt: now, updatedAt: now,
  })));
  await page.route(re(`workflows/${WID}`), (r) => r.fulfill(json({
    id: WID, projectId: pid, name: 'Custom teaser', steps: [], requiresUserInput: true, createdAt: now, updatedAt: now,
  })));
  await page.route(re(`workflows/${WID}/app`), (r) => r.fulfill(json({
    workflowId: WID, name: 'Custom teaser', hasExplicitManifest: true, costClass: sc.costClass ?? 'Uses credits', source: null,
    manifest: { version: 1, title: 'Custom teaser', tagline: 'A short promo', category: 'promo',
      inputs: [{ kind: 'notes', label: 'What should it show?' }], result: { kind: 'video' } },
  })));
  await page.route(re(`workflows/${WID}/estimate`), (r) => r.fulfill(json(sc.estimate ?? estimateOf())));
  await page.route(re(`workflows/${WID}/lint`), (r) => r.fulfill(json({ findings: [], errors: 0, warnings: 0, infos: 0 })));
  await page.route(re(`workflows/${WID}/executions`), (r) => r.fulfill(json([])));
  await page.goto(`/app/projects/${pid}/workflows/${WID}`);
  await expect(page.getByTestId('run-bar')).toBeVisible();
  // WebKit can fill before React hydrates and lose the value: settle the page first.
  await page.waitForLoadState('networkidle');
  const calls = () => page.evaluate(() => (window as unknown as { __calls?: Array<Record<string, unknown>> }).__calls ?? []);
  const count = () => calls().then((c) => c.length);
  return { pid, calls, count };
}

const notes = (page: Page) => page.getByRole('textbox', { name: /What should it show/ });
const runBtn = (page: Page) => page.getByTestId('run-button');
/** Fill and re-check until the value sticks (a fill that lands before hydration is lost). */
const fillStable = (page: Page, field: Locator, value: string) =>
  expect(async () => {
    await field.fill(value);
    await expect(field).toHaveValue(value, { timeout: 500 });
    // The DOM value alone can stick before hydration; Run enabling proves React state has the text.
    await expect(runBtn(page)).toHaveAttribute('aria-disabled', 'false', { timeout: 500 });
  }).toPass({ timeout: 10_000 });
const MSG = 'Tell us what you want to make';

test.describe('B2: a fresh run form is not in error', () => {
  test('neutral until blur, then one message on the field only', async ({ page }) => {
    await setup(page, 'creator');
    await expect(page.getByText(MSG)).toHaveCount(0);
    await expect(notes(page)).not.toHaveAttribute('aria-invalid', 'true');
    // The blocked Run explains itself in plain words; it is not a dashed (drop-zone looking) button.
    await expect(runBtn(page)).toHaveAttribute('aria-disabled', 'true');
    await expect(page.getByTestId('run-block-reason')).toBeVisible();
    const border = await runBtn(page).evaluate((el) => getComputedStyle(el).borderTopStyle);
    expect(border).not.toBe('dashed');
    // Leaving the empty box is the moment it becomes an error: exactly one message.
    await notes(page).focus();
    await notes(page).blur();
    await expect(page.getByText(MSG)).toHaveCount(1);
  });

  test('no builder jargon on a fresh custom app', async ({ page }) => {
    await setup(page, 'creator');
    await expect(page.getByText(/review at step \d|Check: review/i)).toHaveCount(0);
  });

  test('the sticky Run bar does not cover the brief box (scrolled to the bottom)', async ({ page }) => {
    await setup(page, 'creator');
    await notes(page).focus();
    await page.evaluate(() => window.scrollTo(0, document.documentElement.scrollHeight));
    const covered = await page.evaluate(() => {
      const ta = document.querySelector('#app-input-notes') as HTMLElement;
      const bar = document.querySelector('[data-testid="run-bar"]') as HTMLElement;
      const t = ta.getBoundingClientRect();
      const b = bar.getBoundingClientRect();
      const barFixed = getComputedStyle(bar).position;
      const overlap = barFixed === 'fixed' || barFixed === 'sticky' ? t.bottom > b.top + 1 && t.top < b.bottom && b.height > 0 : false;
      // Sample inside the part of the brief that is on screen and above the bar (it may scroll off the top).
      const visTop = Math.max(t.top, 0);
      const visBottom = Math.min(t.bottom, b.top);
      const hit = visBottom > visTop ? document.elementFromPoint(t.left + t.width / 2, (visTop + visBottom) / 2) : ta;
      return { overlap, hitOk: !!hit && (hit === ta || ta.contains(hit)), pos: barFixed, t: [t.top, t.bottom], b: [b.top, b.bottom] };
    });
    expect(covered.overlap, JSON.stringify(covered)).toBe(false);
    expect(covered.hitOk, JSON.stringify(covered)).toBe(true);
  });
});

test.describe('B1: estimate beside Run and a confirm before a costly run', () => {
  test('range beside Run; a costly run asks first; Cancel starts nothing', async ({ page }) => {
    const { count } = await setup(page, 'creator');
    await fillStable(page, notes(page), 'A teaser for my new sneaker drop');
    await expect(runBtn(page)).toHaveAttribute('aria-disabled', 'false');
    await expect(page.getByTestId('run-bar')).toContainText(/credits/);
    await expect(page.getByTestId('run-bar')).toContainText(/60/);
    await expect(page.getByTestId('run-bar')).toContainText(/80/);
    await runBtn(page).click();
    const dlg = page.getByTestId('run-confirm');
    await expect(dlg).toBeVisible();
    await expect(dlg).toContainText(/80/);
    await expect(dlg).toContainText(/500/);
    await page.getByRole('button', { name: 'Cancel' }).click();
    await expect(dlg).toBeHidden();
    expect(await count()).toBe(0);
  });

  test('confirming sends the run (stubbed); a 409 ESTIMATE_NOT_ACCEPTED asks once more and resends with acceptEstimateCredits', async ({ page }) => {
    const gate = { status: 409, body: { code: 'ESTIMATE_NOT_ACCEPTED', estimate: { low: 70, high: 95 }, capCredits: 300, message: 'Needs acceptance' } };
    const { calls, count } = await setup(page, 'creator', { execute: [gate, started] });
    await fillStable(page, notes(page), 'A teaser for my new sneaker drop');
    await runBtn(page).click();
    await page.getByTestId('run-confirm').getByRole('button', { name: 'Run', exact: true }).click();
    await expect.poll(count).toBe(1);
    // Our confirm already sent the figure it showed; the server wants more, so it asks again.
    expect((await calls())[0]!.acceptEstimateCredits).toBe(80);
    // The gate modal shows its confirm; accepting resends with the high figure.
    const accept = page.getByTestId('estimate-gate').getByRole('button', { name: 'Confirm', exact: true });
    await expect(accept).toBeVisible();
    await accept.click();
    await expect.poll(count).toBe(2);
    expect((await calls())[1]!.acceptEstimateCredits).toBe(95);
  });

  test('a free run starts at once, with no dialog', async ({ page }) => {
    const { count } = await setup(page, 'creator', {
      costClass: 'No credits', estimate: estimateOf({ lowCredits: 0, highCredits: 0, typicalCredits: 0, requiredCredits: 0 }),
    });
    await fillStable(page, notes(page), 'Cut my clips');
    await runBtn(page).click();
    await expect.poll(count).toBe(1);
    await expect(page.getByTestId('run-confirm')).toHaveCount(0);
  });

  test('a small paid run (under the threshold) starts at once', async ({ page }) => {
    const { count } = await setup(page, 'creator', { estimate: estimateOf({ lowCredits: 4, highCredits: 8, requiredCredits: 8 }) });
    await fillStable(page, notes(page), 'Quick one');
    await runBtn(page).click();
    await expect.poll(count).toBe(1);
    await expect(page.getByTestId('run-confirm')).toHaveCount(0);
  });

  test('billing off: no estimate UI and no confirm', async ({ page }) => {
    const { count } = await setup(page, 'creator', { estimate: { billingEnabled: false } });
    await fillStable(page, notes(page), 'Anything');
    await runBtn(page).click();
    await expect.poll(count).toBe(1);
    await expect(page.getByTestId('run-confirm')).toHaveCount(0);
    await expect(page.getByTestId('run-bar')).not.toContainText(/credits/);
  });

  test('402 INSUFFICIENT_CREDITS on Trial offers "Choose a plan", not a top-up', async ({ page }) => {
    const refuse = { status: 402, body: { code: 'INSUFFICIENT_CREDITS', required: 80, balance: 3, low: 60, high: 80, hasHistory: true, message: 'Not enough credits' } };
    await setup(page, 'trial', { estimate: estimateOf({ lowCredits: 4, highCredits: 8, requiredCredits: 8, balanceCredits: 3, affordable: true }), execute: [refuse] });
    await fillStable(page, notes(page), 'Anything');
    await runBtn(page).click();
    const alert = page.getByTestId('insufficient-credits-trial');
    await expect(alert).toBeVisible();
    await expect(alert.getByRole('link', { name: 'Choose a plan' })).toBeVisible();
    // Other widgets (low-balance banner, cost breakdown) may say top up; the run's own alert must not.
    await expect(alert).not.toContainText(/top.?up/i);
  });
});
