import * as fs from 'fs';
import * as os from 'os';
import * as path from 'path';
import type { Locator } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { ensureMedia } from '../../../fixtures/editor-v2/seed';

/**
 * R05.2 - R05.8 (initiative editor-v2-wave2, T-008): the media pool. Every test reads the STORED state (annotations,
 * bins, timeline document, project files), not just the control. Names are unique per test so the shared seed
 * project stays usable by the other specs.
 */

const ref = (id: string) => `file:${id}`;
const tile = (ed: EditorPage, mediaRef: string): Locator => ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="${mediaRef}"]`).first();
const tileRefs = (ed: EditorPage) => ed.page.getByTestId('media-clip-tile').evaluateAll((els) => els.map((e) => e.getAttribute('data-media-ref') ?? ''));

/** Opens the Media workspace (the wide pool) and waits for it to list tiles. */
async function openPool(ed: EditorPage): Promise<void> {
  await ed.workspace('media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
  await expect(ed.page.getByTestId('media-clip-tile').first()).toBeVisible();
}

type Bin = { id: string; name: string; parentBinId: string | null; smartFilter?: { kinds?: string[] } | null };
const bins = (ed: EditorPage) => ed.seed.api.json<Bin[]>('GET', `/api/v1/projects/${ed.seed.projectId}/media/bins`);
type Annotation = { mediaRef: string; favourite: boolean; flag: boolean };
const annotations = (ed: EditorPage) => ed.seed.api.json<Annotation[]>('GET', `/api/v1/projects/${ed.seed.projectId}/media/annotations`);

async function openBinMenu(ed: EditorPage, name: string): Promise<void> {
  await ed.tap(ed.page.getByRole('button', { name: `Actions for ${name}` }));
}

test('R05.2 search, sort and the kind filter change which tiles are shown', async ({ seed, freshTimeline, openEditor }) => {
  runOn('desktop'); // phone: the dock's Media item is the Add media sheet (RM.8, 30-phone/media.spec.ts)
  const ed = await openEditor(await freshTimeline('blank'));
  await openPool(ed);
  const all = await tileRefs(ed);
  expect(all).toEqual(expect.arrayContaining([ref(seed.files.clipA), ref(seed.files.clipB), ref(seed.files.still), ref(seed.files.tone)]));

  // Search by name: only clipA.
  await ed.page.getByTestId('media-search').fill('clipA');
  await expect.poll(() => tileRefs(ed)).toEqual([ref(seed.files.clipA)]);
  await ed.page.getByTestId('media-search').fill('zzz-nothing');
  await expect(ed.testId('media-pool-bin-empty')).toContainText('Nothing matches');
  await ed.tap(ed.testId('media-clear-filters'));
  await expect(ed.page.getByTestId('media-search')).toHaveValue('');
  await expect.poll(() => tileRefs(ed)).toEqual(all);

  // Kind filter: pictures only -> the still.
  await ed.tap(ed.testId('media-filter-button'));
  await ed.page.getByTestId('media-filter-kind-image').check();
  await expect.poll(() => tileRefs(ed)).toEqual([ref(seed.files.still)]);
  await ed.page.getByTestId('media-filter-kind-image').uncheck();
  await ed.page.keyboard.press('Escape');
  await expect.poll(() => tileRefs(ed)).toEqual(all);

  // Sort by name: the order of the tile ids follows the file names.
  await ed.page.getByTestId('media-sort').click();
  await ed.page.getByRole('option', { name: 'Name (A to Z)' }).click();
  const names = await ed.page.getByTestId('media-clip-tile').evaluateAll((els) => els.map((e) => e.querySelector('p')?.textContent ?? ''));
  expect(names.length).toBeGreaterThanOrEqual(5);
  expect(names).toEqual([...names].sort((a, b) => a.localeCompare(b, undefined, { sensitivity: 'base', numeric: true })));
});

test('R05.2 one-tap star and flag save at once and survive a reload', async ({ seed, freshTimeline, openEditor }) => {
  runOn('desktop'); // phone: the dock's Media item is the Add media sheet (RM.8, 30-phone/media.spec.ts)
  const mediaRef = ref(seed.files.clipB);
  const ed = await openEditor(await freshTimeline('blank'));
  await openPool(ed);
  const star = ed.page.locator(`[data-testid="media-tile-star"][data-for-media-ref="${mediaRef}"]`);
  const flag = ed.page.locator(`[data-testid="media-tile-flag"][data-for-media-ref="${mediaRef}"]`);
  try {
    await ed.tap(star);
    await ed.tap(flag);
    await expect.poll(async () => (await annotations(ed)).find((a) => a.mediaRef === mediaRef)).toMatchObject({ favourite: true, flag: true });
    await ed.reload();
    await openPool(ed);
    await expect(star).toHaveAttribute('aria-pressed', 'true');
    await expect(flag).toHaveAttribute('aria-pressed', 'true');
    // The state filter now shows exactly the starred source.
    await ed.tap(ed.testId('media-filter-button'));
    await ed.page.getByTestId('media-filter-state').click();
    await ed.page.getByRole('option', { name: 'Starred' }).click();
    await expect.poll(() => tileRefs(ed)).toEqual([mediaRef]);
  } finally {
    await seed.api.json('PUT', `/api/v1/projects/${seed.projectId}/media/annotations/${encodeURIComponent(mediaRef)}`, { favourite: false, flag: false }, [200, 201]);
  }
});

test('R05.3 bins: create, rename, new sub-bin, move to... and delete all work from menus', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const stamp = Date.now();
  const a = `A-${stamp}`;
  const b = `B-${stamp}`;
  const renamed = `A2-${stamp}`;
  const ed = await openEditor(await freshTimeline('blank'));
  await openPool(ed);
  const created: string[] = [];
  const makeBin = async (name: string) => {
    await ed.page.getByLabel('New bin name').fill(name);
    await ed.page.getByRole('button', { name: /^Add bin$/ }).click();
    await expect.poll(async () => (await bins(ed)).find((x) => x.name === name)?.id).toBeTruthy();
    const id = (await bins(ed)).find((x) => x.name === name)!.id;
    created.push(id);
    return id;
  };
  try {
    const aId = await makeBin(a);
    const bId = await makeBin(b);

    await openBinMenu(ed, a);
    await ed.testId(`bin-action-rename-${aId}`).click();
    await ed.page.getByLabel(`New name for ${a}`).fill(renamed);
    await ed.page.getByRole('button', { name: 'Save', exact: true }).click();
    await expect.poll(async () => (await bins(ed)).find((x) => x.id === aId)?.name).toBe(renamed);

    // New sub-bin inside B.
    await openBinMenu(ed, b);
    await ed.testId(`bin-action-subbin-${bId}`).click();
    await expect(ed.testId('new-bin-parent')).toContainText(b);
    const subName = `S-${stamp}`;
    await ed.page.getByLabel('New bin name').fill(subName);
    await ed.page.getByRole('button', { name: /^Add bin$/ }).click();
    await expect.poll(async () => (await bins(ed)).find((x) => x.name === subName)?.parentBinId).toBe(bId);
    created.push((await bins(ed)).find((x) => x.name === subName)!.id);

    // Move A2 into B without dragging.
    await openBinMenu(ed, renamed);
    await ed.testId(`bin-action-move-${aId}-${bId}`).click();
    await expect.poll(async () => (await bins(ed)).find((x) => x.id === aId)?.parentBinId).toBe(bId);

    // Delete B: the dialog names what goes (A2 and S are inside), then the whole subtree is gone.
    await openBinMenu(ed, b);
    await ed.testId(`bin-action-delete-${bId}`).click();
    await expect(ed.testId('bin-delete-text')).toContainText(/2 sub-bins/);
    await ed.testId('bin-delete-confirm').click();
    await expect.poll(async () => (await bins(ed)).filter((x) => created.includes(x.id)).length).toBe(0);
  } finally {
    for (const id of created) await ed.seed.api.json('DELETE', `/api/v1/projects/${ed.seed.projectId}/media/bins/${id}`, undefined, [200, 204, 404]);
  }
});

test('R05.3 a tile menu adds a clip to a bin without dragging, and never offers a smart bin', async ({ seed, freshTimeline, openEditor }) => {
  runOn('desktop'); // phone: the dock's Media item is the Add media sheet (RM.8, 30-phone/media.spec.ts)
  const stamp = Date.now();
  const bin = await seed.api.json<Bin>('POST', `/api/v1/projects/${seed.projectId}/media/bins`, { name: `Plain-${stamp}` }, [200, 201]);
  const smart = await seed.api.json<Bin>('POST', `/api/v1/projects/${seed.projectId}/media/bins`, { name: `Smart-${stamp}`, smartFilter: { kinds: ['audio'] } }, [200, 201]);
  const mediaRef = ref(seed.files.still);
  const ed = await openEditor(await freshTimeline('blank'));
  try {
    await openPool(ed);
    await ed.tap(ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="${mediaRef}"]`));
    await expect(ed.testId(`add-to-bin-${bin.id}`)).toBeVisible();
    await expect(ed.testId(`add-to-bin-${smart.id}`)).toHaveCount(0);
    await ed.tap(ed.testId(`add-to-bin-${bin.id}`));
    await expect.poll(async () => {
      const items = await seed.api.json<Array<{ mediaRef: string }>>('GET', `/api/v1/projects/${seed.projectId}/media/bins/${bin.id}/items`);
      return items.map((i) => i.mediaRef);
    }).toEqual([mediaRef]);
  } finally {
    for (const id of [bin.id, smart.id]) await seed.api.json('DELETE', `/api/v1/projects/${seed.projectId}/media/bins/${id}`, undefined, [200, 204, 404]);
  }
});

