import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * T-010 assistant. Route-stubbed: no server login and no real API; the session is a fabricated
 * `reelbolt_user` + `reelbolt_token` cookie pair and every /api call is answered here.
 * Findings: C3, C4, C16, C17. Runs on all four projects of playwright.usability.config.ts.
 */
const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');
const ORG_ID = '11111111-1111-1111-1111-111111111111';

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function login(page: Page): Promise<void> {
  // Writes go through an in-page fetch wrapper (WebKit does not route POST/PATCH via page.route).
  await page.addInitScript(() => {
    const real = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
      const url = new URL(typeof input === 'string' ? input : input instanceof Request ? input.url : String(input), location.href);
      if (method === 'GET' || !url.pathname.startsWith('/api/')) return real(input, init);
      const ok = (payload: unknown) => new Response(JSON.stringify(payload), { status: 200, headers: { 'Content-Type': 'application/json' } });
      if (url.pathname.endsWith('/assistant/preferences')) return ok({ approvalMode: 'ask' });
      return new Response(JSON.stringify({ error: 'not stubbed' }), { status: 404, headers: { 'Content-Type': 'application/json' } });
    };
    // Notifications blocked, to prove the switch is not offered.
    Object.defineProperty(window, 'Notification', { value: Object.assign(function Notification() {}, { permission: 'denied', requestPermission: async () => 'denied' }), configurable: true });
  });
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
  await page.route('**/api/**', async (route) => {
    const p = new URL(route.request().url()).pathname.replace(/^\/app/, '');
    if (p === '/api/v1/orgs') return json(route, [org]);
    // Without this the onboarding-tour overlay covers the header and intercepts the trigger click.
    if (p === '/api/v1/onboarding') return json(route, { tour: { status: 'skipped', version: 1, updatedAt: null }, checklist: { dismissed: true, items: [] } });
    if (p.endsWith('/assistant/preferences')) return json(route, { approvalMode: 'ask' });
    if (p.endsWith('/agents')) {
      return json(route, [{ id: 'a1', name: 'code-reader', agentType: 'CodeReader', isBuiltIn: true, description: 'Reads your app', color: 'blue' }]);
    }
    return p.endsWith('s') ? json(route, []) : json(route, { error: 'not stubbed' }, 404);
  });
  await page.goto('/app/dashboard');
  await page.waitForLoadState('networkidle');
}

async function openAssistant(page: Page): Promise<void> {
  await page.getByRole('button', { name: /assistant/i }).first().click();
  await expect(page.getByRole('dialog', { name: /assistant/i })).toBeVisible();
}

test('C3: sidebar stays clickable beside the assistant at >=1024px', async ({ page }) => {
  test.skip((page.viewportSize()?.width ?? 0) < 1024, 'docked panel is >=1024px only');
  await login(page);
  await openAssistant(page);
  await expect(page.getByRole('dialog', { name: /assistant/i })).toHaveAttribute('aria-modal', 'false');
  await page.getByRole('link', { name: /projects/i }).first().click({ timeout: 5_000 });
  await expect(page).toHaveURL(/\/app\/projects/);
});

test('C4: phone sheet keeps settings behind one button and Close is 44px', async ({ page }) => {
  test.skip((page.viewportSize()?.width ?? 999) >= 600, 'phone only');
  await login(page);
  await openAssistant(page);
  await expect(page.getByRole('button', { name: 'Assistant settings' })).toBeVisible();
  await expect(page.getByLabel('Ask before changes')).toHaveCount(0);
  await page.getByRole('button', { name: 'Assistant settings' }).click();
  await expect(page.getByLabel('Ask before changes')).toBeVisible();
  await expect(page.getByLabel(/Notify me/)).toHaveCount(0); // notifications are blocked in this stub
  await page.getByLabel('Ask before changes').click();
  await expect(page.getByRole('button', { name: 'Turn on auto mode' })).toBeVisible();
  await page.getByRole('button', { name: 'Keep asking', exact: true }).click();
  const box = await page.getByRole('button', { name: 'Close assistant panel' }).boundingBox();
  expect(box?.width ?? 0).toBeGreaterThanOrEqual(44);
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
});

test('C16: agents page has an h1 and a legend for the lock and open icons', async ({ page }) => {
  await login(page);
  await page.goto('/app/agents');
  await expect(page.getByRole('heading', { level: 1, name: 'Agents' })).toBeVisible();
  await expect(page.getByTestId('agents-legend')).toContainText('built-in');
});

test('C17: home optional-providers link is a 44px target when shown', async ({ page }) => {
  await login(page);
  const link = page.getByRole('link', { name: 'Open inference settings' });
  test.skip((await link.count()) === 0, 'all providers configured on this stack');
  const box = await link.boundingBox();
  expect(box?.height ?? 0).toBeGreaterThanOrEqual(44);
});
