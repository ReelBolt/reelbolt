import type { Locator } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { runOn } from '../../../fixtures/editor-v2/rows';
import { lightApi, withTempProject } from '../../../fixtures/editor-v2/ws';

/**
 * Lane C, MEDIA workspace in the editor (inventory/workspaces.md 2.3, WS-040..WS-072; the same components used to be on the
 * hub's Media tab, which is being removed by editor-v2-seed-and-hub: only the editor's Media workspace is tested).
 * Needs the engine's derivative pipeline (proxies, filmstrips) and the seeded clipA/clipB/vertical/still/tone files.
 */

const tile = (ed: EditorPage, ref: string): Locator => ed.page.locator(`[data-testid="media-clip-tile"][data-media-ref="${ref}"]`).first();
const ref = (id: string) => `file:${id}`;

async function openMedia(ed: EditorPage): Promise<void> {
  await ed.workspace('media');
  await expect(ed.testId('media-pool')).toBeVisible({ timeout: 30_000 });
}

async function selectTile(ed: EditorPage, ref: string): Promise<void> {
  await ed.tap(tile(ed, ref));
  await expect(ed.testId('source-viewer')).toBeVisible();
}

/** WS-040: the three layouts show the same tiles in their own containers. */
test('WS-040 Grid, List and Filmstrip show the same clips in their own layout', async ({ openEditor, seed }) => {
  runOn('desktop'); // phone: the Media dock item is the Add media sheet, a grid only (RM.8)
  const ed = await openEditor(seed.blankTimelineId);
  await openMedia(ed);
  // T-003: the Media page is a page in the shared frame — bins | pool | viewer+metadata.
  await expect(ed.testId('media-bins-zone')).toBeVisible();
  await expect(ed.testId('media-pool-zone')).toBeVisible();
  await expect(ed.testId('media-detail-zone')).toBeVisible();
  const n = await ed.page.getByTestId('media-clip-tile').count();
  expect(n, 'the pool lists the five seeded files and the derush render').toBeGreaterThanOrEqual(5);
  for (const [label, container] of [['List', 'media-clip-list'], ['Filmstrip', 'media-clip-filmstrip'], ['Grid', 'media-clip-grid']] as const) {
    await ed.tap(ed.page.locator('label', { hasText: new RegExp(`^${label}$`) }).first());
    await expect(ed.testId(container)).toBeVisible();
    await expect(ed.page.getByTestId('media-clip-tile')).toHaveCount(n);
  }
});

/** WS-041/059/070/042/046/047: selecting a clip loads viewer, metadata, strip; the original plays; hover reads derivatives with no write. */
test('WS-041 selecting a clip shows its metadata, plays the original and the filmstrip scrubs; selecting again clears', async ({ openEditor, seed }) => {
  runOn('desktop');
  const ed = await openEditor(seed.blankTimelineId);
  await openMedia(ed);
  const posts: string[] = [];
  ed.page.on('request', (r) => { if (r.method() === 'POST' && /derivatives\/request/.test(r.url())) posts.push(r.url()); });
  const t = tile(ed, ref(seed.files.clipA));
  await ed.tap(t);
  await expect(t).toHaveAttribute('aria-pressed', 'true');
  await expect(ed.testId('metadata-value-codec')).toContainText(/h\.?264|avc/i);
  await expect(ed.testId('metadata-panel')).toContainText('clipA.mp4');

  // WS-059 the original plays.
  const video = ed.testId('source-viewer-video');
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => (v.error ? `error ${v.error.code}` : v.readyState >= 2 && v.videoWidth > 0 ? 'ready' : 'loading'))).toBe('ready');
  await video.evaluate((v: HTMLVideoElement) => { v.muted = true; return v.play(); });
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { message: 'the original plays' }).toBeGreaterThan(0.3);
  await video.evaluate((v: HTMLVideoElement) => v.pause());

  // WS-047 derivative badges say what exists; the seed made the proxy and the waveform.
  await expect(ed.testId('derivative-kind-proxy')).toHaveAttribute('data-present', 'true', { timeout: 30_000 });

  // WS-046 the filmstrip cell follows the pointer (desktop).
  const cell = ed.testId('filmstrip-cell');
  if (await cell.count()) {
    await cell.scrollIntoViewIfNeeded();
    const box = (await cell.boundingBox())!;
    await ed.page.mouse.move(box.x + 4, box.y + box.height / 2);
    const first = await cell.getAttribute('data-thumb-index');
    await ed.page.mouse.move(box.x + box.width - 4, box.y + box.height / 2, { steps: 8 });
    await expect.poll(() => cell.getAttribute('data-thumb-index'), { message: 'scrubbing moves to another real thumbnail' }).not.toBe(first);
  }

  // WS-068 opening that clip asked for derivatives at most once.
  expect(posts.length, 'one automatic derivative request per opened clip').toBeLessThanOrEqual(1);

  // Second click deselects.
  await ed.tap(t);
  await expect(t).toHaveAttribute('aria-pressed', 'false');
  await expect(ed.testId('source-viewer')).toHaveCount(0);
});

