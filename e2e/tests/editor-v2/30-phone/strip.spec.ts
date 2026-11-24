import type { CDPSession, Page } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { bladeAt, otherWriterRenames } from '../02-save-sync/sync-helpers';

/**
 * Wave 3 / T-003: the phone clip strip (RM.4, AC 3, AC 12). Real CDP touch on pixel7; iphone14webkit keeps only
 * the assertions that need no drag. The playhead is UI state, so panning is asserted through the strip's own
 * timecode readout; the trim is asserted in the stored document.
 */

const WEBKIT_SKIP = 'WebKit cannot synthesize touch drags';
type Finger = { id: number; x: number; y: number };

class TouchScript {
  private constructor(readonly page: Page, private readonly cdp: CDPSession) {}
  static async on(page: Page): Promise<TouchScript> {
    return new TouchScript(page, await page.context().newCDPSession(page));
  }
  start(fingers: Finger[]) { return this.cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: fingers }); }
  move(fingers: Finger[]) { return this.cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: fingers }); }
  end() { return this.cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); }
  async glide(from: Finger[], to: Finger[], steps = 12): Promise<void> {
    for (let i = 1; i <= steps; i++) {
      await this.move(from.map((f, k) => ({ id: f.id, x: f.x + ((to[k].x - f.x) * i) / steps, y: f.y + ((to[k].y - f.y) * i) / steps })));
      await this.page.waitForTimeout(16);
    }
  }
}

async function countPointerCancels(page: Page): Promise<void> {
  await page.evaluate(() => {
    const w = window as unknown as { __pointerCancels?: number };
    w.__pointerCancels = 0;
    window.addEventListener('pointercancel', () => { w.__pointerCancels = (w.__pointerCancels ?? 0) + 1; }, true);
  });
}
const pointerCancels = (page: Page): Promise<number> => page.evaluate(() => (window as unknown as { __pointerCancels?: number }).__pointerCancels ?? -1);
const ppf = (ed: EditorPage): Promise<number> => ed.canvas.evaluate((el) => Number(el.getAttribute('data-ppf')));
const timecode = (ed: EditorPage): Promise<string> => ed.testId('status-timecode').innerText();

