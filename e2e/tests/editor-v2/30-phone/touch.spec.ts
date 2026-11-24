import type { CDPSession, Page } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Wave 3 / T-002: canvas touch hardening (RM.3), touch gestures (R03.8) and the touch filmstrip scrub
 * (R04.6). Real CDP `Input.dispatchTouchEvent` on pixel7; iphone14webkit keeps only the assertions that need
 * no drag. Every drag spec asserts a stored outcome (the document read back through the API), not that a
 * control exists.
 */

const WEBKIT_SKIP = 'WebKit cannot synthesize touch drags';
const TOUCH_DRAG_KEY = 'reelbolt:editor:touch-drag';

type Finger = { id: number; x: number; y: number };

/** A multi-finger touch script over CDP: start, interpolated moves, and an optional end. */
class TouchScript {
  private constructor(readonly page: Page, private readonly cdp: CDPSession) {}
  static async on(page: Page): Promise<TouchScript> {
    return new TouchScript(page, await page.context().newCDPSession(page));
  }
  start(fingers: Finger[]) { return this.cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: fingers }); }
  move(fingers: Finger[]) { return this.cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: fingers }); }
  end() { return this.cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] }); }
  /** Moves every finger from `from` to `to` in `steps` frames. */
  async glide(from: Finger[], to: Finger[], steps = 12): Promise<void> {
    for (let i = 1; i <= steps; i++) {
      await this.move(from.map((f, k) => ({ id: f.id, x: f.x + ((to[k].x - f.x) * i) / steps, y: f.y + ((to[k].y - f.y) * i) / steps })));
      await this.page.waitForTimeout(16);
    }
  }
  async tap(x: number, y: number): Promise<void> {
    await this.start([{ id: 1, x, y }]);
    await this.end();
  }
}

/** Counts every pointercancel the page sees (capture phase, so nothing can hide one). */
async function countPointerCancels(page: Page): Promise<void> {
  await page.evaluate(() => {
    const w = window as unknown as { __pointerCancels?: number };
    w.__pointerCancels = 0;
    window.addEventListener('pointercancel', () => { w.__pointerCancels = (w.__pointerCancels ?? 0) + 1; }, true);
  });
}
const pointerCancels = (page: Page): Promise<number> => page.evaluate(() => (window as unknown as { __pointerCancels?: number }).__pointerCancels ?? -1);

async function centre(ed: EditorPage, testId: string): Promise<{ x: number; y: number }> {
  const box = (await ed.testId(testId).boundingBox())!;
  return { x: box.x + box.width / 2, y: box.y + box.height / 2 };
}
/** Pixels per frame of the clip strip (the strip says so in its geometry data attribute). */
const stripPpf = async (ed: EditorPage): Promise<number> => Number(await ed.canvas.getAttribute('data-ppf'));

const handleX = async (ed: EditorPage): Promise<number> => (await ed.testId('trim-handle-out').boundingBox())!.x;

test.describe('RM.3 canvas touch hardening', () => {
  test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

  test('RM.3 every touch hit target keeps the gesture: touch-action none and no page scroll chaining', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    for (const id of ['timeline-canvas', 'timeline-canvas-wrapper', 'trim-handle-in', 'trim-handle-out']) {
      const style = await ed.testId(id).evaluate((el) => ({ action: getComputedStyle(el).touchAction, overscroll: getComputedStyle(el).overscrollBehaviorY }));
      expect(style.action, `${id} touch-action`).toBe('none');
      if (id !== 'trim-handle-in' && id !== 'trim-handle-out') expect(style.overscroll, `${id} overscroll-behavior`).toBe('contain');
    }
    // Only the selected clip has handles, and a finger-sized (44 px) hit area on each.
    await expect(ed.page.getByTestId('trim-handle-out')).toHaveCount(1);
    const box = (await ed.testId('trim-handle-out').boundingBox())!;
    expect(box.width).toBeGreaterThanOrEqual(44);
  });

  test('RM.3 a trim-handle drag, a pan and a pinch never end in pointercancel, and the trim is stored', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const touch = await TouchScript.on(ed.page);
    await ed.selectClipAt(30);
    await countPointerCancels(ed.page);

    // Trim the end of clip A shorter by dragging its out handle left.
    const h = await centre(ed, 'trim-handle-out');
    await touch.start([{ id: 1, x: h.x, y: h.y }]);
    await touch.glide([{ id: 1, x: h.x, y: h.y }], [{ id: 1, x: h.x - 40, y: h.y }]);
    await touch.end();
    const tl = await ed.expectServer((t) => (itemNamed(t, 'clipA.mp4')?.recordOut ?? 60) < 60, 'the trim handle drag shortened clip A in the stored document');
    expect(itemNamed(tl, 'clipA.mp4')!.recordOut).toBeLessThan(60);

    // Pan with one finger on empty space, then pinch with two.
    const cv = (await ed.canvas.boundingBox())!;
    const y = await ed.laneY(0);
    const start = { id: 1, x: cv.x + cv.width - 50, y };
    await touch.start([start]);
    await touch.glide([start], [{ id: 1, x: start.x - 120, y }]);
    await touch.end();
    const a = [{ id: 1, x: cv.x + 150, y }, { id: 2, x: cv.x + 230, y }];
    await touch.start(a);
    await touch.glide(a, [{ id: 1, x: cv.x + 90, y }, { id: 2, x: cv.x + 290, y }]);
    await touch.end();

    expect(await pointerCancels(ed.page), 'pan, trim and pinch produced no pointercancel').toBe(0);
  });
});

