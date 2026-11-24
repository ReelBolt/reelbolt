import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { meanLuma, sampleElement } from '../../../fixtures/editor-v2/pixels';

/**
 * Read-only smoke (plan §6.3): hub loads, the kept seed project's timeline opens, the viewer shows a
 * non-black frame, the render list is readable. Selected only with EDITOR_E2E_READONLY=1, against a deployed
 * box (EDITOR_E2E_BASE_URL) and project `audit-seed-20261012-2202` (EDITOR_E2E_READONLY_PROJECT).
 *
 * It writes NOTHING: every non-GET API request except the login itself is aborted in the browser before it
 * leaves, and the seed fixture only GETs. Any write the editor attempted on its own is reported as an
 * annotation so it shows up in the run, never on the server.
 */

const WRITE_ALLOWED = /\/api\/v1\/auth\/(token|refresh)$/;

test.describe('smoke: read-only on a deployed box @smoke-readonly', () => {
  test.beforeEach(async ({ page }, testInfo) => {
    await page.route('**/api/**', async (route) => {
      const req = route.request();
      if (req.method() === 'GET' || req.method() === 'HEAD' || req.method() === 'OPTIONS' || WRITE_ALLOWED.test(new URL(req.url()).pathname)) {
        await route.continue();
        return;
      }
      testInfo.annotations.push({ type: 'blocked-write', description: `${req.method()} ${new URL(req.url()).pathname}` });
      await route.abort('blockedbyclient');
    });
  });

  test('SMOKE-HUB the editor hub lists the seed project\'s timelines', async ({ page, seed }) => {
    expect(seed.seededTimelineId, 'seed timeline found by name').not.toBe('');
    await page.goto(`/app/editor?project=${seed.projectId}`);
    await expect(page.getByTestId('timelines-table')).toBeVisible({ timeout: 45_000 });
    await expect(page.getByTestId(`timeline-row-${seed.seededTimelineId}`)).toBeVisible();
    await expect(page.getByRole('tab')).toHaveText(['Videos', 'Pictures']);
    await expect(page.getByRole('tab', { name: 'Videos' })).toHaveAttribute('aria-selected', 'true');
  });

  test('SMOKE-HUB-LEGACY an old view=media link lands on Videos', async ({ page, seed }) => {
    await page.goto(`/app/editor?project=${seed.projectId}&view=media`);
    await expect(page).toHaveURL(/[?&]view=videos(&|$)/, { timeout: 45_000 });
    await expect(page.getByRole('tab', { name: 'Videos' })).toHaveAttribute('aria-selected', 'true');
    await expect(page.getByTestId('timelines-table')).toBeVisible();
  });

  test('SMOKE-TIMELINE the seed timeline opens with its clips and a saved state', async ({ seed, openEditor }) => {
    const ed = await openEditor(seed.seededTimelineId);
    const server = await ed.serverTimeline();
    expect(server.document.tracks.some((t) => t.items.length > 0), 'the stored timeline holds clips').toBe(true);
    await expect(ed.chip).toContainText(/Saved/);
    await expect(ed.viewer).toBeVisible();
  });

  test('SMOKE-VIEWER the program monitor shows a non-black frame (warm load)', async ({ seed, openEditor }) => {
    const ed: EditorPage = await openEditor(seed.seededTimelineId);
    await ed.page.waitForLoadState('networkidle');
    await ed.reload(); // warm: the audit saw cold loads black (PA-1); the smoke checks the box serves pictures at all
    await ed.seek(75);
    await expect.poll(async () => meanLuma(await sampleElement(ed.page, ed.viewer)), {
      message: 'viewer mean luma at frame 75', timeout: 30_000, intervals: [1000, 2000],
    }).toBeGreaterThan(20);
  });

  test('SMOKE-RENDERS the render list of the seed timeline is readable', async ({ seed, openEditor }) => {
    const list = await seed.api.json<unknown>('GET', `${seed.api.timelinesUrl(seed.projectId, seed.seededTimelineId)}/renders`);
    expect(Array.isArray(list) || typeof list === 'object', 'renders list is JSON').toBe(true);
    const ed = await openEditor(seed.seededTimelineId);
    await ed.workspace('deliver');
    // `render-queue` when the timeline has renders, `render-queue-empty` when it has none: either is readable.
    await expect(ed.page.getByTestId('render-queue').or(ed.page.getByTestId('render-queue-empty')).first()).toBeVisible();
    await expect(ed.page.getByText('Render queue', { exact: true })).toBeVisible();
  });
});
