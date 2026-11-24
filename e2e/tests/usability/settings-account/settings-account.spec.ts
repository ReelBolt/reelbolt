import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * T-008 settings-account. Route-stubbed: no server login and no real API; the session is a fabricated
 * `reelbolt_user` + `reelbolt_token` cookie pair and every /api call is answered here.
 * Findings: A20 (settings tabs, no dead-end cards), A09 (setup checklist), A27 (members page).
 * Runs on the desktop, tablet, pixel7 and iphone14webkit projects of playwright.usability.config.ts.
 */
const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');

type Role = 'Owner' | 'Admin' | 'Member';
interface Scenario {
  role: Role;
  kind: 'Team' | 'Personal';
  platformAdmin?: boolean;
}

const ORG_ID = '11111111-1111-1111-1111-111111111111';

function json(route: Route, body: unknown, status = 200): Promise<void> {
  return route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
}

async function stub(page: Page, s: Scenario): Promise<{ created: unknown[]; patched: unknown[] }> {
  const seen = { created: [] as unknown[], patched: [] as unknown[] };
  const user = {
    email: 'ada@example.com',
    isPlatformAdmin: !!s.platformAdmin,
    mustChangePassword: false,
    emailVerified: true,
    org: ORG_ID,
    orgName: s.kind === 'Personal' ? 'ada@example.com' : 'Acme Team',
    orgKind: s.kind,
    orgRole: s.role,
    displayName: 'Ada',
    termsVersion: '2026-10',
    privacyVersion: '2026-10',
  };
  const host = new URL(BASE).hostname;
  await page.context().addCookies([
    { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(user)), domain: host, path: '/' },
    { name: 'reelbolt_token', value: 'stub', domain: host, path: '/' },
  ]);
  const org = { id: ORG_ID, name: user.orgName, slug: 'acme', kind: s.kind, role: s.role, createdAt: '2026-10-01T00:00:00Z' };
  await page.route('**/api/**', async (route) => {
    const req = route.request();
    const url = new URL(req.url());
    const p = url.pathname.replace(/^\/app/, '');
    if (p === '/api/v1/orgs' && req.method() === 'GET') return json(route, [org]);
    if (p.endsWith('/members')) {
      return json(route, [{ userId: 'u1', email: 'ada@example.com', displayName: 'Ada', role: 'Owner', joinedAt: '2026-10-01T00:00:00Z' }]);
    }
    if (p.endsWith('/invites') && req.method() === 'GET') {
      return json(route, [{ id: 'inv-1', email: 'teammate@example.com', role: 'Member', createdAt: '2026-10-01T00:00:00Z', expiresAt: '2026-12-01T00:00:00Z', emailSent: true }]);
    }
    if (p.endsWith('/invites') && req.method() === 'POST') {
      seen.created.push(req.postDataJSON());
      return json(route, { id: 'inv-2', email: 'teammate@example.com', role: 'Member', createdAt: '2026-10-27T00:00:00Z', expiresAt: '2026-12-01T00:00:00Z', emailSent: true }, 201);
    }
    if (p.includes('/github/account') || p.includes('/github')) {
      return json(route, { connected: false, login: null, avatarUrl: null, canConnect: false, revokedReason: null, connectedAt: null, installUrl: null });
    }
    if (p === '/api/v1/auth/settings') {
      if (req.method() === 'PATCH') {
        const body = req.postDataJSON() as { displayName?: string };
        seen.patched.push(body);
        if (body.displayName !== undefined && body.displayName.trim() === '') {
          return json(route, { error: 'display name must be 1 to 100 characters' }, 400);
        }
        return json(route, { mcpAdminToolsEnabled: false, displayName: body.displayName ?? 'Ada' });
      }
      return json(route, { mcpAdminToolsEnabled: false, displayName: 'Ada' });
    }
    if (p.includes('/projects')) return json(route, []);
    return json(route, p.endsWith('s') ? [] : {});
  });
  return seen;
}

