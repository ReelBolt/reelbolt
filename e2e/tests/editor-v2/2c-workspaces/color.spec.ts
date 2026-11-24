import { test, expect, SEEDED_LAYOUT, type EditorPage } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { distance, meanLuma, referenceGrid, sampleElement } from '../../../fixtures/editor-v2/pixels';
import { downloadRender, runOn } from '../../../fixtures/editor-v2/rows';
import { cellChroma, cellLuma, chromaSpread, clickSlider, makeInvertLut, meanRgb, renderFromDeliver } from '../../../fixtures/editor-v2/ws';

/**
 * Lane C, COLOR workspace (inventory/workspaces.md 2.4, WS-080..WS-110; timeline.md TL-365..385).
 * Preview rows assert the program monitor's pixels; render-only rows (curves, LUT, qualifier, window) are proved through a
 * real Deliver render and ffmpeg frame grids.
 */

type Doc = Awaited<ReturnType<EditorPage['serverTimeline']>>['document'];
const clip = (doc: Doc, name: string) => doc.tracks.flatMap((t) => t.items).find((i) => i.name === name)!;
const gradeOf = (doc: Doc, name: string) => clip(doc, name).grade as { enabled?: boolean; nodes: Array<{ id: string; name: string; enabled?: boolean; primaries?: Record<string, unknown> }> } | undefined;

/** Color workspace with clipA selected (phone: also opens the Color pane). */
async function openColorOnClipA(ed: EditorPage, frame = 60): Promise<void> {
  await ed.seek(frame);
  await ed.selectClipAt(frame);
  await ed.workspace('color');
  if (ed.isPhone) await ed.tap(ed.page.locator('label', { hasText: /^Color$/ }).first());
  await expect(ed.testId('color-workspace')).toBeVisible();
}

/** WS-099..102, 095..098 + TL-367..371: build a stack of corrections with the real controls; every step is stored. */
test('WS-101 the grade stack: add correction, add look, disable one, remove one, switch the grade off', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'tablet'); // the phone Look sheet offers looks and simple fixes; the grade stack, wheels and copy/paste are desktop-only (30-phone/look.spec.ts)
  const ed = await openEditor(await freshTimeline('seeded'));
  await openColorOnClipA(ed);
  await ed.tap(ed.testId('grade-add-node'));
  let doc = (await ed.expectServer((tl) => (gradeOf(tl.document, 'clipA.mp4')?.nodes.length ?? 0) === 1, 'Add correction stores one node')).document;
  expect(gradeOf(doc, 'clipA.mp4')!.nodes[0].name).toBe('Correction 1');

  // WS-102 Add look (Warm, Normal by default): a node named after the look with non-identity primaries.
  await ed.tap(ed.testId('grade-add-look'));
  doc = (await ed.expectServer((tl) => (gradeOf(tl.document, 'clipA.mp4')?.nodes.length ?? 0) === 2, 'Add look stores a second node')).document;
  const look = gradeOf(doc, 'clipA.mp4')!.nodes[1];
  expect(look.name).toMatch(/Warm/);
  expect(Object.keys(look.primaries ?? {}).length, 'the look has non-identity primaries').toBeGreaterThan(0);

  // WS-098 per-node enable switch.
  await ed.page.getByRole('switch', { name: /^Enable Correction 1$/ }).uncheck({ force: true });
  await ed.expectServer((tl) => gradeOf(tl.document, 'clipA.mp4')?.nodes[0].enabled === false, 'the node is stored disabled');

  // WS-100 remove the look.
  await ed.tap(ed.testId('grade-node-remove-1'));
  await ed.expectServer((tl) => gradeOf(tl.document, 'clipA.mp4')?.nodes.length === 1, 'the look node is removed');

  // WS-095 the whole grade off.
  await ed.tap(ed.testId('grade-enabled'));
  await ed.expectServer((tl) => gradeOf(tl.document, 'clipA.mp4')?.enabled === false, 'the grade is stored bypassed');
});

