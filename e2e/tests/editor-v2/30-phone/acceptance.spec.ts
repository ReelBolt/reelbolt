import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, countItems, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { audioTimeline, audioTrack } from '../../../fixtures/editor-v2/audio-lane';
import { stubAnalysis } from '../../../fixtures/editor-v2/analysis-stub';
import { box, createPicture, getPicture, inside, openPicture, overlaps } from '../../../fixtures/editor-v2/picture';
import { renderAndWait, runOn } from '../../../fixtures/editor-v2/rows';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';
import { TouchScript, clippedControls, controlRects, countPointerCancels, insideViewport, noHorizontalScroll, pointerCancels, smallControls } from '../../../fixtures/editor-v2/phone-measure';
import { bladeAt, hasCutAt, otherWriterRenames } from '../02-save-sync/sync-helpers';
import type { Item, Timeline, TimelineDocument } from '../../../fixtures/editor-v2/api';

/**
 * Wave 3 / T-013 (RM.16c): the 14 phone acceptance criteria of the mobile-model verdict, one test each, titled "AC n".
 * The long journeys live in the per-feature specs next to this file; an AC that one of them already proves is checked
 * here by a thin assertion of the same real outcome (the saved document, the server, the measured boxes), not by
 * repeating the journey. Projects: pixel7 (real CDP touch) and iphone14webkit (synthetic pointers); AC 3 and the
 * landscape and pinch checks need real touch and run on pixel7 only (recorded in ledgers/suite-skips.md).
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const DOCK = ['media', 'edit', 'audio', 'look', 'captions'] as const;
const tap = (ed: EditorPage, testId: string) => ed.tap(ed.testId(testId));
const clipNamed = (tl: Timeline, name: string): (Item & { sourceIn?: number; grade?: { nodes?: Array<{ name?: string }> } | null }) | undefined => itemNamed(tl, name) as never;

test('AC 1 no horizontal page scroll at 360, 390 and 412 px, and nothing is clipped (header, strip, dock, every open sheet)', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  for (const width of [360, 390, 412]) {
    await ed.page.setViewportSize({ width, height: 780 });
    await expect(ed.testId('phone-header')).toBeVisible();
    await noHorizontalScroll(ed.page);
    for (const id of ['phone-header', 'phone-strip', 'phone-dock']) await insideViewport(ed.page, id);
    expect(await clippedControls(ed.page, '[data-testid="phone-header"]'), `header at ${width}px`).toEqual([]);
    expect(await clippedControls(ed.page, '[data-testid="phone-dock"]'), `dock at ${width}px`).toEqual([]);
    for (const item of DOCK) {
      await tap(ed, `dock-${item}`);
      await expect(ed.testId('phone-sheet')).toBeVisible();
      await insideViewport(ed.page, 'phone-sheet');
      await noHorizontalScroll(ed.page);
      expect(await clippedControls(ed.page, '[data-testid="phone-sheet"]'), `${item} sheet at ${width}px`).toEqual([]);
    }
    await tap(ed, 'phone-export');
    await expect(ed.testId('deliver-workspace')).toBeVisible();
    await insideViewport(ed.page, 'phone-sheet');
    await noHorizontalScroll(ed.page);
    expect(await clippedControls(ed.page, '[data-testid="phone-sheet"]'), `Export sheet at ${width}px`).toEqual([]);
    await ed.page.keyboard.press('Escape');
    await expect(ed.testId('phone-sheet')).toHaveCount(0);
  }
});

test('AC 2 every control in the header, action bar, dock and sheets is at least 44x44, and Delete keeps 8 px from its neighbours', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  expect(await smallControls(ed.page, '[data-testid="phone-header"]'), 'header').toEqual([]);
  expect(await smallControls(ed.page, '[data-testid="phone-dock"]'), 'dock').toEqual([]);
  expect(await smallControls(ed.page, '[data-testid="phone-action-bar"]'), 'action bar with nothing selected').toEqual([]);
  await ed.selectClipAt(30);
  await expect(ed.testId('phone-action-bar')).toHaveAttribute('data-mode', 'clip');
  expect(await smallControls(ed.page, '[data-testid="phone-action-bar"]'), 'action bar with a clip selected').toEqual([]);
  // Destructive-to-neutral gap, computed from the boxes: nothing else in the bar sits within 8 px of Delete.
  const bar = await controlRects(ed.page, '[data-testid="phone-action-bar"]');
  const del = bar.find((r) => r.name.includes('phone-action-delete'))!;
  expect(del, 'Delete is in the bar').toBeTruthy();
  for (const other of bar.filter((r) => r !== del)) {
    const horizontal = Math.max(other.x - del.right, del.x - other.right);
    const vertical = Math.max(other.y - del.bottom, del.y - other.bottom);
    expect(Math.max(horizontal, vertical), `${other.name} is 8 px clear of Delete`).toBeGreaterThanOrEqual(8);
  }
  for (const item of DOCK) {
    await tap(ed, `dock-${item}`);
    await expect(ed.testId('phone-sheet')).toBeVisible();
    expect(await smallControls(ed.page, '[data-testid="phone-sheet"]'), `${item} sheet`).toEqual([]);
  }
  await tap(ed, 'phone-export');
  await expect(ed.testId('deliver-workspace')).toBeVisible();
  expect(await smallControls(ed.page, '[data-testid="phone-sheet"]'), 'Export sheet').toEqual([]);
});

test('AC 3 a touch stream on the strip never ends in pointercancel for pan, trim-handle drag and pinch', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const touch = await TouchScript.on(ed.page);
  await ed.selectClipAt(30);
  await countPointerCancels(ed.page);
  const handle = (await ed.testId('trim-handle-out').boundingBox())!;
  const h = { x: handle.x + handle.width / 2, y: handle.y + handle.height / 2 };
  await touch.start([{ id: 1, x: h.x, y: h.y }]);
  await touch.glide([{ id: 1, x: h.x, y: h.y }], [{ id: 1, x: h.x - 40, y: h.y }]);
  await touch.end();
  await ed.expectServer((t) => (itemNamed(t, 'clipA.mp4')?.recordOut ?? 60) < 60, 'the trim-handle drag shortened clip A in the saved document');
  const cv = (await ed.canvas.boundingBox())!;
  const y = await ed.laneY(0);
  const pan = { id: 1, x: cv.x + cv.width - 50, y };
  await touch.start([pan]);
  await touch.glide([pan], [{ id: 1, x: pan.x - 120, y }]);
  await touch.end();
  const a = [{ id: 1, x: cv.x + 150, y }, { id: 2, x: cv.x + 230, y }];
  await touch.start(a);
  await touch.glide(a, [{ id: 1, x: cv.x + 90, y }, { id: 2, x: cv.x + 290, y }]);
  await touch.end();
  expect(await pointerCancels(ed.page), 'pan, trim and pinch produced no pointercancel').toBe(0);
});

test('AC 4 phone-only journey with no keyboard and no drag: split, ripple delete, lift, duplicate, nudge, move, trim, undo and redo, each in the saved document', async ({ freshTimeline, openEditor, api, seed }) => {
  test.setTimeout(240_000);
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const more = async (id: string) => { await tap(ed, 'phone-action-more'); await tap(ed, `phone-more-${id}`); };

  await ed.selectClipAt(30);
  await ed.seek(30);
  await ed.splitAtPlayhead();
  await ed.expectServer((t) => countItems(t) === 3 && hasCutAt30(t), 'split: three clips, cut at frame 30');
  await tap(ed, 'phone-undo');
  await ed.expectServer((t) => countItems(t) === 2, 'undo: back to two clips');
  await tap(ed, 'phone-redo');
  await ed.expectServer((t) => countItems(t) === 3, 'redo: split again');
  await tap(ed, 'phone-undo');
  await ed.expectServer((t) => countItems(t) === 2, 'undo again, baseline');

  await ed.selectClipAt(30);
  await tap(ed, 'phone-action-delete');
  const del = await ed.expectServer((t) => countItems(t) === 1, 'ripple delete: clip A is gone');
  expect(clipNamed(del, 'clipB.mp4')!.recordIn, 'the gap closed').toBe(0);
  await tap(ed, 'phone-undo');
  await ed.expectServer((t) => countItems(t) === 2 && clipNamed(t, 'clipB.mp4')?.recordIn === 60, 'undo restores clip A and the gap');

  await ed.selectClipAt(30);
  await more('lift');
  const lifted = await ed.expectServer((t) => countItems(t) === 1, 'lift: clip A is gone');
  expect(clipNamed(lifted, 'clipB.mp4')!.recordIn, 'lift leaves the gap').toBe(60);
  await tap(ed, 'phone-undo');
  await ed.expectServer((t) => countItems(t) === 2, 'undo restores the lifted clip');

  await ed.selectClipAt(30);
  await tap(ed, 'phone-action-duplicate');
  await ed.expectServer((t) => countItems(t) === 3, 'duplicate: a copy is stored');
  await tap(ed, 'phone-undo');
  await ed.expectServer((t) => countItems(t) === 2, 'undo removes the copy');

  await ed.selectClipAt(90);
  await tap(ed, 'phone-action-nudge');
  await tap(ed, 'phone-nudge+10');
  await ed.expectServer((t) => clipNamed(t, 'clipB.mp4')?.recordIn === 70, 'nudge: clip B moved 10 frames later');
  await tap(ed, 'phone-action-sheet-nudge-close');
  await tap(ed, 'phone-undo');
  await ed.expectServer((t) => clipNamed(t, 'clipB.mp4')?.recordIn === 60, 'undo puts clip B back');

  await ed.selectClipAt(30);
  await more('move-later');
  await ed.expectServer((t) => clipNamed(t, 'clipB.mp4')?.recordIn === 0, 'move later: clip A and clip B traded places');
  await ed.selectClipAt(90);
  await more('move-earlier');
  await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.recordIn === 0, 'move earlier: back in the first order');

  await ed.selectClipAt(30);
  await tap(ed, 'phone-action-trim');
  await tap(ed, 'trim-end-10');
  await tap(ed, 'trim-done');
  const trimmed = await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.recordOut === 50, 'trim via the sheet: clip A ends 10 frames earlier');
  expect(clipNamed(trimmed, 'clipB.mp4')!.recordIn, 'the next clip followed (ripple)').toBe(50);
});

test('AC 5 Remove silence is reachable from Captions and its summary matches the analysis', async ({ page, freshTimeline, openEditor, api, seed }) => {
  // clipA is quiet at 0.5-1.0 s: 15 frames, 2 kept on each side by default, so 11 frames go.
  await stubAnalysis(page, [{ fileId: seed.files.clipA, silence: [{ startSec: 0.5, endSec: 1.0 }] }], { readyAtStart: true });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const length = (tl: Timeline) => tl.document.tracks[0].items.reduce((n, i) => n + (i.recordOut - i.recordIn), 0);
  const before = length(await ed.serverTimeline());
  await ed.openRemoveSilence();
  await expect(page.locator('[data-testid^="remove-silence-span-"]'), 'the one measured silence is listed').toHaveCount(1);
  await expect(ed.testId('remove-silence-summary')).toContainText('1 cut, 11 frames removed');
  await tap(ed, 'remove-silence-apply');
  await ed.expectServer((t) => length(t) === before - 11, 'exactly the summarised 11 frames were removed');
});

test('AC 6 Add media from the file picker, insert at the playhead and move to a bin from the tile menu (no drag)', async ({ freshTimeline, openEditor, api, seed }, testInfo) => {
  test.setTimeout(240_000);
  const stamp = Date.now();
  const ref = (id: string) => `file:${id}`;
  const binsUrl = `/api/v1/projects/${seed.projectId}/media/bins`;
  const bin = await api.json<{ id: string }>('POST', binsUrl, { name: `Acc-${stamp}` }, [200, 201]);
  const fileName = `ac6-${stamp}-${testInfo.project.name}.png`;
  const tmp = path.join(os.tmpdir(), fileName);
  fs.copyFileSync(path.join(ensureMedia(), 'still.png'), tmp);
  try {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.seek(60);
    await ed.workspace('media');
    await expect(ed.testId('phone-sheet')).toHaveAttribute('aria-label', 'Add media');
    await expect(ed.page.getByTestId('media-clip-tile').first()).toBeVisible({ timeout: 30_000 });
    await ed.testId('add-media-input').setInputFiles(tmp);
    await expect.poll(async () => {
      const pool = await api.json<Array<{ fileName: string; sizeBytes: number }>>('GET', `/api/v1/projects/${seed.projectId}/media`);
      return pool.find((m) => m.fileName === fileName)?.sizeBytes ?? 0;
    }, { message: 'the picked file is in the project with bytes', timeout: 60_000 }).toBeGreaterThan(0);

    const tileMenu = (mediaRef: string) => ed.tap(ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="${mediaRef}"]`).first());
    await tileMenu(ref(seed.files.vertical));
    await tap(ed, 'tile-place-insert');
    const tl = await ed.expectServer((t) => countItems(t) === 3, 'insert at the playhead is stored');
    expect(tl.document.tracks[0].items.find((i) => JSON.stringify(i).includes(seed.files.vertical))!.recordIn, 'at the playhead').toBe(60);

    await ed.workspace('media');
    await tileMenu(ref(seed.files.still));
    await tap(ed, `add-to-bin-${bin.id}`);
    await expect.poll(async () => {
      const items = await api.json<Array<{ mediaRef: string }>>('GET', `${binsUrl}/${bin.id}/items`);
      return items.map((i) => i.mediaRef);
    }, { message: 'the tile menu pinned the clip to the bin' }).toEqual([ref(seed.files.still)]);
  } finally {
    fs.rmSync(tmp, { force: true });
    await api.json('DELETE', `${binsUrl}/${bin.id}`, undefined, [200, 204, 404]);
  }
});

test('AC 7 a caption edit saves, the Look sheet applies to all clips, Duck music and Clean voice are one undo step each', async ({ freshTimeline, openEditor, api, seed }) => {
  test.setTimeout(240_000);
  await test.step('caption text edit saves', async () => {
    const K = '0a000000-0000-4000-8000-0000000000a1';
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR, {
      mutate: (doc: TimelineDocument) => {
        const v = doc.tracks.find((t) => t.kind === 'video')!;
        const caption: Item = { type: 'caption', id: K, name: 'Hello', recordIn: 0, recordOut: 60, text: 'Hello there my friend', language: 'en', enabled: true, provenance: null };
        doc.tracks.splice(doc.tracks.indexOf(v) + 1, 0, { ...structuredClone(v), id: crypto.randomUUID(), name: 'Subtitles', items: [caption] });
      },
    }));
    await tap(ed, 'dock-captions');
    await tap(ed, `phone-caption-edit-${K}`);
    await ed.testId('caption-edit-text').fill('Hello again, everyone');
    await tap(ed, 'caption-edit-done');
    await ed.expectServer((tl) => tl.document.tracks.flatMap((t) => t.items).some((i) => i.type === 'caption' && i.text === 'Hello again, everyone'), 'the new caption text is stored');
  });
  await test.step('Look sheet applies a look to all clips', async () => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'dock-look');
    await tap(ed, 'look-warm');
    await tap(ed, 'look-apply-all');
    const hasLook = (tl: Timeline, name: string) => Boolean(clipNamed(tl, name)?.grade?.nodes?.some((n) => typeof n.name === 'string' && n.name.startsWith('Look: ')));
    await ed.expectServer((t) => hasLook(t, 'clipA.mp4') && hasLook(t, 'clipB.mp4'), 'both clips carry the look in the saved document');
  });
  await test.step('Duck music and Clean voice are one undo step each', async () => {
    const ed = await openEditor(await audioTimeline(freshTimeline, api, seed, [
      { name: 'Music', clips: [{ name: 'music.wav', recordIn: 0, recordOut: 140 }, { name: 'music.wav', recordIn: 140, recordOut: 240 }] },
      { name: 'Voice', clips: [{ name: 'voice-a.wav', recordIn: 60, recordOut: 120 }, { name: 'voice-b.wav', recordIn: 150, recordOut: 210 }] },
    ]));
    await ed.workspace('audio');
    await tap(ed, 'phone-audio-duck');
    await ed.expectServer((tl) => (audioTrack(tl, 'Music')?.gainDb?.keys?.length ?? 0) > 0, 'the music fader holds a ducking curve');
    await tap(ed, 'phone-undo');
    await ed.expectServer((tl) => !audioTrack(tl, 'Music')?.gainDb?.keys?.length, 'one Undo removed the ducking');
    await tap(ed, 'phone-audio-clean-voice');
    await ed.expectServer((tl) => Boolean(audioTrack(tl, 'Voice')?.denoise) && Boolean(audioTrack(tl, 'Voice')?.leveler), 'noise removal and the leveler are on the Voice track');
    await tap(ed, 'phone-undo');
    await ed.expectServer((tl) => !audioTrack(tl, 'Voice')?.denoise && !audioTrack(tl, 'Voice')?.leveler, 'one Undo took both back');
  });
});

test('AC 8 header Export opens Deliver, Render stays in view without scrolling, and a finished film downloads with bytes', async ({ freshTimeline, openEditor, api, seed }) => {
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  const done = await renderAndWait(api, seed.projectId, id, { range: { inFrame: 0, outFrame: 12 }, quality: 'draft' });
  expect(done.status).toBe('Passed');
  const ed = await openEditor(id);
  await tap(ed, 'phone-export');
  await expect(ed.testId('deliver-workspace')).toBeVisible();
  const render = (await ed.testId('render-start').boundingBox())!;
  const vp = ed.page.viewportSize()!;
  expect(render.y, 'Render starts inside the window').toBeGreaterThanOrEqual(0);
  expect(render.y + render.height, 'Render ends inside the window').toBeLessThanOrEqual(vp.height);
  await expect(ed.testId('film-card')).toBeVisible();
  await expect(ed.testId('film-download'), 'the finished film offers Download').toBeVisible();
  const [download] = await Promise.all([ed.page.waitForEvent('download'), tap(ed, 'film-download')]);
  let bytes = 0;
  for await (const chunk of await download.createReadStream()) bytes += (chunk as Buffer).length;
  expect(bytes, 'the downloaded film has bytes').toBeGreaterThan(0);
});

test('AC 9 the review page plays the newest render with a picture, shows a stale banner after later edits, and pins a comment to the paused frame', async ({ freshTimeline, api, seed, page }) => {
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  const done = await renderAndWait(api, seed.projectId, id, { quality: 'draft' });
  expect(done.status).toBe('Passed');
  await page.goto(`/app/editor/${seed.projectId}/review/${id}`);
  const video = page.getByTestId('mobile-review-video');
  await expect(video).toBeVisible();
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState), { message: 'the video loaded', timeout: 30_000 }).toBeGreaterThanOrEqual(2);
  await video.evaluate(async (v: HTMLVideoElement) => { v.muted = true; await v.play(); });
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { message: 'the video advances', timeout: 20_000 }).toBeGreaterThan(0.2);
  expect(await video.evaluate((v: HTMLVideoElement) => v.videoWidth), 'it has a picture').toBeGreaterThan(0);
  await expect(page.getByTestId('review-stale-banner')).toHaveCount(0);
  // A comment posted while paused at 2 s is stored near 2 s, not at frame 0.
  await video.evaluate((v: HTMLVideoElement) => new Promise<void>((resolve) => { v.pause(); v.addEventListener('seeked', () => resolve(), { once: true }); v.currentTime = 2; }));
  await page.getByTestId('review-body').fill('Fix the cut here');
  await page.getByTestId('review-add').click();
  const url = `${api.timelinesUrl(seed.projectId, id)}/comments`;
  await expect.poll(async () => (await api.json<Array<{ body: string; frame: number }>>('GET', url)).find((c) => c.body === 'Fix the cut here')?.frame ?? -1, { message: 'the stored frame is the paused frame' }).toBeGreaterThan(0);
  // An edit after the render makes the page say so.
  await api.putDocument(seed.projectId, id, (doc) => { doc.name = `${doc.name} edited`; });
  await page.reload();
  await expect(page.getByTestId('review-stale-banner')).toContainText('Newer edits exist');
});

test('AC 10 a 409 shows a bottom bar that never shrinks the strip below 160 px, and Keep my copy works', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const original = (await ed.serverTimeline()).name;
  await otherWriterRenames(ed, 0, 'Renamed by someone else');
  await bladeAt(ed, 75);
  const bar = ed.testId('conflict-dialog');
  await expect(bar).toBeVisible();
  expect((await ed.testId('phone-strip').boundingBox())!.height, 'the strip keeps its height').toBeGreaterThanOrEqual(160);
  await tap(ed, 'conflict-keep-copy');
  await expect(ed.testId('conflict-copy-done')).toContainText(`${original} (my edits)`);
  const copy = (await api.listTimelines(seed.projectId)).find((t) => t.name === `${original} (my edits)`);
  expect(copy, 'the copy is listed').toBeTruthy();
  expect(hasCutAt(await api.getTimeline(seed.projectId, copy!.id), 75), 'the copy holds my edit').toBe(true);
});

test('AC 11 a full-editor URL at 390x844 shows the phone surface and the note, with no full-view offer', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.page.setViewportSize({ width: 390, height: 844 });
  await expect(ed.testId('phone-header')).toBeVisible();
  // One notice at a time: the seeded clips have sound without a track, so that offer shows first and the note follows it.
  await ed.tap(ed.testId('link-migration-dismiss'));
  await expect(ed.page.getByText('Phone view. Some tools need a larger screen.')).toBeVisible();
  await expect(ed.page.getByText(/Open full view/)).toHaveCount(0);
});

test('AC 12 landscape 844x390: viewer, strip and one drawer are visible together, and the strip is at least two lanes tall', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.page.setViewportSize({ width: 844, height: 390 });
  await tap(ed, 'dock-edit');
  await expect(ed.testId('phone-sheet')).toBeVisible();
  const [viewer, strip, sheet] = await Promise.all(['phone-viewer', 'phone-strip', 'phone-sheet'].map(async (id) => (await ed.testId(id).boundingBox())!));
  for (const [name, b] of [['viewer', viewer], ['strip', strip], ['drawer', sheet]] as const) {
    expect(b.width, `${name} is on screen`).toBeGreaterThan(0);
    expect(b.height, `${name} is on screen`).toBeGreaterThan(0);
  }
  expect(sheet.x, 'the drawer is on the right and leaves the viewer free').toBeGreaterThanOrEqual(viewer.x + viewer.width - 1);
  expect(strip.height, 'at least two 56 px lanes').toBeGreaterThanOrEqual(112);
  await noHorizontalScroll(ed.page);
});

test('AC 13 picture editor: layer actions are 44 px, a pinch does not create a text layer, handles are clear of the zoom controls', async ({ page, api, seed }, testInfo) => {
  const pic = await createPicture(api, seed.projectId, seed.files.still, `ac13-${testInfo.project.name}-${Date.now()}`);
  const stage = await openPicture(page, seed.projectId, pic.id);
  await page.getByTestId('picture-dock-layers').tap();
  await expect(page.getByTestId('picture-sheet')).toBeVisible();
  const buttons = page.getByTestId(`layer-row-${pic.layers[0].id}`).getByRole('button');
  for (let i = 0; i < (await buttons.count()); i++) {
    const b = await box(buttons.nth(i), `layer row button ${i}`);
    expect(Math.min(b.width, b.height), `layer row button ${i} is at least 44 px`).toBeGreaterThanOrEqual(43.5);
  }
  await page.getByRole('button', { name: `Select ${pic.layers[0].label}`, exact: true }).first().tap();
  await page.getByTestId('picture-sheet-close').tap();
  // Handles of the selected layer stay inside the stage and clear of the zoom bar.
  const frame = await box(stage, 'the stage');
  const zoomBar = await box(stage.locator('div:has(> button[aria-label="Fit the picture on screen"])').first(), 'the zoom bar');
  const corners = stage.locator('svg rect[fill="#ffffff"]');
  for (let i = 0; i < (await corners.count()); i++) {
    const h = await box(corners.nth(i), `corner handle ${i}`);
    expect(inside(h, frame), `corner handle ${i} is inside the stage`).toBe(true);
    expect(overlaps(h, zoomBar), `corner handle ${i} is clear of the zoom bar`).toBe(false);
  }
  if (testInfo.project.name !== 'pixel7') return; // the pinch needs real CDP touch
  await page.getByRole('toolbar', { name: 'Tools' }).getByRole('button', { name: 'Text', exact: true }).tap();
  const b = await box(page.getByRole('img', { name: 'Your picture' }), 'the picture');
  const writes: string[] = [];
  page.on('request', (r) => { if (/^(PUT|PATCH|POST)$/.test(r.method()) && r.url().includes(pic.id)) writes.push(`${r.method()} ${new URL(r.url()).pathname}`); });
  const touch = await TouchScript.on(page);
  const from = [{ id: 1, x: b.x + b.width / 2 - 20, y: b.y + b.height / 2 }, { id: 2, x: b.x + b.width / 2 + 20, y: b.y + b.height / 2 }];
  await touch.start([from[0]]);
  await page.waitForTimeout(20);
  await touch.start(from);
  await touch.glide(from, [{ id: 1, x: from[0].x - 50, y: from[0].y }, { id: 2, x: from[1].x + 50, y: from[1].y }], 10);
  await touch.end();
  // The picture autosaves 700 ms after an edit (FLUSH_DELAY_MS), so wait longer than that before reading anything.
  await page.waitForTimeout(1200);
  expect(writes, 'no save request was sent after the pinch (an added layer would have been saved)').toEqual([]);
  expect((await getPicture(api, seed.projectId, pic.id)).layers, 'the pinch added no layer').toHaveLength(pic.layers.length);
});

test('AC 14 hiding and showing the page mid-edit loses no edit', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await bladeAt(ed, 60);
  // The save must be prompted by the hide itself, not by the normal autosave debounce.
  const saveSeen = ed.page.waitForRequest((r) => r.method() === 'PATCH' && r.url().includes('/edit-timelines/'), { timeout: 5_000 }).then((r) => r.timing().startTime);
  const hiddenAt = await ed.page.evaluate(() => {
    const t = Date.now();
    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    window.dispatchEvent(new Event('pagehide'));
    return t;
  });
  const sentAt = await saveSeen;
  expect(sentAt - hiddenAt, 'the save request left within 250 ms of the page being hidden').toBeLessThan(250);
  await ed.expectServer((tl) => hasCutAt(tl, 60), 'the edit is stored after the page was hidden');
  await ed.reload();
  expect(hasCutAt(await ed.serverTimeline(), 60), 'the edit is still there after a reload').toBe(true);
});

/** A clip boundary at frame 30 on the first track (used by the AC 4 split check). */
function hasCutAt30(tl: Timeline): boolean {
  return tl.document.tracks[0].items.some((i) => i.recordIn === 30);
}
