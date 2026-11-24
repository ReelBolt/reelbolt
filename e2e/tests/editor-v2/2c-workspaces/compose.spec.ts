import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { referenceGrid } from '../../../fixtures/editor-v2/pixels';
import { downloadRender, probe, runOn, type RenderSummary } from '../../../fixtures/editor-v2/rows';
import { cellLuma, chromaSpread, clickSlider, edgeEnergy, makeGreenScreen, renderFromDeliver } from '../../../fixtures/editor-v2/ws';

/**
 * Lane C, COMPOSE workspace (inventory/workspaces.md 2.5, WS-120..WS-136; timeline.md TL-345..357). Every effect here is
 * render-only (the preview shows the plain picture), so each control is authored through the UI, checked in the stored
 * document, and proved by a real Deliver render of a 1-2 s timeline.
 */

type Api = import('../../../fixtures/editor-v2/api').EditorApi;
type Seed = import('../../../fixtures/editor-v2/index').Seed;

/** Blank timeline with clipA (V1, frames 0..frames) and no sound: renders in seconds. */
async function clipTimeline(freshTimeline: (f: 'blank') => Promise<string>, api: Api, seed: Seed, frames = 30, name = 'clipA.mp4', fileKey: 'clipA' | 'clipB' = 'clipA'): Promise<string> {
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), name, seed.files[fileKey], 0, frames)];
  });
  return id;
}

async function clipOf(ed: EditorPage, name: string) {
  const doc = (await ed.serverTimeline()).document;
  return doc.tracks.flatMap((t) => t.items).find((i) => i.name === name)!;
}

async function openCompose(ed: EditorPage): Promise<void> {
  await ed.workspace('compose');
  if (ed.isPhone) await ed.tap(ed.page.locator('label', { hasText: /^Compose$/ }).first());
  await expect(ed.testId('compose-workspace')).toBeVisible();
}

/** Compose is a selected-target editor: pick the list entry whose properties the next steps use. */
async function composeEntry(ed: EditorPage, entry: 'mask' | 'reframe' | 'keyer' | 'pin' | 'effects' | 'motion' | 'chain' | 'grade' | 'matte' | 'quad'): Promise<void> {
  await ed.tap(ed.testId(`compose-entry-${entry}`));
}

async function renderAndGrid(ed: EditorPage, id: string, seed: Seed, api: Api, sec = 0.5): Promise<{ file: string; grid: number[]; r: RenderSummary }> {
  const r = await renderFromDeliver(ed, id);
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');
  const file = await downloadRender(api, seed.projectId, r);
  return { file, grid: referenceGrid(file, sec), r };
}

/** WS-120 + WS-122: an adjustment clip is added on a new top track, graded to grey, and its Enabled switch decides whether the render is grey. */
test('WS-120 Add adjustment clip makes a track, grading it greys the render below, and Enabled off restores the colours', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the Compose workspace is not on the phone surface (x1-mobile-model verdict)
  test.setTimeout(300_000);
  const id = await clipTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  await openCompose(ed);
  await ed.tap(ed.testId('compose-add-adjustment'));
  let tl = await ed.expectServer((t) => t.document.tracks.some((tr) => tr.name === 'Adjustments'), 'an Adjustments track is stored');
  const track = tl.document.tracks.find((tr) => tr.name === 'Adjustments')!;
  expect(track.kind).toBe('video');
  expect(track.items).toHaveLength(1);
  expect([track.items[0].type, track.items[0].recordIn, track.items[0].recordOut], 'two seconds at the playhead').toEqual(['adjustmentClip', 0, 60]);
  // Undo removes the whole track; adding again restores it.
  if (!ed.isPhone) {
    await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
    await ed.page.keyboard.press('Control+z');
    await ed.expectServer((t) => !t.document.tracks.some((tr) => tr.name === 'Adjustments'), 'undo removes the track');
    await ed.tap(ed.testId('compose-add-adjustment'));
    await ed.expectServer((t) => t.document.tracks.some((tr) => tr.name === 'Adjustments'), 'the track is back');
  }

  // The adjustment is selected: grade it to saturation 0 through the real Grade editor.
  await ed.tap(ed.testId('grade-add-node'));
  await ed.tap(ed.testId('grade-node-select-0'));
  await ed.testId('grade-scalar-saturation-input').fill('0');
  tl = await ed.expectServer((t) => {
    const adj = t.document.tracks.find((tr) => tr.name === 'Adjustments')?.items[0] as { grade?: { nodes: Array<{ primaries?: { saturation?: number } }> } } | undefined;
    return adj?.grade?.nodes[0]?.primaries?.saturation === 0;
  }, 'the adjustment clip carries the grade');

  const grey = await renderAndGrid(ed, id, seed, api);
  expect(chromaSpread(grey.grid), 'WS-122 the layers below are rendered grey').toBeLessThan(6);

  // Enabled off: the render is colourful again.
  await openCompose(ed);
  await ed.tap(ed.page.getByRole('switch', { name: 'Enabled' }).or(ed.page.getByLabel('Enabled')).first());
  await ed.expectServer((t) => (t.document.tracks.find((tr) => tr.name === 'Adjustments')?.items[0] as { enabled?: boolean }).enabled === false, 'the adjustment is stored disabled');
  const colour = await renderAndGrid(ed, id, seed, api);
  expect(chromaSpread(colour.grid), 'a disabled adjustment does nothing').toBeGreaterThan(12);
});

