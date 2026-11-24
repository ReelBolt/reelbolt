import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';

/**
 * Lane A: trim reachability (TL-128 TL-129, op `trim`). The engine has four trim modes; the editor used
 * to hard-code ripple. A "Trim:" menu in the timeline toolbar now chooses what a trim-handle drag does.
 * Each test drags the REAL handle and reads the stored document.
 *
 * Layout: V1 = [A 0..60] [B 60..120] [C 120..180]; B starts 30 frames into its source so slip has room.
 */

const THREE = [
  { name: 'clipA.mp4', file: 'clipA' as const, recordIn: 0, recordOut: 60 },
  { name: 'clipB.mp4', file: 'clipB' as const, recordIn: 60, recordOut: 120, extra: { sourceIn: 30 } },
  { name: 'clipC.mp4', file: 'clipA' as const, recordIn: 120, recordOut: 180, extra: { sourceIn: 30 } },
];

/** Drags a trim handle of the selected clip by `deltaFrames` with the device's own pointer. */
async function dragHandle(ed: EditorPage, handle: 'in' | 'out', deltaFrames: number): Promise<void> {
  const el = ed.testId(`trim-handle-${handle}`);
  await expect(el, 'the selected clip shows its trim handle').toBeVisible();
  const box = (await el.boundingBox())!;
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  const x1 = x + deltaFrames * 2; // PX_PER_FRAME
  if (ed.isPhone) await ed.touchDrag(x, y, x1, y, 16);
  else await ed.mouseDrag(x, y, x1, y, 16);
}

async function setMode(ed: EditorPage, mode: 'ripple' | 'roll' | 'slip' | 'slide'): Promise<void> {
  await ed.chooseTrimMode(mode);
  await expect(ed.testId('trim-mode')).toHaveAttribute('data-mode', mode);
}

test('TL-128 TL-129 ripple trim (default): the out handle shortens the clip and the next clips close up', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no Trim menu in a toolbar: its Trim sheet is covered in 30-phone/actions.spec.ts
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await expect(ed.testId('trim-mode'), 'ripple is the default mode').toHaveAttribute('data-mode', 'ripple');
  await ed.selectClipAt(90);
  await dragHandle(ed, 'out', -20);
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.recordOut ?? 120) < 110, 'clipB\'s out edge moved earlier');
  expect(itemNamed(tl, 'clipB.mp4')!.recordOut, 'by about the 20-frame drag').toBeGreaterThanOrEqual(98);
  expect(itemNamed(tl, 'clipB.mp4')!.recordOut).toBeLessThanOrEqual(102);
  expect(itemNamed(tl, 'clipC.mp4')!.recordIn, 'clipC rippled left with the edge').toBe(itemNamed(tl, 'clipB.mp4')!.recordOut);
  await expect.poll(() => ed.patches.at(-1)?.ops, { message: 'one trim op' }).toEqual(['trim']);
});

test('TL-128 the in handle trims the head (ripple): the clip starts later in its source', async ({ freshTimeline, openEditor, api, seed }) => {
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await dragHandle(ed, 'in', 15);
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.sourceIn ?? 30) > 35, 'clipB\'s source in point moved later');
  expect(itemNamed(tl, 'clipB.mp4')!.sourceIn, 'about 15 frames into the source').toBeGreaterThanOrEqual(43);
  expect(itemNamed(tl, 'clipB.mp4')!.sourceIn).toBeLessThanOrEqual(47);
});

test('TL-128x roll trim: the cut between two clips moves and the total length stays', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no Trim menu in a toolbar: its Trim sheet is covered in 30-phone/actions.spec.ts
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await setMode(ed, 'roll');
  await dragHandle(ed, 'out', 20);
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.recordOut ?? 120) > 130, 'clipB\'s out edge moved later');
  const b = itemNamed(tl, 'clipB.mp4')!;
  const c = itemNamed(tl, 'clipC.mp4')!;
  expect(c.recordIn, 'clipC starts where clipB now ends (the shared cut rolled)').toBe(b.recordOut);
  expect(c.recordOut, 'the end of the timeline did not move').toBe(180);
  await expect.poll(() => ed.patches.at(-1)?.ops, { message: 'one trim op' }).toEqual(['trim']);
  await ed.reload();
  expect(itemNamed(await ed.serverTimeline(), 'clipB.mp4')!.recordOut, 'the roll survives reload').toBe(b.recordOut);
});

test('TL-128x slip trim: the clip keeps its place and length and shows a different part of the source', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no Trim menu in a toolbar: its Trim sheet is covered in 30-phone/actions.spec.ts
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await setMode(ed, 'slip');
  await dragHandle(ed, 'in', 10);
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.sourceIn ?? 30) !== 30, 'clipB\'s source in point changed');
  const b = itemNamed(tl, 'clipB.mp4')!;
  expect([b.recordIn, b.recordOut], 'its place on the timeline is untouched').toEqual([60, 120]);
  expect(itemNamed(tl, 'clipC.mp4')!.recordIn, 'neighbours untouched').toBe(120);
});

test('TL-128x slide trim: the clip moves along the track and its neighbour absorbs the change', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no Trim menu in a toolbar: its Trim sheet is covered in 30-phone/actions.spec.ts
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await ed.tap(ed.testId('trim-mode'));
  await expect(ed.testId('trim-mode-slide'), 'the menu says what slide does and does not do').toContainText(/gap can open/i);
  await ed.page.keyboard.press('Escape');
  await expect(ed.testId('trim-mode-slide'), 'the menu has finished closing').toBeHidden();
  await setMode(ed, 'slide');
  await dragHandle(ed, 'out', 15);
  const tl = await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.recordIn ?? 60) > 70, 'clipB slid later');
  const b = itemNamed(tl, 'clipB.mp4')!;
  expect(b.recordOut - b.recordIn, 'clipB kept its length').toBe(60);
  expect(itemNamed(tl, 'clipC.mp4')!.recordIn, 'clipC absorbed the move').toBe(b.recordOut);
  expect(itemNamed(tl, 'clipC.mp4')!.recordOut, 'the end did not move').toBe(180);
  expect(itemNamed(tl, 'clipA.mp4')!.recordOut, 'clipA is untouched (the slide leaves a gap before the moved clip)').toBe(60);
});

test('TL-128y a roll trim is one undo step (both clips come back with one Undo)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no Trim menu in a toolbar: its Trim sheet is covered in 30-phone/actions.spec.ts
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await setMode(ed, 'roll');
  await dragHandle(ed, 'out', 20);
  await ed.expectServer((t) => (itemNamed(t, 'clipB.mp4')?.recordOut ?? 120) > 130, 'rolled');
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => itemNamed(t, 'clipB.mp4')?.recordOut === 120 && itemNamed(t, 'clipC.mp4')?.recordIn === 120, 'one Undo restores both clips');
});
