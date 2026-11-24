import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, itemNamed, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';

/** R03.3 (T-004): numeric in / out / duration in the inspector, trim-mode keys, the drag readout and the hit area. */

const THREE: ClipSpec[] = [
  { name: 'a.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
  { name: 'b.mp4', file: 'clipB', recordIn: 60, recordOut: 120, extra: { sourceIn: 30 } },
  { name: 'c.mp4', file: 'clipA', recordIn: 120, recordOut: 180, extra: { sourceIn: 30 } },
];
const src = (tl: Awaited<ReturnType<EditorPage['serverTimeline']>>, name: string) => (itemNamed(tl, name) as unknown as { sourceIn: number }).sourceIn;

async function setField(ed: EditorPage, id: string, text: string): Promise<void> {
  const field = ed.testId(id);
  await field.fill(text);
  await field.press('Enter');
}

test('R03.3 typed Out writes recordOut exactly and ripples the clips after it', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await ed.openInspector();
  await setField(ed, 'inspector-out', '100');
  const tl = await ed.expectServer((t) => itemNamed(t, 'b.mp4')?.recordOut === 100, 'out written');
  expect(itemNamed(tl, 'b.mp4')!.recordIn).toBe(60);
  expect(itemNamed(tl, 'c.mp4')!.recordIn, 'the next clip followed').toBe(100);
  expect(src(tl, 'b.mp4'), 'the source in-point is untouched').toBe(30);
});

test('R03.3 typed Duration and typed In (source in-point) are exact', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await ed.openInspector();
  await setField(ed, 'inspector-duration', '45');
  await ed.expectServer((t) => itemNamed(t, 'b.mp4')?.recordOut === 105, 'duration 45');
  await setField(ed, 'inspector-in', '40');
  const tl = await ed.expectServer((t) => src(t, 'b.mp4') === 40, 'source in written');
  expect(itemNamed(tl, 'b.mp4')!.recordIn, 'it still starts where it did').toBe(60);
  expect(itemNamed(tl, 'b.mp4')!.recordOut, 'the head trim shortened it by 10').toBe(95);
  expect(itemNamed(tl, 'c.mp4')!.recordIn).toBe(95);
});

test('R03.3 timecode entry: Out as 00:00:03:20', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await ed.openInspector();
  await ed.page.getByText('Timecode', { exact: true }).click();
  await setField(ed, 'inspector-out', '00:00:03:20');
  await ed.expectServer((t) => itemNamed(t, 'b.mp4')?.recordOut === 110, 'out 110 frames');
});

test('R03.3 a value past the end of the source is refused with a sentence and the document is unchanged', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await ed.openInspector();
  const before = ed.patches.length;
  await setField(ed, 'inspector-duration', '999999');
  await expect(ed.testId('timing-problem')).toContainText(/source|long/i);
  await ed.page.waitForTimeout(1500);
  expect(ed.patches.length, 'nothing was sent').toBe(before);
  expect(itemNamed(await ed.serverTimeline(), 'b.mp4')!.recordOut).toBe(120);
  await setField(ed, 'inspector-duration', '0');
  await expect(ed.testId('timing-problem')).toContainText(/at least one frame/i);
});

test('R03.3 trim mode keys R / T / Y / U switch the mode (desktop)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  for (const [key, mode] of [['Y', 'slip'], ['U', 'slide'], ['T', 'roll'], ['R', 'ripple']] as const) {
    await ed.page.keyboard.press(key);
    await expect(ed.testId('trim-mode')).toHaveAttribute('data-mode', mode);
  }
});

async function dragOut(ed: EditorPage, delta: number, hold = false): Promise<void> {
  const box = (await ed.testId('trim-handle-out').boundingBox())!;
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await ed.page.mouse.move(x, y);
  await ed.page.mouse.down();
  await ed.page.mouse.move(x + delta * 2, y, { steps: 10 });
  if (!hold) await ed.page.mouse.up();
}

test('R03.3 the trim handle has a 10 px+ hit area and a readout names the mode and the frames while dragging (desktop)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  expect((await ed.testId('trim-handle-out').boundingBox())!.width).toBeGreaterThanOrEqual(10);
  await ed.page.keyboard.press('Y');
  await dragOut(ed, 10, true);
  await expect(ed.testId('trim-readout')).toContainText(/Slip: same length/);
  await expect(ed.testId('trim-readout')).toContainText(/shows the source \d+ frames? (earlier|later)/);
  await ed.page.mouse.up();
  await ed.expectServer((t) => src(t, 'b.mp4') !== 30, 'slip changed the source in-point');
});

test('R03.3 slip keeps the span, roll moves the shared cut, ripple shifts the followers (drag, desktop)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  await ed.page.keyboard.press('Y');
  await dragOut(ed, 10);
  let tl = await ed.expectServer((t) => src(t, 'b.mp4') !== 30, 'slipped');
  expect([itemNamed(tl, 'b.mp4')!.recordIn, itemNamed(tl, 'b.mp4')!.recordOut], 'slip leaves the span').toEqual([60, 120]);
  await ed.page.keyboard.press('T');
  await dragOut(ed, -10);
  tl = await ed.expectServer((t) => (itemNamed(t, 'b.mp4')?.recordOut ?? 120) < 120, 'rolled');
  expect(itemNamed(tl, 'c.mp4')!.recordIn, 'the next clip starts where b now ends').toBe(itemNamed(tl, 'b.mp4')!.recordOut);
  expect(itemNamed(tl, 'c.mp4')!.recordOut, 'roll leaves the total length').toBe(180);
  await ed.page.keyboard.press('R');
  await dragOut(ed, -10);
  tl = await ed.expectServer((t) => itemNamed(t, 'c.mp4')!.recordOut < 180, 'ripple shifted the follower');
  expect(itemNamed(tl, 'c.mp4')!.recordIn).toBe(itemNamed(tl, 'b.mp4')!.recordOut);
});