/** WS-121: the effects list: add, amount, remove; a full-strength blur lowers the rendered sharpness. */
test('WS-121 Effects: Add effect (blur), set its amount, render is blurrier, Remove clears the list', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const id = await clipTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = await renderAndGrid(ed, id, seed, api);
  const sharp = edgeEnergy(base.file, 0.5);

  await ed.selectClipAt(15, 0);
  await openCompose(ed);
  await composeEntry(ed, 'effects');
  await ed.tap(ed.testId('compose-effect-add'));
  let c = (await ed.expectServer((t) => (t.document.tracks[0].items[0].effects as unknown[] | undefined)?.length === 1, 'the effect is stored')).document.tracks[0].items[0];
  expect(c.effects).toEqual([{ type: 'blur', amount: 0.5, enabled: true }]);

  // The blur slider runs 0..MAX_BLUR_AMOUNT (2): the right end is amount 2.
  const slider = ed.testId('compose-effect-0').getByRole('slider').first();
  await clickSlider(ed.page, ed.testId('compose-effect-0').locator('.mantine-Slider-root').first(), 0.999);
  await expect(slider).toHaveAttribute('aria-valuenow', '2');
  c = (await ed.expectServer((t) => (t.document.tracks[0].items[0].effects as Array<{ amount: number }>)?.[0]?.amount === 2, 'amount 2 is stored')).document.tracks[0].items[0];

  // WS-136 the node view (the Chain entry) lights the Effects stage.
  await composeEntry(ed, 'chain');
  await expect(ed.page.locator('.react-flow__node', { hasText: /^Effects \(1\)/ })).toHaveCSS('opacity', '1');

  const blurred = await renderAndGrid(ed, id, seed, api);
  expect(edgeEnergy(blurred.file, 0.5), 'the blurred render has less edge energy').toBeLessThan(sharp * 0.8);
  await ed.workspace('compose');

  await composeEntry(ed, 'effects');
  await ed.tap(ed.testId('compose-effect-remove-0'));
  await ed.expectServer((t) => !((t.document.tracks[0].items[0].effects as unknown[] | null | undefined)?.length), 'removing the last effect clears the list');
});