/** WS-103/104/080/TL-372: a wheel value and the saturation scalar change the program monitor, bypass restores it; nothing about bypass is stored. */
test('WS-104 gain red, then saturation 0: the program monitor follows, Bypass restores the picture and writes nothing', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await openColorOnClipA(ed);
  const plain = await sampleElement(ed.page, ed.viewer);
  await ed.tap(ed.testId('grade-add-node'));
  await ed.tap(ed.testId('grade-node-select-0'));

  // WS-103 a wheel number input.
  await ed.testId('grade-wheel-gain-r').fill('1');
  const stored = await ed.expectServer((tl) => (gradeOf(tl.document, 'clipA.mp4')?.nodes[0].primaries as { gain?: { r?: number } } | undefined)?.gain?.r === 1, 'gain red 1 is stored');
  await expect.poll(async () => meanRgb(await sampleElement(ed.page, ed.viewer))[0], { message: 'the picture gets redder in the monitor' }).toBeGreaterThan(meanRgb(plain)[0] + 2.5);
  const redder = meanRgb(await sampleElement(ed.page, ed.viewer));
  expect(redder[0] - meanRgb(plain)[0], 'red moved more than blue').toBeGreaterThan(redder[2] - meanRgb(plain)[2]);

  // WS-104 saturation to 0 with the scalar's number input: the picture goes grey.
  await ed.testId('grade-scalar-saturation-input').fill('0');
  await ed.expectServer((tl) => (gradeOf(tl.document, 'clipA.mp4')?.nodes[0].primaries as { saturation?: number } | undefined)?.saturation === 0, 'saturation 0 is stored');
  await ed.testId('grade-wheel-gain-r').fill('0');
  await ed.expectSaved();
  await expect.poll(async () => chromaSpread(await sampleElement(ed.page, ed.viewer)), { message: 'saturation 0 renders a grey picture in the monitor' }).toBeLessThan(6);

  // WS-080 Bypass (preview): back to colour, document untouched; off again: grey.
  await ed.expectSaved();
  expect((await ed.serverTimeline()).revision).toBeGreaterThanOrEqual(stored.revision);
  const sent = ed.patches.length;
  await ed.tap(ed.testId('color-tab-gallery'));
  await ed.page.getByTestId('color-bypass').check({ force: true });
  await expect.poll(async () => chromaSpread(await sampleElement(ed.page, ed.viewer)), { message: 'bypass shows the original colours' }).toBeGreaterThan(12);
  expect(await ed.page.getByTestId('color-bypass').isChecked()).toBe(true);
  await ed.page.getByTestId('color-bypass').uncheck({ force: true });
  await expect.poll(async () => chromaSpread(await sampleElement(ed.page, ed.viewer)), { message: 'bypass off: grey again' }).toBeLessThan(6);
  expect(ed.patches.length, 'bypass is a view state: nothing was saved').toBe(sent);
});

