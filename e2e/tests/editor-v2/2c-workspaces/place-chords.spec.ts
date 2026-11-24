import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import type { Timeline } from '../../../fixtures/editor-v2/api';

/**
 * T-018: the four placement chords act on the source loaded in the viewer, through the same path as the
 * Place buttons (its marks, the timeline playhead). F9 inserts, F10 overwrites, F8 adds to the end, Shift+F8
 * places on top. With no source loaded the keys change nothing. The right-click menu on the ruler lists the
 * same placements (with the live chord) once a source is loaded.
 */

const V = (tl: Timeline) => tl.document.tracks.filter((t) => t.kind === 'video');

test('F9 inserts the loaded source at the playhead; with no source it does nothing', async ({ freshTimeline, openEditor, seed }) => {
  test.skip(test.info().project.name === 'iphone14webkit', 'The phone has no Source monitor, and WebKit cannot seek a <video>');
  const ed = await openEditor(await freshTimeline('seeded'));
  if (ed.isPhone) test.skip(true, 'The phone has no Source monitor');
  await ed.workspace('cut');
  const before = await ed.serverTimeline();

  await ed.page.keyboard.press('F9');
  await expect(ed.testId('source-monitor-video'), 'no source: nothing opens').toHaveCount(0);
  expect(V(await ed.serverTimeline())[0].items.length, 'no source: nothing is placed').toBe(V(before)[0].items.length);

  await ed.tap(ed.testId(`cut-watch-file:${seed.files.clipA}`));
  await expect(ed.testId('source-monitor-video')).toBeVisible();
  await ed.page.keyboard.press('F9');
  const placed = await ed.expectServer((tl) => V(tl)[0].items.length === V(before)[0].items.length + 1, 'F9 stored the insert');
  const clip = V(placed)[0].items.find((i) => !V(before)[0].items.some((b) => b.id === i.id))!;
  expect(clip.assetRef?.projectFileId).toBe(seed.files.clipA);
});

test('F8 adds the loaded source to the end of the track', async ({ freshTimeline, openEditor, seed }) => {
  test.skip(test.info().project.name === 'iphone14webkit', 'The phone has no Source monitor, and WebKit cannot seek a <video>');
  const ed = await openEditor(await freshTimeline('seeded'));
  if (ed.isPhone) test.skip(true, 'The phone has no Source monitor');
  await ed.workspace('cut');
  const before = await ed.serverTimeline();
  await ed.tap(ed.testId(`cut-watch-file:${seed.files.clipB}`));
  await expect(ed.testId('source-monitor-video')).toBeVisible();
  await ed.page.keyboard.press('F8');
  const placed = await ed.expectServer((tl) => V(tl)[0].items.length === V(before)[0].items.length + 1, 'F8 stored the append');
  const last = V(placed)[0].items[V(placed)[0].items.length - 1];
  expect(last.assetRef?.projectFileId).toBe(seed.files.clipB);
});

test('holding F8 places once: key auto-repeat does not place again', async ({ freshTimeline, openEditor, seed }) => {
  test.skip(test.info().project.name === 'iphone14webkit', 'The phone has no Source monitor, and WebKit cannot seek a <video>');
  const ed = await openEditor(await freshTimeline('seeded'));
  if (ed.isPhone) test.skip(true, 'The phone has no Source monitor');
  await ed.workspace('cut');
  const before = await ed.serverTimeline();
  await ed.tap(ed.testId(`cut-watch-file:${seed.files.clipB}`));
  await expect(ed.testId('source-monitor-video')).toBeVisible();
  await ed.page.keyboard.down('F8');
  await ed.page.keyboard.down('F8'); // a held key repeats with the same key down
  await ed.page.keyboard.down('F8');
  await ed.page.keyboard.up('F8');
  await ed.expectServer((tl) => V(tl)[0].items.length === V(before)[0].items.length + 1, 'F8 stored one append');
  await ed.page.waitForTimeout(1500);
  expect(V(await ed.serverTimeline())[0].items.length, 'held keys did not place again').toBe(V(before)[0].items.length + 1);
});

test('the ruler menu lists the four placements with their live chords once a source is loaded', async ({ freshTimeline, openEditor, seed }) => {
  test.skip(test.info().project.name === 'iphone14webkit', 'The phone has no Source monitor, and WebKit cannot seek a <video>');
  const ed = await openEditor(await freshTimeline('seeded'));
  if (ed.isPhone) test.skip(true, 'The phone has no Source monitor');
  await ed.workspace('edit');
  await ed.page.mouse.click(await ed.frameX(40), (await ed.testId('timeline-canvas').boundingBox())!.y + 8, { button: 'right' });
  await expect(ed.testId('clip-context-menu')).toBeVisible();
  await expect(ed.testId('ctx-point-add-marker')).toBeVisible();
  await expect(ed.testId('ctx-point-insert'), 'no source: no placement').toHaveCount(0);
  await ed.page.keyboard.press('Escape');

  await ed.workspace('cut');
  await ed.tap(ed.testId(`cut-watch-file:${seed.files.clipA}`));
  await expect(ed.testId('source-monitor-video')).toBeVisible();
  await ed.workspace('edit');
  await ed.page.mouse.click(await ed.frameX(40), (await ed.testId('timeline-canvas').boundingBox())!.y + 8, { button: 'right' });
  await expect(ed.testId('ctx-point-insert-chord')).toHaveText('F9');
  await expect(ed.testId('ctx-point-overwrite-chord')).toHaveText('F10');
  await expect(ed.testId('ctx-point-append-chord')).toHaveText('F8');
  await expect(ed.testId('ctx-point-placeOnTop-chord')).toHaveText('Shift+F8');
});
