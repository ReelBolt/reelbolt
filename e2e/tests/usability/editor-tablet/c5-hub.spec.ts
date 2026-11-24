import { expect, test, type Route } from '@playwright/test';

/**
 * usability-fixes T-011 / C5. Route-stubbed: no login and no real API; the session is a fabricated
 * `reelbolt_user` + `reelbolt_token` cookie pair. Writes are stubbed by wrapping fetch in the page
 * (Playwright WebKit does not route POST/PATCH to page.route handlers).
 * Asserts: the hub opens on the most recently updated project (not the newest created, empty one),
 * and Edit on a render says "Opening…" while the timeline is being made.
 */
const BASE = (process.env.LOCAL_STAGING_URL ?? 'http://localhost:28080').replace(/\/+$/, '');
const ORG_ID = '11111111-1111-1111-1111-111111111111';
const EMPTY = 'aaaaaaaa-0000-0000-0000-000000000001';
const BUSY = 'bbbbbbbb-0000-0000-0000-000000000002';

const json = (route: Route, body: unknown, status = 200) =>
  route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });

test('C5 hub opens on the busy project and Edit says it is opening', async ({ page }) => {
  await page.addInitScript(() => {
    const real = window.fetch.bind(window);
    window.fetch = async (input, init) => {
      const method = (init?.method ?? (input instanceof Request ? input.method : 'GET')).toUpperCase();
      const url = new URL(typeof input === 'string' ? input : input instanceof Request ? input.url : String(input), location.href);
      if (method === 'GET' || !url.pathname.startsWith('/api/')) return real(input, init);
      await new Promise((r) => setTimeout(r, 2500));
      return new Response(JSON.stringify({ id: 'tl-1', unplacedItems: [] }), { status: 201, headers: { 'Content-Type': 'application/json' } });
    };
  });
  const user = {
    email: 'ada@example.com', isPlatformAdmin: false, mustChangePassword: false, emailVerified: true,
    org: ORG_ID, orgName: 'Acme Team', orgKind: 'Team', orgRole: 'Owner', displayName: 'Ada',
    termsVersion: '2026-10', privacyVersion: '2026-10',
  };
  const host = new URL(BASE).hostname;
  await page.context().addCookies([
    { name: 'reelbolt_user', value: encodeURIComponent(JSON.stringify(user)), domain: host, path: '/' },
    { name: 'reelbolt_token', value: 'stub', domain: host, path: '/' },
  ]);
  const project = (id: string, name: string, createdAt: string, updatedAt: string) => ({ id, name, description: '', status: 'active', userId: 'u1', createdAt, updatedAt });
  await page.route('**/api/**', async (route) => {
    const p = new URL(route.request().url()).pathname.replace(/^\/app/, '');
    if (p === '/api/v1/projects') {
      return json(route, [
        project(EMPTY, 'Test project', '2026-10-26T00:00:00Z', '2026-10-26T00:00:00Z'),
        project(BUSY, 'Real campaign', '2026-10-01T00:00:00Z', '2026-10-27T00:00:00Z'),
      ]);
    }
    if (p === `/api/v1/projects/${BUSY}/timelines`) {
      return json(route, [{ stepResultId: 'sr-1', workflowExecutionId: 'ex-1', workflowStepId: 'st-1', fileName: 'final.mp4', durationSec: 42, createdAt: '2026-10-25T00:00:00Z' }]);
    }
    if (p === '/api/v1/orgs') return json(route, [{ id: ORG_ID, name: 'Acme Team', slug: 'acme', kind: 'Team', role: 'Owner', createdAt: '2026-10-01T00:00:00Z' }]);
    return p.endsWith('s') || p.includes('edit-timelines') ? json(route, []) : json(route, { error: 'not stubbed' }, 404);
  });

  await page.goto('/app/editor');
  const picker = page.getByTestId('editor-project');
  await expect(picker).toBeVisible();
  await expect(picker).toHaveValue('Real campaign');
  const edit = page.getByTestId('render-edit-sr-1');
  await expect(edit).toBeVisible();
  await edit.click();
  await expect(edit).toContainText('Opening');
});
