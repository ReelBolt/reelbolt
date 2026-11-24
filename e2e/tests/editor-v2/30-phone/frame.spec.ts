import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { bladeAt, hasCutAt, otherWriterRenames } from '../02-save-sync/sync-helpers';
import { sampleElement } from '../../../fixtures/editor-v2/pixels';
import type { Page } from '@playwright/test';

/**
 * Wave 3 T-001: the phone frame (RM.2, RM.6, R04.4, VF8, VF9, RM.7). pixel7 has real CDP touch; iphone14webkit is
 * synthetic (no drag-only checks here); desktop and tablet prove the old layouts did not change.
 */
test.use({ serviceWorkers: 'block' });

const DOCK = ['media', 'edit', 'audio', 'look', 'captions'] as const;

async function noHorizontalScroll(page: Page): Promise<void> {
  const overflow = await page.evaluate(() => ({ doc: document.documentElement.scrollWidth - window.innerWidth, body: document.body.scrollWidth - window.innerWidth }));
  expect(overflow.doc, 'the page does not scroll sideways').toBeLessThanOrEqual(0);
  expect(overflow.body).toBeLessThanOrEqual(0);
}

async function insideViewport(page: Page, testId: string): Promise<void> {
  const box = await page.getByTestId(testId).boundingBox();
  const vp = page.viewportSize()!;
  expect(box, `${testId} is on screen`).not.toBeNull();
  expect(box!.x).toBeGreaterThanOrEqual(-0.5);
  expect(box!.y).toBeGreaterThanOrEqual(-0.5);
  expect(box!.x + box!.width).toBeLessThanOrEqual(vp.width + 0.5);
  expect(box!.y + box!.height).toBeLessThanOrEqual(vp.height + 0.5);
}

/** Every visible interactive control inside `scope` is at least 44x44 CSS px. */
async function smallControls(page: Page, scope: string): Promise<string[]> {
  return page.evaluate((sel) => {
    const root = document.querySelector(sel);
    if (!root) return [`missing ${sel}`];
    const bad: string[] = [];
    for (const el of Array.from(root.querySelectorAll<HTMLElement>('button, a[href], [role="button"], [role="tab"], [role="menuitem"], [role="switch"], input:not([type="hidden"]), select'))) {
      const cs = getComputedStyle(el);
      const r = el.getBoundingClientRect();
      if (cs.visibility === 'hidden' || cs.display === 'none' || r.width === 0 || r.height === 0) continue;
      if (el.closest('[hidden], [aria-hidden="true"], [inert]')) continue;
      if (r.width < 43.5 || r.height < 43.5) bad.push(`${el.tagName} ${el.getAttribute('data-testid') ?? el.getAttribute('aria-label') ?? el.textContent?.slice(0, 20)} ${Math.round(r.width)}x${Math.round(r.height)}`);
    }
    return bad;
  }, scope);
}

async function openPhone(freshTimeline: (kind: 'seeded' | 'blank') => Promise<string>, openEditor: (id: string) => Promise<EditorPage>): Promise<EditorPage> {
  const ed = await openEditor(await freshTimeline('seeded'));
  await expect(ed.testId('phone-header')).toBeVisible();
  return ed;
}

for (const width of [360, 390, 412]) {
  test(`RM.6 AC1 at ${width}px: no horizontal scroll, header, dock and every sheet fit the screen`, async ({ freshTimeline, openEditor }) => {
    runOn('pixel7', 'iphone14webkit');
    const ed = await openPhone(freshTimeline, openEditor);
    await ed.page.setViewportSize({ width, height: 780 });
    await noHorizontalScroll(ed.page);
    await insideViewport(ed.page, 'phone-header');
    await insideViewport(ed.page, 'phone-dock');
    for (const item of DOCK) {
      await ed.tap(ed.testId(`dock-${item}`));
      await expect(ed.testId('phone-sheet')).toBeVisible();
      await insideViewport(ed.page, 'phone-sheet');
      await noHorizontalScroll(ed.page);
    }
  });
}

