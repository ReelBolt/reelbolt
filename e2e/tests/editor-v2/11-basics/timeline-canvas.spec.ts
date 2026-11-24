import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 11 basics: the timeline canvas wheel and drag feedback (remediation-plan.md W0.6 "Wheel/pinch handler registered
 * non-passive; drag preview ghost"; report.md §5 ui1 D9, ui1 D12; T-031 item 9).
 */

/**
 * ui1 D9 (desktop d7): Ctrl+wheel over the timeline zooms the timeline, and the browser is told so
 * (`defaultPrevented`, so the page itself does not zoom). The audit logged 40 console errors "Unable to
 * preventDefault inside passive event listener" for 40 wheel events.
 */
test('ui1 D9 Ctrl+wheel over the timeline is handled without passive-listener errors', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('ui1 D9');
  const errors: string[] = [];
  const ed = await openEditor(await freshTimeline('seeded'));
  ed.page.on('console', (m) => { if (m.type() === 'error') errors.push(m.text()); });
  await ed.page.evaluate(() => {
    const w = window as unknown as { __wheel: { total: number; prevented: number } };
    w.__wheel = { total: 0, prevented: 0 };
    window.addEventListener('wheel', (e) => { w.__wheel.total++; if (e.defaultPrevented) w.__wheel.prevented++; });
  });
  const cv = await ed.canvasBox();
  const x = cv.x + 120;
  const y = await ed.laneY(0);
  await ed.page.mouse.move(x, y);
  await ed.page.keyboard.down('Control');
  for (let i = 0; i < 6; i++) {
    await ed.page.mouse.wheel(0, -100);
    await ed.page.waitForTimeout(50);
  }
  await ed.page.keyboard.up('Control');
  await ed.page.waitForTimeout(300);
  const wheel = await ed.page.evaluate(() => (window as unknown as { __wheel: { total: number; prevented: number } }).__wheel);
  expect(wheel.total, 'the wheel events reached the page').toBeGreaterThan(0);
  expect(wheel.prevented, 'every Ctrl+wheel was taken by the timeline (the page does not zoom)').toBe(wheel.total);
  expect(errors.filter((e) => /passive event listener/i.test(e)), 'no passive-listener console error').toEqual([]);
});

/** ui1 D12 (desktop d7): while a clip is dragged a ghost shows where it will land; it goes away on drop. */
test('ui1 D12 a dragged clip shows a ghost where it will land', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('ui1 D12');
  const ed = await openEditor(await freshTimeline('seeded'));
  const y = await ed.laneY(0);
  const x0 = await ed.frameX(160);
  const x1 = x0 + 80;
  await ed.page.mouse.move(x0, y);
  await ed.page.mouse.down();
  await ed.page.mouse.move(x1, y, { steps: 10 });
  const ghost = ed.page.getByTestId('timeline-drag-ghost');
  await expect(ghost, 'mid-drag a ghost is drawn').toBeVisible();
  const g = (await ghost.boundingBox())!;
  const cv = await ed.canvasBox();
  expect(Math.abs(g.width - 150 * 2), 'the ghost is as long as the clip (150 frames)').toBeLessThan(6);
  expect(g.y, 'the ghost is on the clip lane').toBeLessThan(y);
  expect(g.y + g.height, 'the ghost is on the clip lane').toBeGreaterThan(y);
  expect(g.x - cv.x, 'the ghost has moved with the pointer, right of the clip start (frame 150, x 300)').toBeGreaterThan(300 + 20);
  await ed.page.mouse.up();
  await expect(ghost, 'the ghost goes away on drop').toBeHidden();
});
