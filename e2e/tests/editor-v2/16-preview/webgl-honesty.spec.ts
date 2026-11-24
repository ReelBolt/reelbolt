import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, PAIR } from '../../../fixtures/editor-v2/lane-a';
import { distance, sampleElement } from '../../../fixtures/editor-v2/pixels';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Editor preview fidelity (issue #769): WebGL failure is named and recoverable, and the Simple preview is graded.
 *
 * Desktop project only (the phone profiles start on the Simple preview by design). Per-test launch args are not
 * available in this config, so the environments are EMULATED by init scripts:
 *   - software graphics: WEBGL_debug_renderer_info reports SwiftShader (the stack's headless Chromium already is);
 *   - no WebGL2: getContext('webgl2') returns null.
 */

const GRADED = [
  { name: 'clipA.mp4', file: 'clipA' as const, recordIn: 0, recordOut: 60, extra: { grade: { nodes: [{ id: '5b0c1f6e-7a3d-4e21-9c58-2f1d3a6b7e90', primaries: { exposure: 0.5, saturation: 1.5, temperature: 0.5 } }] } } },
  { name: 'clipB.mp4', file: 'clipB' as const, recordIn: 60, recordOut: 120 },
];

const autoQuality = () => {
  try {
    localStorage.setItem('reelbolt:editor:preview-quality', 'auto');
  } catch { /* storage off */ }
};

test('@preview-gl Auto with software graphics starts LIVE at a smaller size and the grade shows', async ({ freshTimeline, openEditor, api, seed, page }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  await page.addInitScript(autoQuality);
  await page.addInitScript(() => {
    const real = WebGL2RenderingContext.prototype.getParameter;
    WebGL2RenderingContext.prototype.getParameter = function (p: number) {
      return p === 0x9246 ? 'ANGLE (Google, Vulkan 1.3.0 (SwiftShader Device), SwiftShader driver)' : real.call(this, p);
    };
    Object.defineProperty(navigator, 'hardwareConcurrency', { get: () => 4, configurable: true });
  });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, GRADED));
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  await expect.poll(() => ed.viewer.evaluate((c: HTMLCanvasElement) => c.width), { message: 'software graphics: at most 480 on the long side' }).toBeLessThanOrEqual(480);
  await expect(ed.testId('preview-reduced-note')).toContainText(/drawn at a smaller size because/);

  const on = await sampleElement(ed.page, ed.viewer);
  await ed.tap(ed.testId('grade-bypass'));
  await expect.poll(async () => distance(on, await sampleElement(ed.page, ed.viewer)), { message: 'Grade off changes the live picture', timeout: 15_000 }).toBeGreaterThan(1);
});

test('@preview-gl the Simple preview shows the grade (pixels differ with Grade on vs off) and offers Use live preview', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, GRADED));
  await ed.tap(ed.page.getByRole('button', { name: 'Preview options' }));
  await ed.page.getByTestId('degraded-toggle').check({ force: true });
  await ed.page.keyboard.press('Escape');
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'fallback', { timeout: 30_000 });
  const video = ed.testId('program-video');
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState >= 2 && v.videoWidth > 0), { timeout: 30_000 }).toBe(true);
  await expect(video).toHaveCSS('filter', /url\(.*rf-grade-/);

  const on = await sampleElement(ed.page, video);
  await ed.tap(ed.testId('grade-bypass'));
  await expect(video).not.toHaveCSS('filter', /url\(/);
  const off = await sampleElement(ed.page, video);
  expect(distance(on, off), 'the graded Simple preview differs from the ungraded one').toBeGreaterThan(1);
  await ed.tap(ed.testId('grade-bypass'));

  await ed.tap(ed.testId('preview-use-live'));
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  expect(ed.patches, 'preview choices write nothing').toEqual([]);
});

test('@preview-gl no WebGL2: the honest reason, the hint and Try live preview again', async ({ freshTimeline, openEditor, api, seed, page }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  await page.addInitScript(autoQuality);
  await page.addInitScript(() => {
    const real = HTMLCanvasElement.prototype.getContext;
    const patched = function (this: HTMLCanvasElement, type: string, ...rest: unknown[]) {
      return type === 'webgl2' ? null : (real as (this: HTMLCanvasElement, ...a: unknown[]) => unknown).call(this, type, ...rest);
    };
    HTMLCanvasElement.prototype.getContext = patched as unknown as typeof real;
  });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'fallback', { timeout: 30_000 });
  const note = ed.testId('preview-degraded-note');
  await expect(note).toContainText(/graphics acceleration/);
  await expect(note).not.toContainText(/compositor could not be initialised/i);
  await expect(ed.testId('preview-live-retry')).toBeVisible();
  await expect(ed.testId('preview-use-live')).toHaveCount(0);
});

test('@preview-gl a lost graphics context is shown, and clears when the browser restores it', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live', { timeout: 30_000 });
  await ed.viewer.evaluate((c: HTMLCanvasElement) => {
    const ext = c.getContext('webgl2')!.getExtension('WEBGL_lose_context')!;
    (window as unknown as { __lose: typeof ext }).__lose = ext;
    ext.loseContext();
  });
  await expect(ed.testId('preview-context-lost')).toContainText(/graphics context was lost/i);
  await expect(ed.testId('parity-badge')).not.toHaveAttribute('data-parity', 'faithful');
  await ed.page.evaluate(() => (window as unknown as { __lose: { restoreContext(): void } }).__lose.restoreContext());
  await expect(ed.testId('preview-context-lost')).toHaveCount(0, { timeout: 15_000 });
  await expect(ed.page.getByTestId('program-viewer')).toHaveAttribute('data-session-kind', 'live');
});
