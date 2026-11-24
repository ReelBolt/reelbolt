import { test, expect } from '../../../fixtures/editor-v2/lane-b-test';
import { mediaClip, type Item, type Timeline } from '../../../fixtures/editor-v2/api';
import { type EditorPage } from '../../../fixtures/editor-v2/editor-page';
import { meanLuma, sampleElement, type Grid } from '../../../fixtures/editor-v2/pixels';
import {
  addVideoTrack,
  allSentOps,
  captionItem,
  generatorItem,
  itemById,
  itemNamed,
  selectOnTrack,
  sentOps,
  titleItem,
  selectAt,
  trackIndex,
} from '../../../fixtures/editor-v2/lane-b';

/**
 * Lane B (editor-v2-playback-verify): the Inspector, row by row (inventory timeline.md 2.J, TL-240..268).
 * Every test edits through the UI and asserts the STORED document (server GET) and, where the viewer shows
 * the effect, the pixels. Runs on desktop, pixel7 and iphone14webkit (the Inspector is the phone's
 * "Inspector" pane).
 */

const fpsOf = (tl: Timeline) => tl.document.settings.rate.num / tl.document.settings.rate.den;
const V1 = (tl: Timeline) => tl.document.tracks[trackIndex(tl.document, 'video')];

/** Mean colour of the whole program monitor, for "the picture changed" checks. */
function meanRgb(grid: Grid): [number, number, number] {
  const n = grid.length / 3;
  let r = 0;
  let g = 0;
  let b = 0;
  for (let i = 0; i < grid.length; i += 3) {
    r += grid[i];
    g += grid[i + 1];
    b += grid[i + 2];
  }
  return [r / n, g / n, b / n];
}

const FINE_W = 128;
const FINE_H = 72;

/** The program monitor at 128x72 (the standard 32x18 grid is too coarse for a box on a phone-sized viewer). */
async function fineLuma(ed: EditorPage): Promise<number[]> {
  const png = await ed.viewer.screenshot({ animations: 'disabled' });
  return ed.page.evaluate(async ({ b64, w, h }) => {
    const img = new Image();
    img.src = `data:image/png;base64,${b64}`;
    await img.decode();
    const c = document.createElement('canvas');
    c.width = w;
    c.height = h;
    const ctx = c.getContext('2d')!;
    ctx.imageSmoothingQuality = 'high';
    ctx.drawImage(img, 0, 0, w, h);
    const d = ctx.getImageData(0, 0, w, h).data;
    const out: number[] = [];
    for (let i = 0; i < d.length; i += 4) out.push(0.2126 * d[i] + 0.7152 * d[i + 1] + 0.0722 * d[i + 2]);
    return out;
  }, { b64: png.toString('base64'), w: FINE_W, h: FINE_H });
}

/** Cells in the lower-left quadrant (where a lower third's box sits) whose luma differs from `base` by more than 25. */
function changedInLowerLeft(base: number[], now: number[]): number {
  let n = 0;
  for (let y = Math.floor(FINE_H * 0.55); y < FINE_H; y++) {
    for (let x = 0; x < FINE_W / 2; x++) if (Math.abs(now[y * FINE_W + x] - base[y * FINE_W + x]) > 25) n++;
  }
  return n;
}

/** Picks an option of a Mantine Select by its visible label. */
async function choose(ed: EditorPage, selectTestId: string, label: string | RegExp): Promise<void> {
  await ed.tap(ed.testId(selectTestId));
  await ed.tap(ed.page.getByRole('option', { name: label }).first());
}

/** A Mantine Switch: its input is visually hidden, the click goes to the track. */
async function flip(ed: EditorPage, testId: string): Promise<void> {
  await ed.testId(testId).click({ force: true });
}

/** Waits until the program monitor settles on a picture: two equal samples in a row. */
async function viewerGrid(ed: EditorPage): Promise<Grid> {
  let last: Grid = await sampleElement(ed.page, ed.viewer);
  await expect
    .poll(async () => {
      const next = await sampleElement(ed.page, ed.viewer);
      const same = next.every((v, i) => Math.abs(v - last[i]) < 2);
      last = next;
      return same;
    }, { message: 'the program monitor settles', timeout: 15_000, intervals: [400] })
    .toBe(true);
  return last;
}

