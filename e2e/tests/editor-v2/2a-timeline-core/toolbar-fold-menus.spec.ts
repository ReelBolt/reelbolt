import type { Page } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, countItems, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/** T-022: the timeline's menus and toolbar on a small window, and writers run from them while Source shows. */

const TWO: ClipSpec[] = [
  { name: 'a.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
  { name: 'b.mp4', file: 'clipB', recordIn: 60, recordOut: 120 },
];

/** Every entry must be scrollable into the window: not merely attached. */
async function expectReachable(page: Page, id: string): Promise<void> {
  const entry = page.getByTestId(id);
  await entry.scrollIntoViewIfNeeded();
  const box = (await entry.boundingBox())!;
  const view = page.viewportSize()!;
  expect(box.y, `${id} starts in the window`).toBeGreaterThanOrEqual(0);
  expect(box.y + box.height, `${id} ends in the window`).toBeLessThanOrEqual(view.height);
  expect(box.x + box.width, `${id} fits the width`).toBeLessThanOrEqual(view.width);
}

const CLIP_TOP = ['split', 'freeze-frame', 'delete', 'lift', 'copy', 'cut', 'paste', 'duplicate', 'properties'].map((id) => `ctx-${id}`);
const SUBS: Record<string, string[]> = {
  arrange: ['fit-to-fill', 'replace', 'join', 'move-up', 'move-down', 'speed', 'toggle-enabled'],
  link: ['link', 'unlink', 'edit-separately'],
  select: ['select-forward', 'select-backward', 'select-forward-all', 'select-backward-all', 'close-gaps'],
  picture: ['add-mask', 'add-ellipse-mask', 'add-reframe'],
};

for (const [width, height, name] of [[1280, 720, '1280x720'], [640, 360, '200% zoom of 1280x720']] as const) {
  test.describe(name, () => {
    test.use({ viewport: { width, height } });
    test('every clip-menu entry can be reached', async ({ freshTimeline, openEditor, api, seed }) => {
      runOn('desktop');
      const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, TWO));
      const open = async () => {
        await ed.page.mouse.click(await ed.frameX(30), await ed.laneY(0), { button: 'right' });
        await expect(ed.testId('clip-context-menu')).toBeVisible();
      };
      await open();
      for (const id of CLIP_TOP) await expectReachable(ed.page, id);
      for (const [sub, members] of Object.entries(SUBS)) {
        // On a small window an open submenu may cover the parent list: each one is reached from a fresh menu.
        await ed.page.keyboard.press('Escape');
        await expect(ed.testId('clip-context-menu')).toBeHidden();
        await open();
        await expectReachable(ed.page, `ctx-sub-${sub}`);
        await ed.testId(`ctx-sub-${sub}`).hover();
        await expect(ed.testId(`ctx-sub-${sub}-menu`)).toBeVisible();
        for (const id of members) await expectReachable(ed.page, `ctx-${id}`);
      }
    });
  });
}

test.describe('empty lane and ruler at 1280x720', () => {
  test.use({ viewport: { width: 1280, height: 720 } });
  test('every entry can be reached', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, TWO));
    await ed.page.mouse.click(await ed.frameX(200), await ed.laneY(0), { button: 'right' });
    await expect(ed.testId('clip-context-menu')).toBeVisible();
    for (const id of ['point-paste', 'point-mark-in', 'point-mark-out', 'point-add-marker']) await expectReachable(ed.page, `ctx-${id}`);
    await ed.page.keyboard.press('Escape');
    const canvas = (await ed.testId('timeline-canvas').boundingBox())!;
    await ed.page.mouse.click(await ed.frameX(30), canvas.y + 6, { button: 'right' });
    await expect(ed.testId('clip-context-menu')).toBeVisible();
    for (const id of ['point-mark-in', 'point-mark-out', 'point-add-marker']) await expectReachable(ed.page, `ctx-${id}`);
  });
});