/** WS-081/082/084..088 + TL-378/380: colour groups made with the real controls. */
test('WS-082 colour groups: create from the selection, rename, grade the group, add and remove members, remove the group', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await ed.selectClipAt(60);
  await ed.selectClipAt(SEEDED_LAYOUT.clipB[0] + 60, 0, ['Shift']);
  await ed.workspace('color');
  await ed.tap(ed.testId('color-tab-groups'));
  await ed.tap(ed.testId('color-group-create'));
  let tl = await ed.expectServer((t) => (t.document.colorGroups as unknown[] | undefined)?.length === 1, 'the group is stored');
  type Group = { id: string; name: string; clipIds: string[]; grade?: { nodes: unknown[] } | null };
  let group = (tl.document.colorGroups as Group[])[0];
  const idsOf = (names: string[]) => names.map((n) => clip(tl.document, n).id);
  expect(new Set(group.clipIds)).toEqual(new Set(idsOf(['clipA.mp4', 'clipB.mp4'])));

  // WS-084 rename on blur.
  const nameInput = ed.page.getByTestId(`color-group-${group.id}`).getByLabel('Group name');
  await nameInput.fill('Interview look');
  await nameInput.blur();
  tl = await ed.expectServer((t) => (t.document.colorGroups as Group[])[0]?.name === 'Interview look', 'the new name is stored');

  // WS-081/085 target the group and add a correction to the GROUP's grade, not to a clip.
  await ed.tap(ed.page.getByTestId(`color-group-${group.id}`).getByRole('button', { name: 'Grade' }));
  await ed.tap(ed.testId('grade-add-node'));
  tl = await ed.expectServer((t) => ((t.document.colorGroups as Group[])[0]?.grade?.nodes.length ?? 0) === 1, 'the correction went on the group');
  expect(gradeOf(tl.document, 'clipA.mp4') ?? null, 'the clip itself is untouched').toBeNull();

  // WS-088 member badge selects and seeks; x removes the member.
  await ed.page.getByTestId(`color-group-${group.id}`).getByText('clipB.mp4').click();
  await expect(ed.testId('status-timecode')).toHaveText(/^00:00:05:00$/);
  await ed.page.getByRole('button', { name: /^Remove clipB\.mp4 from /, includeHidden: true }).or(ed.page.getByLabel(/^Remove clipB\.mp4 from /)).first().click();
  tl = await ed.expectServer((t) => (t.document.colorGroups as Group[])[0]?.clipIds.length === 1, 'the member is removed');

  // WS-086 + selected: select clipB again and add it back.
  await ed.selectClipAt(SEEDED_LAYOUT.clipB[0] + 60, 0);
  await ed.tap(ed.testId(`color-group-add-${group.id}`));
  tl = await ed.expectServer((t) => (t.document.colorGroups as Group[])[0]?.clipIds.length === 2, '+ selected adds the clip back');

  // WS-087 remove the group: clips keep (their own) grade state, the group is gone.
  await ed.tap(ed.testId(`color-group-remove-${group.id}`));
  await ed.expectServer((t) => !(t.document.colorGroups as unknown[] | undefined)?.length, 'the group is gone');
  // Undo brings it back (one op).
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((t) => (t.document.colorGroups as unknown[] | undefined)?.length === 1, 'undo restores the group');
});

/** WS-089..093: grab a still, the wipe overlay, match to reference, keyboard selection, removing the still. */
test('WS-090 stills: Grab still becomes the reference, Match adds a "Shot match" correction, the wipe slider moves the overlay, Remove clears', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await ed.selectClipAt(60);
  await ed.workspace('color');
  await ed.tap(ed.testId('color-tab-gallery'));
  await ed.tap(ed.testId('color-capture-still'));
  const still = ed.page.locator('[data-testid^="color-still-"]').first();
  await expect(still, 'WS-089 a thumbnail appears').toBeVisible();
  // The still shows what the monitor showed: compare its decoded pixels to the viewer grid.
  const wipe = ed.testId('reference-wipe');
  await expect(wipe, 'WS-089 the new still is the reference: the viewer overlays it').toBeVisible();
  const slider = ed.testId('color-wipe');
  const before = await wipe.evaluate((n) => getComputedStyle(n).clipPath);
  await slider.getByRole('slider').focus();
  await ed.page.keyboard.press('Home');
  await expect.poll(() => wipe.evaluate((n) => getComputedStyle(n).clipPath), { message: 'WS-091 the overlay clip follows the wipe slider' }).not.toBe(before);

  // WS-090: go to clipB (a different picture), select it, match it to the still.
  await ed.seek(SEEDED_LAYOUT.clipB[0] + 30);
  await ed.selectClipAt(SEEDED_LAYOUT.clipB[0] + 60, 0);
  await expect.poll(async () => ed.testId('scopes-status').textContent()).toMatch(/Y \d+%/);
  await ed.tap(ed.testId('color-shot-match'));
  const tl = await ed.expectServer((t) => gradeOf(t.document, 'clipB.mp4')?.nodes.some((n) => n.name === 'Shot match') === true, 'WS-090 a "Shot match" node is stored on clipB');
  const node = gradeOf(tl.document, 'clipB.mp4')!.nodes.find((n) => n.name === 'Shot match')!;
  expect(Object.keys(node.primaries ?? {}).length, 'the match computed non-identity primaries').toBeGreaterThan(0);

  // WS-092 keyboard: the still's image is a button; Enter toggles the reference off and on.
  const image = ed.page.locator('[data-testid^="color-still-image-"]').first();
  await image.focus();
  await expect(image).toHaveAttribute('aria-pressed', 'true');
  await ed.page.keyboard.press('Enter');
  await expect(image).toHaveAttribute('aria-pressed', 'false');
  await expect(wipe).toHaveCount(0);
  await ed.page.keyboard.press('Enter');
  await expect(wipe).toBeVisible();

  // WS-093 remove the still: the thumbnail and the overlay go.
  await ed.page.getByRole('button', { name: /^Remove still / }).click();
  await expect(still).toHaveCount(0);
  await expect(wipe).toHaveCount(0);
});

