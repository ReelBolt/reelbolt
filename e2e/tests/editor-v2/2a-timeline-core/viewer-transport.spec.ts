import { execFileSync } from 'child_process';
import * as fs from 'fs';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { meanLuma, sampleElement } from '../../../fixtures/editor-v2/pixels';

/**
 * Lane A, section 2.D: the transport under the program monitor and the viewer options. Playback
 * INTERNALS (frame pacing, dropped frames, decode) belong to the perf lane; this file checks that each
 * control does its job: the timecode moves, marks are stored, a still is a real picture, the
 * resolution toggle really resizes the monitor's canvas.
 */

const THREE: ClipSpec[] = [
  { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
  { name: 'clipB.mp4', file: 'clipB', recordIn: 60, recordOut: 120 },
  { name: 'clipC.mp4', file: 'clipA', recordIn: 120, recordOut: 180 },
];

const frameOf = (tc: string | null): number => {
  const [h, m, s, f] = (tc ?? '0:0:0:0').split(':').map(Number);
  return ((h * 60 + m) * 60 + s) * 30 + f;
};
const playhead = async (ed: EditorPage) => frameOf(await ed.testId('status-timecode').textContent());
const transport = (ed: EditorPage, name: string) => ed.testId('transport-bar').getByRole('button', { name, exact: true });

test('TL-058 TL-059 TL-061 TL-062 TL-063 TL-057 step buttons, edit buttons, Go to start and end', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.seek(90);
  await ed.tap(transport(ed, 'Step back one frame'));
  expect(await playhead(ed)).toBe(89);
  await ed.tap(transport(ed, 'Step forward one frame'));
  await ed.tap(transport(ed, 'Step forward one frame'));
  expect(await playhead(ed)).toBe(91);
  await ed.tap(transport(ed, 'Next edit'));
  expect(await playhead(ed), 'the next clip boundary').toBe(120);
  await ed.tap(transport(ed, 'Previous edit'));
  expect(await playhead(ed), 'the one before').toBe(60);
  await ed.tap(transport(ed, 'Go to end'));
  expect(await playhead(ed)).toBe(179);
  await ed.tap(transport(ed, 'Go to start'));
  expect(await playhead(ed)).toBe(0);
  await ed.tap(transport(ed, 'Step back one frame'));
  expect(await playhead(ed), 'stepping back at the start stays at 0').toBe(0);
  expect(ed.patches, 'transport writes nothing').toEqual([]);
});

test('TL-064 the timecode field takes a typed timecode and refuses nonsense', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  const field = ed.testId('timecode-field');
  await field.click();
  await field.fill('0315');
  await field.press('Enter');
  await expect(ed.testId('status-timecode')).toHaveText('00:00:03:15');
  await field.click();
  await field.fill('abc');
  await field.press('Enter');
  await expect(ed.page.getByText('Not a timecode')).toBeVisible();
  await expect(ed.testId('status-timecode'), 'the playhead did not jump').toHaveText('00:00:03:15');
  await field.press('Escape');
});

test('TL-055 TL-056 the Mark in and Mark out buttons store the range and show it lit after reload', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.seek(30);
  await ed.tap(ed.testId('mark-in'));
  await ed.seek(90);
  await ed.tap(ed.testId('mark-out'));
  const range = (tl: { document: object }) => (tl.document as { inOut?: unknown }).inOut as { inFrame?: number; outFrame?: number } | null | undefined;
  await ed.expectServer((tl) => range(tl)?.inFrame === 30 && range(tl)?.outFrame === 91, 'the range 30..91 is stored');
  await ed.reload();
  await expect(ed.testId('mark-in')).toHaveAttribute('data-variant', 'light');
  await expect(ed.testId('mark-out')).toHaveAttribute('data-variant', 'light');
});

test('TL-060 TL-068 Play runs the timecode forward and Pause stops it; Play in to out stops at Out', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7'); // WebKit playback is the known P3-VIEW-01@iphone14webkit limit (audit D8)
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.tap(ed.testId('transport-play'));
  await expect(ed.testId('transport-play')).toHaveAttribute('aria-label', 'Pause');
  await expect.poll(() => playhead(ed), { message: 'the timecode advances while playing' }).toBeGreaterThan(10);
  await ed.tap(ed.testId('transport-play'));
  await expect(ed.testId('transport-play')).toHaveAttribute('aria-label', 'Play');
  const stopped = await playhead(ed);
  await expect.poll(() => playhead(ed), { message: 'and stays put once paused', intervals: [500, 500] }).toBe(stopped);

  await ed.seek(20);
  await ed.tap(ed.testId('mark-in'));
  await ed.seek(50);
  await ed.tap(ed.testId('mark-out'));
  await ed.seek(0);
  await expect(transport(ed, 'Play in to out')).toBeEnabled();
  await ed.tap(transport(ed, 'Play in to out'));
  await expect.poll(() => playhead(ed)).toBeGreaterThanOrEqual(20);
  await expect(ed.testId('transport-play'), 'it stops by itself at Out').toHaveAttribute('aria-label', 'Play', { timeout: 20_000 });
  expect(await playhead(ed)).toBeLessThanOrEqual(52);
});

