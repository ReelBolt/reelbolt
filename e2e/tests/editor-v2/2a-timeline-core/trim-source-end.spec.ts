import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';
import { PX_PER_FRAME } from '../../../fixtures/editor-v2/editor-page';

/**
 * T-023: a slip or an extension past the end of the source stops there and says so, so the server never refuses the
 * save and later edits are stored. Escape cancels a drag on one press. A body drag the mode cannot act on says so.
 * B sits far along the timeline, so a long drag to the left stays on the canvas. Their test sources are 8 s long (240 frames).
 */
const SPREAD = [
  { name: 'clipA.mp4', file: 'clipA' as const, recordIn: 0, recordOut: 60 },
  { name: 'clipB.mp4', file: 'clipB' as const, recordIn: 300, recordOut: 360, extra: { sourceIn: 30 } },
];
const THREE = [
  { name: 'clipA.mp4', file: 'clipA' as const, recordIn: 0, recordOut: 60 },
  { name: 'clipB.mp4', file: 'clipB' as const, recordIn: 60, recordOut: 120, extra: { sourceIn: 30 } },
  { name: 'clipC.mp4', file: 'clipA' as const, recordIn: 120, recordOut: 180, extra: { sourceIn: 30 } },
];
const SOURCE_FRAMES = 240;

async function holdBodyDrag(ed: EditorPage, fromFrame: number, deltaFrames: number): Promise<void> {
  const x = await ed.frameX(fromFrame);
  const y = await ed.laneY(0);
  await ed.page.mouse.move(x, y);
  await ed.page.mouse.down();
  await ed.page.mouse.move(x + deltaFrames * PX_PER_FRAME, y, { steps: 10 });
}

test('a slip past the source end stops there, nothing is refused, and a later Delete is stored', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet');
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, SPREAD));
  await ed.selectClipAt(330);
  await ed.chooseTrimMode('slip');
  await holdBodyDrag(ed, 330, -130);
  await ed.page.mouse.move(await ed.frameX(100), await ed.laneY(0), { steps: 6 });
  const readout = ed.testId('trim-readout');
  await expect(readout, 'the readout names the stop before the release').toHaveAttribute('data-limit', 'sourceEnd');
  await expect(ed.testId('trim-limit')).toContainText(/end of the source/);
  await ed.page.mouse.up();
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.sourceIn ?? 30) !== 30, 'the slip is stored');
  const b = itemNamed(tl, 'clipB.mp4')!;
  expect(b.sourceIn, 'stopped on the last frame the clip can show').toBe(SOURCE_FRAMES - 60);
  expect(ed.patches.filter((p) => p.status >= 400), 'the server refused nothing').toEqual([]);
  await expect(ed.chip).toHaveAttribute('data-status', 'saved');
  await ed.page.keyboard.press('Delete');
  await ed.expectServer((t) => itemNamed(t, 'clipB.mp4') === undefined, 'a later Delete is stored');
});

test('a handle route extension past the source end stops there too', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet');
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [SPREAD[0], { ...SPREAD[1], recordIn: 60, recordOut: 120, extra: { sourceIn: 170 } }]));
  await ed.selectClipAt(90);
  const box = (await ed.testId('trim-handle-out').boundingBox())!;
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await ed.page.mouse.move(x, y);
  await ed.page.mouse.down();
  await ed.page.mouse.move(x + 40 * PX_PER_FRAME, y, { steps: 10 });
  await expect(ed.testId('trim-readout')).toHaveAttribute('data-limit', 'sourceEnd');
  await ed.page.mouse.up();
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.recordOut ?? 120) !== 120, 'the trim is stored');
  expect(itemNamed(tl, 'clipB.mp4')!.recordOut, 'ten frames of source were left').toBe(130);
  expect(ed.patches.filter((p) => p.status >= 400)).toEqual([]);
});

test('the readout stays inside the canvas at the right edge, and its sentences read apart', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet');
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  const box = (await ed.testId('trim-handle-out').boundingBox())!;
  const canvas = (await ed.page.getByTestId('timeline-canvas-wrapper').boundingBox())!;
  const y = box.y + box.height / 2;
  await ed.page.mouse.move(box.x + box.width / 2, y);
  await ed.page.mouse.down();
  await ed.page.mouse.move(canvas.x + canvas.width - 4, y, { steps: 8 });
  const readout = ed.testId('trim-readout');
  await expect(readout).toBeVisible();
  const r = (await readout.boundingBox())!;
  expect(r.x + r.width, 'not past the right edge').toBeLessThanOrEqual(canvas.x + canvas.width + 0.5);
  expect(r.x).toBeGreaterThanOrEqual(canvas.x - 0.5);
  await expect(readout, 'the sentence ends before the next starts').toContainText(/\. (Moves|Stopped)/);
  await ed.page.mouse.up();
});

for (const kind of ['a body trim', 'a clip move'] as const) {
  test(`Escape cancels ${kind}: one press, no write, still selected`, async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop', 'tablet');
    skipSyntheticPointers();
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
    await ed.selectClipAt(90);
    if (kind === 'a body trim') await ed.chooseTrimMode('slip');
    const before = ed.patches.length;
    await holdBodyDrag(ed, 90, 10);
    await ed.page.keyboard.press('Escape');
    await expect(ed.testId('trim-readout')).toHaveCount(0);
    await ed.page.mouse.up();
    await ed.page.waitForTimeout(800);
    expect(ed.patches.length, 'nothing was written').toBe(before);
    await expect(ed.testId('trim-handle-out'), 'the clip is still selected').toBeVisible();
  });
}

test('a clip that cannot slide says so: the plain cursor, and a hint while it moves', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet');
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [THREE[0]]));
  await ed.selectClipAt(30);
  await ed.chooseTrimMode('slide');
  const x = await ed.frameX(30);
  const y = await ed.laneY(0);
  await ed.page.mouse.move(x, y);
  const canvas = ed.page.getByTestId('timeline-canvas-wrapper').locator('canvas').first();
  await expect.poll(() => canvas.evaluate((c: HTMLElement) => c.style.cursor)).not.toContain('slide');
  await ed.page.mouse.down();
  await ed.page.mouse.move(x + 30 * PX_PER_FRAME, y, { steps: 8 });
  await expect(ed.testId('trim-fallback-hint')).toHaveAttribute('data-reason', 'noNeighbour');
  await ed.page.mouse.up();
  await expect(ed.testId('trim-fallback-hint')).toHaveCount(0);
  await ed.expectServer((t) => (itemNamed(t, 'clipA.mp4')?.recordIn ?? 0) > 0, 'the drag moved it');
});
