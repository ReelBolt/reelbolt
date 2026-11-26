import { test, expect, EditorPage, type Seed } from '../../../fixtures/editor-v2';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { startRender, waitRender, runOn } from '../../../fixtures/editor-v2/rows';
import { TABLET, seekTo, tapOn } from './journey-helpers';

/**
 * T-027 Journey 4 (Wave 2 exit): version history. Edits and named versions build a history with real causes (named,
 * before a render, before a restore); EVERY listed row restores, and each time the stored document equals the stored
 * snapshot of that point. Then two tabs: an overlapping edit meets the other tab's save, "Keep my copy" saves mine as a
 * new timeline, and both documents exist.
 */

type Version = { revision: number; name?: string; cause: string; createdAt: string; expiresAt?: string };

const videoItems = (tl: Timeline): Item[] => tl.document.tracks.find((t) => t.kind === 'video')!.items;
const hasCutAt = (tl: Timeline, frame: number): boolean => videoItems(tl).some((i) => i.recordIn === frame);

/** Key-sorted JSON, so two documents compare by content and not by property order. */
const canon = (v: unknown): string => JSON.stringify(v, (_k, x: unknown) => (x && typeof x === 'object' && !Array.isArray(x) ? Object.fromEntries(Object.entries(x as object).sort(([a], [b]) => a.localeCompare(b))) : x));

async function openVersions(ed: EditorPage, touch: boolean): Promise<void> {
  const tab = ed.page.getByRole('tab', { name: /versions/i }).first();
  if (!(await tab.isVisible().catch(() => false))) await ed.openInspector();
  await tapOn(ed, tab, touch);
  await expect(ed.testId('revision-history-panel')).toBeVisible();
}

async function refresh(ed: EditorPage, touch: boolean): Promise<void> {
  await ed.openDrawer();
  // A toast from the last step can sit over the drawer's corner: let it go before tapping.
  await expect(ed.page.locator('.mantine-Notification-root')).toHaveCount(0, { timeout: 30_000 });
  await tapOn(ed, ed.testId('revision-history-panel').getByRole('button', { name: 'Refresh' }), touch);
}

/** The visible Split button, cut at the playhead (the same control on desktop and tablet). */
async function splitAt(ed: EditorPage, frame: number, touch: boolean): Promise<void> {
  await seekTo(ed, frame, touch);
  const visible = ed.page.locator('[data-testid="blade-at-playhead"]:visible').first();
  await tapOn(ed, visible, touch);
}

async function nameVersion(ed: EditorPage, name: string, touch: boolean): Promise<void> {
  await ed.openDrawer(); // tablet: a seek closes the Inspector drawer that holds the Versions tab
  await tapOn(ed, ed.testId('version-save-named'), touch);
  await ed.testId('version-name-input').fill(name);
  await tapOn(ed, ed.testId('version-name-save'), touch);
}

async function journey4History(ed: EditorPage, seed: Seed, touch: boolean): Promise<void> {
  const { api } = seed;
  const base = `${api.timelinesUrl(seed.projectId, ed.timelineId)}`;
  const versions = () => api.json<{ versions: Version[] }>('GET', `${base}/versions`).then((r) => r.versions);
  const snapshot = (revision: number) => api.json<{ document: unknown }>('GET', `${base}/revisions/${revision}`).then((r) => r.document);

  // 1. Build a history through the UI: two named versions, a render start, then one more edit.
  await openVersions(ed, touch);
  await splitAt(ed, 75, touch);
  await ed.expectServer((t) => hasCutAt(t, 75), 'the first split is stored');
  await nameVersion(ed, 'Edit one', touch);
  await expect.poll(async () => (await versions()).some((v) => v.name === 'Edit one'), { message: 'Edit one is a named version' }).toBe(true);
  await splitAt(ed, 225, touch);
  await ed.expectServer((t) => hasCutAt(t, 225), 'the second split is stored');
  await nameVersion(ed, 'Edit two', touch);
  await expect.poll(async () => (await versions()).some((v) => v.name === 'Edit two'), { message: 'Edit two is a named version' }).toBe(true);
  await splitAt(ed, 375, touch);
  await ed.expectServer((t) => hasCutAt(t, 375), 'the third split is stored');
  const pinned = await startRender(api, seed.projectId, ed.timelineId); // pins this revision, which has no point of its own yet
  await expect.poll(async () => (await versions()).some((v) => v.cause === 'beforeRender'), { message: 'a render start stores a version' }).toBe(true);
  await waitRender(api, seed.projectId, ed.timelineId, pinned.executionId); // never leave a Running render behind: it holds the org's execution slot for the next spec
  await splitAt(ed, 525, touch);
  await ed.expectServer((t) => hasCutAt(t, 525), 'the fourth split is stored (it has no point yet, so the restore has something to keep)');

  // 2. Restoring "Edit one" asks first, then brings its document back as a new revision and keeps what it replaced.
  await refresh(ed, touch);
  const editOne = (await versions()).find((v) => v.name === 'Edit one')!;
  const before = await ed.serverTimeline();
  await tapOn(ed, ed.testId(`revision-restore-${editOne.revision}`), touch);
  await expect(ed.testId('version-restore-text')).toContainText('Before you restored');
  expect((await ed.serverTimeline()).revision, 'asking changed nothing').toBe(before.revision);
  await tapOn(ed, ed.testId(`revision-restore-confirm-${editOne.revision}`), touch);
  await expect.poll(async () => (await ed.serverTimeline()).revision, { message: 'the restore is stored' }).toBeGreaterThan(before.revision);
  await ed.expectServer((t) => t.revision > before.revision && hasCutAt(t, 75) && !hasCutAt(t, 225) && !hasCutAt(t, 375) && !hasCutAt(t, 525), '"Edit one" is the stored timeline again');
  await expect.poll(async () => (await versions()).some((v) => v.cause === 'beforeRestore'), { message: 'the replaced state is kept as a point' }).toBe(true);

  // 3. EVERY listed point restores, and the stored document is exactly that point's snapshot.
  const listed = (await versions()).map((v) => v.revision);
  expect(listed.length, 'the history lists named, before-render and before-restore points').toBeGreaterThanOrEqual(4);
  const causes = new Set((await versions()).map((v) => v.cause));
  for (const cause of ['named', 'beforeRender', 'beforeRestore']) expect(causes.has(cause), `a "${cause}" point is listed`).toBe(true);
  for (const revision of listed) {
    const prior = (await ed.serverTimeline()).revision;
    const stored = await snapshot(revision);
    await refresh(ed, touch);
    await tapOn(ed, ed.testId(`revision-restore-${revision}`), touch);
    await tapOn(ed, ed.testId(`revision-restore-confirm-${revision}`), touch);
    const after = await ed.expectServer((t) => t.revision > prior, `point ${revision} was restored as a new revision`);
    expect(canon(after.document), `point ${revision}: the stored document equals that point's snapshot`).toBe(canon(stored));
  }
}