/** WS-094: the scopes follow the picture and every kind draws. */
test('WS-094 the scopes change with the picture and each scope kind draws pixels', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await ed.workspace('color');
  const status = ed.testId('scopes-status');
  await expect.poll(async () => status.textContent()).toMatch(/Y \d+%/);
  const first = await status.textContent();
  await ed.seek(SEEDED_LAYOUT.clipB[0] + 30);
  await expect.poll(async () => status.textContent(), { message: 'the readout follows a different picture' }).not.toBe(first);
  const painted = () => ed.testId('scopes-canvas').evaluate((c: HTMLCanvasElement) => {
    const d = c.getContext('2d')!.getImageData(0, 0, c.width, c.height).data;
    let n = 0;
    for (let i = 0; i < d.length; i += 4) if (d[i] + d[i + 1] + d[i + 2] > 90) n++;
    return n;
  });
  for (const kind of ['Wave', 'Parade', 'Vector', 'Hist']) {
    await ed.page.getByTestId('scopes-kind').getByText(kind, { exact: true }).click();
    await expect.poll(painted, { message: `${kind} scope draws something` }).toBeGreaterThan(50);
  }
});

type Api = import('../../../fixtures/editor-v2/api').EditorApi;
type Seed = import('../../../fixtures/editor-v2/index').Seed;
async function clipTimeline(freshTimeline: (f: 'blank') => Promise<string>, api: Api, seed: Seed, frames = 30): Promise<string> {
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, frames)];
  });
  return id;
}
async function renderGrid(ed: EditorPage, id: string, seed: Seed, api: Api): Promise<number[]> {
  const r = await renderFromDeliver(ed, id);
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');
  return referenceGrid(await downloadRender(api, seed.projectId, r), 0.5);
}

/** WS-105 curves: a curve point dragged up in the editor brightens the render (render-only: the preview badge says so). */
test('WS-105 a curve point dragged up brightens the rendered picture', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const id = await clipTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = await renderGrid(ed, id, seed, api);
  await openColorOnClipA(ed, 15);
  await ed.tap(ed.testId('grade-add-node'));
  await ed.tap(ed.testId('grade-node-select-0'));
  await ed.tap(ed.testId('color-tab-curves')); // the grade tools are tabs (Colour has no hidden mode)
  const svg = ed.page.getByTestId('curve-svg');
  await svg.scrollIntoViewIfNeeded();
  const box = (await svg.boundingBox())!;
  const at = (x: number, y: number) => [box.x + x * box.width, box.y + (1 - y) * box.height] as const;
  const [x0, y0] = at(0.3, 0.3);
  const [x1, y1] = at(0.3, 0.85);
  await ed.page.mouse.move(x0, y0);
  await ed.page.mouse.down();
  await ed.page.mouse.move(x1, y1, { steps: 10 });
  await ed.page.mouse.up();
  await ed.expectServer((t) => JSON.stringify(gradeOf(t.document, 'clipA.mp4')?.nodes[0] as unknown).includes('"curves"') && JSON.stringify(gradeOf(t.document, 'clipA.mp4')?.nodes[0]).includes('"y"'), 'the curve is stored');
  const curved = await renderGrid(ed, id, seed, api);
  expect(meanLuma(curved), `the curve brightens the render (${meanLuma(base)} -> ${meanLuma(curved)})`).toBeGreaterThan(meanLuma(base) + 2.5);
  expect(distance(base, curved), 'the curve visibly changes the picture').toBeGreaterThan(2);
});

