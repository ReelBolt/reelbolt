import { expect, test, type Page, type Route } from '@playwright/test';

/**
 * T-009 files-projects (B6 B7 B9 B14 B15 B17). Route-stubbed: the project's file list is replaced, uploads
 * and project creation never reach the server. Needs only a signed-in session (LOCAL_STAGING_*) and
 * LOCAL_STAGING_CEO_PROJECT for the project id; nothing is created or spent.
 */
const EMAIL = process.env.LOCAL_STAGING_EMAIL ?? '';
const PASSWORD = process.env.LOCAL_STAGING_PASSWORD ?? '';
const PROJECT = process.env.LOCAL_STAGING_CEO_PROJECT ?? '';
test.skip(!EMAIL || !PASSWORD || !PROJECT, 'LOCAL_STAGING_* not set');

const file = (id: string, name: string, summary: string | null) => ({
  id, projectId: PROJECT, originalFileName: name, originalPath: name, category: 'userFiles', mimeType: 'text/plain',
  sizeBytes: 12, storageKey: `k/${id}`, agentSummary: summary, summaryStatus: summary ? 'Done' : 'Pending',
  indexingStatus: 'Pending', uploadedAt: new Date().toISOString(),
});
const FILES = [
  file('f1', 'notes.txt', null),
  file('f2', 'DESIGN.md', '{"summary":"A design doc.","video_generation_relevance":"High relevance."}'),
];

async function login(page: Page): Promise<void> {
  await page.goto('/app/login');
  await page.waitForLoadState('networkidle');
  await page.locator('input[type="email"], input[autocomplete="username"], input[autocomplete="email"]').first().fill(EMAIL);
  await page.locator('input[type="password"]').first().fill(PASSWORD);
  await page.getByRole('button', { name: /log in|sign in/i }).click();
  await page.waitForURL(/\/app\/(dashboard|projects)/, { timeout: 45_000, waitUntil: 'commit' });
  await page.waitForLoadState('networkidle');
}

async function stubFiles(page: Page): Promise<void> {
  await page.route(new RegExp(`/api/v1/projects/${PROJECT}/files(\\?.*)?$`), async (route: Route) => {
    if (route.request().method() === 'GET') return route.fulfill({ json: FILES });
    return route.fallback();
  });
}

test('B6 B7 B9: legend, duplicate prompt, prose summary', async ({ page }) => {
  await stubFiles(page);
  await login(page);
  await page.goto(`/app/projects/${PROJECT}`);
  await page.getByRole('tab', { name: /^Files/ }).click();
  await expect(page.getByTestId('file-scope-legend')).toContainText('2 files in this project');

  // B7: choosing a file whose name exists asks first; skipping uploads nothing.
  let posted = 0;
  await page.route(new RegExp(`/api/v1/projects/${PROJECT}/files$`), (route) => {
    if (route.request().method() === 'POST') { posted += 1; return route.fulfill({ status: 500, json: { message: 'stub' } }); }
    return route.fallback();
  });
  await page.locator('input[type="file"]:not([webkitdirectory])').first()
    .setInputFiles({ name: 'notes.txt', mimeType: 'text/plain', buffer: Buffer.from('hi') });
  const dialog = page.getByRole('dialog', { name: /already exists/i });
  await expect(dialog).toBeVisible();
  await dialog.getByRole('button', { name: 'Skip duplicates' }).click();
  await expect(dialog).toBeHidden();
  expect(posted).toBe(0);

  // B9: JSON summary reads as prose, not braces.
  await page.getByText('DESIGN.md').first().click();
  const drawer = page.getByRole('dialog').last();
  await expect(drawer.getByText('A design doc.')).toBeVisible();
  await expect(drawer.getByText('Video generation relevance')).toBeVisible();
  await expect(drawer).not.toContainText('{"summary"');
});

test('B14 B15 B17: card legend, custom empty-name message, plain network failure', async ({ page }) => {
  await login(page);
  await page.goto('/app/projects');
  await expect(page.getByTestId('project-card-legend').first()).toBeVisible();

  await page.getByRole('button', { name: /new project/i }).first().click();
  const modal = page.getByRole('dialog', { name: /new project/i });
  await modal.getByRole('button', { name: 'Create project' }).click();
  await expect(modal.getByText('Give the project a name')).toBeVisible();

  await page.route('**/api/v1/projects', (route) => (route.request().method() === 'POST' ? route.abort('failed') : route.fallback()));
  await modal.getByLabel(/Name/).fill('Stub project');
  await modal.getByRole('button', { name: 'Create project' }).click();
  await expect(page.getByText(/Could not reach ReelBolt/)).toBeVisible();
  await expect(page.getByText('Failed to fetch')).toHaveCount(0);
});