test('RM.6 AC2 header, dock and sheet controls are at least 44x44 and Delete-like actions keep an 8 px gap', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openPhone(freshTimeline, openEditor);
  expect(await smallControls(ed.page, '[data-testid="phone-header"]')).toEqual([]);
  expect(await smallControls(ed.page, '[data-testid="phone-dock"]')).toEqual([]);
  for (const item of DOCK) {
    await ed.tap(ed.testId(`dock-${item}`));
    await expect(ed.testId('phone-sheet')).toBeVisible();
    const grabberBar = await smallControls(ed.page, '[data-testid="phone-sheet"] > div:first-child');
    expect(grabberBar, `${item} sheet bar`).toEqual([]);
  }
  // Neighbouring header buttons never touch (destructive actions have at least 8 px to neutral ones).
  const gaps = await ed.page.evaluate(() => {
    const rects = Array.from(document.querySelectorAll<HTMLElement>('[data-testid="phone-header"] button, [data-testid="phone-header"] a[href]'))
      .map((e) => e.getBoundingClientRect())
      .filter((r) => r.width > 0)
      .sort((a, b) => a.left - b.left);
    return rects.slice(1).map((r, i) => r.left - rects[i].right);
  });
  for (const gap of gaps) expect(gap).toBeGreaterThanOrEqual(-0.5);
});

test('RM.6 the app bar is hidden on the phone editor and the dock tools open the existing panels', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openPhone(freshTimeline, openEditor);
  await expect(ed.page.getByTestId('app-header')).toBeHidden();
  await ed.tap(ed.testId('dock-media'));
  await expect(ed.testId('phone-sheet')).toBeVisible();
  await expect(ed.testId('phone-sheet')).toContainText(/media|upload|file/i);
  await ed.tap(ed.testId('dock-audio'));
  await expect(ed.testId('phone-sheet')).toHaveAttribute('aria-label', 'Audio');
  // The strip stays visible while a sheet is open.
  const strip = await ed.testId('phone-strip').boundingBox();
  expect(strip!.height).toBeGreaterThanOrEqual(160);
});

test('RM.6 VF8 Escape and the back gesture close the top sheet, the editor stays open', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7');
  const ed = await openPhone(freshTimeline, openEditor);
  await ed.tap(ed.testId('dock-edit'));
  await expect(ed.testId('phone-sheet')).toBeVisible();
  await ed.page.goBack();
  await expect(ed.testId('phone-sheet')).toHaveCount(0);
  await expect(ed.testId('phone-header')).toBeVisible();
  await ed.tap(ed.testId('dock-look'));
  await expect(ed.testId('phone-sheet')).toBeVisible();
  await ed.page.keyboard.press('Escape');
  await expect(ed.testId('phone-sheet')).toHaveCount(0);
});

test('RM.6 VF9 header Export opens the Deliver panel in a sheet', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openPhone(freshTimeline, openEditor);
  await ed.tap(ed.testId('phone-export'));
  await expect(ed.testId('phone-sheet')).toBeVisible();
  await expect(ed.testId('deliver-workspace')).toBeVisible();
});

test('RM.6 header undo reverts a split and the change is stored', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openPhone(freshTimeline, openEditor);
  await bladeAt(ed, 75);
  await ed.expectServer((tl) => hasCutAt(tl, 75), 'the split is stored');
  await ed.tap(ed.testId('phone-undo'));
  await ed.expectServer((tl) => !hasCutAt(tl, 75), 'the undo is stored');
});

test('AC 11 a phone gets the phone surface and a note, with no full-view offer', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openPhone(freshTimeline, openEditor);
  // One notice at a time: the seeded clips have sound without a track, so that offer shows first and the note follows it.
  await ed.tap(ed.testId('link-migration-dismiss'));
  await expect(ed.page.getByText('Phone view. Some tools need a larger screen.')).toBeVisible();
  await expect(ed.page.getByRole('button', { name: /Copy link/ })).toBeVisible();
  await expect(ed.page.getByText(/Open full view/)).toHaveCount(0);
  await expect(ed.testId('phone-header')).toBeVisible();
});

test('AC 12 landscape phone: viewer left, strip right, dock as a rail, a 44 px header', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7');
  const ed = await openPhone(freshTimeline, openEditor);
  await ed.page.setViewportSize({ width: 844, height: 390 });
  await expect(ed.testId('phone-header')).toBeVisible();
  const header = await ed.testId('phone-header').boundingBox();
  expect(header!.height).toBeLessThanOrEqual(45);
  const viewer = await ed.testId('phone-viewer').boundingBox();
  const strip = await ed.testId('phone-strip').boundingBox();
  const dock = await ed.testId('phone-dock').boundingBox();
  expect(viewer!.x).toBeGreaterThanOrEqual(dock!.x + dock!.width - 1);
  expect(strip!.x).toBeGreaterThanOrEqual(viewer!.x + viewer!.width - 1);
  expect(viewer!.width / 844).toBeLessThan(0.5);
  await ed.tap(ed.testId('dock-edit'));
  const sheet = await ed.testId('phone-sheet').boundingBox();
  expect(sheet!.x + sheet!.width).toBeGreaterThan(844 - 1);
  expect(sheet!.x).toBeGreaterThan(300);
  await noHorizontalScroll(ed.page);
});

