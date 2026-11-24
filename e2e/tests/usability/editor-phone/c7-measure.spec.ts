import * as fs from 'fs';
import type { Page } from '@playwright/test';
import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * usability-fixes T-005 / finding C7 (phone editor cramped): measures the editor on pixel7 (412x915) and
 * iphone14webkit (390x844). Soft assertions, so one run reports every number. Run with the editor-v2 config
 * against the isolated stack. Writes /tmp/uf-editor-phone/measure-<project>.json and screenshots.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const OUT = process.env.C7_OUT ?? '/tmp/uf-editor-phone';
type R = { x: number; y: number; width: number; height: number; bottom: number };
const rect = (page: Page, id: string): Promise<R | null> =>
  page.evaluate((t) => {
    const el = document.querySelector(`[data-testid="${t}"]`);
    if (!el) return null;
    const r = el.getBoundingClientRect();
    return r.width === 0 ? null : { x: r.x, y: r.y, width: r.width, height: r.height, bottom: r.bottom };
  }, id);

test('C7 phone editor measurements', async ({ freshTimeline, openEditor, api, seed }, info) => {
  fs.mkdirSync(OUT, { recursive: true });
  const name = info.project.name;
  const m: Record<string, unknown> = {};
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  const page = ed.page;
  const vh = page.viewportSize()!.height;
  m.viewport = page.viewportSize();
  await page.screenshot({ path: `${OUT}/${name}-1-timeline.png` });

  // viewer + transport share of height
  const viewer = await rect(page, 'phone-viewer');
  const transport = await rect(page, 'transport-bar');
  m.viewer = viewer; m.transport = transport;
  expect.soft(viewer, 'viewer present').not.toBeNull();
  expect.soft(transport, 'transport present').not.toBeNull();
  if (viewer && transport) {
    const top = Math.min(viewer.y, transport.y), bottom = Math.max(viewer.bottom, transport.bottom);
    const share = (bottom - top) / vh;
    m.viewerTransportShare = share;
    expect.soft(share, `viewer+transport share of height (${(share * 100).toFixed(1)}%)`).toBeLessThanOrEqual(0.42);
    m.transportRowHeight = transport.height;
  }
  const strip = await rect(page, 'phone-strip');
  m.strip = strip;

  // Early preview + dropped-frame counter
  m.earlyPreviewVisible = await ed.testId('early-preview-label').isVisible().catch(() => false);
  m.droppedFramesPresent = (await page.getByTestId('dropped-frames').count()) > 0;
  expect.soft(m.earlyPreviewVisible, 'Early preview label visible').toBe(true);
  expect.soft(m.droppedFramesPresent, 'dropped-frame counter present').toBe(true);

  // toolbar / header / dock / action bar targets and font sizes
  const small = await page.evaluate(() => {
    const out: Array<{ scope: string; name: string; w: number; h: number }> = [];
    for (const scope of ['phone-header', 'phone-dock', 'phone-action-bar', 'transport-bar']) {
      const root = document.querySelector(`[data-testid="${scope}"]`);
      if (!root) { out.push({ scope, name: 'MISSING', w: 0, h: 0 }); continue; }
      for (const el of Array.from(root.querySelectorAll<HTMLElement>('button, a[href], [role="button"], [role="tab"], input'))) {
        const r = el.getBoundingClientRect();
        if (r.width === 0 || r.height === 0 || getComputedStyle(el).visibility === 'hidden') continue;
        if (r.width < 43.5 || r.height < 43.5) out.push({ scope, name: el.getAttribute('data-testid') ?? el.getAttribute('aria-label') ?? (el.textContent ?? '').trim().slice(0, 20), w: Math.round(r.width), h: Math.round(r.height) });
      }
    }
    return out;
  });
  m.targetsUnder44 = small;
  expect.soft(small, 'controls under 44px').toEqual([]);
  const tiny = await page.evaluate(() => {
    const out: Array<{ text: string; px: number }> = [];
    const w = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT);
    for (let n = w.nextNode(); n; n = w.nextNode()) {
      const t = (n.textContent ?? '').trim();
      const el = n.parentElement;
      if (!t || !el || el.closest('canvas, script, style')) continue;
      const r = el.getBoundingClientRect();
      if (r.width === 0 || r.height === 0 || getComputedStyle(el).visibility === 'hidden') continue;
      const px = parseFloat(getComputedStyle(el).fontSize);
      if (px < 11.95) out.push({ text: t.slice(0, 30), px });
    }
    return out;
  });
  m.textUnder12 = tiny;
  expect.soft(tiny, 'text under 12px').toEqual([]);

  // dock labels whole
  const dock = await page.evaluate(() => {
    const root = document.querySelector('[data-testid="phone-dock"]');
    const vw = window.innerWidth;
    const rows: Array<{ id: string; text: string; clipped: boolean; left: number; right: number }> = [];
    root?.querySelectorAll<HTMLElement>('[data-testid^="dock-"]').forEach((el) => {
      const r = el.getBoundingClientRect();
      rows.push({ id: el.getAttribute('data-testid')!, text: (el.textContent ?? '').trim(), clipped: el.scrollWidth > el.clientWidth + 1 || r.right > vw + 1 || r.left < -1, left: Math.round(r.left), right: Math.round(r.right) });
    });
    return rows;
  });
  m.dock = dock;
  expect.soft(dock.filter((d) => d.clipped), 'dock items clipped').toEqual([]);
  // Deliver: header export button / deliver workspace entry, whole
  const exportBtn = await rect(page, 'phone-export');
  m.exportButton = exportBtn;
  m.exportTextClipped = await page.evaluate(() => { const b = document.querySelector('[data-testid="phone-export"]') as HTMLElement | null; if (!b) return null; return Array.from(b.querySelectorAll<HTMLElement>('*')).concat([b]).some((e) => e.scrollWidth > e.clientWidth + 1); });
  expect.soft(m.exportTextClipped, 'Export label text clipped').toBe(false);
  expect.soft(exportBtn && exportBtn.x >= 0 && exportBtn.x + exportBtn.width <= page.viewportSize()!.width, 'Export (Deliver) button whole on screen').toBeTruthy();

  // select a clip -> action bar surfaces; inspector (Edit sheet) usable height
  await ed.selectClipAt(30);
  const bar = await rect(page, 'phone-action-bar');
  m.actionBarAfterSelect = bar;
  await page.screenshot({ path: `${OUT}/${name}-2-clip-selected.png` });
  const sheetFrac = async (id: string) => { const r = await rect(page, id); return r ? r.height / vh : null; };
  await ed.tap(ed.testId('dock-edit'));
  await page.waitForTimeout(500);
  const sheet = await rect(page, 'phone-sheet');
  m.editSheet = sheet;
  m.editSheetHeightShare = sheet ? sheet.height / vh : null;
  expect.soft(sheet, 'edit sheet opens').not.toBeNull();
  if (sheet) expect.soft(sheet.height / vh, `inspector sheet height share (${(sheet.height / vh * 100).toFixed(1)}%)`).toBeGreaterThanOrEqual(0.48);
  m.clipSheetShare = await sheetFrac('clip-sheet');
  await page.screenshot({ path: `${OUT}/${name}-3-edit-sheet.png` });
  await page.keyboard.press('Escape');

  // selecting a clip while the sheet is closed surfaces it: clip sheet or action bar with clip actions
  await ed.selectClipAt(30);
  m.clipSurfaced = {
    actionBarMode: await ed.testId('phone-action-bar').getAttribute('data-mode').catch(() => null),
    clipSheet: await ed.testId('clip-sheet').isVisible().catch(() => false),
  };
  expect.soft(m.clipSurfaced).toMatchObject({ actionBarMode: 'clip' });

  // every timeline gesture reachable from the clip actions (action bar + More)
  const reach: Record<string, boolean> = {};
  for (const id of ['phone-action-split', 'phone-action-duplicate', 'phone-action-delete', 'phone-action-nudge', 'phone-action-more']) {
    reach[id] = await ed.testId(id).isVisible().catch(() => false);
  }
  await ed.tap(ed.testId('phone-action-more'));
  await page.waitForTimeout(300);
  for (const id of ['phone-more-lift', 'phone-more-unlink', 'phone-more-freeze']) reach[id] = await ed.testId(id).isVisible().catch(() => false);
  m.gestureReach = reach;
  await page.screenshot({ path: `${OUT}/${name}-4-more.png` });
  expect.soft(Object.entries(reach).filter(([, v]) => !v).map(([k]) => k), 'clip actions unreachable').toEqual([]);

  fs.writeFileSync(`${OUT}/measure-${name}.json`, JSON.stringify(m, null, 2));
  info.annotations.push({ type: 'measure', description: JSON.stringify(m) });
});