test('ArrowLeft out of a submenu returns to its entry and the arrows keep working', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, TWO));
  await ed.page.mouse.click(await ed.frameX(30), await ed.laneY(0), { button: 'right' });
  await ed.testId('ctx-sub-arrange').focus();
  await ed.page.keyboard.press('ArrowRight');
  await expect(ed.testId('ctx-sub-arrange-menu')).toBeVisible();
  await ed.page.keyboard.press('ArrowLeft');
  await expect(ed.testId('ctx-sub-arrange')).toBeFocused();
  await ed.page.keyboard.press('ArrowDown');
  await expect(ed.testId('ctx-sub-link')).toBeFocused();
  await ed.page.keyboard.press('ArrowRight');
  await expect(ed.testId('ctx-sub-link-menu')).toBeVisible();
});

async function openSource(ed: EditorPage, file: string): Promise<void> {
  await ed.workspace('cut');
  await ed.tap(ed.testId(`cut-watch-file:${file}`));
  await expect(ed.testId('viewer-mode-switch')).toHaveAttribute('data-mode', 'source');
}

test('a menu writer run in Source switches to Record, then runs', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, TWO));
  await openSource(ed, seed.files.clipA);
  await ed.page.mouse.click(await ed.frameX(30), await ed.laneY(0), { button: 'right' });
  await ed.testId('ctx-copy').click();
  await expect(ed.testId('viewer-mode-switch'), 'Copy changes nothing on the timeline').toHaveAttribute('data-mode', 'source');
  await ed.page.mouse.click(await ed.frameX(30), await ed.laneY(0), { button: 'right' });
  await ed.testId('ctx-delete').click();
  await expect(ed.testId('viewer-mode-switch')).toHaveAttribute('data-mode', 'record');
  await ed.expectServer((t) => countItems(t) === 1, 'the clip is deleted');
});

test('a toolbar writer run in Source switches to Record, then runs; undo stays on Source', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, TWO));
  await ed.seek(30);
  await ed.tap(ed.testId('blade-at-playhead'));
  await ed.expectServer((t) => countItems(t) === 3, 'split once in Record');
  await openSource(ed, seed.files.clipA);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => countItems(t) === 2, 'undone');
  await expect(ed.testId('viewer-mode-switch'), 'undo does not switch').toHaveAttribute('data-mode', 'source');
  await ed.tap(ed.testId('blade-at-playhead'));
  await expect(ed.testId('viewer-mode-switch')).toHaveAttribute('data-mode', 'record');
  await ed.expectServer((t) => countItems(t) === 3, 'split from Source');
});

for (const width of [1280, 1440]) {
  test.describe(`toolbar at ${width}`, () => {
    test.use({ viewport: { width, height: 800 } });
    test('folds trim first, then steppers: every control is on screen or one click away', async ({ freshTimeline, openEditor, api, seed }) => {
      runOn('desktop');
      const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, TWO));
      const bar = ed.testId('timeline-toolbar');
      await expect(bar).toHaveAttribute('data-fold', /trim|steppers|zoom-slider|labels/);
      await expect.poll(() => bar.evaluate((el) => el.scrollWidth <= el.clientWidth + 1), { message: 'nothing is scrolled out' }).toBe(true);
      const barBox = (await bar.boundingBox())!;
      for (const id of ['select-menu', 'add-menu', 'zoom-out', 'zoom-in', 'snap-toggle', 'clip-actions-open']) {
        const box = (await ed.testId(id).boundingBox())!;
        expect(box.x + box.width, `${id} is inside the bar`).toBeLessThanOrEqual(barBox.x + barBox.width);
      }
      await ed.chooseTrimMode('roll');
      await expect(ed.testId('trim-mode')).toHaveAttribute('data-mode', 'roll');
      await ed.stepper('step-playhead-+10');
      await expect(ed.testId('status-timecode')).toHaveText('00:00:00:10');
    });
  });
}