/** WS-060 / R05.5: there is no Original/Proxy switch; the player moves to the proxy by itself once it exists, labelled "Simple preview". */
test('WS-060 R05.5 the player uses the proxy derivative by itself (no switch), labels it Simple preview, and it plays', async ({ openEditor, seed }) => {
  runOn('desktop');
  const ed = await openEditor(seed.blankTimelineId);
  await openMedia(ed);
  await selectTile(ed, ref(seed.files.clipA));
  await expect(ed.testId('source-viewer-preview-label')).toHaveText('Simple preview', { timeout: 30_000 });
  await expect(ed.testId('proxy-toggle')).toHaveCount(0);
  const video = ed.testId('source-viewer-video');
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentSrc), { message: 'the player reads the proxy route' }).toMatch(/\/media\/derivatives\/[^/]+\/content/);
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => (v.error ? `error ${v.error.code}` : v.readyState >= 2 && v.videoWidth > 0 ? 'ready' : 'loading'))).toBe('ready');
  await video.evaluate((v: HTMLVideoElement) => { v.muted = true; return v.play(); });
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.currentTime), { message: 'the proxy plays' }).toBeGreaterThan(0.3);
});

/** WS-067/069: Prepare / Request again publishes a request (202) and Check again re-reads the derivatives. */
test('WS-067 Request again answers 202 and Check again re-reads the derivative list', async ({ openEditor, seed }) => {
  runOn('desktop');
  const ed = await openEditor(seed.blankTimelineId);
  await openMedia(ed);
  await selectTile(ed, ref(seed.files.clipB));
  const requests: number[] = [];
  const reads: string[] = [];
  ed.page.on('response', (r) => {
    if (r.request().method() === 'POST' && /derivatives\/request/.test(r.url())) requests.push(r.status());
    if (r.request().method() === 'GET' && /\/media\/[^/]+\/derivatives$/.test(new URL(r.url()).pathname)) reads.push(r.url());
  });
  await ed.tap(ed.testId('prepare-derivatives-button'));
  await expect.poll(() => requests, { message: 'the request is accepted (202)' }).toContain(202);
  const before = reads.length;
  await ed.tap(ed.testId('prepare-derivatives-check'));
  await expect.poll(() => reads.length, { message: 'Check again reads the derivatives again' }).toBeGreaterThan(before);
});