test('TL-240 TL-243 the Inspector asks for a selection, then reads the clip back', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.openInspector();
  await expect(ed.testId('inspector-panel')).toContainText('Select a clip');
  await selectOnTrack(ed, 'video', 30);
  await expect(ed.testId('inspector-name')).toHaveValue('clipA.mp4');
  const panel = ed.testId('inspector-panel');
  await expect(panel.locator('[data-testid="inspector-duration"]'), 'duration readout (R03.3 typed fields)').toHaveValue('150');
  await expect(panel, 'track readout').toContainText(/V1|Video/i);
  await expect(panel).toContainText('Video clip');
});

test('TL-241 TL-242 rename a clip and switch it off: stored, and the viewer stops drawing it', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await selectOnTrack(ed, 'video', 30);
  const before = meanLuma(await viewerGrid(ed));
  expect(before, 'clipA is a bright test pattern').toBeGreaterThan(40);

  await ed.testId('inspector-name').fill('Opening shot');
  await ed.testId('inspector-name').blur();
  await ed.expectServer((tl) => V1(tl).items[0].name === 'Opening shot', 'the new name is stored');

  await flip(ed, 'inspector-enabled');
  await ed.expectServer((tl) => V1(tl).items[0].enabled === false, 'the clip is stored as disabled');
  await expect.poll(async () => meanLuma(await sampleElement(ed.page, ed.viewer)), { message: 'a disabled clip is no longer drawn', timeout: 15_000 }).toBeLessThan(before / 3);

  await ed.reload();
  expect(V1(await ed.serverTimeline()).items[0].enabled).toBe(false);
});

test('S7 TL-248 TL-251 TL-253 the Inspector follows the track a clip sits on', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  // A clip on a PICTURE track: picture controls, no sound controls (its sound is neither previewed nor rendered).
  await selectOnTrack(ed, 'video', 30);
  for (const id of ['inspector-positionX', 'inspector-positionY', 'inspector-scale', 'inspector-rotation', 'inspector-opacity', 'inspector-crop-left', 'inspector-hold', 'inspector-reverse', 'inspector-speed', 'inspector-fade-in']) {
    await expect(ed.testId(id), `${id} is offered on a picture-track clip`).toBeVisible();
  }
  for (const id of ['inspector-gainDb', 'inspector-channel-map']) {
    await expect(ed.page.getByTestId(id), `${id} does nothing on a picture-track clip, so it is not offered`).toHaveCount(0);
  }
  await expect(ed.testId('inspector-picture-track-sound-note')).toContainText(/not played or rendered/i);

  // A clip on a SOUND track: sound controls, no picture controls.
  await selectOnTrack(ed, 'audio', 30);
  await expect(ed.testId('inspector-name')).toHaveValue('tone.wav');
  for (const id of ['inspector-volume', 'inspector-mute', 'inspector-channel-map', 'inspector-speed', 'inspector-reverse', 'inspector-fade-in', 'inspector-fade-out']) {
    await expect(ed.testId(id), `${id} is offered on a sound-track clip`).toBeVisible();
  }
  for (const id of ['inspector-positionX', 'inspector-scale', 'inspector-rotation', 'inspector-opacity', 'inspector-crop-left', 'inspector-hold', 'inspector-extract-audio']) {
    await expect(ed.page.getByTestId(id), `${id} has no effect on a sound-track clip, so it is not offered`).toHaveCount(0);
  }
});

test('S20 TL-248 typing a number is ONE edit: "50" (percent) in Opacity writes a single op and the picture dims', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.seek(30);
  await selectOnTrack(ed, 'video', 30);
  const before = meanLuma(await viewerGrid(ed));
  const opacity = ed.testId('inspector-opacity');
  await opacity.click();
  await opacity.press('Control+a');
  await opacity.pressSequentially('50', { delay: 120 });
  await opacity.press('Tab');
  const tl = await ed.expectServer((t) => {
    const o = (V1(t).items[0].transform as { opacity?: { value?: number } } | null | undefined)?.opacity;
    return o?.value === 0.5;
  }, 'opacity 0.5 is stored');
  expect(V1(tl).items[0].name).toBe('clipA.mp4');
  // One setProperty for the whole typed number (not "0", "0." and "0.5"), so one undo step.
  await expect.poll(() => sentOps(ed, 'setProperty'), { message: 'one setProperty op was sent for the typed number', timeout: 15_000 }).toBe(1);
  expect(allSentOps(ed).filter((o) => o === 'setProperty')).toHaveLength(1);
  await expect.poll(async () => meanLuma(await sampleElement(ed.page, ed.viewer)), { message: 'opacity 0.5 dims the picture', timeout: 15_000 }).toBeLessThan(before * 0.75);
  // One Undo takes the whole edit back (the History tab holds the only Undo button).
  await ed.openPanelTab(/history/i);
  await ed.tap(ed.testId('history-undo'));
  await ed.expectServer((t) => {
    const o = (V1(t).items[0].transform as { opacity?: { value?: number } } | null | undefined)?.opacity;
    return (o?.value ?? 1) === 1;
  }, 'one Undo restores opacity 1');
});