test('TL-069 Loop wraps playback at the end of the In/Out range', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.seek(20);
  await ed.tap(ed.testId('mark-in'));
  await ed.seek(50);
  await ed.tap(ed.testId('mark-out'));
  // Start close to Out so the wrap comes within a few seconds, and so a run with Loop OFF (which stops at Out) cannot pass.
  await ed.seek(47);
  await ed.tap(transport(ed, 'Loop'));
  await expect(transport(ed, 'Loop')).toHaveAttribute('aria-pressed', 'true');
  // Record every timecode the page shows from inside the page: a Node-side poll samples at a fixed rate and, on a slow runner,
  // can beat against the one-second loop and never see the wrap.
  await ed.testId('status-timecode').evaluate((el) => {
    const w = window as unknown as { __tcSeen: string[] };
    w.__tcSeen = [];
    new MutationObserver(() => { w.__tcSeen.push(el.textContent ?? ''); }).observe(el, { childList: true, characterData: true, subtree: true });
  });
  await ed.tap(ed.testId('transport-play'));
  // With Loop on, playback keeps running past Out and the playhead never leaves the range.
  let seen: number[] = [];
  await expect.poll(async () => {
    seen = (await ed.page.evaluate(() => (window as unknown as { __tcSeen: string[] }).__tcSeen)).map((tc) => frameOf(tc));
    // The playhead reached the end of the range (Out is 50) and later came back near In (20): a real wrap, not a seek or a stop.
    const reachedOut = seen.findIndex((f) => f >= 49);
    return reachedOut >= 0 && seen.slice(reachedOut + 1).some((f) => f <= 30);
  }, { message: 'the playhead wrapped from Out back to In', timeout: 30_000, intervals: [250] }).toBe(true);
  expect(Math.max(...seen), 'never past Out').toBeLessThanOrEqual(52);
  await expect(ed.testId('transport-play'), 'still playing').toHaveAttribute('aria-label', 'Pause');
  await ed.tap(ed.testId('transport-play'));
});

test('TL-070 Save still downloads a real picture of the monitor, in live and in Single-clip mode', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.seek(20);
  await expect(ed.testId('session-kind')).toHaveText('Live preview', { timeout: 30_000 });
  // The monitor must have presented the frame before the still is taken (otherwise the still is rightly empty).
  await expect.poll(async () => meanLuma(await sampleElement(ed.page, ed.viewer)), { message: 'the monitor shows the frame', timeout: 30_000 }).toBeGreaterThan(15);
  const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId('save-still'))]);
  expect(download.suggestedFilename()).toMatch(/\.png$/);
  const file = await download.path();
  const head = fs.readFileSync(file).subarray(0, 8);
  expect([...head], 'a PNG').toEqual([0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]);
  const raw = execFileSync('ffmpeg', ['-v', 'error', '-i', file, '-vf', 'scale=1:1:flags=area', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-']);
  expect(raw[0] + raw[1] + raw[2], 'the picture is not black').toBeGreaterThan(30);

  // Single-clip mode shows a <video>, not the canvas: the still is taken from the frame on screen (never a blank PNG).
  await ed.tap(ed.page.getByRole('button', { name: 'Preview options' }));
  await ed.page.getByTestId('degraded-toggle').check({ force: true });
  await expect(ed.testId('session-kind')).toHaveText(/Simple preview/, { timeout: 30_000 });
  await ed.page.keyboard.press('Escape');
  await expect.poll(
    () => ed.testId('program-video').evaluate((v: HTMLVideoElement) => v.readyState >= 2 && v.videoWidth > 0),
    { message: 'the single-clip video shows a frame', timeout: 30_000 },
  ).toBe(true);
  const [second] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId('save-still'))]);
  const raw2 = execFileSync('ffmpeg', ['-v', 'error', '-i', (await second.path())!, '-vf', 'scale=1:1:flags=area', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-']);
  expect(raw2[0] + raw2[1] + raw2[2], 'the single-clip still is a real picture too').toBeGreaterThan(30);
});

test('TL-071 the before/after grade switch toggles and writes nothing to the document', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  const sw = ed.testId('grade-bypass');
  await expect(sw).toHaveAttribute('aria-pressed', 'false');
  await ed.tap(sw);
  await expect(sw).toHaveAttribute('aria-pressed', 'true');
  await ed.tap(sw);
  await expect(sw).toHaveAttribute('aria-pressed', 'false');
  expect(ed.patches, 'view state only').toEqual([]);
});

test('TL-072 TL-073 TL-074 TL-054 Preview options: playback resolution resizes the monitor canvas, Single-clip mode switches the session', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await expect(ed.viewer).toHaveAttribute('width', '960', { timeout: 30_000 }); // half of 1920 is the default
  await ed.tap(ed.page.getByRole('button', { name: 'Preview options' }));
  await expect(ed.page.getByTestId('preview-toggles')).toBeVisible();
  await ed.page.getByTestId('preview-toggles').getByText('Full', { exact: true }).click();
  await expect(ed.viewer, 'Full decodes at the timeline size').toHaveAttribute('width', '1920', { timeout: 30_000 });
  await ed.page.getByTestId('preview-toggles').getByText('Quarter', { exact: true }).click();
  await expect(ed.viewer).toHaveAttribute('width', '480', { timeout: 30_000 });
  await ed.page.getByTestId('degraded-toggle').check({ force: true });
  await expect(ed.testId('session-kind')).toHaveText(/Simple preview/, { timeout: 30_000 });
  await expect(ed.testId('program-video')).toBeVisible();
  await expect(ed.testId('preview-degraded-note')).toContainText(/Simple preview/);
  await ed.page.getByTestId('degraded-toggle').uncheck({ force: true });
  await expect(ed.testId('session-kind')).toHaveText('Live preview', { timeout: 30_000 });
  expect(ed.patches, 'preview options are view state').toEqual([]);
});
