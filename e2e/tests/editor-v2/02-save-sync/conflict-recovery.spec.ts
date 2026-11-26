import { test, expect, EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { bladeAt, expectTapTarget, hasCutAt, otherWriterAddsMarker, otherWriterRenames, videoItems } from './sync-helpers';

/**
 * Wave 1 T-004, R02.1 conflict recovery. A second writer moves the revision under an open tab; the tab's next
 * edit either merges silently (disjoint) or offers "Keep my copy" / "Use the latest" (overlapping). Nothing is
 * ever silently lost: the stored documents are asserted either way.
 */

test('R02.1 another writer saved first, disjoint edit: merged silently, both changes stored', async ({ freshTimeline, openEditor, page }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const base = await ed.serverTimeline();
  const other = await otherWriterAddsMarker(ed, 'From elsewhere', 20);
  expect(other.revision).toBe(base.revision + 1);

  await bladeAt(ed, 75);
  await expect(page.getByText('Your edits were merged with changes from another tab or device.')).toBeVisible();
  const stored = await ed.expectServer((tl) => hasCutAt(tl, 75) && (tl.document.markers ?? []).some((m) => m.name === 'From elsewhere'), 'the split AND the other writer\'s marker are both stored');
  expect(stored.revision).toBe(base.revision + 2);
  await expect(ed.chip).toHaveText('Saved');
  await expect(page.getByTestId('conflict-dialog')).toHaveCount(0);
});

test('R02.1 another writer touched the same clip: the choice dialog; Keep my copy saves a new timeline, nothing lost', async ({ freshTimeline, openEditor, page, api, seed }, testInfo) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const base = await ed.serverTimeline();
  const original = base.name;
  await otherWriterRenames(ed, 0, 'Renamed by someone else');

  await bladeAt(ed, 75);
  const dialog = page.getByTestId('conflict-dialog');
  await expect(dialog).toBeVisible();
  // The dialog explains why in a sentence; the phone's compact bar says the short form (the chip below carries the reason).
  await expect(dialog).toContainText(testInfo.project.name === 'desktop' ? 'your changes touch the same part' : 'Your edits are safe');
  // Desktop and tablet show a dialog; the phone shows a compact bar (Wave 3 RM.7) with the same sentence in the chip.
  if (testInfo.project.name === 'desktop') await expect(page.getByRole('dialog')).toContainText('This timeline was changed somewhere else');
  await expect(ed.chip).toHaveText('Not saved. This timeline was changed somewhere else.');
  await expect(page.getByTestId('conflict-keep-copy')).toHaveText('Keep my copy');
  await expect(page.getByTestId('conflict-use-latest')).toHaveText('Use the latest');
  if (testInfo.project.name !== 'desktop') {
    await expectTapTarget(page.getByTestId('conflict-keep-copy'), 'Keep my copy');
    await expectTapTarget(page.getByTestId('conflict-use-latest'), 'Use the latest');
  }
  // The editor is frozen while it waits for the choice.
  if (testInfo.project.name === 'desktop') await expect(page.getByTestId('read-only-chip').first()).toBeVisible();

  await page.getByTestId('conflict-keep-copy').click();
  await expect(page.getByTestId('conflict-copy-done')).toContainText(`${original} (my edits)`);

  // The copy exists as a new timeline in the project and holds MY split; the original keeps THEIR rename.
  const list = await api.listTimelines(seed.projectId);
  const copy = list.find((t) => t.name === `${original} (my edits)`);
  expect(copy, 'the copy is listed by the API').toBeTruthy();
  const copyTl = await api.getTimeline(seed.projectId, copy!.id);
  expect(hasCutAt(copyTl, 75), 'the copy holds my split').toBe(true);
  const orig = await api.getTimeline(seed.projectId, id);
  expect(hasCutAt(orig, 75), 'the original was not overwritten by my edit').toBe(false);
  expect(videoItems(orig)[0].name).toBe('Renamed by someone else');

  await page.getByTestId('conflict-open-copy').click();
  await expect(page).toHaveURL(new RegExp(`/timeline/${copy!.id}`));
});

test('R02.1 "Use the latest" downloads a recovery file with my edits, then loads what is saved', async ({ freshTimeline, openEditor, page }) => {
  runOn('desktop', 'pixel7');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  await otherWriterRenames(ed, 0, 'Theirs');
  await bladeAt(ed, 75);
  await expect(page.getByTestId('conflict-dialog')).toBeVisible();
  const download = page.waitForEvent('download');
  await page.getByTestId('conflict-use-latest').click();
  const file = await download;
  expect(file.suggestedFilename()).toMatch(/-my-edits\.json$/);
  const path = await file.path();
  const fs = await import('fs');
  const saved = JSON.parse(fs.readFileSync(path, 'utf8')) as { localDoc: { tracks: Array<{ items: Array<{ recordIn: number }> }> } };
  expect(fs.statSync(path).size, 'the file is not empty').toBeGreaterThan(0);
  expect(saved.localDoc.tracks.some((t) => t.items.some((i) => i.recordIn === 75)), 'the file holds my split').toBe(true);
  await expect(ed.canvas).toBeVisible({ timeout: 45_000 });
  await expect(ed.chip).toHaveText('Saved');
  await expect(page.getByTestId('conflict-dialog')).toHaveCount(0);
  expect(hasCutAt(await ed.serverTimeline(), 75)).toBe(false);
});