/** WS-071 + WS-054/055: annotations are saved and restored, and a smart bin lists exactly what its filter says. */
test('WS-071 annotate a clip (keyword, note, favourite) and a smart bin on that keyword lists just that clip', async ({ openEditor, seed, api }) => {
  runOn('desktop');
  const keyword = `hero${Date.now()}`;
  const ed = await openEditor(seed.blankTimelineId);
  await openMedia(ed);
  await selectTile(ed, ref(seed.files.clipB));
  const kw = ed.page.getByTestId('annotation-keywords').getByRole('textbox').or(ed.testId('annotation-keywords')).first();
  await kw.click();
  await kw.fill(keyword);
  await kw.press('Enter');
  await ed.testId('annotation-note').fill('chosen take');
  await ed.page.getByTestId('annotation-favourite').check({ force: true });
  // R05.8: annotations save on change, no Save button.
  await expect(ed.testId('annotation-message')).toHaveText('Saved.');
  const list = await api.json<Array<{ mediaRef: string; keywords?: string[]; note?: string; favourite?: boolean }>>('GET', `/api/v1/projects/${seed.projectId}/media/annotations`);
  const stored = list.find((x) => x.mediaRef === ref(seed.files.clipB))!;
  expect(stored.keywords).toContain(keyword);
  expect(stored.note).toBe('chosen take');
  expect(stored.favourite).toBe(true);

  // Reload restores it.
  await ed.reload();
  await openMedia(ed);
  await selectTile(ed, ref(seed.files.clipB));
  await expect(ed.testId('annotation-note')).toHaveValue('chosen take');

  // Smart bin: keyword + video kind, through the rule editor (R05.6).
  await ed.tap(ed.testId('smart-bin-new'));
  await ed.page.getByTestId('smart-bin-name-input').fill(`smart-${keyword}`);
  await ed.page.getByTestId('smart-bin-kind-video').check();
  const kws = ed.page.getByTestId('smart-bin-keywords').getByRole('textbox').or(ed.testId('smart-bin-keywords')).first();
  await kws.click();
  await kws.fill(keyword);
  await kws.press('Enter');
  await ed.testId('smart-bin-save').click();
  let bin!: { id: string; name: string };
  await expect.poll(async () => {
    const bins = await api.json<Array<{ id: string; name: string }>>('GET', `/api/v1/projects/${seed.projectId}/media/bins`);
    bin = bins.find((b) => b.name === `smart-${keyword}`)!;
    return !!bin;
  }, { message: 'the smart bin is stored' }).toBe(true);
  await expect(ed.testId(`bin-smart-${bin.id}`)).toBeVisible();
  await ed.tap(ed.testId(`bin-name-${bin.id}`));
  await expect(ed.page.getByTestId('media-clip-tile'), 'the smart bin lists exactly the annotated clip').toHaveCount(1);
  await expect(tile(ed, ref(seed.files.clipB))).toBeVisible();
  await api.json('DELETE', `/api/v1/projects/${seed.projectId}/media/bins/${bin.id}`, undefined, [200, 204]);
});