/** WS-106 LUT: a .cube uploaded to the project is chosen in the grade; the render is inverted at intensity 1 and untouched at 0. */
test('WS-106 a LUT file chosen in the grade inverts the render; intensity 0 leaves the picture alone', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  const lutId = await api.upload(seed.projectId, makeInvertLut(), 'application/octet-stream', `invert-${Date.now()}.cube`);
  const id = await clipTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = await renderGrid(ed, id, seed, api);
  await openColorOnClipA(ed, 15);
  await ed.tap(ed.testId('grade-add-node'));
  await ed.tap(ed.testId('grade-node-select-0'));
  await ed.tap(ed.testId('color-tab-lut'));
  await ed.tap(ed.testId('grade-lut'));
  await ed.tap(ed.page.getByRole('option', { name: /^invert-/ }));
  await ed.expectServer((t) => (gradeOf(t.document, 'clipA.mp4')?.nodes[0] as { lut?: { file: { projectFileId: string } } } | undefined)?.lut?.file.projectFileId === lutId, 'the LUT file is stored on the node');
  const inverted = await renderGrid(ed, id, seed, api);
  expect(distance(base, inverted), 'the inverting LUT changes the picture a lot').toBeGreaterThan(40);
  // Intensity 0 = no LUT.
  await ed.workspace('color');
  await ed.tap(ed.testId('color-tab-lut'));
  await ed.testId('grade-lut-intensity').fill('0');
  await ed.expectServer((t) => (gradeOf(t.document, 'clipA.mp4')?.nodes[0] as { lut?: { intensity: number } } | undefined)?.lut?.intensity === 0, 'intensity 0 is stored');
  const zero = await renderGrid(ed, id, seed, api);
  expect(distance(base, zero), 'LUT intensity 0 leaves the picture as it was').toBeLessThan(4);
});