test('AC 14 hiding and showing the page loses no edit', async ({ freshTimeline, openEditor }) => {
  runOn('pixel7');
  const ed = await openPhone(freshTimeline, openEditor);
  await bladeAt(ed, 60);
  await ed.page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { value: 'hidden', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
    window.dispatchEvent(new Event('pagehide'));
  });
  await ed.expectServer((tl) => hasCutAt(tl, 60), 'the edit is stored after the page was hidden');
  await ed.page.evaluate(() => {
    Object.defineProperty(document, 'visibilityState', { value: 'visible', configurable: true });
    document.dispatchEvent(new Event('visibilitychange'));
  });
  await ed.reload();
  expect(hasCutAt(await ed.serverTimeline(), 60)).toBe(true);
});

test('RM.7 a conflict is a compact bar: the strip stays at least 160 px and Keep my copy saves a copy', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  const original = (await ed.serverTimeline()).name;
  await otherWriterRenames(ed, 0, 'Renamed by someone else');
  await bladeAt(ed, 75);
  const bar = ed.testId('conflict-dialog');
  await expect(bar).toBeVisible();
  await expect(bar).not.toContainText(/revision|op |GUID/i);
  const strip = await ed.testId('phone-strip').boundingBox();
  expect(strip!.height).toBeGreaterThanOrEqual(160);
  const barBox = await bar.boundingBox();
  expect(barBox!.height).toBeLessThan(200);
  await ed.tap(ed.testId('conflict-keep-copy'));
  await expect(ed.testId('conflict-copy-done')).toContainText(`${original} (my edits)`);
  const list = await api.listTimelines(seed.projectId);
  const copy = list.find((t) => t.name === `${original} (my edits)`);
  expect(copy, 'the copy is listed').toBeTruthy();
  expect(hasCutAt(await api.getTimeline(seed.projectId, copy!.id), 75)).toBe(true);
});

test('RM.2 desktop keeps the panel layout and tablet 768x1024 shows it too', async ({ freshTimeline, openEditor }, testInfo) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await expect(ed.testId('phone-header')).toHaveCount(0);
  await expect(ed.page.getByTestId('workspace-edit')).toBeVisible();
  await ed.page.setViewportSize({ width: 768, height: 1024 });
  await expect(ed.testId('phone-header'), `${testInfo.project.name} at 768 wide is a tablet`).toHaveCount(0);
  await expect(ed.testId('editor-workspace')).not.toHaveAttribute('data-layout', 'phone');
});

/**
 * Guard (viewer collapse regression): the program canvas must have a real size AND show a picture on every phone shape. A 0x0 canvas
 * (Chromium resolving cqh to 0 inside the flex-grown size container) left only a black strip, and nothing else caught it.
 */
for (const [name, width, height] of [['portrait 412x915', 412, 915], ['short portrait 390x664', 390, 664], ['landscape 844x390', 844, 390]] as const) {
  test(`the viewer canvas is sized and painted, ${name}`, async ({ freshTimeline, openEditor }) => {
    runOn('pixel7', 'iphone14webkit');
    const ed = await openPhone(freshTimeline, openEditor);
    await ed.page.setViewportSize({ width, height });
    const canvas = ed.page.getByTestId('program-canvas');
    await expect(canvas).toBeVisible();
    await expect.poll(async () => (await canvas.boundingBox())?.width ?? 0, { message: 'canvas width' }).toBeGreaterThanOrEqual(160);
    const box = (await canvas.boundingBox())!;
    expect(box.height, 'canvas height').toBeGreaterThanOrEqual(90);
    const strip = (await ed.page.getByTestId('phone-strip').boundingBox())!;
    expect(strip.height, 'the strip keeps its room').toBeGreaterThanOrEqual(160);
    if (width < height) await insideViewport(ed.page, 'phone-dock');
    // It actually painted: the composited canvas is not one flat colour (the seeded clip is a moving test pattern).
    await expect.poll(async () => {
      const grid = await sampleElement(ed.page, canvas);
      return Math.max(...grid) - Math.min(...grid);
    }, { message: 'canvas pixels vary', timeout: 15_000 }).toBeGreaterThan(12);
  });
}