/** WS-129/130: shape mask: switch, kind, size; the render keeps the inside and drops the outside. */
test('WS-129 Shape mask (ellipse, small) cuts the picture in the render: centre stays, corners go', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const id = await clipTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = await renderAndGrid(ed, id, seed, api);
  await ed.selectClipAt(15, 0);
  await openCompose(ed);
  await composeEntry(ed, 'mask');
  await ed.tap(ed.testId('mask-reframe-add-mask'));
  await ed.tap(ed.testId('mask-reframe-kind'));
  await ed.tap(ed.page.getByRole('option', { name: 'Ellipse' }));
  for (const key of ['w', 'h']) {
    await clickSlider(ed.page, ed.testId(`mask-reframe-${key}`).locator('.mantine-Slider-root').first(), 0.3);
  }
  const tl = await ed.expectServer((t) => {
    const m = t.document.tracks[0].items[0].mask as { kind?: string; w?: number; h?: number } | undefined;
    return m?.kind === 'ellipse' && (m.w ?? 1) < 0.4 && (m.h ?? 1) < 0.4;
  }, 'the mask is stored as a small ellipse');
  void tl;
  const masked = await renderAndGrid(ed, id, seed, api);
  expect(cellLuma(masked.grid, 16, 9), 'inside the mask the picture is kept').toBeGreaterThan(cellLuma(base.grid, 16, 9) * 0.6);
  expect(cellLuma(masked.grid, 1, 1), 'outside the mask it is cut away').toBeLessThan(Math.max(10, cellLuma(base.grid, 1, 1) * 0.3));
});

/** WS-131: Chroma key: the green field of an overlay clip lets the base picture show through in the render. */
test('WS-131 Chroma key makes the green of the top clip transparent in the render', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const green = await api.upload(seed.projectId, makeGreenScreen(), 'video/mp4', `green-${Date.now()}.mp4`);
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipB.mp4', seed.files.clipB, 0, 30)];
    const top = structuredClone(doc.tracks.find((t) => t.kind === 'video')!);
    top.id = crypto.randomUUID();
    top.name = 'V2';
    top.items = [mediaClip(crypto.randomUUID(), 'green.mp4', green, 0, 30)];
    doc.tracks.splice(1, 0, top);
  });
  const ed = await openEditor(id);
  const plain = await renderAndGrid(ed, id, seed, api);
  // Without the key the top clip is a green field.
  const cornerRgb = (grid: number[]) => [grid[(1 * 32 + 1) * 3], grid[(1 * 32 + 1) * 3 + 1], grid[(1 * 32 + 1) * 3 + 2]];
  const [pr, pg, pb] = cornerRgb(plain.grid);
  expect(pg, `unkeyed corner is green (${pr},${pg},${pb})`).toBeGreaterThan(Math.max(pr, pb) + 80);

  await ed.selectClipAt(15, 1);
  await openCompose(ed);
  await composeEntry(ed, 'keyer');
  await ed.tap(ed.testId('compose-keyer-toggle'));
  await ed.expectServer((t) => !!t.document.tracks[1].items[0].keyer, 'the keyer is stored');
  const keyed = await renderAndGrid(ed, id, seed, api);
  const [kr, kg, kb] = cornerRgb(keyed.grid);
  expect(kg, `keyed corner shows the base picture, not green (${kr},${kg},${kb})`).toBeLessThan(Math.max(kr, kb) + 60);
  // The red square in the middle of the overlay is kept.
  const mid = [keyed.grid[(9 * 32 + 16) * 3], keyed.grid[(9 * 32 + 16) * 3 + 1], keyed.grid[(9 * 32 + 16) * 3 + 2]];
  expect(mid[0], `the red square survives the key (${mid.join(',')})`).toBeGreaterThan(mid[1] + 60);
});

/** WS-132: Corner pin: the inputs store the corner and the render warps the picture. */
test('WS-132 Corner pin moves the picture’s top-left corner in the render', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const id = await clipTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = await renderAndGrid(ed, id, seed, api);
  await ed.selectClipAt(15, 0);
  await openCompose(ed);
  await composeEntry(ed, 'pin');
  await ed.tap(ed.testId('compose-pin-toggle'));
  // A convex pinch: the top-left corner slides along the top edge, so the left strip of the frame is emptied.
  await ed.page.getByTestId('compose-pin-topLeft').getByLabel('x').fill('0.4');
  await ed.expectServer((t) => {
    const p = t.document.tracks[0].items[0].cornerPin as { topLeft?: { x: number; y: number } } | undefined;
    return p?.topLeft?.x === 0.4 && p.topLeft.y === 0;
  }, 'the corner is stored');
  const pinned = await renderAndGrid(ed, id, seed, api);
  expect(cellLuma(base.grid, 2, 2), 'baseline top-left is picture').toBeGreaterThan(20);
  expect(cellLuma(pinned.grid, 2, 2), 'the pinned corner is empty').toBeLessThan(cellLuma(base.grid, 2, 2) * 0.4);
  expect(cellLuma(pinned.grid, 28, 15), 'the opposite corner is still picture').toBeGreaterThan(10);
});