test('TL-244 Speed 2 retimes the clip to half its length and ripples what follows', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectOnTrack(ed, 'video', 30);
  const speed = ed.testId('inspector-speed');
  await speed.fill('2');
  await speed.press('Enter');
  const tl = await ed.expectServer((t) => (V1(t).items[0] as Item & { playbackRate?: number }).playbackRate === 2, 'speed 2 is stored');
  expect(V1(tl).items[0].recordOut - V1(tl).items[0].recordIn, 'half as long').toBe(75);
  expect(V1(tl).items[1].recordIn, 'the next clip rippled left').toBe(75);
  await expect.poll(() => allSentOps(ed), { message: 'a retime op was sent' }).toContain('retime');
  await ed.reload();
  expect((V1(await ed.serverTimeline()).items[0] as Item & { playbackRate?: number }).playbackRate).toBe(2);
});

test('TL-245 TL-246 Reverse and Freeze frame are stored, and exclude each other', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectOnTrack(ed, 'video', 30);
  await flip(ed, 'inspector-reverse');
  await ed.expectServer((t) => V1(t).items[0].reverse === true, 'reverse is stored');
  await expect(ed.testId('inspector-hold'), 'freeze frame is off while reverse is on').toBeDisabled();
  await flip(ed, 'inspector-reverse');
  await ed.expectServer((t) => !V1(t).items[0].reverse, 'reverse is cleared');
  await flip(ed, 'inspector-hold');
  await ed.expectServer((t) => V1(t).items[0].hold === true, 'freeze frame is stored');
  await expect(ed.testId('inspector-reverse'), 'reverse is off while freeze frame is on').toBeDisabled();
});

test('TL-251 TL-252 Crop and Fades are stored', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectOnTrack(ed, 'video', 30);
  const left = ed.testId('inspector-crop-left');
  await left.fill('0.25');
  await left.press('Enter');
  await ed.expectServer((t) => (V1(t).items[0].crop as { left?: number } | undefined)?.left === 0.25, 'crop left 0.25 is stored');
  // R07.3: fades are typed in seconds; the document stores frames.
  const fps = (await ed.serverTimeline()).document.settings.rate;
  const frames = (seconds: number) => Math.round((seconds * fps.num) / fps.den);
  const fadeIn = ed.testId('inspector-fade-in');
  await fadeIn.fill('0.4');
  await fadeIn.press('Enter');
  const fadeOut = ed.testId('inspector-fade-out');
  await fadeOut.fill('0.3');
  await fadeOut.press('Enter');
  const tl = await ed.expectServer((t) => V1(t).items[0].fadeInFrames === frames(0.4) && V1(t).items[0].fadeOutFrames === frames(0.3), 'both fades are stored');
  expect(V1(tl).items[0].fadeInFrames).toBe(frames(0.4));
  await ed.reload();
  const again = V1(await ed.serverTimeline()).items[0];
  expect([again.fadeInFrames, again.fadeOutFrames]).toEqual([frames(0.4), frames(0.3)]);
});

test('S5 TL-253 TL-247 a sound clip: gain is stored, a channel map is stored and the preview plays it, so the badge stays faithful', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectOnTrack(ed, 'audio', 30);
  const A = (t: Timeline) => t.document.tracks[trackIndex(t.document, 'audio')].items[0];
  await expect(ed.testId('inspector-parity'), 'a plain sound clip is faithful').toHaveAttribute('data-parity', 'faithful');

  await ed.tap(ed.testId('inspector-advanced-toggle')); // the dB box lives under Advanced (R07.3)
  const gain = ed.testId('inspector-gainDb');
  await gain.fill('-12');
  await gain.press('Enter');
  await ed.expectServer((t) => (A(t).gainDb as { value?: number } | undefined)?.value === -12, 'gain -12 dB is stored');

  await choose(ed, 'inspector-channel-map', 'Left only, both sides');
  await ed.expectServer((t) => A(t).channelMap === 'left', 'the channel map "left" is stored');
  // The audio graph routes the channels as the render's pan filter does (docs/editor.md, preview table), so a channel map is no longer a render-only finding.
  await expect(ed.testId('inspector-parity'), 'the preview routes the channels like the render, so the badge stays faithful').toHaveAttribute('data-parity', 'faithful');
  await expect(ed.testId('inspector-parity-reason')).not.toContainText(/not previewed/i);

  await choose(ed, 'inspector-channel-map', 'Stereo (as recorded)');
  await ed.expectServer((t) => (A(t).channelMap ?? 'stereo') === 'stereo', 'back to stereo clears the map');
  await expect(ed.testId('inspector-parity')).toHaveAttribute('data-parity', 'faithful');
});