/** WS-053/049/045/058/051/052/057/056/048: the whole bin workflow with the real controls. */
test('WS-053 bins: create (and the empty-name message), pin a clip by menu and by drop, remove a pin, rename, nest by drag, delete', async ({ openEditor, seed, api }) => {
  runOn('desktop');
  const stamp = Date.now();
  const ed = await openEditor(seed.blankTimelineId);
  await openMedia(ed);
  const newName = ed.page.getByRole('textbox', { name: 'New bin name' });

  // WS-053 empty name -> message; real names create bins.
  await ed.page.getByRole('button', { name: 'Add bin' }).click();
  await expect(ed.testId('bin-message')).toContainText('A bin name is required.');
  await newName.fill(`A-${stamp}`);
  await newName.press('Enter');
  // The field clears when the create lands: typing the next name before then loses it.
  await expect(ed.page.getByText(`A-${stamp}`).first()).toBeVisible();
  await expect(newName).toHaveValue('');
  await newName.fill(`B-${stamp}`);
  await newName.press('Enter');
  const binsNow = async () => api.json<Array<{ id: string; name: string; parentBinId?: string | null }>>('GET', `/api/v1/projects/${seed.projectId}/media/bins`);
  await expect.poll(async () => (await binsNow()).filter((b) => b.name.endsWith(`-${stamp}`)).length).toBe(2);
  const all = await binsNow();
  const a = all.find((b) => b.name === `A-${stamp}`)!;
  const b = all.find((b) => b.name === `B-${stamp}`)!;
  await expect(ed.testId(`bin-count-${a.id}`)).toHaveText(/0/);

  // WS-045 pin clipA into A by the tile menu; the count, the list and the server agree.
  const more = ed.page.locator(`[data-testid="media-tile-menu"][data-for-media-ref="${ref(seed.files.clipA)}"]`).first();
  await ed.tap(more);
  await ed.tap(ed.testId(`add-to-bin-${a.id}`));
  await expect(ed.testId(`bin-count-${a.id}`)).toHaveText(/1/);
  // WS-049 select the bin: only its pins show.
  await ed.tap(ed.testId(`bin-name-${a.id}`));
  await expect(ed.page.getByTestId('media-clip-tile')).toHaveCount(1);
  await expect(ed.testId('bin-items-list')).toContainText('clipA.mp4');
  // WS-058 remove the pin.
  await ed.page.locator('[data-testid^="bin-item-remove-"]').first().click();
  await expect(ed.testId(`bin-count-${a.id}`)).toHaveText(/0/);

  // WS-057 drop a tile on bin B (HTML5 drag), then on All media (removes every pin).
  await ed.tap(ed.testId('bin-root-drop-target'));
  await tile(ed, ref(seed.files.clipB)).dragTo(ed.testId(`bin-drop-target-${b.id}`));
  await expect(ed.testId(`bin-count-${b.id}`)).toHaveText(/1/);
  await tile(ed, ref(seed.files.clipB)).dragTo(ed.testId('bin-root-drop-target'));
  await expect(ed.testId(`bin-count-${b.id}`)).toHaveText(/0/);

  // WS-051 rename A (pencil, Enter).
  await ed.page.getByRole('button', { name: `Actions for A-${stamp}` }).click();
  await ed.testId(`bin-action-rename-${a.id}`).click();
  const input = ed.page.locator(`[data-bin-id="${a.id}"] input`).first();
  await input.fill(`A2-${stamp}`);
  await input.press('Enter');
  await expect.poll(async () => (await binsNow()).find((x) => x.id === a.id)?.name).toBe(`A2-${stamp}`);

  // WS-056 nest A2 under B by dragging its name onto B's row.
  await ed.testId(`bin-name-${a.id}`).dragTo(ed.testId(`bin-drop-target-${b.id}`));
  await expect.poll(async () => (await binsNow()).find((x) => x.id === a.id)?.parentBinId, { message: 'A2 is nested under B on the server' }).toBe(b.id);
  // WS-050 B can collapse its child.
  await ed.page.getByLabel(new RegExp(`^Collapse B-${stamp}`)).click();
  await expect(ed.testId(`bin-name-${a.id}`)).toHaveCount(0);

  // WS-052 deleting B removes its children too; the sources stay in the pool.
  await ed.page.getByRole('button', { name: `Actions for B-${stamp}` }).click();
  await ed.testId(`bin-action-delete-${b.id}`).click();
  // Deleting a bin asks first (it names the sub-bins and clips it holds); confirm it.
  await expect(ed.testId('bin-delete-text')).toContainText(/1 sub-bin/);
  await ed.tap(ed.testId('bin-delete-confirm'));
  await expect.poll(async () => (await binsNow()).filter((x) => x.name.endsWith(`-${stamp}`)).length).toBe(0);
  await expect(tile(ed, ref(seed.files.clipA))).toBeVisible();
});

/** WS-072: a project with no media says so; bins need a file first. */
test('WS-072 a project with no media shows the empty pool and no bin tools', async ({ page, seed }) => {
  runOn('desktop');
  const api = lightApi(page);
  await withTempProject(api, 'ws072', async (project) => {
    const tl = await api.createTimeline(project.id, 'empty-media');
    await page.goto(`/app/editor/${project.id}/timeline/${tl.id}`);
    await expect(page.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });
    await page.getByTestId('workspace-media').click();
    await expect(page.getByTestId('media-pool-empty')).toBeVisible({ timeout: 30_000 });
    await expect(page.getByTestId('bin-tree'), 'no bin tools until a file exists').toHaveCount(0);
  });
  void seed;
});

async function seekSource(ed: EditorPage, seconds: number): Promise<void> {
  const video = ed.testId('source-viewer-video');
  await video.evaluate((v: HTMLVideoElement, t: number) => new Promise<void>((resolve) => { v.addEventListener('seeked', () => resolve(), { once: true }); v.currentTime = t; }), seconds);
}

