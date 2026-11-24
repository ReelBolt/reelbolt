import type { Locator } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { expectLargerScreenState, runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 05 media pool and bins ❌ rows. Acceptance: remediation-plan.md W0.3 (original-media URL), W0.4 (probe at
 * ingest), W0.7 (bins receive clips via "Add to bin…", no HTML5 drag; reparenting bins is cut on phone);
 * verdict-table.md P2-BIN / P2-META / P2-VIEW / P6-BIN rows.
 */

const tile = (ed: EditorPage, mediaRef: string) => ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="${mediaRef}"]`).first();

/**
 * Opens the tile's menu (the "…" button T-040 pinned to the tile's corner, named "More for <file>") and
 * returns it once it shows its "Add to bin" section: the path into a bin that needs no drag.
 */
async function openAddToBin(ed: EditorPage, mediaRef: string): Promise<Locator> {
  const more = ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="${mediaRef}"]`).first();
  if (ed.isPhone && !(await more.isVisible().catch(() => false))) {
    const pane = ed.page.locator('label', { hasText: /^Media$/ }).first();
    if (await pane.isVisible().catch(() => false)) await pane.tap();
  }
  await ed.tap(more);
  const menu = ed.page.getByRole('menu');
  await expect(menu.getByText(/add to bin/i).first(), 'a visible "Add to bin" menu (reachable by tap)').toBeVisible();
  return menu;
}

async function openTile(ed: EditorPage, mediaRef: string): Promise<void> {
  await ed.workspace('media');
  await ed.tap(tile(ed, mediaRef));
}

/** P2-META-01 (W0.4, R05.1): the Metadata panel shows the probed facts of clipA (h264, 30 fps, 1280x720, 12 s). */
test('P2-META-01 the metadata panel shows clipA\'s probed codec, frame rate, resolution and duration', async ({ seed, openEditor }) => {
  runOn('desktop');
  knownFailure('P2-META-01');
  const ed = await openEditor(seed.seededTimelineId);
  await openTile(ed, `file:${seed.files.clipA}`);
  await expect(ed.testId('metadata-panel')).toBeVisible();
  await expect(ed.testId('metadata-value-codec')).toContainText(/h\.?264|avc/i);
  await expect(ed.testId('metadata-value-frameRate')).toContainText(/\b30\b/);
  await expect(ed.testId('metadata-value-resolution')).toContainText(/1280\s*[x×]\s*720/);
  await expect(ed.testId('metadata-value-duration')).toContainText(/12|00:00:12/);
});

/** P2-VIEW-01 (M) (W0.3, Seed 1): on a phone the source viewer's default Original mode plays clipA (no 409 from /content). */
test('P2-VIEW-01 phone source viewer plays the original clip', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P2-VIEW-01');
  const ed = await openEditor(seed.seededTimelineId);
  const failed: string[] = [];
  ed.page.on('response', (r) => { if (r.url().includes(seed.files.clipA) && r.status() >= 400) failed.push(`${r.status()} ${new URL(r.url()).pathname}`); });
  await openTile(ed, `file:${seed.files.clipA}`);
  const video = ed.page.getByTestId('source-viewer-video').first();
  await expect(video).toBeAttached();
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => (v.error ? `media error ${v.error.code} (${v.currentSrc})` : v.readyState >= 2 ? 'loaded' : `readyState ${v.readyState}`)), { message: 'the original loads', timeout: 20_000 }).toBe('loaded');
  // Playwright's Linux WebKit decodes H.264 unreliably (T-074 probe: all-zero frames, currentTime often stuck at 0;
  // 3 of 5 runs here), so on that project the row asserts what WebKit can prove: the original loads with no failed
  // request (the 409 the audit found). Playback on a real iPhone stays a manual check (audit D8).
  if (test.info().project.name === 'iphone14webkit') {
    test.info().annotations.push({ type: 'environment', description: 'playback not asserted: Linux WebKit H.264 decode (T-074)' });
  } else {
    await video.evaluate((v: HTMLVideoElement) => { v.muted = true; return v.play(); });
    await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { message: 'the original plays' }).toBeGreaterThan(0.2);
  }
  expect(failed, 'no failed request for clipA').toEqual([]);
});

/**
 * P2-VIEW-04 (M) (W0.3 409 half; the explanatory UI is deferred by R05): a render output's tile either plays
 * or states why it cannot; it never points the player at the text-editor route (`/content` -> 409).
 */
