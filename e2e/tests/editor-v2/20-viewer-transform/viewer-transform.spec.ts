import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { distance, meanLuma, sampleElement } from '../../../fixtures/editor-v2/pixels';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * R07.2 (T-017): direct transform on the viewer. A handle layer over the program frame for the selected
 * clip: drag to move, corner handles to scale, a rotate handle, Fit / Fill, a 3x3 position pad and
 * picture-in-corner presets. Every assertion reads the STORED document; one gesture is one undo step.
 */

type Tl = Awaited<ReturnType<EditorPage['serverTimeline']>>;
type Chan = { value?: number | null; keys?: Array<{ frame: number; value: number }> | null };
type Xf = { positionX?: Chan; positionY?: Chan; scale?: Chan; rotation?: Chan } | null | undefined;
const clipOf = (tl: Tl) => tl.document.tracks.find((t) => t.kind === 'video')!.items[0] as unknown as { transform?: Xf };
const val = (c: Chan | undefined, fallback: number) => (typeof c?.value === 'number' ? c.value : fallback);
const xf = (tl: Tl) => {
  const t = clipOf(tl).transform;
  return { x: val(t?.positionX, 0), y: val(t?.positionY, 0), scale: val(t?.scale, 1), rotation: val(t?.rotation, 0) };
};

const ONE: ClipSpec[] = [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 150 }];

async function open(ft: Parameters<typeof clipsTimeline>[0], api: Parameters<typeof clipsTimeline>[1], seed: Parameters<typeof clipsTimeline>[2], openEditor: (id: string) => Promise<EditorPage>, clips: ClipSpec[] = ONE) {
  const ed = await openEditor(await clipsTimeline(ft, api, seed, clips));
  await ed.selectClipAt(30);
  await ed.seek(30);
  await expect(ed.testId('viewer-xform-body')).toBeVisible({ timeout: 30_000 });
  return ed;
}

const center = async (ed: EditorPage, id: string) => {
  const b = (await ed.testId(id).boundingBox())!;
  return { x: b.x + b.width / 2, y: b.y + b.height / 2, w: b.width, h: b.height };
};

const preset = async (ed: EditorPage, id: string) => {
  await ed.tap(ed.testId('viewer-xform-menu'));
  await ed.tap(ed.testId(id));
};

test('R07.2 dragging the picture writes positionX/positionY, one undo step, and the rendered preview moves', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await open(freshTimeline, api, seed, openEditor);
  // A smaller picture first so the move shows in the pixels (corner preset: scale 0.3, bottom right).
  await preset(ed, 'viewer-xform-corner-br');
  await ed.expectServer((tl) => xf(tl).scale > 0.29 && xf(tl).scale < 0.31, 'the corner preset is stored');
  await ed.page.keyboard.press('Escape');
  const before = await sampleElement(ed.page, ed.viewer);
  const frameBox = (await ed.testId('program-frame').boundingBox())!;
  const revBefore = (await ed.serverTimeline()).revision;
  const c = await center(ed, 'viewer-xform-body');
  const dx = -frameBox.width * 0.3;
  const dy = -frameBox.height * 0.25;
  await ed.mouseDrag(c.x, c.y, c.x + dx, c.y + dy);
  const start = xf(await ed.serverTimeline());
  const tl = await ed.expectServer((t) => Math.abs(xf(t).x - start.x) < 1 && xf(t).x < 0.31 - 0.2, 'the dragged position is stored');
  const after = xf(tl);
  expect(after.x, 'positionX moved by the drag (within snapping tolerance)').toBeCloseTo(0.31 - 0.3, 1);
  expect(after.y, 'positionY moved by the drag').toBeCloseTo(0.31 - 0.25, 1);
  expect(after.scale, 'a move does not touch the scale').toBeCloseTo(0.3, 3);
  await expect.poll(async () => distance(before, await sampleElement(ed.page, ed.viewer)), { message: 'the preview pixels moved', timeout: 15_000 }).toBeGreaterThan(0.5);
  expect(tl.revision, 'one gesture, one stored revision step').toBeLessThanOrEqual(revBefore + 1 + 1);
  // One undo reverts the whole drag, not a fragment of it.
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => Math.abs(xf(t).x - 0.31) < 0.001 && Math.abs(xf(t).y - 0.31) < 0.001, 'a single undo restores the position before the drag');
});