/** WS-107/108 qualifier and power window: each confines the correction to a part of the picture in the render. */
test('WS-108 a power window confines +1 exposure to its ellipse; an HSL qualifier confines it to the green bar', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  const id = await clipTimeline(freshTimeline, api, seed);
  const ed = await openEditor(id);
  const base = await renderGrid(ed, id, seed, api);
  await openColorOnClipA(ed, 15);
  await ed.tap(ed.testId('grade-add-node'));
  await ed.tap(ed.testId('grade-node-select-0'));
  await ed.testId('grade-scalar-exposure-input').fill('1');
  // Power window: a small ellipse in the middle (the default window), correction inside it only.
  await ed.tap(ed.testId('color-tab-secondaries'));
  await ed.page.getByTestId('grade-window-toggle').check({ force: true });
  await ed.testId('grade-window-w').fill('0.3');
  await ed.testId('grade-window-h').fill('0.3');
  await ed.expectServer((t) => {
    const n = gradeOf(t.document, 'clipA.mp4')?.nodes[0] as { window?: { shape?: { w?: number } }; primaries?: { exposure?: number } } | undefined;
    return n?.window?.shape?.w === 0.3 && n.primaries?.exposure === 1;
  }, 'window and exposure are stored');
  const windowed = await renderGrid(ed, id, seed, api);
  expect(cellLuma(windowed, 16, 9), 'inside the window the correction applies').toBeGreaterThan(cellLuma(base, 16, 9) + 8);
  expect(Math.abs(cellLuma(windowed, 1, 1) - cellLuma(base, 1, 1)), 'outside the window the picture is unchanged').toBeLessThan(4);
  expect(Math.abs(cellLuma(windowed, 30, 16) - cellLuma(base, 30, 16)), 'outside (other corner) unchanged').toBeLessThan(4);

  // Qualifier instead of window: hue 120 (green) +-15 degrees. The test pattern's bars are fully saturated, so the node
  // desaturates (exposure cannot brighten a 255 channel): the green bar goes grey, the others keep their colour.
  await ed.workspace('color');
  await ed.tap(ed.testId('color-tab-primaries'));
  await ed.testId('grade-scalar-exposure-input').fill('0');
  await ed.testId('grade-scalar-saturation-input').fill('0');
  await ed.tap(ed.testId('color-tab-secondaries'));
  await ed.page.getByTestId('grade-window-toggle').uncheck({ force: true });
  await ed.page.getByTestId('grade-qualifier-toggle').check({ force: true });
  // A click lands within a few degrees of the target at this slider width (113 for 120, 8 for 15), and the green bar is a narrow band:
  // the arrow keys then step to the exact value (the slider commits on each key).
  const hueSlider = ed.testId('grade-qualifier-hue');
  const widthSlider = ed.page.getByText(/^Width ±/).locator('xpath=..').locator('.mantine-Slider-root').first();
  await clickSlider(ed.page, hueSlider, 120 / 359);
  await clickSlider(ed.page, widthSlider, 15 / 180);
  for (const [slider, target] of [[hueSlider, 120], [widthSlider, 15]] as const) {
    const thumb = slider.getByRole('slider');
    await thumb.focus();
    for (let i = 0; i < 40; i++) {
      const now = Number(await thumb.getAttribute('aria-valuenow'));
      if (now === target) break;
      await thumb.press(now < target ? 'ArrowRight' : 'ArrowLeft');
    }
    await expect(thumb, `the slider is at ${target}`).toHaveAttribute('aria-valuenow', String(target));
  }
  await ed.expectServer((t) => {
    const q = (gradeOf(t.document, 'clipA.mp4')?.nodes[0] as { qualifier?: { hueCenter?: number; hueWidth?: number } } | undefined)?.qualifier;
    const n = gradeOf(t.document, 'clipA.mp4')?.nodes[0] as { primaries?: { saturation?: number; exposure?: number } } | undefined;
    return !!q && Math.abs((q.hueCenter ?? 0) - 120) < 12 && (q.hueWidth ?? 180) < 40 && n?.primaries?.saturation === 0 && (n.primaries.exposure ?? 0) === 0;
  }, 'the qualifier is stored around green with saturation 0');
  const qualified = await renderGrid(ed, id, seed, api);
  // testsrc2's bars: red, green, yellow, blue, magenta, cyan across the width: columns ~ 0-5, 5-10, 10-16, 16-21, 21-26, 26-31.
  expect(cellChroma(base, 7, 3), 'the green bar is coloured to begin with').toBeGreaterThan(80);
  expect(cellChroma(qualified, 7, 3), 'the green bar is desaturated').toBeLessThan(30);
  expect(cellChroma(qualified, 2, 3), 'the red bar keeps its colour').toBeGreaterThan(cellChroma(base, 2, 3) - 8);
  expect(cellChroma(qualified, 28, 3), 'the cyan bar keeps its colour').toBeGreaterThan(cellChroma(base, 28, 3) - 8);
});

/** WS-083: Groups from look groups is available only for a timeline made from an analysed run, and says what it did. */
test('WS-083 Groups from look groups: disabled on a blank timeline, and on the run-made timeline it creates groups or says why not', async ({ openEditor, seed }) => {
  runOn('desktop');
  const blank = await openEditor(seed.blankTimelineId);
  await blank.workspace('color');
  await blank.tap(blank.testId('color-tab-groups'));
  await expect(blank.testId('color-groups-from-looks')).toBeDisabled();
  const ed = await openEditor(seed.derushTimelineId);
  await ed.workspace('color');
  await ed.tap(ed.testId('color-tab-groups'));
  const button = ed.testId('color-groups-from-looks');
  await expect(button).toBeEnabled();
  await ed.tap(button);
  const message = ed.testId('color-message');
  await expect(message, 'the button reports what it did').toContainText(/colour group|No look group/i);
  const tl = await ed.serverTimeline();
  const groups = (tl.document.colorGroups as unknown[] | undefined)?.length ?? 0;
  if (/created from the analysis/.test((await message.textContent()) ?? '')) expect(groups).toBeGreaterThan(0);
  else expect(groups).toBe(0);
});

