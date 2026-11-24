import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, countItems, itemNamed, seekAndBlur } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';

/**
 * Wave 3 / T-005: the Add media sheet (RM.8; x1 acceptance criterion 6). The dock's Media item opens it on a phone.
 * No drag: every step is a tap or a chosen file, and every outcome is read back from the server (the saved document,
 * the media pool, the bin items), never from the control.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const ref = (id: string) => `file:${id}`;
type PoolItem = { mediaRef: string; fileName: string; sizeBytes: number; bins: Array<{ binId: string; itemId: string }> };
type Bin = { id: string; name: string };

async function openSheet(ed: EditorPage): Promise<void> {
  await ed.workspace('media');
  await expect(ed.testId('phone-sheet')).toHaveAttribute('aria-label', 'Add media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
  await expect(ed.page.getByTestId('media-clip-tile').first()).toBeVisible({ timeout: 30_000 });
}
const openTileMenu = async (ed: EditorPage, mediaRef: string): Promise<void> => {
  await ed.tap(ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="${mediaRef}"]`).first());
};
const pool = (ed: EditorPage) => ed.seed.api.json<PoolItem[]>('GET', `/api/v1/projects/${ed.seed.projectId}/media`);
const binsUrl = (projectId: string) => `/api/v1/projects/${projectId}/media/bins`;

test.describe('RM.8 Add media', () => {
  test('RM.8 a file chosen with the picker is uploaded, listed with a readiness chip, and has bytes on the server', async ({ freshTimeline, openEditor, seed, api }, testInfo) => {
    const ed = await openEditor(await freshTimeline('blank'));
    await openSheet(ed);
    const fileName = `add-media-${Date.now()}-${testInfo.project.name}.png`;
    const tmp = path.join(os.tmpdir(), fileName);
    fs.copyFileSync(path.join(ensureMedia(), 'still.png'), tmp);
    try {
      const input = ed.testId('add-media-input');
      await expect(input).toHaveAttribute('accept', 'video/*,audio/*,image/*');
      await expect(input).toHaveAttribute('multiple', '');
      await input.setInputFiles(tmp);
      await expect(ed.testId('add-media-upload-row')).toBeVisible();
      await expect.poll(async () => (await api.listFiles(seed.projectId)).some((f) => f.originalFileName === fileName), { message: 'the file is in the project', timeout: 60_000 }).toBe(true);
      await expect(ed.testId('add-media-upload-row')).toHaveAttribute('data-status', 'done', { timeout: 30_000 });
      await expect(ed.testId('add-media-upload-text')).toHaveText('Added');
      await expect.poll(async () => (await pool(ed)).find((m) => m.fileName === fileName)?.sizeBytes ?? 0, { message: 'the pool lists the file with bytes', timeout: 60_000 }).toBeGreaterThan(0);
      const tile = ed.page.getByTestId('add-media-tile').filter({ hasText: fileName });
      await expect(tile).toBeVisible({ timeout: 30_000 });
      await expect(tile.getByTestId('add-media-chip')).toHaveText(/^(Ready|Processing preview|Preview not ready - you can still add it)$/);
    } finally {
      fs.rmSync(tmp, { force: true });
    }
  });

  test('RM.8 Insert at playhead stores the clip at the playhead, closes the sheet to a peek, and Undo takes it back', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await seekAndBlur(ed, 60);
    await openSheet(ed);
    await openTileMenu(ed, ref(seed.files.vertical));
    await ed.tap(ed.testId('tile-place-insert'));
    const tl = await ed.expectServer((t) => countItems(t) === 3, 'the picked clip is in the saved document');
    const placed = tl.document.tracks[0].items.find((i) => JSON.stringify(i).includes(seed.files.vertical))!;
    expect(placed.recordIn, 'it sits at the playhead frame').toBe(60);
    expect(itemNamed(tl, 'clipB.mp4')!.recordIn, 'what followed was pushed later').toBeGreaterThan(60);
    await expect(ed.testId('phone-sheet')).toHaveAttribute('data-detent', 'peek');
    await expect(ed.testId('undo-snackbar')).toContainText('Added');
    await ed.tap(ed.testId('undo-snackbar-undo'));
    const back = await ed.expectServer((t) => countItems(t) === 2, 'one Undo removed the whole insert');
    expect(itemNamed(back, 'clipB.mp4')!.recordIn).toBe(60);
  });

  test('RM.8 Add to end from the tile menu puts the clip after the last one', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await openSheet(ed);
    await openTileMenu(ed, ref(seed.files.vertical));
    await ed.tap(ed.testId('tile-place-append'));
    const tl = await ed.expectServer((t) => countItems(t) === 3, 'the clip was added');
    const placed = tl.document.tracks[0].items.find((i) => JSON.stringify(i).includes(seed.files.vertical))!;
    expect(placed.recordIn, 'it starts where clip B ends').toBe(120);
  });

  test('RM.8 Add to bin from the tile menu stores the pin, and a new bin can be made from a name', async ({ freshTimeline, openEditor, seed, api }) => {
    const stamp = Date.now();
    const bin = await api.json<Bin>('POST', binsUrl(seed.projectId), { name: `Pin-${stamp}` }, [200, 201]);
    const made = `Made-${stamp}`;
    try {
      const ed = await openEditor(await freshTimeline('blank'));
      await openSheet(ed);
      await openTileMenu(ed, ref(seed.files.still));
      await ed.tap(ed.testId(`add-to-bin-${bin.id}`));
      await expect(ed.testId('add-media-message')).toHaveText(`Added to "Pin-${stamp}".`);
      await expect.poll(async () => (await pool(ed)).find((m) => m.mediaRef === ref(seed.files.still))?.bins.some((b) => b.binId === bin.id), { message: 'the pool shows the pin' }).toBe(true);
      const items = await api.json<Array<{ mediaRef: string }>>('GET', `${binsUrl(seed.projectId)}/${bin.id}/items`);
      expect(items.map((i) => i.mediaRef)).toEqual([ref(seed.files.still)]);

      await ed.tap(ed.testId('add-media-new-bin'));
      await ed.testId('add-media-bin-name').fill(made);
      await ed.tap(ed.testId('add-media-bin-create'));
      await expect.poll(async () => (await api.json<Bin[]>('GET', binsUrl(seed.projectId))).some((b) => b.name === made), { message: 'the new bin is stored' }).toBe(true);
    } finally {
      for (const b of await api.json<Bin[]>('GET', binsUrl(seed.projectId))) {
        if (b.id === bin.id || b.name === made) await api.json('DELETE', `${binsUrl(seed.projectId)}/${b.id}`, undefined, [200, 204, 404]);
      }
    }
  });

  test('RM.8 a smart bin refuses "Add to bin" in plain words and nothing is stored', async ({ freshTimeline, openEditor, seed, api }) => {
    const name = `Smart-${Date.now()}`;
    const bin = await api.json<Bin>('POST', binsUrl(seed.projectId), { name, smartFilter: { favourite: true } }, [200, 201]);
    try {
      const ed = await openEditor(await freshTimeline('blank'));
      await openSheet(ed);
      await openTileMenu(ed, ref(seed.files.still));
      await ed.tap(ed.testId(`add-to-bin-${bin.id}`));
      await expect(ed.testId('add-media-problem')).toHaveText(`"${name}" is a smart bin: it shows every source matching its filter. Tag the clip instead, or add it to an ordinary bin.`);
      const items = await api.json<unknown>('GET', `${binsUrl(seed.projectId)}/${bin.id}/items`).catch(() => []);
      expect(JSON.stringify(items)).not.toContain(seed.files.still);
    } finally {
      await api.json('DELETE', `${binsUrl(seed.projectId)}/${bin.id}`, undefined, [200, 204, 404]);
    }
  });

  test('RM.8 dragging a finger across a tile shows where you are in the clip (touch scrub keeps working in the sheet)', async ({ freshTimeline, openEditor }) => {
    test.skip(test.info().project.name === 'iphone14webkit', 'WebKit cannot synthesize touch drags (audit D8)');
    const ed = await openEditor(await freshTimeline('blank'));
    await openSheet(ed);
    const picture = ed.page.getByTestId('media-clip-tile').first();
    await picture.scrollIntoViewIfNeeded();
    await expect(picture).toHaveCSS('touch-action', 'pan-y');
    const box = (await picture.boundingBox())!;
    const y = box.y + box.height / 2;
    const cdp = await ed.page.context().newCDPSession(ed.page);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x: box.x + box.width * 0.15, y, id: 1 }] });
    for (let i = 1; i <= 10; i++) {
      await cdp.send('Input.dispatchTouchEvent', { type: 'touchMove', touchPoints: [{ x: box.x + box.width * (0.15 + 0.07 * i), y: y + 1, id: 1 }] });
      await ed.page.waitForTimeout(16);
    }
    await expect(ed.page.getByTestId('media-tile-scrub-label'), 'a position label while the finger is down').toBeVisible();
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await expect(ed.page.getByTestId('media-tile-scrub-label')).toHaveCount(0);
    await expect(ed.page.getByTestId('filmstrip-strip')).not.toContainText(/hover/i);
  });

  test('RM.8 the sheet controls are fingertip sized', async ({ freshTimeline, openEditor }) => {
    const ed = await openEditor(await freshTimeline('blank'));
    await openSheet(ed);
    for (const id of ['add-media-pick', 'add-media-capture', 'add-media-bin-all', 'add-media-new-bin']) {
      const box = (await ed.testId(id).boundingBox())!;
      expect(box.height, `${id} height`).toBeGreaterThanOrEqual(43.5);
      expect(box.width, `${id} width`).toBeGreaterThanOrEqual(43.5);
    }
    const menu = (await ed.page.getByTestId('media-tile-menu').first().boundingBox())!;
    expect(menu.height).toBeGreaterThanOrEqual(43.5);
  });
});