test('R07.2 a corner handle writes scale and rotate handle writes rotation', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await open(freshTimeline, api, seed, openEditor);
  await preset(ed, 'viewer-xform-corner-tl');
  await ed.page.keyboard.press('Escape');
  await ed.expectServer((tl) => xf(tl).scale < 0.31, 'start from the small picture');
  const br = await center(ed, 'viewer-xform-handle-br');
  const body = await center(ed, 'viewer-xform-body');
  await ed.mouseDrag(br.x, br.y, br.x + body.w * 0.4, br.y + body.h * 0.4);
  const scaled = await ed.expectServer((tl) => xf(tl).scale > 0.4, 'a corner drag stores a larger scale');
  expect(xf(scaled).scale).toBeLessThan(0.8);
  const rot = await center(ed, 'viewer-xform-rotate');
  const c = await center(ed, 'viewer-xform-body');
  // The handle follows the pointer: dragged level with the centre it is a quarter turn from "up"
  // (the handle sits above the picture, or below it when the picture touches the top of the frame).
  await ed.mouseDrag(rot.x, rot.y, c.x + 150, c.y);
  const turned = await ed.expectServer((tl) => Math.abs(xf(tl).rotation) > 45, 'the rotate handle stores a rotation');
  expect(Math.abs(xf(turned).rotation)).toBeGreaterThan(80);
  expect(Math.abs(xf(turned).rotation)).toBeLessThan(100);
});

test('R07.2 Fit, Fill, the 3x3 pad and the corner presets store exact values', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await open(freshTimeline, api, seed, openEditor);
  await preset(ed, 'viewer-xform-corner-br');
  let s = xf(await ed.expectServer((tl) => xf(tl).scale < 0.31 && xf(tl).scale > 0.29, 'corner preset stored'));
  expect(s.scale).toBeCloseTo(0.3, 5);
  expect(s.x).toBeCloseTo((1 - 0.3) / 2 - 0.04, 5);
  expect(s.y).toBeCloseTo((1 - 0.3) / 2 - 0.04, 5);
  expect(s.rotation).toBe(0);
  // Pad: top-left keeps the scale and puts the edges flush.
  await ed.page.keyboard.press('Escape');
  await preset(ed, 'viewer-xform-pad-0');
  s = xf(await ed.expectServer((tl) => xf(tl).x < -0.3, 'pad top-left stored'));
  expect(s.x).toBeCloseTo(-(1 - 0.3) / 2, 5);
  expect(s.y).toBeCloseTo(-(1 - 0.3) / 2, 5);
  expect(s.scale).toBeCloseTo(0.3, 5);
  await ed.page.keyboard.press('Escape');
  await preset(ed, 'viewer-xform-pad-4');
  s = xf(await ed.expectServer((tl) => Math.abs(xf(tl).x) < 1e-6 && Math.abs(xf(tl).y) < 1e-6, 'pad centre stored'));
  expect(s.scale).toBeCloseTo(0.3, 5);
  // Fit and Fill on a 16:9 source in a 16:9 canvas are both scale 1, upright, centred.
  await ed.page.keyboard.press('Escape');
  await preset(ed, 'viewer-xform-fill');
  s = xf(await ed.expectServer((tl) => Math.abs(xf(tl).scale - 1) < 1e-6, 'Fill stored'));
  expect(s).toMatchObject({ x: 0, y: 0, rotation: 0 });
  await ed.page.keyboard.press('Escape');
  await preset(ed, 'viewer-xform-corner-tl');
  await ed.expectServer((tl) => xf(tl).scale < 0.31, 'small again');
  await ed.page.keyboard.press('Escape');
  await preset(ed, 'viewer-xform-fit');
  s = xf(await ed.expectServer((tl) => Math.abs(xf(tl).scale - 1) < 1e-6 && Math.abs(xf(tl).x) < 1e-6, 'Fit stored'));
  expect(s).toMatchObject({ x: 0, y: 0, rotation: 0 });
});