test('TL-255 Relink points the clip at another source and keeps its position', async ({ freshTimeline, openEditor, seed }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectOnTrack(ed, 'video', 30);
  await choose(ed, 'inspector-relink', /clipB\.mp4/);
  const tl = await ed.expectServer((t) => V1(t).items[0].assetRef?.projectFileId === seed.files.clipB, 'the clip now points at clipB');
  expect([V1(tl).items[0].recordIn, V1(tl).items[0].recordOut]).toEqual([0, 150]);
});

test('TL-256..259 add a transition between two clips, change its type and easing, then remove it', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  // clipB gets 60 frames of handle before its first frame, so a transition has footage to reach into.
  await api.putDocument(seed.projectId, id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    v.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 150), mediaClip(crypto.randomUUID(), 'clipB.mp4', seed.files.clipB, 150, 300, { sourceIn: 60 })];
  });
  const ed = await openEditor(id);
  await selectOnTrack(ed, 'video', 30);
  await ed.tap(ed.testId('transition-word-wipeleft'));
  await ed.tap(ed.testId('transition-add'));
  const tl = await ed.expectServer((t) => V1(t).items.some((i) => i.type === 'transition'), 'a transition item is stored');
  const tr = V1(tl).items.find((i) => i.type === 'transition')!;
  expect(tr.transitionType).toBe('wipeleft');
  expect(tr.recordOut - tr.recordIn, 'half a second long').toBe(Math.round(0.5 * fpsOf(tl)));
  expect(tr.recordIn, 'centred on the cut at 150').toBe(150 - Math.floor(Math.round(0.5 * fpsOf(tl)) / 2));
  await expect.poll(() => allSentOps(ed), { message: 'an addTransition op was sent' }).toContain('addTransition');

  // Select the transition on the canvas (it straddles frame 150) and change what it is.
  // The seam glyph covers most of a short transition on the canvas, so pick it from the Edits list.
  await ed.openPanelTab(/edits/i);
  await ed.tap(ed.testId(`edit-index-row-${tr.id}`));
  await ed.openInspector();
  await expect(ed.testId('inspector-panel')).toContainText('transition');
  await ed.tap(ed.testId('transition-word-slideleft'));
  await ed.expectServer((t) => V1(t).items.find((i) => i.type === 'transition')?.transitionType === 'slideleft', 'the new type is stored');
  await choose(ed, 'inspector-transition-easing', 'easeInQuad');
  await ed.expectServer((t) => V1(t).items.find((i) => i.type === 'transition')?.easing === 'easeInQuad', 'the new easing is stored');

  await ed.tap(ed.testId('transition-remove'));
  const gone = await ed.expectServer((t) => !V1(t).items.some((i) => i.type === 'transition'), 'the transition is removed');
  expect(V1(gone).items.map((i) => i.name), 'the two clips are back').toEqual(['clipA.mp4', 'clipB.mp4']);
  expect(V1(gone).items[0].recordOut, 'hard cut at the middle').toBe(150);
});

test('TL-260 TL-261 a generator: change its type and its colour', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  await addVideoTrack(api, seed.projectId, id, 'Cards', [generatorItem('Card', 0, 120)], { dropAudio: true });
  const ed = await openEditor(id);
  const doc = (await ed.serverTimeline()).document;
  const idx = doc.tracks.findIndex((t) => t.name === 'Cards');
  await ed.seek(30);
  await selectAt(ed, 30, idx);
  await ed.openInspector();
  const G = (t: Timeline) => itemNamed(t, 'Card');

  const colour = ed.testId('inspector-generator-color');
  await colour.fill('#ff0000');
  await colour.blur();
  await ed.expectServer((t) => (G(t).generatorConfig as { color?: string }).color === '#ff0000', 'the colour is stored');
  await expect
    .poll(async () => {
      const [r, g, b] = meanRgb(await sampleElement(ed.page, ed.viewer));
      return r > 150 && g < 80 && b < 80;
    }, { message: 'the monitor shows the red card', timeout: 15_000 })
    .toBe(true);

  await choose(ed, 'inspector-generator-type', 'bars');
  await ed.expectServer((t) => G(t).generatorType === 'bars', 'the generator type is stored');
  await expect(ed.testId('inspector-parity'), 'bars preview as the render\'s HD colour bars').toHaveAttribute('data-parity', 'faithful');
  await expect(ed.testId('inspector-parity-reason')).toContainText(/colour bars/i);
});

