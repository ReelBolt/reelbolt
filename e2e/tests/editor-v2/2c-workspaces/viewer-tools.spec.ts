import { test, expect } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-004 (§7.3) and T-005 (§7.2): the on-canvas mask handles keep the paused auto-show whether or not a
 * picture tool is active, add play-time persistence for the shape tools, and are reachable from the clip
 * menu (which writes the default mask and activates the tool).
 *
 * e2e dependencies are not installed in this worktree, so this spec was written to the existing
 * editor-v2 conventions but not run here.
 */

const MASK = { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.3, h: 0.3, feather: 0.05, invert: false };

/** A blank timeline with one clip carrying a rectangle mask, selected at frame 30. */
async function maskedClip(freshTimeline: (f: 'blank') => Promise<string>, api: import('../../../fixtures/editor-v2/api').EditorApi, seed: import('../../../fixtures/editor-v2').Seed) {
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 150, { mask: MASK })];
  });
  return id;
}

/** A blank timeline with one plain clip (no mask yet), ready for the clip menu's Add mask. */
async function plainClip(freshTimeline: (f: 'blank') => Promise<string>, api: import('../../../fixtures/editor-v2/api').EditorApi, seed: import('../../../fixtures/editor-v2').Seed) {
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 150)];
  });
  return id;
}

test('T-004 paused auto-show with no tool; playback without a tool does not mount the handles', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet');
  test.setTimeout(120_000);
  const ed = await openEditor(await maskedClip(freshTimeline, api, seed));
  await ed.selectClipAt(30);
  await ed.seek(30);

  // Paused and no tool active: today's auto-show, unchanged.
  await expect(ed.testId('viewer-mask-body')).toBeVisible({ timeout: 30_000 });

  // Press play: no picture tool is active, so nothing mounts and no handle drag is possible.
  await ed.tap(ed.testId('transport-play'));
  await expect(ed.testId('viewer-mask-body')).toBeHidden({ timeout: 10_000 });

  // Back to a still picture: the same mask returns.
  await ed.tap(ed.testId('transport-play'));
  await expect(ed.testId('viewer-mask-body')).toBeVisible({ timeout: 10_000 });

  // The mask itself was never touched by playing or pausing (no document change).
  const tl = await ed.serverTimeline();
  const clip = tl.document.tracks.find((t) => t.kind === 'video')!.items[0] as { mask?: typeof MASK };
  expect(clip.mask).toEqual(MASK);
});

test('T-005 add a mask from the clip menu, play, drag it, and store ONE revision', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(180_000);
  const ed = await openEditor(await plainClip(freshTimeline, api, seed));
  await ed.selectClipAt(30);
  await ed.seek(30);

  // §7.2: the clip menu is a real entry point; it writes the default rectangle, selects the clip, pauses
  // and activates the mask tool without forcing a page change.
  await ed.tap(ed.testId('clip-actions-open'));
  await ed.tap(ed.testId('clip-action-add-mask'));
  const added = await ed.expectServer((tl) => {
    const clip = tl.document.tracks.find((t) => t.kind === 'video')!.items[0] as { mask?: { kind?: string } };
    return clip.mask?.kind === 'rect';
  }, 'the default rectangle mask is stored');
  await expect(ed.testId('viewer-mask-body')).toBeVisible({ timeout: 10_000 });

  // Playing with the mask tool active keeps the handles (T-004); the drag stops the transport and
  // writes exactly ONE setProperty mask — one revision, one undo step.
  await ed.tap(ed.testId('transport-play'));
  await expect(ed.testId('viewer-mask-body')).toBeVisible({ timeout: 10_000 });
  const before = await ed.serverTimeline();
  expect(before.revision).toBeGreaterThanOrEqual(added.revision);
  const frame = (await ed.testId('program-frame').boundingBox())!;
  const body = (await ed.testId('viewer-mask-body').boundingBox())!;
  await ed.mouseDrag(body.x + body.width / 2, body.y + body.height / 2, body.x + body.width / 2 + frame.width * 0.2, body.y + body.height / 2);
  const after = await ed.expectServer((tl) => {
    const clip = tl.document.tracks.find((t) => t.kind === 'video')!.items[0] as { mask?: { x?: number } };
    return (clip.mask?.x ?? 0.5) > 0.6;
  }, 'the dragged mask is stored once');
  expect(after.revision, 'the drag is one revision').toBe(before.revision + 1);
});
