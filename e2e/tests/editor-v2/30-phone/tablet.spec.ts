import type { Locator, Page } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, countItems, itemNamed, seekAndBlur } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Wave 3 / T-012: the tablet (RM.15, decision D2 "works, not certified"). The `tablet` project is an iPad-like
 * 1024x768 touch device on the desktop layout; a test switches to 820x1180 for portrait. Layout facts are measured
 * in the browser (no horizontal page scroll, the viewer fully visible, every control a finger can hit is >= 44 px)
 * and the journeys (split, trim, delete) are driven by touch and read back from the SAVED document.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('tablet'); });

const LANDSCAPE = { width: 1024, height: 768 };
const PORTRAIT = { width: 820, height: 1180 };

async function noPageScroll(page: Page): Promise<void> {
  const over = await page.evaluate(() => document.documentElement.scrollWidth - window.innerWidth);
  expect(over, 'the page never scrolls sideways').toBeLessThanOrEqual(0);
}

async function viewerFullyVisible(ed: EditorPage): Promise<void> {
  const size = ed.page.viewportSize()!;
  const box = (await ed.viewer.boundingBox())!;
  expect(box, 'the viewer is on screen').toBeTruthy();
  expect(box.x).toBeGreaterThanOrEqual(-0.5);
  expect(box.y).toBeGreaterThanOrEqual(-0.5);
  expect(box.x + box.width, 'the viewer is not clipped on the right').toBeLessThanOrEqual(size.width + 0.5);
  expect(box.y + box.height, 'the viewer is not clipped at the bottom').toBeLessThanOrEqual(size.height + 0.5);
  expect(box.width, 'the picture is not squeezed to a sliver').toBeGreaterThan(180);
}

/** Every visible button or link inside `scope` that is smaller than a finger (44 px) on its short side, described. */
async function smallTargets(scope: Locator): Promise<string[]> {
  return scope.evaluate((root) => {
    const out: string[] = [];
    for (const el of Array.from(root.querySelectorAll<HTMLElement>('button, a[href], [role="button"], input:not([type="hidden"])'))) {
      // A native checkbox or radio is hit through its label (Mantine draws the box and the label is the target).
      const hit = el instanceof HTMLInputElement && (el.type === 'checkbox' || el.type === 'radio') ? (el.closest('.mantine-Checkbox-root, .mantine-Radio-root, .mantine-Switch-root') ?? el.closest('label') ?? el) : el;
      const r = hit.getBoundingClientRect();
      if (r.width === 0 || r.height === 0 || getComputedStyle(el).visibility === 'hidden') continue;
      // Number-field steppers are aria-hidden and out of the tab order; the field itself is the target.
      if (el.closest('[aria-hidden="true"]')) continue;
      if (r.height < 43.5 || r.width < 43.5) out.push(`${el.getAttribute('data-testid') ?? el.getAttribute('aria-label') ?? el.textContent?.trim().slice(0, 20)} ${Math.round(r.width)}x${Math.round(r.height)}`);
    }
    return out;
  });
}