test('P2-VIEW-04 phone: a render output tile plays or says why, never a 409 from /content', async ({ seed, openEditor, api }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P2-VIEW-04');
  const pool = await api.json<Array<{ mediaRef: string; fileName: string; category: string }>>('GET', `/api/v1/projects/${seed.projectId}/media`);
  const output = pool.find((m) => m.category === 'outputFiles' && m.fileName.startsWith('remediation derush'));
  expect(output, 'the derush run output is in the pool').toBeTruthy();
  const ed = await openEditor(seed.seededTimelineId);
  const bad: string[] = [];
  ed.page.on('response', (r) => { if (/\/files\/[^/]+\/content/.test(r.url()) && r.status() >= 400) bad.push(`${r.status()} ${new URL(r.url()).pathname}`); });
  await openTile(ed, output!.mediaRef);
  const video = ed.page.getByTestId('source-viewer-video').first();
  const unplayable = ed.page.getByTestId('source-viewer-unplayable').first();
  await expect.poll(async () => {
    if (await unplayable.isVisible().catch(() => false)) return 'explained';
    if (!(await video.count())) return 'no player';
    const st = await video.evaluate((v: HTMLVideoElement) => (v.error ? `error ${v.error.code}` : v.readyState));
    return typeof st === 'number' && st >= 2 ? 'playable' : String(st);
  }, { message: 'the output plays or the viewer says why', timeout: 20_000 }).toMatch(/^(explained|playable)$/);
  expect(bad, 'no player request to the text-editor route').toEqual([]);
});

/** P2-BIN-06 (M) (W0.7): a clip goes into a bin by tap ("Add to bin…"), no drag. */
test('P2-BIN-06 phone: pin a clip into a bin with "Add to bin"', async ({ seed, openEditor, api }, testInfo) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P2-BIN-06');
  const name = `bin06-${testInfo.project.name}-${Date.now()}`;
  const bin = await api.json<{ id: string }>('POST', `/api/v1/projects/${seed.projectId}/media/bins`, { name }, [200, 201]);
  const ed = await openEditor(seed.seededTimelineId);
  // The Add media sheet (RM.8): the tile's Actions button opens the menu straight away.
  await ed.workspace('media');
  const menu = await openAddToBin(ed, `file:${seed.files.clipA}`);
  await ed.tap(menu.getByRole('menuitem', { name }).first());
  await expect.poll(async () => {
    const items = await api.json<unknown>('GET', `/api/v1/projects/${seed.projectId}/media/bins/${bin.id}/items`);
    return JSON.stringify(items).includes(seed.files.clipA);
  }, { message: 'clipA is pinned in the bin on the server' }).toBe(true);
});

/** P6-BIN-03 (M) (W0.7, R05.3): a smart bin is listed on the phone so choosing it says why it cannot hold a clip; nothing is written. */
test('P6-BIN-03 phone: "Add to bin" refuses a smart bin in plain words and writes nothing', async ({ seed, openEditor, api }, testInfo) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P6-BIN-03');
  const name = `smart03-${testInfo.project.name}-${Date.now()}`;
  const bin = await api.json<{ id: string }>('POST', `/api/v1/projects/${seed.projectId}/media/bins`, { name, smartFilter: { favourite: true } }, [200, 201]);
  const ed = await openEditor(seed.seededTimelineId);
  await ed.workspace('media');
  const menu = await openAddToBin(ed, `file:${seed.files.clipA}`);
  await ed.tap(menu.getByRole('menuitem', { name }).first());
  await expect(ed.testId('add-media-problem')).toContainText('is a smart bin');
  const items = await api.json<unknown>('GET', `/api/v1/projects/${seed.projectId}/media/bins/${bin.id}/items`).catch(() => []);
  expect(JSON.stringify(items)).not.toContain(seed.files.clipA);
});

/** P2-BIN-04 (M) (cut on phone, R05): reparenting bins shows the "needs a larger screen" state. */
test('P2-BIN-04 phone: moving a bin shows the "needs a larger screen" state', async ({ seed, openEditor, api }, testInfo) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P2-BIN-04');
  await api.json('POST', `/api/v1/projects/${seed.projectId}/media/bins`, { name: `bin04-${testInfo.project.name}-${Date.now()}` }, [200, 201]);
  const ed = await openEditor(seed.seededTimelineId);
  await ed.workspace('media');
  await expectLargerScreenState(ed.page, /bin/i, 'Moving a bin on a phone');
});