/** WS-061..064: In/Out marks at the real playhead, Clear, and Create subclip placing the marked range at the end of the timeline. */
test('WS-064 In/Out marks and Create subclip: the marked 3 s lands at the end of the timeline with its sound, and one undo removes both', async ({ freshTimeline, openEditor, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('blank'));
  await openMedia(ed);
  await selectTile(ed, ref(seed.files.clipA));
  const video = ed.testId('source-viewer-video');
  await expect.poll(() => video.evaluate((v: HTMLVideoElement) => v.readyState)).toBeGreaterThanOrEqual(2);

  // WS-061 / WS-062: marks at the real playhead (30 fps: 2 s = frame 60, 5 s = frame 150).
  await seekSource(ed, 2);
  await ed.tap(ed.testId('mark-in'));
  await expect(ed.testId('mark-in-value')).toContainText('00:00:02:00');
  await seekSource(ed, 5);
  await ed.tap(ed.testId('mark-out'));
  await expect(ed.testId('mark-out-value')).toContainText('00:00:05:00');
  await expect(ed.testId('mark-span-value')).toContainText(/90|00:00:03:00/);

  // WS-063 Clear marks, then set them again.
  await ed.tap(ed.testId('mark-clear'));
  await expect(ed.testId('mark-in-value')).toContainText(/not set/i);
  await seekSource(ed, 2);
  await ed.tap(ed.testId('mark-in'));
  await seekSource(ed, 5);
  await ed.tap(ed.testId('mark-out'));

  // WS-064 Create subclip.
  await ed.tap(ed.testId('create-subclip'));
  const tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.kind === 'video' && tr.items.length === 1), 'the subclip is placed');
  const v = tl.document.tracks.find((tr) => tr.kind === 'video')!.items[0];
  expect(v.recordOut - v.recordIn, 'exactly the marked 90 frames').toBe(90);
  expect(v.sourceIn, 'starting at the in mark').toBe(60);
  expect(v.name).toMatch(/\[.*\]$/);
  const a = tl.document.tracks.filter((tr) => tr.kind === 'audio').flatMap((tr) => tr.items);
  expect(a.length, 'the camera sound comes with it').toBe(1);
  expect([a[0].recordIn, a[0].recordOut]).toEqual([v.recordIn, v.recordOut]);

  // One undo removes picture and sound together.
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((t) => t.document.tracks.every((tr) => tr.items.length === 0), 'one undo removes both clips');
});

/** WS-065/066/322: Insert at playhead and Add to end place the clip with its sound at the right frames. */
test('WS-065 Insert at playhead puts clipB (with its sound) at the playhead and Add to end appends after it', async ({ freshTimeline, openEditor, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('blank'));
  await openMedia(ed);
  await selectTile(ed, ref(seed.files.clipB));
  await ed.testId('media-insert-length').waitFor();
  await ed.tap(ed.testId('media-insert-at-playhead'));
  let tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.kind === 'video' && tr.items.length === 1), 'clipB is inserted');
  const first = tl.document.tracks.find((tr) => tr.kind === 'video')!.items[0];
  expect(first.recordIn, 'at the playhead (frame 0)').toBe(0);
  expect(first.recordOut, 'for its probed 8 s').toBe(240);
  expect(tl.document.tracks.filter((tr) => tr.kind === 'audio').flatMap((tr) => tr.items), 'with its camera sound').toHaveLength(1);

  await selectTile(ed, ref(seed.files.vertical));
  await ed.tap(ed.testId('media-append'));
  tl = await ed.expectServer((t) => (t.document.tracks.find((tr) => tr.kind === 'video')!.items.length) === 2, 'the vertical clip is appended');
  const second = tl.document.tracks.find((tr) => tr.kind === 'video')!.items[1];
  expect(second.recordIn, 'it starts where the first one ends').toBe(240);
  expect(second.recordOut - second.recordIn, 'the 6 s clip').toBe(180);
});
