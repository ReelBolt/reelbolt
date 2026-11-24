import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline } from '../../../fixtures/editor-v2/lane-a';

/**
 * usability-fixes T-011 (C6, C8, C14) on the 820x1180 tablet viewport and desktop. Uses the editor-v2 stack
 * and fixtures; run with the config beside this file. Soft assertions so one run reports every number.
 */
// isMobile + hasTouch makes (pointer: coarse) true, so the touch-tablet layout is what is asserted.
test.use({ serviceWorkers: 'block', isMobile: true, hasTouch: true });

test('C8 tablet: Inspector docked, Export whole, tabs and toggles', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const page = ed.page;
  await page.setViewportSize({ width: 820, height: 1180 });
  expect(await page.evaluate(() => window.matchMedia('(pointer: coarse)').matches), 'coarse pointer').toBe(true);
  await expect(ed.testId('panel-right'), 'Inspector is docked and visible').toBeVisible();
  await expect(ed.testId('inspector-toggle')).toHaveCount(0);
  const viewerBox = (await ed.testId('panel-viewer').boundingBox())!;
  expect(viewerBox.width, 'viewer keeps at least 280 px').toBeGreaterThanOrEqual(280);
  const exp = ed.testId('open-deliver');
  await expect.soft(exp).toHaveText('Export');
  const box = (await exp.boundingBox())!;
  const clipped = await exp.evaluate((el) => el.scrollWidth > el.clientWidth + 1);
  expect.soft(clipped, 'Export label is not truncated').toBe(false);
  expect.soft(box.height).toBeGreaterThanOrEqual(43.5);
  for (const id of ['cut', 'edit', 'deliver']) {
    const t = (await ed.testId(`workspace-${id}`).boundingBox())!;
    expect.soft(t.width, `workspace ${id} width`).toBeGreaterThanOrEqual(44);
    expect.soft(t.height, `workspace ${id} height`).toBeGreaterThanOrEqual(44);
  }
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect.soft(overflow).toBeLessThanOrEqual(0);
});

test('C6 deliver: Render before OTIO, format follows timeline, Export button acts', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const page = ed.page;
  await ed.testId('open-deliver').click();
  const controls = ed.testId('deliver-controls');
  const result = ed.testId('deliver-result');
  await expect(controls).toBeVisible();
  const c = (await controls.boundingBox())!;
  const r = (await result.boundingBox())!;
  expect.soft(c.y < r.y || c.x < r.x, 'Render settings come first (left/top)').toBe(true);
  await expect.soft(page.getByTestId('render-fit-warning'), 'default format does not crop the timeline').toHaveCount(0);
  await ed.testId('open-deliver').click();
  await expect.soft(page.getByTestId('render-start'), 'Export on Deliver focuses Render').toBeFocused();
});

test('C14 jargon moved, ? opens shortcuts', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const page = ed.page;
  await expect.soft(page.getByTestId('dropped-frames')).toBeHidden();
  await expect.soft(page.getByText(/^rev \d+$/)).toHaveCount(0);
  await page.locator('body').click({ position: { x: 5, y: 5 } });
  await page.keyboard.press('?');
  await expect.soft(page.getByRole('dialog')).toBeVisible();
});
