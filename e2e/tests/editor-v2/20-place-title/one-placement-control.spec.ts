import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-010 (§9.5): one placement control. The pool tile menu, the source viewer, Cut and the phone all
 * show the same four (or the two the phone offers) labels from the one catalogue, and each host still
 * runs the mode it names.
 *
 * e2e dependencies are not installed in this worktree, so this spec was written to the existing
 * editor-v2 conventions but not run here.
 */

const LABELS = ['Insert at playhead', 'Overwrite', 'Add to end', 'Place on top'];

async function desktopTileMenu(ed: EditorPage) {
  await expect(ed.page.getByTestId('media-clip-tile').first()).toBeVisible({ timeout: 30_000 });
  await ed.tap(ed.page.getByTestId('media-tile-menu').first());
  return ed.page.getByRole('menu');
}

async function phoneTileMenu(ed: EditorPage) {
  const tile = ed.page.getByTestId('add-media-tile').first();
  await ed.tap(tile.getByTestId('media-tile-menu'));
  return ed.page.getByTestId('add-media-menu');
}

test('T-010 the pool tile menu and Cut show the shared placement labels; Insert still places at the playhead', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  test.setTimeout(120_000);
  const ed = await openEditor(await freshTimeline('blank'));
  await ed.workspace('media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });

  const menu = await desktopTileMenu(ed);
  for (const label of LABELS) await expect(menu.getByText(label, { exact: true })).toBeVisible();
  await ed.tap(menu.getByTestId('tile-place-insert'));
  await ed.expectServer((tl) => tl.document.tracks.flatMap((t) => t.items).length === 1, 'Insert at playhead stored the clip');

  // The Cut source tape offers the same Insert label, from the same source.
  await ed.workspace('cut');
  const cutInsert = ed.page.locator('[data-testid^="cut-insert-"]').first();
  await expect(cutInsert).toContainText('Insert at playhead');
});

test('T-010 the phone preview uses the same two labels (no visible change)', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  test.setTimeout(120_000);
  const ed = await openEditor(await freshTimeline('blank'));
  await ed.workspace('media');
  const menu = await phoneTileMenu(ed);
  await ed.tap(menu.getByTestId('tile-preview'));
  await expect(ed.testId('preview-insert')).toContainText('Insert at playhead');
  await expect(ed.testId('preview-append')).toContainText('Add to end');
  await ed.tap(ed.testId('preview-append'));
  await ed.expectServer((tl) => tl.document.tracks.flatMap((t) => t.items).length === 1, 'Add to end stored the clip');
});
