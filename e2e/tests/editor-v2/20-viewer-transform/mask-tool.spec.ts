import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-012: the on-canvas mask tool. Drawing on the picture writes ONE `mask` setProperty, one undo restores,
 * Escape during a drag cancels with no write, and a click with no drag writes nothing. Assertions read the
 * STORED document.
 */

type Tl = Awaited<ReturnType<EditorPage['serverTimeline']>>;
type Mask = { kind?: string; x?: number; y?: number; w?: number; h?: number } | null | undefined;
const maskOf = (tl: Tl): Mask => (tl.document.tracks.find((t) => t.kind === 'video')!.items[0] as unknown as { mask?: Mask }).mask;

const ONE: ClipSpec[] = [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 150 }];

async function openMaskTool(ft: Parameters<typeof clipsTimeline>[0], api: Parameters<typeof clipsTimeline>[1], seed: Parameters<typeof clipsTimeline>[2], openEditor: (id: string) => Promise<EditorPage>) {
  const ed = await openEditor(await clipsTimeline(ft, api, seed, ONE));
  await ed.selectClipAt(30);
  await ed.seek(30);
  await ed.workspace('compose');
  await ed.tap(ed.testId('compose-entry-mask'));
  await ed.tap(ed.testId('compose-mask-draw'));
  await expect(ed.testId('viewer-mask-draw')).toBeVisible({ timeout: 30_000 });
  return ed;
}

async function layerBox(ed: EditorPage) {
  return (await ed.testId('viewer-mask-draw').boundingBox())!;
}

test('T-012 a drag on the picture writes one mask, one undo removes it', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openMaskTool(freshTimeline, api, seed, openEditor);
  const b = await layerBox(ed);
  await ed.mouseDrag(b.x + b.width * 0.25, b.y + b.height * 0.25, b.x + b.width * 0.75, b.y + b.height * 0.75);
  const tl = await ed.expectServer((t) => !!maskOf(t), 'the drawn mask is stored');
  const m = maskOf(tl)!;
  expect(m.kind).toBe('rect');
  expect(m.x).toBeCloseTo(0.5, 1);
  expect(m.w).toBeCloseTo(0.5, 1);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => !maskOf(t), 'one undo removes the drawn mask');
});

test('T-012 Escape during a drag and a bare click write nothing', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openMaskTool(freshTimeline, api, seed, openEditor);
  const rev = (await ed.serverTimeline()).revision;
  const b = await layerBox(ed);
  await ed.page.mouse.click(b.x + b.width * 0.5, b.y + b.height * 0.5);
  await ed.page.mouse.move(b.x + b.width * 0.2, b.y + b.height * 0.2);
  await ed.page.mouse.down();
  await ed.page.mouse.move(b.x + b.width * 0.7, b.y + b.height * 0.7, { steps: 6 });
  await ed.page.keyboard.press('Escape');
  await ed.page.mouse.up();
  await ed.page.waitForTimeout(800);
  const tl = await ed.serverTimeline();
  expect(maskOf(tl) ?? null).toBeNull();
  expect(tl.revision).toBe(rev);
});
