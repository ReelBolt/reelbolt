import { test, expect } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane C, LAYOUT (inventory/workspaces.md 2.9, WS-195, WS-196): the phone pane switch reaches every workspace's tools, and the
 * 601-768 px "tablet portrait" width (phone layout, desktop-grade tools) neither overflows nor hides the tools.
 */

const TOOLS: Array<[workspace: 'media' | 'color' | 'compose' | 'audio' | 'deliver', label: RegExp, testId: string]> = [
  ['media', /^Media$/, 'media-pool'],
  ['color', /^Color$/, 'color-workspace'],
  ['compose', /^Compose$/, 'compose-workspace'],
  ['audio', /^Audio$/, 'mixer-panel'],
  ['deliver', /^Deliver$/, 'deliver-workspace'],
];

test('WS-195 on a phone the dock opens a sheet with each tool: media, look, audio and deliver (no Cut or Compose)', async ({ openEditor, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openEditor(seed.seededTimelineId);
  for (const [workspace, testId] of [['media', 'media-pool'], ['color', 'look-sheet'], ['audio', 'phone-audio-sheet'], ['deliver', 'deliver-workspace']] as const) {
    await ed.workspace(workspace);
    await expect(ed.testId(testId).first(), `${workspace}: the tools are visible`).toBeVisible();
  }
  // Back to the timeline: the sheet closes and the strip returns.
  await ed.workspace('edit');
  await expect(ed.canvas).toBeVisible();
});

test('WS-196 at 700 px wide the editor uses the tablet layout (phone is under 600 px) without horizontal overflow, and 590 px gets the phone layout', async ({ page, seed, openEditor }) => {
  runOn('desktop');
  await page.setViewportSize({ width: 700, height: 900 });
  const ed = await openEditor(seed.seededTimelineId);
  const overflow = () => page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(await overflow(), 'no horizontal page scroll in the editor').toBeLessThanOrEqual(1);
  await expect(page.getByTestId('editor-workspace'), '700 px is the tablet layout, not the phone shell').not.toHaveAttribute('data-layout', 'phone');
  for (const [workspace, , testId] of TOOLS) {
    await ed.workspace(workspace);
    await expect(ed.testId(testId).first(), `${workspace}: the tools are visible at 700 px`).toBeVisible();
    expect(await overflow(), `${workspace}: no horizontal overflow`).toBeLessThanOrEqual(1);
  }
  // Under 600 px the phone layout (and the phone's gates) applies, still without sideways scroll.
  await page.setViewportSize({ width: 590, height: 900 });
  await expect(page.getByTestId('editor-workspace')).toHaveAttribute('data-layout', 'phone', { timeout: 15_000 });
  expect(await overflow(), 'no horizontal overflow at 590 px').toBeLessThanOrEqual(1);
});