/** WS-133/134/135: Stabilise, Reframe and Slow motion quality: stored, and the renders complete (reframe changes the crop). */
test('WS-134 Reframe holds the subject point in a vertical render; Stabilise and Slow motion renders complete', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(480_000);
  const id = await clipTimeline(freshTimeline, api, seed, 30, 'clipA.mp4');
  const ed = await openEditor(id);
  await ed.selectClipAt(15, 0);
  await openCompose(ed);
  await composeEntry(ed, 'reframe');
  await ed.tap(ed.testId('mask-reframe-add-reframe'));
  await ed.expectServer((t) => !!t.document.tracks[0].items[0].reframe, 'reframe is stored');
  const setX = async (frac: number) => {
    const value = await clickSlider(ed.page, ed.testId('mask-reframe-point-x').locator('.mantine-Slider-root').first(), frac);
    expect(Math.abs(value - frac), 'the slider moved to where it was clicked').toBeLessThan(0.1);
  };
  const vertical = async () => {
    await ed.workspace('deliver');
    await ed.tap(ed.testId('render-preset'));
    await ed.tap(ed.page.getByRole('option', { name: /^Vertical/ }));
    return renderAndGrid(ed, id, seed, api);
  };
  await setX(0.05);
  await ed.expectServer((t) => ((t.document.tracks[0].items[0].reframe as { x?: number }).x ?? 1) < 0.2, 'subject x near the left is stored');
  const left = await vertical();
  await ed.workspace('compose');
  await setX(0.95);
  await ed.expectServer((t) => ((t.document.tracks[0].items[0].reframe as { x?: number }).x ?? 0) > 0.8, 'subject x near the right is stored');
  const right = await vertical();
  const dist = left.grid.reduce((s, v, i) => s + Math.abs(v - right.grid[i]), 0) / left.grid.length;
  expect(dist, 'cropping around the left vs the right of the frame gives different pictures').toBeGreaterThan(8);

  // WS-133 Stabilise and WS-135 optical-flow slow motion: stored, and the render completes.
  await ed.workspace('compose');
  await composeEntry(ed, 'reframe');
  await ed.page.getByTestId('mask-reframe-remove-reframe').click();
  await composeEntry(ed, 'motion');
  await ed.page.getByTestId('compose-stabilize').check({ force: true });
  await ed.tap(ed.testId('compose-retime-quality'));
  await ed.tap(ed.page.getByRole('option', { name: /Optical flow/ }));
  await ed.expectServer((t) => { const c = t.document.tracks[0].items[0]; return c.stabilize === true && c.retimeQuality === 'opticalFlow'; }, 'stabilise and optical flow are stored');
  await api.putDocument(seed.projectId, id, (doc) => { doc.tracks[0].items[0].playbackRate = 0.5; doc.tracks[0].items[0].recordOut = 30; });
  await ed.reload();
  await ed.workspace('deliver');
  await ed.tap(ed.testId('render-preset'));
  await ed.tap(ed.page.getByRole('option', { name: /^Full HD landscape/ }));
  const r = await renderFromDeliver(ed, id);
  test.info().annotations.push({ type: 'stabilise-render', description: `${r.status} ${r.errorMessage ?? ''}` });
  expect(r.status, `a clip with stabilise + optical flow renders (${r.errorMessage ?? ''})`).toBe('Passed');
  expect(probe(await downloadRender(api, seed.projectId, r)).durationSec).toBeGreaterThan(0.9);
});

