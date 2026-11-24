import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';
import { PX_PER_FRAME } from '../../../fixtures/editor-v2/editor-page';

/**
 * T-019: with Slip on, dragging a clip's BODY slips it: same place and length, a different part of the source,
 * one `trim` op and one undo step. The readout says so in words before the pointer is released.
 * Layout: V1 = [A 0..60] [B 60..120] [C 120..180]; B starts 30 frames into its source so slip has room.
 */
const THREE = [
  { name: 'clipA.mp4', file: 'clipA' as const, recordIn: 0, recordOut: 60 },
  { name: 'clipB.mp4', file: 'clipB' as const, recordIn: 60, recordOut: 120, extra: { sourceIn: 30 } },
  { name: 'clipC.mp4', file: 'clipA' as const, recordIn: 120, recordOut: 180, extra: { sourceIn: 30 } },
];

async function bodyDrag(ed: EditorPage, fromFrame: number, deltaFrames: number): Promise<void> {
  const x = await ed.frameX(fromFrame);
  const y = await ed.laneY(0);
  await ed.mouseDrag(x, y, x + deltaFrames * PX_PER_FRAME, y, 16);
}

test('T-019 slip body drag: length unchanged, the picture moves with the pointer, one undo step', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet');
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await ed.chooseTrimMode('slip');
  await expect(ed.testId('trim-mode'), 'the mode on is visible without opening a menu').toHaveAttribute('data-mode', 'slip');
  await bodyDrag(ed, 90, 10);
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.sourceIn ?? 30) !== 30, 'clipB\'s source in point changed');
  const b = itemNamed(tl, 'clipB.mp4')!;
  expect([b.recordIn, b.recordOut], 'place and length are untouched').toEqual([60, 120]);
  expect(b.sourceIn, 'the picture followed the pointer: the source offset went back by about the drag').toBeLessThan(30);
  expect(itemNamed(tl, 'clipC.mp4')!.recordIn, 'neighbours untouched').toBe(120);
  await expect.poll(() => ed.patches.at(-1)?.ops, { message: 'one trim op' }).toEqual(['trim']);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => itemNamed(t, 'clipB.mp4')?.sourceIn === 30, 'one Undo restores the source offset');
});

test('T-019 the readout names the slip in words while dragging', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet');
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await ed.chooseTrimMode('slip');
  const x = await ed.frameX(90);
  const y = await ed.laneY(0);
  await ed.page.mouse.move(x, y);
  await ed.page.mouse.down();
  await ed.page.mouse.move(x + 24, y, { steps: 6 });
  await expect(ed.testId('trim-readout')).toContainText(/Slip: same length/);
  await ed.page.mouse.up();
});
