import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * R10.3 (T-003): on-canvas mask and reframe handles. Dragging on the picture stores the mask / reframe
 * point; every assertion reads the STORED document (API GET) after a reload; one drag is one undo step.
 * The Look sheet on a phone offers no mask or reframe ("on desktop"), so there is no touch row to run there.
 */

type Tl = Awaited<ReturnType<EditorPage['serverTimeline']>>;
type Mask = { kind?: string; x?: number; y?: number; w?: number; h?: number; feather?: number; invert?: boolean } | null;
type Item = { mask?: Mask; reframe?: { x?: number; y?: number } | null };
const clipOf = (tl: Tl) => tl.document.tracks.find((t) => t.kind === 'video')!.items[0] as unknown as Item;

const MASK: Mask = { kind: 'rect', x: 0.5, y: 0.5, w: 0.3, h: 0.3, feather: 0.05, invert: false };

async function open(
  ft: Parameters<typeof clipsTimeline>[0],
  api: Parameters<typeof clipsTimeline>[1],
  seed: Parameters<typeof clipsTimeline>[2],
  openEditor: (id: string) => Promise<EditorPage>,
  extra: Record<string, unknown>,
  ready: string,
) {
  const clips: ClipSpec[] = [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 150, extra: extra as never }];
  const ed = await openEditor(await clipsTimeline(ft, api, seed, clips));
  await ed.selectClipAt(30);
  await ed.seek(30);
  await expect(ed.testId(ready)).toBeVisible({ timeout: 30_000 });
  return ed;
}

const centre = async (ed: EditorPage, id: string) => {
  const b = (await ed.testId(id).boundingBox())!;
  return { x: b.x + b.width / 2, y: b.y + b.height / 2, w: b.width, h: b.height };
};

test('R10.3 drag a rectangle mask on the viewer stores the mask', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await open(freshTimeline, api, seed, openEditor, { mask: MASK }, 'viewer-mask-body');
  const frame = (await ed.testId('program-frame').boundingBox())!;
  const c = await centre(ed, 'viewer-mask-body');
  await ed.mouseDrag(c.x, c.y, c.x + frame.width * 0.2, c.y - frame.height * 0.2);
  await ed.expectServer((tl) => (clipOf(tl).mask?.x ?? 0.5) > 0.6, 'the dragged mask is stored');
  await ed.reload();
  const m = clipOf(await ed.serverTimeline()).mask!;
  expect(m.x, 'moved right by about 20% of the picture').toBeGreaterThan(0.65);
  expect(m.x).toBeLessThan(0.75);
  expect(m.y, 'moved up by about 20%').toBeLessThan(0.35);
  expect([m.w, m.h, m.feather, m.kind], 'size, feather and shape untouched by a move').toEqual([0.3, 0.3, 0.05, 'rect']);
  // A corner handle resizes, the feather handle softens.
  await ed.selectClipAt(30);
  await ed.seek(30);
  const br = await centre(ed, 'viewer-mask-handle-br');
  await ed.mouseDrag(br.x, br.y, br.x + frame.width * 0.1, br.y + frame.height * 0.1);
  await ed.expectServer((tl) => (clipOf(tl).mask?.w ?? 0.3) > 0.35, 'a corner drag stores a larger mask');
});

test('R10.3 reframe point drag stores x,y', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await open(freshTimeline, api, seed, openEditor, { reframe: { x: 0.5, y: 0.5 } }, 'viewer-reframe-handle-point');
  const frame = (await ed.testId('program-frame').boundingBox())!;
  const c = await centre(ed, 'viewer-reframe-handle-point');
  await ed.mouseDrag(c.x, c.y, c.x - frame.width * 0.25, c.y + frame.height * 0.25);
  await ed.expectServer((tl) => (clipOf(tl).reframe?.x ?? 0.5) < 0.4, 'the dragged reframe point is stored');
  await ed.reload();
  const r = clipOf(await ed.serverTimeline()).reframe!;
  expect(r.x, 'x moved left by about 25%').toBeGreaterThan(0.2);
  expect(r.x).toBeLessThan(0.3);
  expect(r.y, 'y moved down by about 25%').toBeGreaterThan(0.7);
  expect(r.y).toBeLessThan(0.8);
});

test('R10.3 one undo reverts a whole drag', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await open(freshTimeline, api, seed, openEditor, { mask: MASK }, 'viewer-mask-body');
  const frame = (await ed.testId('program-frame').boundingBox())!;
  const c = await centre(ed, 'viewer-mask-body');
  await ed.mouseDrag(c.x, c.y, c.x + frame.width * 0.15, c.y + frame.height * 0.15);
  await ed.expectServer((tl) => (clipOf(tl).mask?.x ?? 0.5) > 0.55, 'the drag is stored');
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((tl) => Math.abs((clipOf(tl).mask?.x ?? 0) - 0.5) < 0.001 && Math.abs((clipOf(tl).mask?.y ?? 0) - 0.5) < 0.001, 'a single undo restores the mask before the drag');
});

test('R10.3 touch drag of the mask on a phone', async () => {
  test.skip(true, 'phone Look sheet offers no mask or reframe controls ("Colour wheels, curves, keying, masks and reframing are on desktop"), so there is no touch handle to drive');
});