test.describe('R03.8 touch gestures on the timeline', () => {
  test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

  test('R03.8 a tap selects a clip', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await expect(ed.page.getByTestId('trim-handle-out')).toHaveCount(0);
    await ed.selectClipAt(30);
    await expect(ed.page.getByTestId('trim-handle-out')).toHaveCount(1);
    await expect(ed.testId('phone-action-bar')).toHaveAttribute('data-mode', 'clip');
  });

  test('R03.8 one finger drags the timeline sideways without moving the clip', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const touch = await TouchScript.on(ed.page);
    await ed.selectClipAt(30);
    const x0 = await handleX(ed);
    const cv = (await ed.canvas.boundingBox())!;
    const y = await ed.laneY(0);
    // The strip keeps the playhead at its centre: dragging left moves the content with the finger (the playhead goes later).
    const from = { id: 1, x: cv.x + cv.width - 60, y };
    await touch.start([from]);
    await touch.glide([from], [{ id: 1, x: from.x - 80, y }]);
    await touch.end();
    await expect.poll(() => handleX(ed), { message: 'the timeline panned with the finger' }).toBeLessThan(x0 - 40);
    await expect(ed.page.getByTestId('trim-handle-out'), 'the clip stayed selected').toHaveCount(1);
    const tl = await ed.serverTimeline();
    expect(itemNamed(tl, 'clipA.mp4')!.recordIn, 'a drag on a clip does not move it').toBe(0);
  });

  test('R03.8 pinch zooms time without changing the selection', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const touch = await TouchScript.on(ed.page);
    await ed.selectClipAt(30);
    const ppf0 = await stripPpf(ed);
    const revisionBefore = (await ed.serverTimeline()).revision;
    const cv = (await ed.canvas.boundingBox())!;
    const y = await ed.laneY(0);
    const a = [{ id: 1, x: cv.x + 150, y }, { id: 2, x: cv.x + 230, y }];
    await touch.start(a);
    await touch.glide(a, [{ id: 1, x: cv.x + 90, y }, { id: 2, x: cv.x + 290, y }]);
    await touch.end();
    await expect.poll(() => stripPpf(ed), { message: 'the pinch spread zoomed the strip in' }).toBeGreaterThan(ppf0 * 1.5);
    await expect(ed.testId('phone-action-bar'), 'selection unchanged (the handle may be off-screen after zooming)').toHaveAttribute('data-mode', 'clip');
    expect((await ed.serverTimeline()).revision, 'a pinch edits no document').toBe(revisionBefore);
  });

  test('R03.8 a double tap on a clip zooms to it', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const touch = await TouchScript.on(ed.page);
    await ed.selectClipAt(30);
    const ppf0 = await stripPpf(ed);
    const y = await ed.laneY(0);
    const onClip = await ed.frameX(30);
    await touch.tap(onClip, y);
    await touch.tap(onClip, y);
    await expect.poll(() => stripPpf(ed), { message: 'zoomed in on the clip' }).toBeGreaterThan(ppf0 * 1.5);
    // The clip (60 frames) now spans most of the strip width.
    const cv = (await ed.canvas.boundingBox())!;
    expect(60 * (await stripPpf(ed)), 'the clip fills the strip').toBeGreaterThan(cv.width * 0.6);
  });

  test('R03.8 a long press opens the clip action menu when touch drag is off', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const touch = await TouchScript.on(ed.page);
    const cv = (await ed.canvas.boundingBox())!;
    const y = await ed.laneY(0);
    await touch.start([{ id: 1, x: await ed.frameX(30), y }]);
    // The hold itself is the behaviour under test (500 ms); the menu is then polled, not slept for.
    await ed.page.waitForTimeout(550);
    await expect(ed.page.getByTestId('clip-context-menu')).toBeVisible();
    await touch.end();
    const tl = await ed.serverTimeline();
    expect(itemNamed(tl, 'clipA.mp4')!.recordIn, 'the long press moved nothing').toBe(0);
  });

  test('R03.8 a long press picks a clip up and the move is stored when touch drag is on', async ({ page, freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    // The per-device flag (default off) is read from localStorage, so it is set before the editor loads.
    await page.addInitScript((key) => localStorage.setItem(key, 'on'), TOUCH_DRAG_KEY);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const touch = await TouchScript.on(ed.page);
    const cv = (await ed.canvas.boundingBox())!;
    const y = await ed.laneY(0);
    const from = { id: 1, x: await ed.frameX(90), y };
    await touch.start([from]);
    await ed.page.waitForTimeout(550);
    await expect(ed.page.getByTestId('clip-context-menu'), 'no menu when touch drag is on').toHaveCount(0);
    await touch.glide([from], [{ id: 1, x: from.x + 60, y }]);
    await touch.end();
    await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.recordIn ?? 60) > 60, 'the picked-up clip B was moved later in the stored document');
  });
});