test.describe('RM.4 phone clip strip', () => {
  test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

  test('RM.4 the strip is a labelled region listing the selected clip, and a tap selects without moving the playhead', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await expect(ed.page.getByRole('region', { name: 'Clip strip' })).toBeVisible();
    await expect(ed.testId('strip-description')).toHaveText(/No clip selected, 2 clips/);
    const before = await timecode(ed);
    await ed.selectClipAt(30);
    await expect(ed.testId('strip-description')).toHaveText(/^Clip 1 of 2, clipA\.mp4, 0:00 to 0:02/);
    await expect(ed.page.getByTestId('trim-handle-out')).toHaveCount(1);
    expect(await timecode(ed), 'a tap selects and does not seek').toBe(before);
  });

  test('RM.4 a tap on empty strip deselects', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await expect(ed.page.getByTestId('trim-handle-out')).toHaveCount(1);
    // Before the first frame the strip is empty (the playhead sits at the centre, so the left half shows negative time).
    await ed.tapAt(await ed.frameX(-20), await ed.laneY(0));
    await expect(ed.page.getByTestId('trim-handle-out')).toHaveCount(0);
    await expect(ed.testId('strip-description')).toHaveText(/No clip selected/);
  });

  test('RM.4 previous and next buttons move between clips with no drag', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await ed.tap(ed.testId('strip-next'));
    await expect(ed.testId('strip-description')).toHaveText(/^Clip 2 of 2, clipB\.mp4/);
    await expect(ed.testId('strip-next')).toBeDisabled();
    await ed.tap(ed.testId('strip-prev'));
    await expect(ed.testId('strip-description')).toHaveText(/^Clip 1 of 2, clipA\.mp4/);
  });

  test('RM.4 AC3 one-finger pan moves the playhead, pinch changes the zoom, neither ends in pointercancel nor edits the document', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const touch = await TouchScript.on(ed.page);
    await countPointerCancels(ed.page);
    const revisionBefore = (await ed.serverTimeline()).revision;
    const cv = (await ed.canvas.boundingBox())!;
    const y = await ed.laneY(0);

    expect(await timecode(ed)).toBe('00:00:00:00');
    const from = { id: 1, x: cv.x + cv.width - 60, y };
    await touch.start([from]);
    await touch.glide([from], [{ id: 1, x: from.x - 150, y }]);
    await touch.end();
    await expect.poll(() => timecode(ed), { message: 'dragging the strip left moved the playhead later' }).not.toBe('00:00:00:00');

    const zoomBefore = await ppf(ed);
    const a = [{ id: 1, x: cv.x + cv.width / 2 - 40, y }, { id: 2, x: cv.x + cv.width / 2 + 40, y }];
    await touch.start(a);
    await touch.glide(a, [{ id: 1, x: cv.x + cv.width / 2 - 100, y }, { id: 2, x: cv.x + cv.width / 2 + 100, y }]);
    await touch.end();
    await expect.poll(() => ppf(ed), { message: 'pinching out zoomed in' }).toBeGreaterThan(zoomBefore);

    expect(await pointerCancels(ed.page), 'pan and pinch produced no pointercancel').toBe(0);
    expect((await ed.serverTimeline()).revision, 'panning and zooming edit no document').toBe(revisionBefore);
  });

  test('RM.4 a two-finger drag pans without changing the selection', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const touch = await TouchScript.on(ed.page);
    await ed.selectClipAt(30);
    const cv = (await ed.canvas.boundingBox())!;
    const y = await ed.laneY(0);
    const a = [{ id: 1, x: cv.x + cv.width / 2 + 40, y }, { id: 2, x: cv.x + cv.width / 2 + 100, y }];
    await touch.start(a);
    await touch.glide(a, a.map((f) => ({ id: f.id, x: f.x - 80, y })));
    await touch.end();
    await expect.poll(() => timecode(ed)).not.toBe('00:00:00:00');
    await expect(ed.page.getByTestId('trim-handle-out'), 'the clip stayed selected').toHaveCount(1);
  });

  test('RM.4 dragging the selected clip\'s end handle stores the trim', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const touch = await TouchScript.on(ed.page);
    await ed.selectClipAt(30);
    const box = (await ed.testId('trim-handle-out').boundingBox())!;
    expect(box.width, 'a finger-sized hit area').toBeGreaterThanOrEqual(44);
    const h = { id: 1, x: box.x + box.width / 2, y: box.y + box.height / 2 };
    await touch.start([h]);
    await touch.glide([h], [{ id: 1, x: h.x - 40, y: h.y }]);
    await touch.end();
    const tl = await ed.expectServer((t) => (itemNamed(t, 'clipA.mp4')?.recordOut ?? 60) < 60, 'the handle drag shortened clip A in the stored document');
    expect(itemNamed(tl, 'clipA.mp4')!.recordOut).toBeLessThan(60);
    await ed.reload();
    expect(itemNamed(await ed.serverTimeline(), 'clipA.mp4')!.recordOut, 'the trim survives a reload').toBeLessThan(60);
  });

  test('RM.4 AC12 landscape 844x390 shows at least two full lanes in the strip', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.page.setViewportSize({ width: 844, height: 390 });
    await expect(ed.testId('phone-strip')).toBeVisible();
    await ed.testId('timeline-canvas-wrapper').scrollIntoViewIfNeeded();
    const surface = (await ed.testId('timeline-canvas-wrapper').boundingBox())!;
    // Ruler 28 + marker lane 14 + two 56 px lanes.
    expect(surface.height, 'room for the ruler and two 56 px lanes').toBeGreaterThanOrEqual(28 + 14 + 2 * 56 - 1);
    expect(surface.y + surface.height).toBeLessThanOrEqual(391);
    const lanes = await ed.canvas.evaluate((el) => (el as HTMLCanvasElement).height / (window.devicePixelRatio || 1));
    expect(lanes).toBeGreaterThanOrEqual(28 + 14 + 2 * 56);
  });

  test('RM.4 a conflict bar leaves the strip at least 160 px high', async ({ freshTimeline, openEditor }) => {
    const ed = await openEditor(await freshTimeline('seeded'));
    await otherWriterRenames(ed, 0, 'Renamed by someone else');
    await bladeAt(ed, 75);
    await expect(ed.testId('conflict-dialog')).toBeVisible();
    const strip = (await ed.testId('phone-strip').boundingBox())!;
    expect(strip.height).toBeGreaterThanOrEqual(160);
    const surface = (await ed.testId('timeline-canvas-wrapper').boundingBox())!;
    expect(surface.height, 'the strip surface is still there').toBeGreaterThan(0);
  });
});
