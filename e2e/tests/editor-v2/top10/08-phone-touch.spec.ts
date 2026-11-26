import { desktopLayoutOnly } from '../../../fixtures/editor-v2/rows';
import { test, expect } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #8 (report.md §3): a clip can be moved by dragging it. Desktop drags with the mouse (control,
 * passes on origin/master); Pixel 7 drags with REAL touch (CDP Input.dispatchTouchEvent), which on
 * origin/master ends in pointercancel and leaves the document unchanged. WebKit cannot drag by touch.
 */
test('TOP10-08 P3-CANVAS-03 P4-OPS-06 dragging a clip moves it', async ({ freshTimeline, openEditor, api, seed }, testInfo) => {
  desktopLayoutOnly('moving a clip by drag is behind the touch-drag flag, off by default; 30-phone/touch covers the flag on and off');
  test.skip(testInfo.project.name === 'iphone14webkit', 'synthetic pointers: Playwright WebKit cannot drag by touch (audit D8)');
  knownFailure('TOP10-08');
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 60)];
  });
  const ed = await openEditor(id);
  const y = await ed.laneY(0);
  const [x0, x1] = [await ed.frameX(30), await ed.frameX(90)];
  await ed.selectClipAt(30);
  if (testInfo.project.name === 'pixel7') await ed.touchDrag(x0, y, x1, y, 16);
  else await ed.mouseDrag(x0, y, x1, y, 16);
  const moved = await ed.expectServer((tl) => (tl.document.tracks[0].items[0]?.recordIn ?? 0) >= 40, 'the dragged clip starts later');
  expect(moved.document.tracks[0].items[0].recordOut - moved.document.tracks[0].items[0].recordIn, 'a move keeps the length').toBe(60);
});