/** Two tabs of one browser; tab A edits the clip tab B already edited, so its save meets B's. */
async function journey4Conflict(ed: EditorPage, seed: Seed, touch: boolean, context: import('@playwright/test').BrowserContext, projectName: string): Promise<void> {
  const { api } = seed;
  const original = await ed.serverTimeline();
  const edB = await EditorPage.open(await context.newPage(), seed, ed.timelineId, projectName);

  // Tab B cuts the first clip at 40 and it is saved.
  await splitAt(edB, 40, touch);
  await edB.expectServer((t) => hasCutAt(t, 40), 'tab B\'s split is stored');
  // Tab A (which never heard of it) cuts the same clip at 100.
  await splitAt(ed, 100, touch);
  const dialog = ed.page.getByTestId('conflict-dialog');
  await expect(dialog).toBeVisible({ timeout: 20_000 });
  await expect(dialog).toContainText('your changes touch the same part');
  await expect(ed.chip).toHaveText('Not saved. This timeline was changed somewhere else.');
  await tapOn(ed, ed.page.getByTestId('conflict-keep-copy'), touch);
  await expect(ed.page.getByTestId('conflict-copy-done')).toContainText(`${original.name} (my edits)`);

  // Both documents exist: the original keeps tab B's cut, the copy holds tab A's.
  const list = await api.listTimelines(seed.projectId);
  const copy = list.find((t) => t.name === `${original.name} (my edits)`);
  expect(copy, 'the copy is listed by the API').toBeTruthy();
  try {
    const mine = await api.getTimeline(seed.projectId, copy!.id);
    const theirs = await api.getTimeline(seed.projectId, ed.timelineId);
    expect(hasCutAt(mine, 100), 'the copy holds my cut').toBe(true);
    expect(hasCutAt(theirs, 40), 'the original keeps tab B\'s cut').toBe(true);
    expect(hasCutAt(theirs, 100), 'the original was not overwritten by my edit').toBe(false);
    expect(mine.id).not.toBe(theirs.id);
    await tapOn(ed, ed.page.getByTestId('conflict-open-copy'), touch);
    await expect(ed.page).toHaveURL(new RegExp(`/timeline/${copy!.id}`));
    await expect(ed.canvas).toBeVisible({ timeout: 45_000 });
    expect(hasCutAt(await api.getTimeline(seed.projectId, copy!.id), 100), 'the copy opens and still holds my cut').toBe(true);
  } finally {
    if (copy) await api.json('DELETE', api.timelinesUrl(seed.projectId, copy.id), undefined, [200, 204, 404]);
  }
}

test.describe('J4 version history', () => {
  test('J4 desktop: every listed version restores to its snapshot; a two-tab conflict keeps both versions', async ({ freshTimeline, openEditor, seed, context }, testInfo) => {
    runOn('desktop');
    test.setTimeout(420_000);
    await context.addInitScript(() => { Reflect.deleteProperty(window, 'BroadcastChannel'); });
    const ed = await openEditor(await freshTimeline('seeded'));
    await journey4History(ed, seed, false);
    const ed2 = await openEditor(await freshTimeline('seeded'));
    await journey4Conflict(ed2, seed, false, context, testInfo.project.name);
  });

  test.describe('tablet 1024x768, touch, coarse pointer', () => {
    test.use({ viewport: TABLET, hasTouch: true });
    test('J4 tablet: the same by touch', async ({ page, freshTimeline, openEditor, seed, context }, testInfo) => {
      runOn('desktop');
      test.setTimeout(420_000);
      await context.addInitScript(() => { Reflect.deleteProperty(window, 'BroadcastChannel'); });
      const ed = await openEditor(await freshTimeline('seeded'));
      expect(await page.evaluate(() => window.matchMedia('(pointer: coarse)').matches), 'a coarse pointer').toBe(true);
      await journey4History(ed, seed, true);
      const ed2 = await openEditor(await freshTimeline('seeded'));
      await journey4Conflict(ed2, seed, true, context, testInfo.project.name);
    });
  });
});