test.describe('A20 settings tabs', () => {
  test('tabs, no dead-end GitHub card, no admin MCP tools for a Member, API keys explained', async ({ page }) => {
    await stub(page, { role: 'Member', kind: 'Team' });
    await page.goto('/app/settings');
    await page.waitForLoadState('networkidle');
    for (const name of ['Profile', 'Security', 'Notifications', 'Your data']) {
      await expect(page.getByRole('tab', { name })).toBeVisible();
    }
    await expect(page.getByText('Ask your administrator')).toHaveCount(0);
    await page.getByRole('tab', { name: 'Security' }).click();
    await expect(page.getByLabel('Current Password')).toBeVisible();
    await expect(page.getByTestId('mcp-admin-tools-card')).toHaveCount(0);
    await expect(page.getByTestId('connect-tools-card')).toContainText('does not use personal API keys');
    expect(page.url()).toContain('#security');
  });

  test('an Owner still sees the admin MCP tools switch', async ({ page }) => {
    await stub(page, { role: 'Owner', kind: 'Team' });
    await page.goto('/app/settings#security');
    await page.waitForLoadState('networkidle');
    await expect(page.getByTestId('mcp-admin-tools-card')).toBeVisible();
  });

  test('tabs are 44px tall on touch widths', async ({ page }) => {
    await stub(page, { role: 'Owner', kind: 'Team' });
    await page.goto('/app/settings');
    await page.waitForLoadState('networkidle');
    const box = await page.getByRole('tab', { name: 'Security' }).boundingBox();
    expect(box!.height).toBeGreaterThanOrEqual(44);
  });
});

test.describe('A02 display name', () => {
  test('Profile has a Your name field that saves through PATCH /auth/settings', async ({ page }) => {
    const seen = await stub(page, { role: 'Owner', kind: 'Team' });
    await page.goto('/app/settings');
    await page.waitForLoadState('networkidle');
    const field = page.getByLabel('Your name');
    await expect(field).toHaveValue('Ada');
    await field.fill('Ada Lovelace');
    await page.getByRole('button', { name: 'Save name' }).click();
    await expect.poll(() => seen.patched.length).toBe(1);
    expect(seen.patched[0]).toEqual({ displayName: 'Ada Lovelace' });
  });

  test('a server refusal is shown next to the field', async ({ page }) => {
    await stub(page, { role: 'Owner', kind: 'Team' });
    await page.goto('/app/settings');
    await page.waitForLoadState('networkidle');
    await page.getByLabel('Your name').fill('   ');
    await page.getByRole('button', { name: 'Save name' }).click();
    await expect(page.getByText('display name must be 1 to 100 characters')).toBeVisible();
  });

  test('the dashboard greets by display name', async ({ page }) => {
    await stub(page, { role: 'Owner', kind: 'Team' });
    await page.goto('/app/dashboard');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading', { level: 1 })).toContainText('Ada');
  });
});

test.describe('A09 setup checklist', () => {
  test('a workspace Owner who is not a platform admin sees no "Finish setup" card', async ({ page }) => {
    await stub(page, { role: 'Owner', kind: 'Team' });
    await page.goto('/app/dashboard');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading', { level: 1 })).toBeVisible();
    await expect(page.getByText(/Finish setup/)).toHaveCount(0);
  });
});

test.describe('A27 members page', () => {
  test('a pending invite can be resent, with a labelled 44px action', async ({ page }) => {
    const seen = await stub(page, { role: 'Owner', kind: 'Team' });
    await page.goto('/app/org/members');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('heading', { name: 'People with access' })).toBeVisible();
    const resend = page.getByRole('button', { name: 'Resend invitation to teammate@example.com' });
    await expect(resend).toBeVisible();
    expect((await resend.boundingBox())!.height).toBeGreaterThanOrEqual(44);
    await resend.click();
    await expect.poll(() => seen.created.length).toBe(1);
    expect(seen.created[0]).toEqual({ email: 'teammate@example.com', role: 'Member' });
  });

  test('the Personal workspace page offers a way to create a team workspace', async ({ page }) => {
    await stub(page, { role: 'Owner', kind: 'Personal' });
    await page.goto('/app/org/members');
    await page.waitForLoadState('networkidle');
    await expect(page.getByRole('button', { name: 'Create a team workspace' })).toBeVisible();
  });
});
