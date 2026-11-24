import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import type { Timeline } from '../../../fixtures/editor-v2/api';

/**
 * T-017: the one viewer's Source | Record switch on Cut and Edit. A source opened from Cut's tape is
 * watched, marked and placed from the viewer; Space in Source changes nothing in the document.
 */

const V = (tl: Timeline) => tl.document.tracks.filter((t) => t.kind === 'video');

test('open a source on Cut, mark in and out, Insert; Record shows the placed clip', async ({ freshTimeline, openEditor, seed }) => {
  test.skip(test.info().project.name === 'iphone14webkit', 'The phone has no Source monitor, and WebKit cannot seek a <video>');
  const ed = await openEditor(await freshTimeline('seeded'));
  if (ed.isPhone) test.skip(true, 'The phone has no Source monitor');
  await ed.workspace('cut');
  await expect(ed.testId('viewer-mode-switch'), 'the switch is in the viewer header on Cut').toBeVisible();
  const before = await ed.serverTimeline();

  await ed.tap(ed.testId(`cut-watch-file:${seed.files.clipA}`));
  await expect(ed.testId('viewer-mode-switch')).toHaveAttribute('data-mode', 'source');
  const video = ed.testId('source-monitor-video');
  await expect(video).toBeVisible();

  const seekSource = (sec: number) =>
    ed.page.evaluate((s) => {
      (document.querySelector('[data-testid="source-monitor-video"]') as HTMLVideoElement).currentTime = s;
    }, sec);
  await seekSource(3);
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { timeout: 10_000 }).toBeGreaterThan(2.9);
  await ed.tap(ed.testId('source-monitor-mark-in'));
  await seekSource(5);
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { timeout: 10_000 }).toBeGreaterThan(4.9);
  await ed.tap(ed.testId('source-monitor-mark-out'));
  await expect(ed.testId('source-monitor-marks')).toContainText('60');

  await ed.tap(ed.testId('source-monitor-place-menu'));
  await ed.tap(ed.testId('source-monitor-place-insert'));
  const placed = await ed.expectServer((tl) => V(tl)[0].items.length === V(before)[0].items.length + 1, 'the marked range is stored');
  const clip = V(placed)[0].items.find((i) => !V(before)[0].items.some((b) => b.id === i.id))!;
  expect(clip.recordOut - clip.recordIn, 'two seconds at 30 fps').toBe(60);
  expect(clip.sourceIn, 'starts at the In mark (3 s)').toBe(90);
  await expect(ed.testId('viewer-mode-switch'), 'placing leaves the viewer on Source').toHaveAttribute('data-mode', 'source');

  await ed.tap(ed.testId('viewer-mode-record'));
  await expect(ed.testId('viewer-mode-switch')).toHaveAttribute('data-mode', 'record');
  await expect(ed.testId('source-monitor')).toHaveCount(0);
  await expect(ed.testId('program-viewer')).toBeVisible();
});

test('Space in Source does not change the document revision', async ({ freshTimeline, openEditor, seed }) => {
  test.skip(test.info().project.name === 'iphone14webkit', 'The phone has no Source monitor, and WebKit cannot seek a <video>');
  const ed = await openEditor(await freshTimeline('seeded'));
  if (ed.isPhone) test.skip(true, 'The phone has no Source monitor');
  await ed.workspace('cut');
  await ed.tap(ed.testId(`cut-watch-file:${seed.files.clipA}`));
  await expect(ed.testId('source-monitor-video')).toBeVisible();
  const before = await ed.serverTimeline();
  const sourceTime = () => ed.testId('source-monitor-video').evaluate((v: HTMLVideoElement) => v.currentTime);
  expect(await sourceTime()).toBeLessThan(0.1);
  // The click on Watch left focus on that button: Space must still reach the source.
  await ed.page.keyboard.press('Space');
  await expect.poll(sourceTime, { message: 'Space played the source', timeout: 10_000 }).toBeGreaterThan(0.2);
  const playhead = await ed.testId('status-timecode').textContent();
  await ed.page.keyboard.press('KeyK');
  await ed.page.keyboard.press('KeyI');
  // Timeline writers do nothing while Source shows.
  await ed.page.keyboard.press('Delete');
  await ed.page.keyboard.press('Control+KeyD');
  await ed.page.keyboard.press('Space');
  await ed.page.keyboard.press('KeyK');
  await expect(ed.testId('status-timecode'), 'the timeline playhead did not move').toHaveText(playhead ?? '');
  const after = await ed.serverTimeline();
  expect(after.revision, 'no op was written').toBe(before.revision);
  await ed.page.keyboard.press('Shift+KeyV'); // the live chord returns to Record
  await expect(ed.testId('viewer-mode-switch')).toHaveAttribute('data-mode', 'record');
});

test('the picture box is the same box in Source and Record; Cut review returns to Record first', async ({ freshTimeline, openEditor, seed }) => {
  test.skip(test.info().project.name === 'iphone14webkit', 'The phone has no Source monitor, and WebKit cannot seek a <video>');
  const ed = await openEditor(await freshTimeline('seeded'));
  if (ed.isPhone) test.skip(true, 'The phone has no Source monitor');
  await ed.workspace('cut');
  const box = async (id: string) => ed.testId(id).evaluate((node) => { const r = node.getBoundingClientRect(); return { x: r.x, y: r.y, w: r.width, h: r.height }; });
  const record = await box('program-frame');
  await ed.tap(ed.testId(`cut-watch-file:${seed.files.clipA}`));
  await expect(ed.testId('source-monitor-frame')).toBeVisible();
  const source = await box('source-monitor-frame');
  for (const key of ['x', 'y', 'w', 'h'] as const) expect(Math.abs(source[key] - record[key]), `picture box ${key}`).toBeLessThanOrEqual(1);
  const after = await box('program-frame');
  for (const key of ['x', 'y', 'w', 'h'] as const) expect(Math.abs(after[key] - record[key]), `program frame ${key}`).toBeLessThanOrEqual(1);
  // Review plays the timeline, so it switches to Record first.
  await ed.testId('cut-review-start').click();
  await expect(ed.testId('viewer-mode-switch')).toHaveAttribute('data-mode', 'record');
});
