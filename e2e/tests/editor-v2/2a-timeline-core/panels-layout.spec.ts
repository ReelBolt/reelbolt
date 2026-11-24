import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane A, section 2.H: panel resizing, the panel tabs and the phone's pane switch. The tab CONTENT
 * belongs to lane B (inspector, left panels); here each tab must open its own non-empty panel.
 */

const LEFT_TABS = ['Markers', 'Edits', 'Transcript', 'Subtitles', 'Takes', 'Structure', 'Comments', 'History'];
const RIGHT_TABS = ['Inspector', 'Assist', 'Settings', 'Versions', 'Compare'];

test.describe('desktop panels', () => {
  test.beforeEach(() => {
    runOn('desktop');
  });

  test('TL-140 TL-141 TL-142 TL-143 the three separators resize their panels by keyboard and by drag, and the size is remembered', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const size = async (id: string, dim: 'width' | 'height') => Math.round((await ed.testId(id).boundingBox())![dim]);

    const left0 = await size('panel-left', 'width');
    const sepLeft = ed.page.getByRole('separator', { name: 'Resize left panel' });
    await sepLeft.focus();
    await ed.page.keyboard.press('ArrowRight');
    expect(await size('panel-left', 'width'), 'ArrowRight grows the left panel by 16 px').toBe(left0 + 16);
    await ed.page.keyboard.press('Shift+ArrowLeft');
    expect(await size('panel-left', 'width'), 'Shift+Arrow moves 64 px').toBe(left0 + 16 - 64);

    const right0 = await size('panel-right', 'width');
    await ed.page.getByRole('separator', { name: 'Resize right panel' }).focus();
    await ed.page.keyboard.press('ArrowLeft');
    expect(await size('panel-right', 'width'), 'ArrowLeft grows the right panel').toBe(right0 + 16);

    const tl0 = await size('panel-timeline', 'height');
    await ed.page.getByRole('separator', { name: 'Resize timeline' }).focus();
    await ed.page.keyboard.press('ArrowUp');
    expect(await size('panel-timeline', 'height'), 'ArrowUp makes the timeline taller').toBe(tl0 + 16);

    // Drag the left separator 60 px to the right.
    const box = (await sepLeft.boundingBox())!;
    const before = await size('panel-left', 'width');
    await ed.mouseDrag(box.x + box.width / 2, box.y + 100, box.x + box.width / 2 + 60, box.y + 100, 8);
    expect(await size('panel-left', 'width'), 'dragging widened it by about 60 px').toBeGreaterThanOrEqual(before + 50);

    const remembered = await size('panel-left', 'width');
    await ed.reload();
    expect(await size('panel-left', 'width'), 'the size survives reload').toBe(remembered);
    expect(ed.patches, 'layout is never a document change').toEqual([]);
  });

  test('TL-144 TL-145 every left tab (8) and right tab (5) opens its own panel', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    for (const [list, tabs] of [['Timeline panels', LEFT_TABS], ['Clip panels', RIGHT_TABS]] as const) {
      const tablist = ed.page.getByRole('tablist', { name: list });
      for (const name of tabs) {
        const tab = tablist.getByRole('tab', { name, exact: true });
        await tab.click();
        await expect(tab, `${name} is selected`).toHaveAttribute('aria-selected', 'true');
        const panel = ed.page.getByRole('tabpanel').filter({ visible: true });
        await expect(panel.first(), `${name} shows something`).not.toBeEmpty();
      }
    }
  });
});

test.describe('phone panes', () => {
  test.beforeEach(() => {
    runOn('pixel7', 'iphone14webkit');
  });

  test('TL-146 TL-147 the bottom dock reaches the timeline and every tool sheet (the phone has no pane switch)', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-layout', 'phone');
    await expect(ed.canvas).toBeVisible();
    await ed.selectClipAt(30);
    for (const [name, testId] of [['color', 'look-sheet'], ['audio', 'phone-audio-sheet'], ['deliver', 'phone-sheet'], ['edit', 'timeline-canvas']] as const) {
      await ed.workspace(name);
      await expect(ed.page.getByTestId(testId).first(), `${name} dock button shows its tools`).toBeVisible();
    }
    expect(ed.patches).toEqual([]);
  });
});