test('R07.2 arrow keys nudge the picture by one pixel, Shift by ten', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await open(freshTimeline, api, seed, openEditor);
  await ed.testId('viewer-transform').focus();
  await ed.page.keyboard.press('ArrowRight');
  let s = xf(await ed.expectServer((tl) => xf(tl).x > 0, 'a nudge is stored'));
  expect(s.x).toBeCloseTo(1 / 1920, 5);
  await ed.page.keyboard.press('Shift+ArrowDown');
  s = xf(await ed.expectServer((tl) => xf(tl).y > 0, 'a Shift nudge is stored'));
  expect(s.y).toBeCloseTo(10 / 1080, 5);
});

test('R07.2 on a keyframed clip a placement writes a key at the playhead, not the static value', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const keyed: ClipSpec[] = [{
    name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 150,
    extra: { transform: { positionX: { value: 0, keys: [{ frame: 0, value: -0.1, interp: 'linear' }, { frame: 120, value: 0.1, interp: 'linear' }] } } } as never,
  }];
  const ed = await open(freshTimeline, api, seed, openEditor, keyed);
  await preset(ed, 'viewer-xform-corner-br');
  const tl = await ed.expectServer((t) => (((clipOf(t).transform?.positionX?.keys ?? []).some((k) => k.frame === 30))), 'a position key is written at frame 30');
  const keys = clipOf(tl).transform!.positionX!.keys!;
  expect(keys.length, 'the two original keys stay and one is added').toBe(3);
  expect(keys.find((k) => k.frame === 30)!.value).toBeCloseTo((1 - 0.3) / 2 - 0.04, 5);
  expect(val(clipOf(tl).transform!.positionX, 0), 'the static value is untouched').toBe(0);
});

test.describe('R07.2 tablet (1024x768, coarse pointer)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  test('R07.2 tablet: handles are >= 44 px and a touch drag moves and rotates the picture', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await open(freshTimeline, api, seed, openEditor);
    expect(await ed.page.evaluate(() => window.matchMedia('(pointer: coarse)').matches), 'the page sees a coarse pointer').toBe(true);
    await preset(ed, 'viewer-xform-corner-br');
    await ed.expectServer((tl) => xf(tl).scale < 0.31, 'small picture stored');
    await ed.page.keyboard.press('Escape');
    for (const id of ['viewer-xform-handle-tl', 'viewer-xform-handle-br', 'viewer-xform-rotate', 'viewer-xform-menu']) {
      const b = (await ed.testId(id).boundingBox())!;
      expect(Math.min(b.width, b.height), `${id} is a 44 px target`).toBeGreaterThanOrEqual(43.5);
    }
    const before = await sampleElement(ed.page, ed.viewer);
    const c = await center(ed, 'viewer-xform-body');
    await ed.touchDrag(c.x, c.y, c.x - 120, c.y - 60);
    await ed.expectServer((tl) => xf(tl).x < 0.31 - 0.1, 'a touch drag stores the position');
    await expect.poll(async () => distance(before, await sampleElement(ed.page, ed.viewer)), { message: 'the preview moved', timeout: 15_000 }).toBeGreaterThan(0.5);
    expect(meanLuma(before)).toBeGreaterThan(0);
    const rot = await center(ed, 'viewer-xform-rotate');
    const c2 = await center(ed, 'viewer-xform-body');
    await ed.touchDrag(rot.x, rot.y, c2.x + 120, c2.y);
    await ed.expectServer((tl) => Math.abs(xf(tl).rotation) > 70, 'a touch rotate stores a rotation');
  });
});
