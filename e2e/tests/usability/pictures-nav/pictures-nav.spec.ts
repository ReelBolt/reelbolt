import { expect, test, type Page } from '@playwright/test';

/**
 * T-012 (C9, C13, C15). Route-stubbed: no backend, no provider calls. The API is answered by a
 * catch-all stub, so the specs only exercise what the browser renders.
 */
const PROJECT = { id: 'p-1', name: 'Channel launch' };

async function signInStubbed(page: Page, baseURL: string) {
  const user = encodeURIComponent(
    JSON.stringify({ email: 'qa@example.com', isPlatformAdmin: false, mustChangePassword: false, orgName: 'qa@example.com', orgKind: 'Personal', orgRole: 'Owner' }),
  );
  await page.context().addCookies([
    { name: 'reelbolt_token', value: 'qa-stub-token', url: baseURL },
    { name: 'reelbolt_user', value: user, url: baseURL },
  ]);
  await page.route('**/api/v1/**', (route) => {
    const url = new URL(route.request().url());
    const json = (body: unknown, status = 200) => route.fulfill({ status, contentType: 'application/json', body: JSON.stringify(body) });
    if (url.pathname === '/api/v1/projects') return json([PROJECT]);
    if (url.pathname.endsWith('/image-documents')) return json([]);
    if (route.request().method() === 'GET') return json([]);
    return json({}, 404);
  });
}

test.describe('pictures hub (C13)', () => {
  test('YouTube 1280x720 is the preselected preset', async ({ page, baseURL }) => {
    await signInStubbed(page, baseURL!);
    await page.goto('/app/editor?project=p-1&view=pictures');
    const youtube = page.getByTestId('picture-shape-youtube');
    await expect(youtube).toBeVisible();
    await expect(youtube).toHaveAttribute('aria-checked', 'true');
    await expect(youtube).toContainText('1280 × 720');
  });

  test('the hub remembers the last project', async ({ page, baseURL }) => {
    await signInStubbed(page, baseURL!);
    await page.goto('/app/editor?project=p-1&view=pictures');
    await expect(page.getByTestId('picture-shape-youtube')).toBeVisible();
    await page.goto('/app/editor?view=pictures');
    await expect(page.getByTestId('editor-project')).toHaveValue('Channel launch');
  });
});

test.describe('account menu (C15)', () => {
  test('links to Account and Plan next to Logout', async ({ page, baseURL }) => {
    await signInStubbed(page, baseURL!);
    await page.goto('/app/dashboard');
    await page.getByRole('button', { name: /^Account menu/ }).click();
    await expect(page.getByRole('menuitem', { name: 'Account' })).toHaveAttribute('href', '/app/settings');
    await expect(page.getByRole('menuitem', { name: /Plan/ })).toHaveAttribute('href', '/app/settings/billing');
    await expect(page.getByRole('menuitem', { name: 'Logout' })).toBeVisible();
  });
});

test.describe('picture editor header (C9)', () => {
  test('on a phone the chrome is one compact row and no breadcrumb trail', async ({ page, baseURL }, info) => {
    test.skip(!['pixel7', 'iphone14webkit'].includes(info.project.name), 'phone only');
    await signInStubbed(page, baseURL!);
    await page.goto('/app/editor/thumbnails/doc-1?project=p-1');
    const row = page.getByTestId('picture-header-compact');
    await expect(row).toBeVisible();
    const box = await row.boundingBox();
    expect(box!.height).toBeLessThan(64);
  });
});
