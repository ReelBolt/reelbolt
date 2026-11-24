import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline } from '../../../fixtures/editor-v2/lane-a';
import { runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';

/**
 * T-020: a ripple trim says what will shift before the release (a count, a duration and a light ghost per
 * clip), and the snapping toggle shows its state and live chord. Layout: V1 = [A 0..60] [B 60..120] [C 120..180].
 */
const THREE = [
  { name: 'clipA.mp4', file: 'clipA' as const, recordIn: 0, recordOut: 60 },
  { name: 'clipB.mp4', file: 'clipB' as const, recordIn: 60, recordOut: 120, extra: { sourceIn: 30 } },
  { name: 'clipC.mp4', file: 'clipA' as const, recordIn: 120, recordOut: 180, extra: { sourceIn: 30 } },
];

test('T-020 ripple drag: the readout counts the clips that move and a ghost shows each, gone on release', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet');
  skipSyntheticPointers();
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  await ed.selectClipAt(90);
  const box = (await ed.testId('trim-handle-out').boundingBox())!;
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await ed.page.mouse.move(x, y);
  await ed.page.mouse.down();
  await ed.page.mouse.move(x - 20, y, { steps: 6 });
  await expect(ed.testId('trim-shift')).toContainText(/Moves 1 clip by 00:00:00:\d\d/);
  await expect(ed.testId('ripple-ghost')).toHaveCount(1);
  await ed.page.mouse.up();
  await expect(ed.testId('ripple-ghost')).toHaveCount(0);
});

test('T-020 snapping off is visible on the toggle', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, THREE));
  const toggle = ed.testId('snap-toggle');
  await expect(toggle).toHaveAttribute('data-snapping', 'on');
  await toggle.click();
  await expect(toggle).toHaveAttribute('data-snapping', 'off');
  await toggle.hover();
  await expect(ed.page.getByRole('tooltip')).toContainText(/Snapping off \(N\)/);
});
