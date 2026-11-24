import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, countItems, itemNamed, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * R03.1 (initiative editor-v2-wave2, T-004): Delete is ripple delete (D7), Shift+Delete lifts, a gap is an
 * object you can click and Delete closes, and "Close all gaps" is one button. Every test reads the STORED document.
 */

const FOUR: ClipSpec[] = [
  { name: 'a.mp4', file: 'clipA', recordIn: 0, recordOut: 30 },
  { name: 'b.mp4', file: 'clipB', recordIn: 30, recordOut: 60 },
  { name: 'c.mp4', file: 'clipA', recordIn: 60, recordOut: 90 },
  { name: 'd.mp4', file: 'clipB', recordIn: 90, recordOut: 120 },
];

test('R03.1 delete a middle clip from the Clip menu: the following clips shift left and the stored document has no gap', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR));
  await ed.selectClipAt(45);
  await ed.clipAction('delete');
  const tl = await ed.expectServer((t) => !itemNamed(t, 'b.mp4'), 'b is gone');
  expect(tl.document.tracks[0].items.map((i) => [i.name, i.recordIn, i.recordOut])).toEqual([['a.mp4', 0, 30], ['c.mp4', 30, 60], ['d.mp4', 60, 90]]);
});

test('R03.1 the Delete key is a ripple delete and Shift+Delete leaves a gap (desktop)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR));
  await ed.selectClipAt(45);
  await ed.page.keyboard.press('Delete');
  const closed = await ed.expectServer((t) => !itemNamed(t, 'b.mp4'), 'Delete removed b');
  expect(itemNamed(closed, 'c.mp4')!.recordIn, 'c closed up against a').toBe(30);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => countItems(t) === 4, 'undo brings b back');
  await ed.selectClipAt(45);
  await ed.page.keyboard.press('Shift+Delete');
  const lifted = await ed.expectServer((t) => !itemNamed(t, 'b.mp4'), 'Shift+Delete removed b');
  expect(itemNamed(lifted, 'c.mp4')!.recordIn, 'the gap stays: c has not moved').toBe(60);
});

test('R03.1 Lift from the Clip menu leaves a gap (touch has no Shift+Delete)', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, FOUR));
  await ed.selectClipAt(45);
  await ed.clipAction('lift');
  const tl = await ed.expectServer((t) => !itemNamed(t, 'b.mp4'), 'b lifted');
  expect(itemNamed(tl, 'c.mp4')!.recordIn).toBe(60);
});

const WITH_GAP: ClipSpec[] = [FOUR[0], FOUR[2], FOUR[3]];

test('R03.1 clicking a gap selects it and the visible Close selected gap button closes it (desktop and tablet; the phone has no gap tool)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, WITH_GAP));
  await ed.tapAt(await ed.frameX(45), await ed.laneY(0));
  const gap = ed.testId('selected-gap');
  await expect(gap, 'the gap is shown as selected').toBeVisible();
  await expect(gap).toHaveAttribute('data-gap-start', '30');
  await expect(gap).toHaveAttribute('data-gap-end', '60');
  await ed.tap(ed.testId('close-selected-gap'));
  const tl = await ed.expectServer((t) => itemNamed(t, 'c.mp4')?.recordIn === 30, 'c closed up');
  expect(itemNamed(tl, 'd.mp4')!.recordIn).toBe(60);
  await expect(gap).toHaveCount(0);
});

test('R03.1 select a gap and press Delete: it closes (desktop)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, WITH_GAP));
  await ed.tapAt(await ed.frameX(45), await ed.laneY(0));
  await expect(ed.testId('selected-gap')).toBeVisible();
  await ed.page.keyboard.press('Delete');
  const tl = await ed.expectServer((t) => itemNamed(t, 'c.mp4')?.recordIn === 30, 'Delete closed the selected gap');
  expect(tl.document.tracks[0].items.map((i) => [i.name, i.recordIn])).toEqual([['a.mp4', 0], ['c.mp4', 30], ['d.mp4', 60]]);
  await expect.poll(() => ed.patches.at(-1)?.ops, { message: 'one deleteGap op' }).toEqual(['deleteGap']);
});

test('R03.1 Close all gaps closes every gap on the track in one step and Undo brings them back', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no gap tool (RM.5 action bar: Delete ripples, Lift leaves the gap); skip recorded in suite-skips.md
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, [
      { name: 'a.mp4', file: 'clipA', recordIn: 0, recordOut: 20 },
      { name: 'b.mp4', file: 'clipB', recordIn: 40, recordOut: 60 },
      { name: 'c.mp4', file: 'clipA', recordIn: 80, recordOut: 100 },
    ]),
  );
  await ed.selectClipAt(10);
  await ed.tap(ed.testId('close-all-gaps'));
  const tl = await ed.expectServer((t) => itemNamed(t, 'c.mp4')?.recordIn === 40, 'both gaps closed');
  expect(tl.document.tracks[0].items.map((i) => [i.name, i.recordIn, i.recordOut])).toEqual([['a.mp4', 0, 20], ['b.mp4', 20, 40], ['c.mp4', 40, 60]]);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => itemNamed(t, 'c.mp4')?.recordIn === 80, 'one Undo restores both gaps');
});
