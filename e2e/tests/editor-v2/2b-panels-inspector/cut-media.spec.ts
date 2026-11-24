import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import { SEEDED_LAYOUT, type EditorPage } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { allSentOps } from '../../../fixtures/editor-v2/lane-b';

/**
 * Lane B: the Media workspace's timeline-specific controls and the Cut workspace
 * (inventory timeline.md 2.K, TL-320..323, TL-330..334). The pool itself is the hub inventory's.
 *
 * S9: Cut > Insert and Media > Insert at playhead are the same job - both must bring the paired
 * "Camera audio" clip, so a shot inserted from the Cut tape is not silent.
 */

const V = (tl: Timeline) => tl.document.tracks.filter((t) => t.kind === 'video');
const A = (tl: Timeline) => tl.document.tracks.filter((t) => t.kind === 'audio');
const camera = (tl: Timeline) => tl.document.tracks.find((t) => t.kind === 'audio' && /^Camera audio/.test(t.name));

/** Opens the Media workspace and selects a source tile (phones: through the pane switch). */
async function pickSource(ed: EditorPage, fileId: string): Promise<void> {
  await ed.workspace('media');
  const tile = ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="file:${fileId}"]`).first();
  if (ed.isPhone && !(await tile.isVisible().catch(() => false))) {
    const pane = ed.page.locator('label', { hasText: /^Media$/ }).first();
    if (await pane.isVisible().catch(() => false)) await pane.tap();
  }
  await ed.tap(tile);
}

test('TL-320 TL-322 Insert at playhead puts the picture and its sound at the edit point, and ONE undo removes both', async ({ freshTimeline, openEditor, seed }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(160); // nearest edit point is 150 (the clipA|clipB cut)
  const before = await ed.serverTimeline();
  await pickSource(ed, seed.files.clipB);
  await expect(ed.testId('media-insert-length')).toContainText(/with its sound/i);
  await ed.tap(ed.testId('media-insert-at-playhead'));
  const placed = await ed.expectServer((tl) => V(tl)[0].items.length === V(before)[0].items.length + 1 && !!camera(tl), 'the picture clip and a Camera audio track are stored');
  const inserted = V(placed)[0].items.find((i) => !V(before)[0].items.some((b) => b.id === i.id))!;
  expect(inserted.recordIn, 'inserted at the nearest edit point').toBe(150);
  expect(inserted.assetRef?.projectFileId).toBe(seed.files.clipB);
  const sound = camera(placed)!.items[0];
  expect([sound.recordIn, sound.recordOut], 'the sound has the picture\'s span').toEqual([inserted.recordIn, inserted.recordOut]);
  expect(sound.assetRef?.projectFileId).toBe(seed.files.clipB);
  expect(V(placed)[0].items.find((i) => i.name === 'clipB.mp4' && i.id !== inserted.id)!.recordIn, 'what followed rippled right by the inserted length').toBeGreaterThanOrEqual(inserted.recordOut);

  await ed.workspace('edit');
  await ed.openPanelTab(/history/i);
  await expect(ed.testId('history-entry-1')).toContainText(/with its sound/i);
  await expect(ed.testId('history-entry-2')).toHaveCount(0);
  await ed.tap(ed.testId('history-undo'));
  await ed.expectServer((tl) => V(tl)[0].items.length === V(before)[0].items.length && (camera(tl)?.items.length ?? 0) === 0, 'one undo removed the picture AND the sound');
});

test('TL-321 Add to end appends the source at the end of the track with its sound', async ({ freshTimeline, openEditor, seed }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await pickSource(ed, seed.files.clipB);
  await ed.tap(ed.testId('media-append'));
  const tl = await ed.expectServer((t) => V(t)[0].items.length === 5, 'the clip is appended');
  const last = V(tl)[0].items[4];
  expect(last.recordIn, 'starts where the last clip ended').toBe(SEEDED_LAYOUT.still[1]);
  expect(last.assetRef?.projectFileId).toBe(seed.files.clipB);
  expect(camera(tl)?.items[0].recordIn, 'its sound starts at the same frame').toBe(last.recordIn);
});

test('TL-323 Create subclip adds the marked range to the end of the timeline', async ({ freshTimeline, openEditor, seed }) => {
  // The source viewer's marks need the <video> to seek; Playwright's Linux WebKit never advances a video (the same
  // limit as P3-VIEW-01@iphone14webkit in expected-failures.ts), so the marks cannot be taken there.
  test.skip(test.info().project.name === 'iphone14webkit', 'Playwright WebKit cannot seek a <video> (audit D8)');
  const ed = await openEditor(await freshTimeline('seeded'));
  await pickSource(ed, seed.files.clipA);
  const video = ed.testId('source-viewer-video');
  await expect(video).toBeVisible();
  const seekSource = (sec: number) =>
    ed.page.evaluate((s) => {
      const v = document.querySelector('[data-testid="source-viewer-video"]') as HTMLVideoElement;
      v.currentTime = s;
    }, sec);
  await seekSource(3);
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { timeout: 10_000 }).toBeGreaterThan(2.9);
  await ed.tap(ed.testId('mark-controls').getByTestId('mark-in')); // the timeline transport has its own mark-in
  await seekSource(5);
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { timeout: 10_000 }).toBeGreaterThan(4.9);
  await ed.tap(ed.testId('mark-controls').getByTestId('mark-out'));
  await expect(ed.testId('mark-span-value')).toContainText('60');
  await expect(ed.testId('media-insert-length')).toContainText(/marked range/i);
  await ed.tap(ed.testId('create-subclip'));
  const tl = await ed.expectServer((t) => V(t)[0].items.length === 5, 'the subclip is on the timeline');
  const sub = V(tl)[0].items[4];
  expect(sub.recordIn, 'at the end of the track').toBe(SEEDED_LAYOUT.still[1]);
  expect(sub.recordOut - sub.recordIn, 'two seconds at 30 fps').toBe(60);
  expect(sub.sourceIn, 'starts at the In mark (3 s)').toBe(90);
  expect(sub.name, 'named after the source').toMatch(/clipA/);
});

test('S9 TL-330 TL-332 Cut > Insert brings the sound too (same as Media > Insert), in one undo step', async ({ freshTimeline, openEditor, seed }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(160);
  const before = await ed.serverTimeline();
  await ed.workspace('cut');
  await expect(ed.testId('cut-track'), 'the track the insert acts on').toBeVisible();
  await ed.tap(ed.testId(`cut-insert-file:${seed.files.clipB}`));
  const placed = await ed.expectServer((tl) => V(tl)[0].items.length === V(before)[0].items.length + 1 && !!camera(tl), 'picture and Camera audio are stored');
  const inserted = V(placed)[0].items.find((i) => !V(before)[0].items.some((b) => b.id === i.id))!;
  expect(inserted.recordIn).toBe(150);
  const sound = camera(placed)!.items[0];
  expect([sound.recordIn, sound.recordOut, sound.assetRef?.projectFileId]).toEqual([inserted.recordIn, inserted.recordOut, seed.files.clipB]);
  await expect(ed.testId('cut-message')).toContainText(/with its sound/i);
  await expect.poll(() => allSentOps(ed), { message: 'an insert was sent' }).toEqual(expect.arrayContaining(['insert']));
  if (!ed.isPhone) {
    await ed.page.keyboard.press('Control+z');
    await ed.expectServer((tl) => V(tl)[0].items.length === V(before)[0].items.length && (camera(tl)?.items.length ?? 0) === 0, 'one undo removed both');
  }
  expect(A(placed).length).toBeGreaterThan(A(before).length - 1);
});

test('TL-331 the Cut overview strip: a click moves the playhead, a drag edits nothing', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.workspace('cut');
  const strip = ed.testId('cut-overview');
  await expect(strip).toBeVisible();
  // T-003: the overview is the page's own band above the timeline, not the top of the left column.
  await expect(ed.testId('panel-overview').getByTestId('cut-overview')).toBeVisible();
  await expect(ed.testId('panel-timeline')).toBeVisible();
  const box = (await strip.boundingBox())!;
  await ed.seek(0);
  await ed.tapAt(box.x + box.width * 0.6, box.y + 30);
  await expect(ed.testId('status-timecode'), 'the playhead moved into the timeline').not.toHaveText('00:00:00:00');
  const before = ed.patches.length;
  if (!ed.isPhone) {
    await ed.mouseDrag(box.x + box.width * 0.1, box.y + 30, box.x + box.width * 0.5, box.y + 30);
    await ed.page.waitForTimeout(1500);
    expect(ed.patches.length, 'dragging in the overview does not edit').toBe(before);
  }
});

test('TL-333 TL-334 Fast review plays each clip at a speed that follows its length', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  // One short (2 s) and one long (5 s, the whole source) clip on V1, so the review speeds differ.
  await api.putDocument(seed.projectId, id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    v.items = [
      { ...v.items[0], recordIn: 0, recordOut: 60 },
      { ...v.items[1], recordIn: 60, recordOut: 210 },
    ];
  });
  const ed = await openEditor(id);
  await ed.workspace('cut');
  const speed = ed.testId('shuttle-speed');
  await ed.seek(0);
  await ed.tap(ed.testId('cut-review-start'));
  const shortSpeed = (await speed.textContent())?.trim();
  await expect(ed.testId('cut-review-toggle'), 'Review turns the speed-follow switch on').toBeChecked();
  await expect.poll(async () => (await ed.testId('status-timecode').textContent()) !== '00:00:00:00', { message: 'the playhead advances', timeout: 20_000 }).toBe(true);
  await ed.tap(ed.testId('transport-play')); // pause
  await ed.seek(70); // inside the long clip
  await ed.tap(ed.testId('cut-review-start'));
  await expect.poll(async () => (await speed.textContent())?.trim(), { message: 'the long clip plays at a different (faster) speed', timeout: 20_000 }).not.toBe(shortSpeed);
  await ed.tap(ed.testId('transport-play')); // stop
});