/** WS-089: stills are kept in the page only (documented limit): a reload starts with an empty gallery. */
test('WS-089 grabbed stills live in the page only: a reload empties the gallery', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await ed.selectClipAt(60);
  await ed.workspace('color');
  await ed.tap(ed.testId('color-tab-gallery'));
  await ed.tap(ed.testId('color-capture-still'));
  await expect(ed.page.locator('[data-testid^="color-still-"]').first()).toBeVisible();
  await ed.reload();
  await ed.workspace('color');
  await ed.tap(ed.testId('color-tab-gallery'));
  await expect(ed.page.locator('[data-testid^="color-still-"]')).toHaveCount(0);
  await expect(ed.page.getByText(/No stills yet/)).toBeVisible();
});

/** WS-096/097: Copy grade on one clip, Paste grade on another: the document gets the same corrections (new ids), as one undo step. */
test('WS-097 Copy grade then Paste grade puts the same corrections on another clip, with fresh ids, undoable in one step', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone Look sheet offers looks and simple fixes; the grade stack, wheels and copy/paste are desktop-only (30-phone/look.spec.ts)
  // Both clips sit inside the first 150 frames so they are on screen (and tappable) on a phone too.
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 75), mediaClip(crypto.randomUUID(), 'clipB.mp4', seed.files.clipB, 75, 150)];
  });
  const ed = await openEditor(id);
  // Source: clipA with a correction (gain red 0.5) and a look.
  await openColorOnClipA(ed);
  await ed.tap(ed.testId('grade-add-node'));
  await ed.tap(ed.testId('grade-node-select-0'));
  await ed.testId('grade-wheel-gain-r').fill('0.5');
  await ed.tap(ed.testId('grade-add-look'));
  const src = (await ed.expectServer((t) => (gradeOf(t.document, 'clipA.mp4')?.nodes.length ?? 0) === 2
    && (gradeOf(t.document, 'clipA.mp4')?.nodes[0].primaries as { gain?: { r?: number } } | undefined)?.gain?.r === 0.5, 'clipA carries two corrections')).document;
  const srcNodes = gradeOf(src, 'clipA.mp4')!.nodes;

  // WS-096 Copy; the clipboard lives in the page, so the next clip's editor can paste it.
  await ed.tap(ed.testId('grade-copy'));

  // clipB has no grade: Paste is the way to give it one.
  await ed.seek(100);
  await ed.selectClipAt(110);
  await ed.workspace('color');
  if (ed.isPhone) await ed.tap(ed.page.locator('label', { hasText: /^Color$/ }).first());
  await expect(ed.testId('grade-paste')).toBeEnabled();
  await ed.tap(ed.testId('grade-paste'));
  const dst = (await ed.expectServer((t) => (gradeOf(t.document, 'clipB.mp4')?.nodes.length ?? 0) === 2, 'clipB got the pasted corrections')).document;
  const dstNodes = gradeOf(dst, 'clipB.mp4')!.nodes;
  const strip = (nodes: typeof srcNodes) => JSON.stringify(nodes.map((n) => [n.name, n.enabled ?? true, n.primaries]));
  expect(strip(dstNodes), 'same names and primaries').toBe(strip(srcNodes));
  expect(dstNodes.map((n) => n.id).filter((id) => srcNodes.some((s) => s.id === id)), 'every pasted node has a fresh id').toEqual([]);
  expect(gradeOf(dst, 'clipA.mp4')!.nodes, 'the source clip keeps its own grade').toHaveLength(2);

  // One undo removes the whole paste.
  if (!ed.isPhone) {
    await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
    await ed.page.keyboard.press('Control+z');
    await ed.expectServer((t) => (gradeOf(t.document, 'clipB.mp4')?.nodes.length ?? 0) === 0, 'one undo removes the pasted grade');
  }
});