test('R02.1 two tabs of one browser, disjoint edits (no tab detection): merged, both stored', async ({ freshTimeline, context, seed, openEditor }, testInfo) => {
  runOn('desktop', 'pixel7');
  // Without BroadcastChannel (Safari private windows, older browsers) nothing tells the tabs apart: the rebase must still protect them.
  await context.addInitScript(() => { Reflect.deleteProperty(window, 'BroadcastChannel'); });
  const id = await freshTimeline('seeded');
  const edA = await openEditor(id);
  const pageB = await context.newPage();
  const edB = await EditorPage.open(pageB, seed, id, testInfo.project.name);
  await edB.openPanelTab(/markers/i);
  await edB.testId('marker-name-input').fill('Tab B');
  await edB.tap(edB.testId('marker-add'));
  await edB.expectServer((tl) => (tl.document.markers ?? []).some((m) => m.name === 'Tab B'), 'tab B\'s marker is stored');

  await bladeAt(edA, 75);
  await edA.expectServer((tl) => hasCutAt(tl, 75) && (tl.document.markers ?? []).some((m) => m.name === 'Tab B'), 'tab A\'s split merged with tab B\'s marker');
  await expect(edA.chip).toHaveText('Saved');
});

test('R02.1 two tabs of one browser, overlapping edits: Keep my copy, nothing lost', async ({ freshTimeline, context, seed, openEditor, api }, testInfo) => {
  runOn('desktop');
  await context.addInitScript(() => { Reflect.deleteProperty(window, 'BroadcastChannel'); });
  const id = await freshTimeline('seeded');
  const edA = await openEditor(id);
  const base = await edA.serverTimeline();
  const pageB = await context.newPage();
  const edB = await EditorPage.open(pageB, seed, id, testInfo.project.name);
  await bladeAt(edB, 75);
  await edB.expectServer((tl) => hasCutAt(tl, 75), 'tab B\'s split is stored');

  await bladeAt(edA, 100);
  await expect(edA.page.getByTestId('conflict-dialog')).toBeVisible();
  await edA.page.getByTestId('conflict-keep-copy').click();
  await expect(edA.page.getByTestId('conflict-copy-done')).toBeVisible();
  const copy = (await api.listTimelines(seed.projectId)).find((t) => t.name === `${base.name} (my edits)`);
  expect(copy).toBeTruthy();
  const copyTl = await api.getTimeline(seed.projectId, copy!.id);
  expect(hasCutAt(copyTl, 100), 'tab A\'s edit survives in the copy').toBe(true);
  const orig = await api.getTimeline(seed.projectId, id);
  expect(hasCutAt(orig, 75), 'tab B\'s edit survives in the original').toBe(true);
});

test('R02.1 the same timeline in a second tab: "Open in another tab", and taking over makes the first read-only', async ({ freshTimeline, context, seed, openEditor }, testInfo) => {
  runOn('desktop', 'pixel7');
  const id = await freshTimeline('seeded');
  const edA = await openEditor(id);
  // Not EditorPage.open: that waits for "Saved", and this tab says "Open in another tab".
  const pageB = await context.newPage();
  await pageB.goto(edA.url);
  const chipB = pageB.getByTestId('save-state-chip');

  const notice = pageB.getByTestId('other-tab-banner');
  await expect(notice).toBeVisible({ timeout: 45_000 });
  await expect(notice).toContainText('Open in another tab');
  await expect(chipB).toHaveText('Open in another tab');
  await expect(pageB.getByTestId('read-only-chip').first()).toBeVisible();
  // The first tab keeps editing: no banner there.
  await expect(edA.page.getByTestId('other-tab-banner')).toHaveCount(0);
  if (testInfo.project.name !== 'desktop') await expectTapTarget(pageB.getByTestId('take-over'), 'Edit here instead');

  // Tab A has an unsaved edit when tab B takes over: it is flushed first, then A goes read-only.
  await bladeAt(edA, 75);
  await pageB.getByTestId('take-over').click();
  await expect(edA.page.getByTestId('taken-over-banner')).toBeVisible();
  await expect(edA.page.getByTestId('taken-over-banner')).toContainText('Editing moved to another tab');
  await edA.expectServer((tl) => hasCutAt(tl, 75), 'tab A\'s last edit was saved before it let go');
  // Tab B reloads from what A saved and is now the editing tab.
  await expect(chipB).toHaveText('Saved', { timeout: 45_000 });
  await expect(pageB.getByTestId('other-tab-banner')).toHaveCount(0);
  const edB = await EditorPage.open(pageB, seed, id, testInfo.project.name);
  await bladeAt(edB, 100);
  await edB.expectServer((tl) => hasCutAt(tl, 75) && hasCutAt(tl, 100), 'the taker edits on top of what the first tab saved');
});
