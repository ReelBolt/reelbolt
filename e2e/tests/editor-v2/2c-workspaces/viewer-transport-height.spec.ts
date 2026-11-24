import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import type { Page } from '@playwright/test';
import type { EditorPage } from '../../../fixtures/editor-v2/editor-page';

/**
 * The Source transport fits the Record transport's height, so the picture box is the same size in both modes
 * and Record never gives up picture height for Source's controls. Measured with a source loaded and marks set
 * (the longest the Source row gets), on Edit, at the small sizes where every pixel of the picture counts.
 */
const SIZES = [
  { width: 1024, height: 768 },
  { width: 1280, height: 720 },
  { width: 1440, height: 900 },
];

async function measure(page: Page) {
  return page.evaluate(() => {
    const box = (id: string) => {
      const r = document.querySelector(`[data-testid="${id}"]`)?.getBoundingClientRect();
      return r ? { w: Math.round(r.width * 10) / 10, h: Math.round(r.height * 10) / 10, y: Math.round(r.y * 10) / 10 } : null;
    };
    const source = document.querySelector<HTMLElement>('[data-testid="source-monitor-transport"]');
    return {
      program: box('program-frame'),
      source: box('source-monitor-frame'),
      slot: box('viewer-transport-slot'),
      sourceTransport: source ? { client: source.clientHeight, scroll: source.scrollHeight, clientW: source.clientWidth, scrollW: source.scrollWidth } : null,
    };
  });
}

async function checkEverySize(ed: EditorPage, clipA: string, sizes: typeof SIZES) {
  await ed.workspace('cut');
  await ed.tap(ed.testId(`cut-watch-file:${clipA}`));
  await expect(ed.testId('source-monitor-video')).toBeVisible();
  await expect.poll(() => ed.testId('source-monitor-mark-in').isEnabled(), { timeout: 15_000 }).toBe(true);
  await ed.tap(ed.testId('source-monitor-mark-in'));
  await ed.tap(ed.testId('source-monitor-mark-out'));
  await ed.workspace('edit');
  for (const size of sizes) {
    await ed.page.setViewportSize(size);
    await ed.tap(ed.testId('viewer-mode-record'));
    await expect(ed.testId('viewer-mode-switch')).toHaveAttribute('data-mode', 'record');
    await ed.page.waitForTimeout(300);
    const record = await measure(ed.page);
    await ed.tap(ed.testId('viewer-mode-source'));
    await expect(ed.testId('source-monitor-frame')).toBeVisible();
    await ed.page.waitForTimeout(300);
    const source = await measure(ed.page);
    console.log(`viewer-size ${size.width}x${size.height} ${test.info().project.name} record=${JSON.stringify(record.program)} slot=${JSON.stringify(record.slot)} source=${JSON.stringify(source.source)} sourceProgram=${JSON.stringify(source.program)} slotS=${JSON.stringify(source.slot)} sourceTransport=${JSON.stringify(source.sourceTransport)}`);
    expect(source.source?.w, `${size.width}x${size.height}: same width`).toBe(record.program?.w);
    expect(source.source?.h, `${size.width}x${size.height}: same height`).toBe(record.program?.h);
    expect(source.slot?.h, `${size.width}x${size.height}: the transport slot does not change`).toBe(record.slot?.h);
    const t = source.sourceTransport!;
    expect(t.scroll, `${size.width}x${size.height}: nothing in the Source transport is cut off below`).toBeLessThanOrEqual(t.client + 1);
    expect(t.scrollW, `${size.width}x${size.height}: nothing in the Source transport is cut off sideways`).toBeLessThanOrEqual(t.clientW + 1);
  }
}

test('the picture box is the same size in Source and Record, and Source fits Record\'s transport height', async ({ freshTimeline, openEditor, seed }) => {
  test.skip(test.info().project.name !== 'desktop', 'Measured on the desktop project at each size; the phone has no Source monitor');
  const ed = await openEditor(await freshTimeline('seeded'));
  await checkEverySize(ed, seed.files.clipA, SIZES);
});

test.describe('touch tablet 1024x768', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });
  test('44 px targets: Source still fits Record\'s transport height', async ({ freshTimeline, openEditor, seed }) => {
    test.skip(test.info().project.name !== 'desktop', 'The touch context is set here, as J1 tablet does');
    const ed = await openEditor(await freshTimeline('seeded'));
    await checkEverySize(ed, seed.files.clipA, [SIZES[0]]);
  });
});