test.describe('R04.6 touch filmstrip scrub on media tiles', () => {
  test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

  test('R04.6 no hover wording on a touch device', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.workspace('media');
    await expect(ed.page.getByTestId('filmstrip-strip')).toBeVisible();
    await expect(ed.page.getByTestId('filmstrip-strip')).not.toContainText(/hover/i);
  });

  test('R04.6 press and drag across a tile scrubs the strip and shows a position label', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.workspace('media');
    const tile = ed.page.getByTestId('media-clip-tile').first();
    await tile.scrollIntoViewIfNeeded();
    const box = (await tile.boundingBox())!;
    const touch = await TouchScript.on(ed.page);
    const y = box.y + box.height / 2;
    const from = { id: 1, x: box.x + box.width * 0.15, y };
    await touch.start([from]);
    await touch.glide([from], [{ id: 1, x: box.x + box.width * 0.85, y: y + 2 }]);
    await expect(ed.page.getByTestId('media-tile-scrub-label'), 'a position label while the finger is down').toBeVisible();
    await touch.end();
    await expect(ed.page.getByTestId('media-tile-scrub-label')).toHaveCount(0);
    await expect(ed.page.getByTestId('filmstrip-strip')).toBeVisible();
    // The cell exists only once the engine has built the filmstrip derivative (the seeded stack may not have yet; the
    // desktop media spec guards it the same way). When it exists, the scrub must have moved it past the first thumbnail.
    const cell = ed.page.getByTestId('filmstrip-cell');
    test.skip((await cell.count()) === 0, 'the filmstrip derivative is not built on this stack yet (see ledgers/suite-skips.md)');
    await expect(cell).toHaveAttribute('data-thumb-index', /^[1-9]\d*$/);
  });

  test('R04.6 a vertical drag on a tile scrolls the page and does not scrub', async ({ freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', WEBKIT_SKIP);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.workspace('media');
    const tile = ed.page.getByTestId('media-clip-tile').first();
    await tile.scrollIntoViewIfNeeded();
    const box = (await tile.boundingBox())!;
    await expect(tile).toHaveCSS('touch-action', 'pan-y');
    const touch = await TouchScript.on(ed.page);
    const from = { id: 1, x: box.x + box.width / 2, y: box.y + box.height / 2 };
    await touch.start([from]);
    await touch.glide([from], [{ id: 1, x: from.x + 3, y: from.y - 60 }]);
    await expect(ed.page.getByTestId('media-tile-scrub-label')).toHaveCount(0);
    await touch.end();
  });
});
