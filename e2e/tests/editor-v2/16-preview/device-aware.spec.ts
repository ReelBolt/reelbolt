import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, PAIR } from '../../../fixtures/editor-v2/lane-a';
import { distance, meanLuma, sampleElement } from '../../../fixtures/editor-v2/pixels';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * R04.5 device-aware preview and VF12 the viewer skeleton (T-010).
 *
 * A low-budget device is emulated the way a real one reports itself: navigator.hardwareConcurrency /
 * deviceMemory overrides before the page runs (and the pixel7 profile, a phone by viewport). The editor then
 * starts on the LIVE preview at a size the device can carry, and playback advances.
 */

const frameOf = (tc: string | null): number => {
  const [h, m, s, f] = (tc ?? '0:0:0:0').split(':').map(Number);
  return ((h * 60 + m) * 60 + s) * 30 + f;
};
const playhead = async (ed: EditorPage) => frameOf(await ed.testId('status-timecode').textContent());

test('R04.5 a low-budget PHONE starts on the LIVE preview (every layer and effect on screen), and playback advances', async ({ freshTimeline, openEditor, api, seed, page }) => {
  runOn('pixel7', 'iphone14webkit');
  test.setTimeout(180_000);
  // Ask for Auto (the suite's fixture would otherwise pick Half) and report a weak device.
  await page.addInitScript(() => {
    try {
      localStorage.setItem('reelbolt:editor:preview-quality', 'auto');
    } catch { /* storage off */ }
    Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 2, configurable: true });
    Object.defineProperty(navigator, 'deviceMemory', { get: () => 2, configurable: true });
  });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));

  // Auto never picks the Simple preview: a phone gets the live compositor, drawn no larger than a phone needs.
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  await expect(ed.testId('simple-preview-label')).toHaveCount(0);
  const size = await ed.viewer.evaluate((c: HTMLCanvasElement) => Math.max(c.width, c.height));
  expect(size, 'the picture is sized for a phone').toBeLessThanOrEqual(640);
  expect(size).toBeGreaterThanOrEqual(256);

  // The picture is the composited timeline and it moves while playing.
  await expect.poll(async () => meanLuma(await sampleElement(ed.page, ed.viewer)), { message: 'the live preview shows a picture', timeout: 30_000, intervals: [1000] }).toBeGreaterThan(15);
  const start = await playhead(ed);
  const g0 = await sampleElement(ed.page, ed.viewer);
  await ed.tap(ed.testId('transport-play'));
  await expect.poll(() => playhead(ed), { message: 'the playhead moves', timeout: 30_000, intervals: [500] }).toBeGreaterThan(start + 10);
  await expect
    .poll(async () => distance(g0, await sampleElement(ed.page, ed.viewer)), { message: 'the picture changes while it plays', timeout: 30_000, intervals: [500] })
    .toBeGreaterThan(1);
  await ed.tap(ed.testId('transport-play'));
  // Paused, the canvas is back at the full playback size whatever the adaptive size was while playing.
  await expect(ed.page.getByTestId('program-frame')).toHaveAttribute('data-adaptive-scale', '1');
  expect(ed.patches, 'preview choices write nothing').toEqual([]);
});

test('R04.5 Preview quality reads Auto / Full / Half / Quarter, the pick is remembered and Full plays live', async ({ freshTimeline, openEditor, api, seed, page }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  await page.addInitScript(() => {
    try {
      if (!sessionStorage.getItem('rb-e2e-auto-once')) {
        sessionStorage.setItem('rb-e2e-auto-once', '1');
        localStorage.setItem('reelbolt:editor:preview-quality', 'auto');
      }
    } catch { /* storage off */ }
    Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 2, configurable: true });
  });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  // A weak desktop starts LIVE, drawn no larger than the viewer shows it and under the low-budget ceiling (854), not on the Simple preview.
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  await expect.poll(() => ed.viewer.evaluate((c: HTMLCanvasElement) => c.width), { message: 'the canvas is fitted to the viewer under the ceiling' }).toBeLessThanOrEqual(854);
  expect(await ed.viewer.evaluate((c: HTMLCanvasElement) => c.width)).toBeGreaterThanOrEqual(256);

  await ed.tap(ed.page.getByRole('button', { name: 'Preview options' }));
  const toggles = ed.page.getByTestId('preview-toggles');
  for (const word of ['Auto', 'Full', 'Half', 'Quarter']) await expect(toggles.getByText(word, { exact: true })).toBeVisible();
  await expect(ed.page.getByTestId('auto-preview-reason')).toContainText(/drawn at a smaller size because .*2 processor cores/);

  await toggles.getByText('Full', { exact: true }).click();
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  await expect(ed.viewer).toHaveAttribute('width', '1920');
  await ed.reload();
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  expect(await ed.page.evaluate(() => localStorage.getItem('reelbolt:editor:preview-quality')), 'the pick is remembered on this device').toBe('full');
});

test('VF12 the viewer frame has its final size at first paint, and never an unexplained "No preview"', async ({ freshTimeline, openEditor, api, seed, page }) => {
  test.setTimeout(120_000);
  await page.addInitScript(() => {
    const w = window as unknown as { __frames: Array<{ w: number; h: number }>; __noPreviewSeenEarly: boolean };
    w.__frames = [];
    w.__noPreviewSeenEarly = false;
    const watched = new WeakSet<Element>();
    const resize = new ResizeObserver((entries) => {
      for (const e of entries) w.__frames.push({ w: Math.round(e.contentRect.width * 10) / 10, h: Math.round(e.contentRect.height * 10) / 10 });
    });
    new MutationObserver(() => {
      const el = document.querySelector('[data-testid="program-frame"]');
      if (el && !watched.has(el)) {
        watched.add(el);
        resize.observe(el);
      }
      // "No preview" is only legitimate once a session exists (data-booted); never while the frame boots.
      if (el && el.getAttribute('data-booted') === 'false' && document.querySelector('[data-testid="preview-unavailable"]')) w.__noPreviewSeenEarly = true;
    }).observe(document, { childList: true, subtree: true, attributes: true });
  });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const frame = ed.page.getByTestId('program-frame');
  await expect(frame).toHaveAttribute('data-booted', 'true', { timeout: 30_000 });
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', /live|fallback/, { timeout: 30_000 });
  await ed.page.waitForTimeout(500); // let any late layout change show up in the observer log

  const { frames, early } = await ed.page.evaluate(() => {
    const w = window as unknown as { __frames: Array<{ w: number; h: number }>; __noPreviewSeenEarly: boolean };
    return { frames: w.__frames, early: w.__noPreviewSeenEarly };
  });
  expect(frames.length, 'the frame was measured').toBeGreaterThan(0);
  const last = frames[frames.length - 1];
  for (const f of frames) {
    expect(Math.abs(f.w - last.w), `width at first paint ${f.w} vs final ${last.w}`).toBeLessThanOrEqual(1);
    expect(Math.abs(f.h - last.h), `height at first paint ${f.h} vs final ${last.h}`).toBeLessThanOrEqual(1);
  }
  expect(last.w / last.h, 'the program aspect (16:9)').toBeCloseTo(16 / 9, 1);
  expect(early, 'no "No preview" alert while the viewer boots').toBe(false);
});