test('R05.4 dragging a tile onto track 2 at a time puts the clip there (indicator follows the pointer)', async ({ seed, api, freshTimeline, openEditor }) => {
  runOn('desktop');
  const timelineId = await freshTimeline('blank');
  await api.putDocument(seed.projectId, timelineId, (doc) => {
    doc.tracks.splice(1, 0, { id: crypto.randomUUID(), kind: 'video', name: 'V2', height: 64, locked: false, enabled: true, mute: false, solo: false, items: [] } as never);
  });
  const ed = await openEditor(timelineId);
  // The Edit workspace opens on the media pool, so a tile and the timeline are on screen together.
  await expect(ed.testId('tab-media')).toHaveAttribute('aria-selected', 'true');
  const source = tile(ed, ref(seed.files.clipA));
  await expect(source).toBeVisible();
  const doc = (await ed.serverTimeline()).document;
  const trackIndex = doc.tracks.findIndex((t: { name: string }) => t.name === 'V2');
  expect(trackIndex).toBe(1);
  await source.scrollIntoViewIfNeeded();
  const x = await ed.frameX(40);
  const y = await ed.laneY(trackIndex);
  const from = await source.boundingBox();
  await ed.page.mouse.move(from!.x + from!.width / 2, from!.y + from!.height / 2);
  await ed.page.mouse.down();
  await ed.page.mouse.move(from!.x + 20, from!.y + 20, { steps: 4 });
  await ed.page.mouse.move(x, y, { steps: 12 });
  const indicator = ed.testId('pool-drop-indicator');
  await expect(indicator).toHaveAttribute('data-allowed', 'true');
  await expect(indicator).toHaveAttribute('data-track', doc.tracks[trackIndex].id);
  await ed.page.mouse.up();
  const tl = await ed.expectServer((t) => (t.document.tracks[trackIndex].items?.length ?? 0) === 1, 'the clip landed on track 2');
  const clip = tl.document.tracks[trackIndex].items[0] as { name: string; recordIn: number };
  expect(clip.name).toBe('clipA.mp4');
  expect(Math.abs(clip.recordIn - 40)).toBeLessThanOrEqual(2);
  await expect(ed.page.getByTestId('landed-at')).toBeVisible();
  expect(tl.document.tracks[0].items?.length ?? 0, 'track 1 untouched').toBe(0);
});