test('S6 TL-262..265 a title: template, text, size and colour are stored, and the template shows in the viewer', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  await addVideoTrack(api, seed.projectId, id, 'Titles', [titleItem('Hello', 0, 120)], { dropAudio: true });
  const ed = await openEditor(id);
  const idx = (await ed.serverTimeline()).document.tracks.findIndex((t) => t.name === 'Titles');
  await ed.seek(30);
  await selectAt(ed, 30, idx);
  await ed.openInspector();
  const T = (t: Timeline) => itemNamed(t, 'title Hello');

  await viewerGrid(ed); // settled
  const plain = await fineLuma(ed);
  await choose(ed, 'inspector-title-template', 'Lower third');
  await ed.expectServer((t) => T(t).template === 'lowerThird', 'the lower-third template is stored');
  // The template reaches the preview: a dark box under a left-aligned, low title darkens the bottom-left of the picture.
  await expect
    .poll(async () => changedInLowerLeft(plain, await fineLuma(ed)), { message: 'the lower third draws its box low and left in the preview (a plain centred title leaves that quadrant alone)', timeout: 15_000 })
    .toBeGreaterThan(8);
  await expect(ed.testId('inspector-parity-reason')).toContainText('lowerThird');

  await choose(ed, 'inspector-title-template', 'Plain (centred)');
  await ed.expectServer((t) => !T(t).template, 'plain clears the template');

  const text = ed.testId('inspector-text');
  await text.fill('Second line');
  await text.blur();
  await ed.expectServer((t) => T(t).text === 'Second line', 'the text is stored');

  const size = ed.testId('inspector-font-size');
  await size.fill('120');
  await size.press('Enter');
  await ed.expectServer((t) => T(t).fontSize === 120, 'the font size is stored');

  const color = ed.testId('inspector-title-color');
  await color.fill('#00ff00');
  await color.blur();
  await ed.expectServer((t) => T(t).color === '#00ff00', 'the title colour is stored');
});

test('TL-266 a caption: its text is stored from the Inspector', async ({ freshTimeline, openEditor, api, seed }) => {
  const id = await freshTimeline('seeded');
  await addVideoTrack(api, seed.projectId, id, 'Subs', [captionItem('first words', 0, 120)], { dropAudio: true });
  const ed = await openEditor(id);
  const idx = (await ed.serverTimeline()).document.tracks.findIndex((t) => t.name === 'Subs');
  await selectAt(ed, 30, idx);
  await ed.openInspector();
  const text = ed.testId('inspector-caption-text');
  await text.fill('edited words');
  await text.blur();
  await ed.expectServer((t) => itemById(t, (t.document.tracks[idx].items[0] as Item).id).text === 'edited words', 'the caption text is stored');
});

test('TL-267 the Preview parity and Provenance blocks describe the selected clip', async ({ freshTimeline, openEditor }) => {
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectOnTrack(ed, 'video', 30);
  await expect(ed.testId('inspector-parity')).toHaveAttribute('data-parity', 'faithful');
  await expect(ed.testId('inspector-parity-reason')).toContainText(/previewed as rendered/i);
  await expect(ed.testId('inspector-panel')).toContainText(/provenance/i);
  // A retimed clip is previewed approximately (timing yes, pitch-preserving audio and frame blending no): the class changes with the document.
  const speed = ed.testId('inspector-speed');
  await speed.fill('2');
  await speed.press('Enter');
  await speed.blur(); // WebKit on a phone: leave the field as well, whichever of Enter/blur the engine acts on first
  await ed.expectServer((t) => (t.document.tracks[0].items[0] as { playbackRate?: number }).playbackRate === 2, 'speed 2 is stored');
  await expect(ed.testId('inspector-parity')).toHaveAttribute('data-parity', 'approximate');
  await expect(ed.testId('inspector-parity-reason')).toContainText(/fast motion/i);
});

test('TL-240 two selected clips are edited one at a time', async ({ freshTimeline, openEditor }) => {
  test.skip(test.info().project.name !== 'desktop', 'multi-select needs a keyboard modifier');
  const ed = await openEditor(await freshTimeline('seeded'));
  await selectAt(ed, 30);
  await ed.selectClipAt(200, 0, ['Shift']);
  await ed.openInspector();
  await expect(ed.testId('inspector-panel')).toContainText(/2 items selected/);
});
