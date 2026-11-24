import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-001 "a page is a place": Colour is in the URL, survives a reload, moves with browser history,
 * and moving between pages is navigation — never an edit and never a timeline refetch.
 *
 * e2e dependencies are not installed in this worktree, so this spec is written to the existing
 * editor-v2 conventions but was not run here.
 */

const itemCount = (tl: Timeline) => tl.document.tracks.reduce((n, t) => n + t.items.length, 0);
const pageParam = (name: string) => new RegExp(`[?&]page=${name}(&|$)`);

/** A GET of the timeline document itself (not its revisions/history), counted after the first load. */
function countDocumentGets(ed: EditorPage): string[] {
  const gets: string[] = [];
  const documentGet = new RegExp(`/edit-timelines/${ed.timelineId}(\\?|$)`);
  ed.page.on('request', (request) => {
    if (request.method() === 'GET' && documentGet.test(request.url())) gets.push(request.url());
  });
  return gets;
}

test('PAGE-URL Colour survives reload, browser back returns to Edit, and no edit or fetch is lost', async ({
  freshTimeline,
  openEditor,
  page,
}) => {
  runOn('desktop', 'tablet');
  test.setTimeout(180_000);
  const ed = await openEditor(await freshTimeline('seeded'));

  // First-time disposition: the mount normalises the URL with replaceState (no extra entry).
  await expect(page).toHaveURL(pageParam('edit'));

  // An edit that must survive the page moves.
  const before = await ed.serverTimeline();
  await ed.seek(75);
  await ed.splitAtPlayhead();
  const saved = await ed.expectServer((tl) => itemCount(tl) > itemCount(before), 'the blade reached the server');
  const savedItems = itemCount(saved);
  const savedRevision = saved.revision;

  const documentGets = countDocumentGets(ed);

  // Open Colour: one pushed entry and no router navigation, so nothing is requested.
  await ed.workspace('color');
  await expect(page).toHaveURL(pageParam('colour'));
  await expect(ed.testId('color-workspace')).toBeVisible();
  expect(documentGets, 'a page change issues no timeline request').toHaveLength(0);

  // Reload with no page in memory: the URL itself opens Colour.
  await ed.reload();
  await expect(ed.testId('color-workspace')).toBeVisible({ timeout: 30_000 });
  expect(documentGets, 'the reload is the only timeline GET').toHaveLength(1);

  // Browser back: the page moves to Edit, with no refetch and no remount (the workspace is still there).
  await page.goBack();
  await expect(page).toHaveURL(pageParam('edit'));
  await expect(ed.canvas).toBeVisible({ timeout: 30_000 });
  expect(documentGets, 'back/forward issues no timeline GET').toHaveLength(1);

  // No lost edit: the document is still the one saved before the page moves.
  const after = await ed.serverTimeline();
  expect(itemCount(after), 'the blade is still on the timeline').toBe(savedItems);
  expect(after.revision, 'no document write happened while switching pages').toBe(savedRevision);
});

test('PAGE-URL color is the legacy alias, nonsense normalises to Edit, and a bare link restores this device', async ({
  freshTimeline,
  openEditor,
  page,
}) => {
  runOn('desktop', 'tablet');
  test.setTimeout(180_000);
  const ed = await openEditor(await freshTimeline('seeded'));

  // The old spelling still opens Colour, and is normalised in the URL.
  await page.goto(`${ed.url}?page=color`);
  await expect(ed.testId('color-workspace')).toBeVisible({ timeout: 45_000 });
  await expect(page).toHaveURL(pageParam('colour'));

  // An unknown value opens Edit and is normalised, not carried around.
  await page.goto(`${ed.url}?page=nonsense`);
  await expect(ed.canvas).toBeVisible({ timeout: 45_000 });
  await expect(page).toHaveURL(pageParam('edit'));

  // A recipient link with no page opens the page this device last used.
  await ed.workspace('color');
  await expect(page).toHaveURL(pageParam('colour'));
  await page.goto(ed.url);
  await expect(ed.testId('color-workspace')).toBeVisible({ timeout: 45_000 });
  await expect(page).toHaveURL(pageParam('colour'));
});
