import { test, expect } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-013 (§7.2): choosing Corner pin in Compose puts its four corners on the picture; dragging a corner writes
 * the existing `cornerPin` value in ONE revision. Written to the editor-v2 conventions; the lead runs the stack.
 */
const PIN = { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } };

test('T-013 Corner pin handles sit on the picture and a drag stores ONE cornerPin revision', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 150, { cornerPin: PIN })];
  });
  const ed = await openEditor(id);
  await ed.selectClipAt(30);
  await ed.seek(30);
  await ed.workspace('compose');
  await ed.tap(ed.testId('compose-entry-pin'));
  await expect(ed.testId('viewer-pin-bottomRight')).toBeVisible({ timeout: 10_000 });

  const before = await ed.serverTimeline();
  const handle = (await ed.testId('viewer-pin-bottomRight').boundingBox())!;
  const frame = (await ed.testId('program-frame').boundingBox())!;
  const fromX = handle.x + handle.width / 2;
  const fromY = handle.y + handle.height / 2;
  await ed.mouseDrag(fromX, fromY, fromX - frame.width * 0.2, fromY - frame.height * 0.2);
  const after = await ed.expectServer((tl) => {
    const clip = tl.document.tracks.find((t) => t.kind === 'video')!.items[0] as { cornerPin?: { bottomRight: { x: number; y: number } } };
    return (clip.cornerPin?.bottomRight.x ?? 1) < 0.9;
  }, 'the dragged corner is stored');
  expect(after.revision, 'the drag is one revision').toBe(before.revision + 1);
  await expect(ed.testId('compose-pin-bottomRight')).toBeVisible();
});