test('R05.4 a sound cannot be dropped on a video track (the indicator says no) and nothing is written', async ({ seed, freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('blank'));
  const source = tile(ed, ref(seed.files.tone));
  await expect(source).toBeVisible();
  const before = (await ed.serverTimeline()).revision;
  await source.scrollIntoViewIfNeeded();
  const from = await source.boundingBox();
  await ed.page.mouse.move(from!.x + from!.width / 2, from!.y + from!.height / 2);
  await ed.page.mouse.down();
  await ed.page.mouse.move(from!.x + 20, from!.y + 20, { steps: 4 });
  await ed.page.mouse.move(await ed.frameX(30), await ed.laneY(0), { steps: 12 });
  await expect(ed.testId('pool-drop-indicator')).toHaveAttribute('data-allowed', 'false');
  await ed.page.mouse.up();
  await expect(ed.testId('pool-drop-indicator')).toHaveCount(0);
  expect((await ed.serverTimeline()).revision).toBe(before);
});

test('R05.4 the tile Place menu puts a source on the timeline without dragging (touch path)', async ({ seed, freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7', 'iphone14webkit');
  const ed = await openEditor(await freshTimeline('blank'));
  await openPool(ed);
  await ed.tap(ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="${ref(seed.files.clipB)}"]`));
  await ed.tap(ed.testId('tile-place-append'));
  const tl = await ed.expectServer((t) => t.document.tracks.some((tr: { kind: string; items?: Array<{ name: string }> }) => tr.kind === 'video' && (tr.items ?? []).some((i) => i.name === 'clipB.mp4')), 'clipB is on a video track');
  expect(JSON.stringify(tl.document)).toContain(seed.files.clipB);
});

test('R05.5 the source viewer has one player: no Original/Proxy switch, and it plays', async ({ seed, freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('blank'));
  await openPool(ed);
  await ed.tap(tile(ed, ref(seed.files.clipA)));
  await expect(ed.testId('source-viewer')).toBeVisible();
  await expect(ed.page.getByRole('radio', { name: /^(Original|Proxy)$/ })).toHaveCount(0);
  await expect(ed.testId('proxy-toggle')).toHaveCount(0);
  const video = ed.testId('source-viewer-video');
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => (v.error ? `error ${v.error.code}` : v.readyState >= 2 && v.videoWidth > 0 ? 'ready' : 'loading'))).toBe('ready');
  await video.evaluate((v: HTMLVideoElement) => { v.muted = true; return v.play(); });
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime)).toBeGreaterThan(0.3);
});

test('R05.6 a smart bin is created from a plain-words rule, matches the right sources, and its rule can be edited', async ({ seed, freshTimeline, openEditor }) => {
  runOn('desktop');
  const name = `Pictures-${Date.now()}`;
  const ed = await openEditor(await freshTimeline('blank'));
  await openPool(ed);
  let binId = '';
  try {
    await ed.tap(ed.testId('smart-bin-new'));
    // An empty rule is refused with a message and nothing is stored.
    await ed.page.getByTestId('smart-bin-name-input').fill(name);
    await ed.testId('smart-bin-save').click();
    await expect(ed.testId('smart-bin-error')).toContainText(/at least one/i);
    expect((await bins(ed)).some((b) => b.name === name)).toBe(false);

    await ed.page.getByTestId('smart-bin-kind-image').check();
    await expect(ed.testId('smart-bin-summary')).toContainText(/Pictures: \d+ sources? match/);
    await ed.testId('smart-bin-save').click();
    await expect.poll(async () => (await bins(ed)).find((b) => b.name === name)?.smartFilter?.kinds).toEqual(['image']);
    binId = (await bins(ed)).find((b) => b.name === name)!.id;

    // Selecting it lists exactly the pictures; the tree counts the match.
    await ed.tap(ed.testId(`bin-name-${binId}`));
    await expect.poll(() => tileRefs(ed)).toEqual([ref(seed.files.still)]);
    await expect(ed.testId(`bin-count-${binId}`)).toContainText('1 match');

    // Edit the rule in place: sound instead of pictures.
    await openBinMenu(ed, name);
    await ed.testId(`bin-action-rule-${binId}`).click();
    await ed.page.getByTestId('smart-bin-kind-image').uncheck();
    await ed.page.getByTestId('smart-bin-kind-audio').check();
    await ed.testId('smart-bin-save').click();
    await expect.poll(async () => (await bins(ed)).find((b) => b.id === binId)?.smartFilter?.kinds).toEqual(['audio']);
    await expect.poll(() => tileRefs(ed)).toEqual([ref(seed.files.tone)]);
  } finally {
    if (binId) await seed.api.json('DELETE', `/api/v1/projects/${seed.projectId}/media/bins/${binId}`, undefined, [200, 204, 404]);
  }
});

test('R05.7 uploading from the pool shows progress and the new tile appears', async ({ seed, api, freshTimeline, openEditor }) => {
  runOn('desktop'); // phone: the dock's Media item is the Add media sheet (RM.8, 30-phone/media.spec.ts)
  const ed = await openEditor(await freshTimeline('blank'));
  await openPool(ed);
  const fileName = `pool-upload-${Date.now()}-${test.info().project.name}.png`;
  const tmp = path.join(os.tmpdir(), fileName);
  fs.copyFileSync(path.join(ensureMedia(), 'still.png'), tmp);
  try {
    const before = await ed.page.getByTestId('media-clip-tile').count();
    await ed.page.locator('input[type="file"][accept="video/*,audio/*,image/*"]').first().setInputFiles(tmp);
    await expect(ed.page.getByTestId('media-upload-row').first()).toBeVisible();
    await expect.poll(async () => (await api.listFiles(seed.projectId)).some((f) => f.originalFileName === fileName), { timeout: 60_000 }).toBe(true);
    await expect(ed.page.getByTestId('media-upload-row').first()).toHaveAttribute('data-status', 'done', { timeout: 30_000 });
    await expect(ed.page.getByTestId('media-clip-tile')).toHaveCount(before + 1, { timeout: 30_000 });
    await ed.page.getByTestId('media-search').fill(fileName);
    await expect(ed.page.getByTestId('media-clip-tile')).toHaveCount(1);
  } finally {
    fs.rmSync(tmp, { force: true });
  }
});

test.describe('tablet (coarse pointer, 1024x768)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  test('R05.2 R05.4 the pool opens first, star saves, and the Place menu works with a finger', async ({ seed, freshTimeline, openEditor }) => {
    runOn('desktop');
    const mediaRef = ref(seed.files.clipA);
    const ed = await openEditor(await freshTimeline('blank'));
    await expect(ed.testId('tab-media')).toHaveAttribute('aria-selected', 'true');
    const star = ed.page.locator(`[data-testid="media-tile-star"][data-for-media-ref="${mediaRef}"]`);
    try {
      await star.tap();
      await expect.poll(async () => (await annotations(ed)).find((a) => a.mediaRef === mediaRef)?.favourite).toBe(true);
      const box = await star.boundingBox();
      expect(box!.width, 'touch target >= 44 px').toBeGreaterThanOrEqual(44);
      await ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="${mediaRef}"]`).tap();
      await ed.testId('tile-place-append').tap();
      await ed.expectServer((t) => JSON.stringify(t.document).includes(seed.files.clipA), 'clipA is on the timeline');
    } finally {
      await seed.api.json('PUT', `/api/v1/projects/${seed.projectId}/media/annotations/${encodeURIComponent(mediaRef)}`, { favourite: false }, [200, 201]);
    }
  });
});