/** WS-123..128 + TL-353..355: an insert’s matte and quad keyframes; the key chips seek to their key. */
test('WS-128 Insert: the quad key chips are live buttons that move the playhead to their key; Add/Remove key and Reset edit the quad', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const id = await freshTimeline('blank');
  const q = (x: number) => ({ topLeft: { x, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } });
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [
      mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 150),
      { type: 'insertClip', id: crypto.randomUUID(), name: 'Screen insert', recordIn: 0, recordOut: 150, assetRef: { kind: 'projectFile', projectFileId: seed.files.still, stepResultId: null }, enabled: true, quad: { value: q(0), keys: [{ frame: 0, value: q(0), interp: 'linear' }, { frame: 60, value: q(0.2), interp: 'linear' }] } },
    ];
    // the insert sits on its own track so both are selectable
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    const insert = v.items.pop()!;
    const top = structuredClone(v);
    top.id = crypto.randomUUID();
    top.name = 'Inserts';
    top.items = [insert];
    doc.tracks.splice(1, 0, top);
  });
  const ed = await openEditor(id);
  await ed.selectClipAt(30, 1);
  await openCompose(ed);
  await composeEntry(ed, 'quad');
  await expect(ed.testId('compose-quad-key-60')).toBeVisible();

  // WS-128: the chip for the key at frame 60 (00:00:02:00) is enabled and seeks there.
  const chip = ed.testId('compose-quad-key-60');
  await expect(chip).toBeEnabled();
  await chip.click();
  await expect(ed.testId('status-timecode')).toHaveText('00:00:02:00');
  await ed.testId('compose-quad-key-0').click();
  await expect(ed.testId('status-timecode')).toHaveText('00:00:00:00');

  // WS-125 add a key at the playhead (frame 30), WS-126 remove it, WS-127 reset corners at a key.
  await ed.seek(30);
  await ed.tap(ed.testId('compose-quad-add-key'));
  await ed.expectServer((t) => ((t.document.tracks[1].items[0].quad as { keys: unknown[] }).keys.length) === 3, 'a third key is stored');
  await ed.tap(ed.testId('compose-quad-remove-key'));
  await ed.expectServer((t) => ((t.document.tracks[1].items[0].quad as { keys: unknown[] }).keys.length) === 2, 'the key is removed again');
  await ed.seek(60);
  await ed.page.getByRole('button', { name: 'Reset corners' }).click();
  await ed.expectServer((t) => {
    const k = (t.document.tracks[1].items[0].quad as { keys: Array<{ frame: number; value: { topLeft: { x: number } } }> }).keys.find((x) => x.frame === 60);
    return k?.value.topLeft.x === 0;
  }, 'reset puts the key back on the unit square');

  // WS-124 edit a corner number at the key; WS-123 matte switch stores the default matte.
  await ed.page.getByTestId('compose-quad-topLeft').getByLabel('x').fill('0.15');
  await ed.expectServer((t) => (t.document.tracks[1].items[0].quad as { keys: Array<{ frame: number; value: { topLeft: { x: number } } }> }).keys.find((x) => x.frame === 60)?.value.topLeft.x === 0.15, 'the corner edit goes to the key at the playhead');
  await composeEntry(ed, 'matte');
  await ed.tap(ed.testId('compose-matte-toggle'));
  await ed.expectServer((t) => (t.document.tracks[1].items[0].matte as { color?: string } | null)?.color === '#00FF00', 'the default matte is stored');
});

/** WS-136: the node view lights exactly the stages the clip uses. */
test('WS-136 the node view dims the stages a clip does not use and lights the ones it does', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const id = await clipTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  await ed.selectClipAt(15, 0);
  await openCompose(ed);
  const node = (label: RegExp) => ed.page.locator('.react-flow__node', { hasText: label });
  await composeEntry(ed, 'chain');
  await expect(node(/^Mask$/)).toHaveCSS('opacity', '0.4');
  await composeEntry(ed, 'mask');
  await ed.tap(ed.testId('mask-reframe-add-mask'));
  await composeEntry(ed, 'chain');
  await expect(node(/^Mask$/)).toHaveCSS('opacity', '1');
  await expect(node(/^Corner pin$/)).toHaveCSS('opacity', '0.4');
  await composeEntry(ed, 'pin');
  await ed.tap(ed.testId('compose-pin-toggle'));
  await composeEntry(ed, 'chain');
  await expect(node(/^Corner pin$/)).toHaveCSS('opacity', '1');
});