test.describe('RM.15 tablet layout', () => {
  test('RM.15 landscape 1024x768: nothing scrolls sideways, the viewer is fully visible, the rail is 64 px', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.page.setViewportSize(LANDSCAPE);
    await noPageScroll(ed.page);
    await viewerFullyVisible(ed);
    const rail = (await ed.testId('workspace-bar').boundingBox())!;
    expect(Math.round(rail.width), 'the workspace rail is 64 px wide so "Compose" fits').toBe(64);
    await expect(ed.page.getByTestId('editor-workspace')).toHaveAttribute('data-layout', 'tablet');
    const touch = await ed.page.evaluate(() => document.querySelector('[data-editor-surface]')?.getAttribute('data-ed-touch'));
    expect(touch, 'a coarse pointer turns on the 44 px density').toBe('true');
  });

  test('RM.15 portrait 820x1180: the left panel stays inline; the Inspector is docked on touch, a drawer with a fine pointer', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.page.setViewportSize(PORTRAIT);
    const left = (await ed.testId('panel-left').boundingBox())!;
    const coarse = await ed.page.evaluate(() => window.matchMedia('(pointer: coarse)').matches);
    await noPageScroll(ed.page);
    await viewerFullyVisible(ed);
    if (coarse) {
      // usability-fixes C8: on a touch tablet the Inspector stays docked beside the viewer; the left panel gives up some width.
      await expect(ed.testId('panel-right')).toBeVisible();
      await expect(ed.page.getByTestId('inspector-toggle')).toHaveCount(0);
      expect(left.width, 'the left panel stays inline').toBeGreaterThanOrEqual(239.5);
      expect((await ed.testId('panel-viewer').boundingBox())!.width, 'the viewer keeps room').toBeGreaterThan(280);
      return;
    }
    // A desktop window of the same width keeps today's layout: left panel inline, Inspector in a drawer.
    expect(left.width, 'the left panel keeps at least 300 px').toBeGreaterThanOrEqual(299.5);
    await expect(ed.testId('inspector-toggle')).toBeVisible();
    const toggle = (await ed.testId('inspector-toggle').boundingBox())!;
    expect(toggle.height, 'the inspector button is finger-sized').toBeGreaterThanOrEqual(43.5);
    await ed.tap(ed.testId('inspector-toggle'));
    await expect(ed.testId('panel-right')).toBeVisible();
    await ed.tap(ed.testId('inspector-toggle'));
    await expect(ed.page.getByTestId('panel-right')).toHaveCount(0);
  });

  test('RM.15 every control in the header, the toolbar and the inspector is at least 44 px', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.page.setViewportSize(LANDSCAPE);
    await ed.selectClipAt(30);
    // The editor's own header (the app navigation bar above it is the site chrome, outside the editor surface).
    const header = await smallTargets(ed.page.locator('header', { has: ed.testId('header-more') }).first());
    expect(header, 'header controls under 44 px').toEqual([]);
    const toolbar = await smallTargets(ed.testId('timeline-toolbar'));
    expect(toolbar, 'timeline toolbar controls under 44 px').toEqual([]);
    await ed.openDrawer();
    const inspector = await smallTargets(ed.testId('panel-right'));
    expect(inspector, 'inspector controls under 44 px').toEqual([]);
    const rail = await smallTargets(ed.testId('workspace-bar'));
    expect(rail, 'rail items under 44 px').toEqual([]);
  });

  test('RM.15 the toolbar wraps instead of clipping: every tool is on screen', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.page.setViewportSize(PORTRAIT);
    const size = ed.page.viewportSize()!;
    for (const id of ['undo', 'redo', 'blade-at-playhead', 'snap-toggle', 'zoom-out', 'zoom-in', 'zoom-fit', 'clip-actions-open', 'select-menu', 'add-menu']) {
      const box = await ed.testId(id).boundingBox();
      expect(box, `${id} is drawn`).toBeTruthy();
      expect(box!.x + box!.width, `${id} is inside the screen`).toBeLessThanOrEqual(size.width + 0.5);
      expect(box!.x).toBeGreaterThanOrEqual(-0.5);
    }
  });

  test('RM.15 the header says plainly that the tablet is not certified yet', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.tap(ed.testId('header-more'));
    await expect(ed.testId('tablet-note')).toHaveText('Tablet: works, not certified yet');
    await expect(ed.page.getByText('Early preview').first()).toBeVisible();
  });
});

test.describe('RM.15 tablet journeys by touch', () => {
  test('RM.15 split by touch is stored in the document', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await seekAndBlur(ed, 30);
    await ed.selectClipAt(15);
    await ed.tap(ed.testId('blade-at-playhead'));
    const tl = await ed.expectServer((t) => countItems(t) === 3, 'clip A was split in two in the stored document');
    const spans = tl.document.tracks[0].items.map((i) => [i.recordIn, i.recordOut]);
    expect(spans).toContainEqual([0, 30]);
    expect(spans).toContainEqual([30, 60]);
  });

  test('RM.15 trim by touch drag is stored in the document', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    const handle = (await ed.testId('trim-handle-out').boundingBox())!;
    const x = handle.x + handle.width / 2;
    const y = handle.y + handle.height / 2;
    await ed.touchDrag(x, y, x - 40, y);
    await ed.expectServer((t) => (itemNamed(t, 'clipA.mp4')?.recordOut ?? 60) < 60, 'the trim handle drag shortened clip A in the stored document');
  });

  test('RM.15 delete from the clip action menu button is stored in the document', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await ed.tap(ed.testId('clip-actions-open'));
    await ed.tap(ed.testId('clip-action-delete'));
    const tl = await ed.expectServer((t) => countItems(t) === 1, 'clip A is gone from the stored document');
    expect(itemNamed(tl, 'clipB.mp4')).toBeTruthy();
  });

  test('RM.15 portrait: delete from the action menu is stored and survives a reload', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.page.setViewportSize(PORTRAIT);
    await ed.selectClipAt(30);
    await ed.tap(ed.testId('clip-actions-open'));
    await ed.tap(ed.testId('clip-action-delete'));
    await ed.expectServer((t) => countItems(t) === 1, 'the clip is deleted from the portrait layout too');
    await ed.reload();
    await ed.expectServer((t) => countItems(t) === 1, 'a reload still shows one clip');
  });
});
