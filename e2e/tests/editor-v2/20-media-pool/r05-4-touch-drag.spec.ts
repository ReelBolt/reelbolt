import type { Locator } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';

const ref = (id: string) => `file:${id}`;
const tile = (ed: EditorPage, mediaRef: string): Locator => ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="${mediaRef}"]`).first();

async function openPool(ed: EditorPage): Promise<void> {
  await ed.workspace('media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
  await expect(ed.page.getByTestId('media-clip-tile').first()).toBeVisible();
}

/**
 * R05.4: touch drag of a tile onto the timeline. Playwright has no long-press, so the finger is driven through the
 * DevTools touch protocol (Chromium): touchStart, rest for the long press, touchMove across, touchEnd.
 */
test.describe('R05.4 touch drag (tablet 1024x768, coarse pointer)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  async function finger(ed: EditorPage) {
    const cdp = await ed.page.context().newCDPSession(ed.page);
    const send = (type: 'touchStart' | 'touchMove' | 'touchEnd', x?: number, y?: number) =>
      cdp.send('Input.dispatchTouchEvent', { type, touchPoints: type === 'touchEnd' ? [] : [{ x: x!, y: y! }] });
    return { send };
  }

  test('R05.4 long-press a tile, drag onto track 2 and release: the clip lands there; the lane lights up while over it', async ({ seed, api, freshTimeline, openEditor }) => {
    runOn('desktop');
    const timelineId = await freshTimeline('blank');
    await api.putDocument(seed.projectId, timelineId, (doc) => {
      doc.tracks.splice(1, 0, { id: crypto.randomUUID(), kind: 'video', name: 'V2', height: 64, locked: false, enabled: true, mute: false, solo: false, items: [] } as never);
    });
    const ed = await openEditor(timelineId);
    expect(await ed.page.evaluate(() => window.matchMedia('(pointer: coarse)').matches)).toBe(true);
    const source = tile(ed, ref(seed.files.clipA));
    await expect(source).toBeVisible();
    const doc = (await ed.serverTimeline()).document;
    const trackIndex = doc.tracks.findIndex((t: { name: string }) => t.name === 'V2');
    await source.scrollIntoViewIfNeeded();
    const from = (await source.boundingBox())!;
    const sx = from.x + from.width / 2;
    const sy = from.y + from.height / 2;
    const x = await ed.frameX(40);
    const y = await ed.laneY(trackIndex);
    const { send } = await finger(ed);

    await send('touchStart', sx, sy);
    await ed.page.waitForTimeout(600); // the long press (a real hold, not a state poll)
    await expect(ed.testId('pool-touch-chip')).toBeVisible();
    for (let i = 1; i <= 12; i += 1) await send('touchMove', sx + ((x - sx) * i) / 12, sy + ((y - sy) * i) / 12);
    await expect(ed.testId('pool-drop-lane')).toHaveAttribute('data-allowed', 'true');
    await expect(ed.testId('pool-drop-indicator')).toHaveAttribute('data-track', doc.tracks[trackIndex].id);
    await send('touchEnd');

    const tl = await ed.expectServer((t) => (t.document.tracks[trackIndex].items?.length ?? 0) === 1, 'the clip landed on track 2');
    const clip = tl.document.tracks[trackIndex].items[0] as { name: string; recordIn: number };
    expect(clip.name).toBe('clipA.mp4');
    expect(Math.abs(clip.recordIn - 40)).toBeLessThanOrEqual(2);
    expect(tl.document.tracks[0].items?.length ?? 0, 'track 1 untouched').toBe(0);
    await expect(ed.testId('pool-touch-chip')).toHaveCount(0);
  });

  test('R05.4 a sound over a video lane shows the refusal highlight and releasing writes nothing', async ({ seed, freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('blank'));
    const source = tile(ed, ref(seed.files.tone));
    await expect(source).toBeVisible();
    const before = (await ed.serverTimeline()).revision;
    await source.scrollIntoViewIfNeeded();
    const from = (await source.boundingBox())!;
    const sx = from.x + from.width / 2;
    const sy = from.y + from.height / 2;
    const { send } = await finger(ed);
    await send('touchStart', sx, sy);
    await ed.page.waitForTimeout(600);
    await send('touchMove', await ed.frameX(30), await ed.laneY(0));
    await expect(ed.testId('pool-drop-lane')).toHaveAttribute('data-allowed', 'false');
    await send('touchEnd');
    await expect(ed.testId('pool-drop-lane')).toHaveCount(0);
    expect((await ed.serverTimeline()).revision).toBe(before);
  });

  test('R05.4 a quick swipe over a tile scrolls instead of lifting it, and the Place menu still works', async ({ seed, freshTimeline, openEditor }) => {
    runOn('desktop');
    const ed = await openEditor(await freshTimeline('blank'));
    const source = tile(ed, ref(seed.files.clipB));
    await expect(source).toBeVisible();
    await source.scrollIntoViewIfNeeded();
    const from = (await source.boundingBox())!;
    const { send } = await finger(ed);
    await send('touchStart', from.x + 20, from.y + 20);
    await send('touchMove', from.x + 20, from.y + 80);
    await ed.page.waitForTimeout(600);
    await expect(ed.testId('pool-touch-chip')).toHaveCount(0);
    await send('touchEnd');
    await ed.tap(ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="${ref(seed.files.clipB)}"]`));
    await ed.tap(ed.testId('tile-place-append'));
    await ed.expectServer((t) => JSON.stringify(t.document).includes(seed.files.clipB), 'the Place menu still puts clipB on the timeline');
  });
});
