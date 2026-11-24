import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-003: one panel-geometry model. Each page opens at the §6.1 sizes, a resize survives a reload
 * (the record stores fractions), and the two resets — this page, every page — put the defaults back.
 * The page compositions themselves are checked by their own specs; this one checks the frame.
 */

type Workspace = Parameters<EditorPage['workspace']>[0];

const TABLE: Array<[Workspace, number, number]> = [
  // workspace, left px, timeline px (§6.1)
  ['edit', 300, 320],
  ['cut', 320, 240],
  ['audio', 360, 280],
  ['compose', 340, 180],
  ['color', 300, 180],
];

async function panelWidth(ed: EditorPage, testId: string): Promise<number> {
  return ed.testId(testId).evaluate((el) => el.getBoundingClientRect().width);
}

async function panelHeight(ed: EditorPage, testId: string): Promise<number> {
  return ed.testId(testId).evaluate((el) => el.getBoundingClientRect().height);
}

async function dragLeftSeparator(page: EditorPage['page'], dx: number): Promise<void> {
  const separator = page.getByRole('separator', { name: 'Resize left panel' }).first();
  const box = await separator.boundingBox();
  if (!box) throw new Error('the left separator is not visible');
  const x = box.x + box.width / 2;
  const y = box.y + box.height / 2;
  await page.mouse.move(x, y);
  await page.mouse.down();
  await page.mouse.move(x + dx, y, { steps: 6 });
  await page.mouse.up();
}

test('T-003 each page opens at the geometry table’s sizes', async ({ page, seed, openEditor }) => {
  runOn('desktop');
  await page.setViewportSize({ width: 1440, height: 900 });
  const ed = await openEditor(seed.seededTimelineId);
  for (const [workspace, left, timeline] of TABLE) {
    await ed.workspace(workspace);
    expect(await panelWidth(ed, 'panel-left'), `${workspace}: left zone`).toBeCloseTo(left, 0);
    expect(await panelHeight(ed, 'panel-timeline'), `${workspace}: timeline height`).toBeCloseTo(timeline, 0);
  }
});

test('T-003 Media: bins | pool | viewer+metadata, with the timeline hidden until Show timeline', async ({ page, seed, openEditor }) => {
  runOn('desktop');
  await page.setViewportSize({ width: 1440, height: 900 });
  const ed = await openEditor(seed.seededTimelineId);
  await ed.workspace('media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
  expect(await panelWidth(ed, 'panel-left'), 'bins are 280').toBeCloseTo(280, 0);
  expect(await panelWidth(ed, 'panel-right'), 'viewer+metadata is 420').toBeCloseTo(420, 0);
  await expect(ed.testId('media-bins-zone')).toBeVisible();
  await expect(ed.testId('media-pool-zone')).toBeVisible();
  await expect(ed.testId('media-detail-zone')).toBeVisible();
  await expect(ed.testId('panel-timeline'), 'hidden by default').toHaveCount(0);
  await ed.tap(ed.testId('media-timeline-toggle'));
  await expect(ed.testId('panel-timeline')).toBeVisible();
  expect(await panelHeight(ed, 'panel-timeline'), 'the on-demand strip is 200').toBeCloseTo(200, 0);
  await ed.tap(ed.testId('media-timeline-toggle'));
  await expect(ed.testId('panel-timeline')).toHaveCount(0);
});

test('T-003 Deliver fills the width with two columns and no centre or timeline', async ({ page, seed, openEditor }) => {
  runOn('desktop');
  await page.setViewportSize({ width: 1440, height: 900 });
  const ed = await openEditor(seed.seededTimelineId);
  await ed.workspace('deliver');
  await expect(ed.testId('deliver-workspace')).toBeVisible();
  expect(await panelWidth(ed, 'panel-left'), 'the settings column is 420').toBeCloseTo(420, 0);
  expect(await panelWidth(ed, 'panel-right'), 'the result column takes the rest').toBeGreaterThan(700);
  await expect(ed.testId('deliver-controls-zone')).toBeVisible();
  await expect(ed.testId('deliver-result-zone')).toBeVisible();
  await expect(ed.testId('panel-viewer'), 'no centre').toHaveCount(0);
  await expect(ed.testId('panel-timeline'), 'no timeline').toHaveCount(0);
});

test('T-003 a resized page survives a reload, and Reset this page restores the default', async ({ page, seed, openEditor }) => {
  runOn('desktop');
  await page.setViewportSize({ width: 1440, height: 900 });
  const ed = await openEditor(seed.seededTimelineId);
  await ed.workspace('edit');
  const before = await panelWidth(ed, 'panel-left');
  await dragLeftSeparator(page, 40);
  await expect.poll(() => panelWidth(ed, 'panel-left')).toBeGreaterThan(before + 20);

  await ed.reload();
  await expect.poll(() => panelWidth(ed, 'panel-left'), { message: 'the fraction is re-derived after reload' }).toBeGreaterThan(before + 20);

  await ed.testId('layout-menu').click();
  await ed.testId('layout-reset-page').click();
  await expect.poll(() => panelWidth(ed, 'panel-left'), { message: 'Reset this page restores 300' }).toBeCloseTo(300, 0);
});

test('T-003 Reset every page asks for confirmation and clears the other pages too', async ({ page, seed, openEditor }) => {
  runOn('desktop');
  await page.setViewportSize({ width: 1440, height: 900 });
  const ed = await openEditor(seed.seededTimelineId);

  await ed.workspace('edit');
  await dragLeftSeparator(page, 40);
  await expect.poll(() => panelWidth(ed, 'panel-left')).toBeGreaterThan(320);

  await ed.workspace('cut');
  await ed.testId('layout-menu').click();
  await ed.testId('layout-reset-all').click();
  await expect(ed.testId('layout-reset-all-confirm'), 'the confirmation is a dialog, not a double press').toBeVisible();
  await ed.testId('layout-reset-all-cancel').click();
  await expect(ed.testId('layout-reset-all-confirm')).toBeHidden();
  expect(await panelWidth(ed, 'panel-left'), 'cancel keeps the page as it was').toBeCloseTo(320, 0);

  await ed.testId('layout-menu').click();
  await ed.testId('layout-reset-all').click();
  await ed.testId('layout-reset-all-confirm').click();
  await ed.reload();
  await ed.workspace('edit');
  await expect.poll(() => panelWidth(ed, 'panel-left'), { message: 'every page is back to its default' }).toBeCloseTo(300, 0);
});
