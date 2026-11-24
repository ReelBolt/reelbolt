import type { Locator } from '@playwright/test';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { mediaClip, type Timeline } from '../../../fixtures/editor-v2/api';
import { expectLargerScreenState, runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 03 timeline editing, (M) rows: the edit works on desktop and is unreachable by touch (verdict-table
 * P1-OPS / P3-CANVAS / P4-OPS mobile cells). Acceptance: remediation-plan.md W0.7 — `touch-action: none`
 * on the canvas, pointer capture, 44 px handle slop, and a clip action menu reachable by tap (Delete, Lift,
 * Split, Nudge ±1 frame; D7: Delete closes the gap, Shift+Delete lifts). Real touch is CDP on Pixel 7;
 * WebKit runs only the tap rows ("synthetic pointers").
 *
 * Everything sits in the first 150 frames so the clip is on screen on a 412 px phone.
 */

const item = (tl: Timeline, name: string, track = 0) => tl.document.tracks[track].items.find((i) => i.name === name);

/** A blank timeline holding clipA [0,60) and clipB [60,120) on V1 (and, optionally, a second video track). */
async function twoClips(freshTimeline: (f: 'blank') => Promise<string>, api: import('../../../fixtures/editor-v2/api').EditorApi, seed: import('../../../fixtures/editor-v2').Seed, secondVideoTrack = false): Promise<string> {
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    const v = doc.tracks.find((t) => t.kind === 'video')!;
    v.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 60), mediaClip(crypto.randomUUID(), 'clipB.mp4', seed.files.clipB, 60, 120)];
    if (secondVideoTrack) doc.tracks.splice(1, 0, { ...structuredClone(v), id: crypto.randomUUID(), name: 'Video 2', items: [] });
  });
  return id;
}

/**
 * Selects the clip at `frame` by tap and opens the clip action menu T-040 designed for touch: the toolbar's
 * "Clip actions" button opens a menu of 44 px buttons (Delete, Lift, Split, Nudge ±1). Returns the menu.
 */
async function openClipActions(ed: EditorPage, frame: number): Promise<Locator> {
  await ed.selectClipAt(frame);
  await ed.tap(ed.page.getByRole('button', { name: 'Clip actions' }));
  const menu = ed.page.getByTestId('clip-actions-menu');
  await expect(menu, 'the clip action menu opens by tap').toBeVisible();
  return menu;
}

/** Taps `action` inside the clip action menu (never a same-named control elsewhere, e.g. "Extract audio"). */
async function tapClipThen(ed: EditorPage, frame: number, action: RegExp): Promise<void> {
  const menu = await openClipActions(ed, frame);
  const control = menu.getByRole('button', { name: action }).first();
  await expect(control, `a visible control named ${action} (reachable by tap)`).toBeVisible();
  const box = (await control.boundingBox())!;
  expect(Math.min(box.width, box.height), `${action} is a 44 px target`).toBeGreaterThanOrEqual(44);
  await ed.tap(control);
}

test('P1-OPS-03 lift a clip by tap (gap stays)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P1-OPS-03');
  const ed = await openEditor(await twoClips(freshTimeline, api, seed));
  await tapClipThen(ed, 30, /^lift/i);
  await ed.expectServer((tl) => !item(tl, 'clipA.mp4') && item(tl, 'clipB.mp4')?.recordIn === 60, 'clipA is lifted and clipB keeps its place');
});

test('P1-OPS-04 delete a clip by tap closes the gap (extract)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P1-OPS-04');
  const ed = await openEditor(await twoClips(freshTimeline, api, seed));
  await tapClipThen(ed, 30, /^(delete|ripple delete|extract)/i);
  await ed.expectServer((tl) => !item(tl, 'clipA.mp4') && item(tl, 'clipB.mp4')?.recordIn === 0, 'clipA is removed and clipB closes the gap');
});

test('P4-OPS-06 nudge a clip one frame by tap', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P4-OPS-06');
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', seed.files.clipA, 0, 60)];
  });
  const ed = await openEditor(id);
  await tapClipThen(ed, 30, /nudge.*(right|later|forward|\+ ?1)/i);
  await ed.expectServer((tl) => item(tl, 'clipA.mp4')?.recordIn === 1, 'clipA moved one frame later');
});

test('P1-OPS-05 trim a clip\'s out edge by touch drag', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  skipSyntheticPointers();
  knownFailure('P1-OPS-05');
  const ed = await openEditor(await twoClips(freshTimeline, api, seed));
  await ed.selectClipAt(30);
  const y = await ed.laneY(0);
  // Grab 2 px inside clipA's out edge (frame 60) and pull it back 25 frames.
  await ed.touchDrag((await ed.frameX(60)) - 2, y, await ed.frameX(35), y, 16);
  const trimmed = await ed.expectServer((tl) => (item(tl, 'clipA.mp4')?.recordOut ?? 60) < 50, 'clipA\'s out edge moved earlier');
  expect(item(trimmed, 'clipA.mp4')!.recordOut, 'trimmed by about the drag distance').toBeGreaterThan(25);
});

test('P1-OPS-09 move a clip to another track by touch drag', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  skipSyntheticPointers();
  knownFailure('P1-OPS-09');
  const ed = await openEditor(await twoClips(freshTimeline, api, seed, true));
  await ed.selectClipAt(30);
  const [y0, y1, x] = [await ed.laneY(0), await ed.laneY(1), await ed.frameX(30)];
  await ed.touchDrag(x, y0, x, y1, 16);
  await ed.expectServer((tl) => !!item(tl, 'clipA.mp4', 1) && !item(tl, 'clipA.mp4', 0), 'clipA now sits on Video 2');
});

test('P3-CANVAS-03 touch drags are classified: ruler scrubs, a clip drag is one move', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  skipSyntheticPointers();
  knownFailure('P3-CANVAS-03');
  const ed = await openEditor(await twoClips(freshTimeline, api, seed));
  const cv = await ed.canvasBox();
  // Ruler drag 10 -> 100: the playhead follows the whole drag (audit: 8 frames of a 3.3 s drag).
  await ed.touchDrag(await ed.frameX(10), cv.y + 10, await ed.frameX(100), cv.y + 10, 16);
  await expect.poll(async () => {
    const [h, m, s, f] = ((await ed.testId('status-timecode').textContent()) ?? '0:0:0:0').split(':').map(Number);
    return ((h * 60 + m) * 60 + s) * 30 + f;
  }, { message: 'ruler drag scrubbed to about frame 100' }).toBeGreaterThan(85);
  expect(ed.patches, 'scrubbing writes nothing').toEqual([]);

  // Clip drag: exactly one PATCH with one move op.
  await ed.selectClipAt(90);
  const y = await ed.laneY(0);
  await ed.touchDrag(await ed.frameX(90), y, await ed.frameX(130), y, 16);
  await ed.expectServer((tl) => (item(tl, 'clipB.mp4')?.recordIn ?? 60) > 60, 'clipB moved later');
  expect(ed.patches.map((p) => p.ops.join(',')), 'one PATCH carrying one move').toEqual(['move']);
});

/* ------------------------------------------------------------------ cut on phone (W0.7: "needs a larger screen") */

test('P3-SEL-02 phone: marquee selection shows the "needs a larger screen" state', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P3-SEL-02');
  const ed = await openEditor(seed.seededTimelineId);
  // The state lives where a phone acts on a selection: the clip action menu ("1 clip selected").
  const menu = await openClipActions(ed, 60);
  await expectLargerScreenState(ed.page, /select/i, 'Selecting several clips on a phone', menu);
});

test('P4-UX-02 phone: nudging a group of clips shows the "needs a larger screen" state', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P4-UX-02');
  const ed = await openEditor(seed.seededTimelineId);
  // Nudge lives in the clip action menu; a group needs several clips selected, which that menu says needs a larger screen.
  const menu = await openClipActions(ed, 60);
  await expect(menu.getByRole('button', { name: /nudge/i }).first(), 'the one-clip nudge is there').toBeVisible();
  await expectLargerScreenState(ed.page, /group|several|multiple/i, 'Nudging a group of clips on a phone', menu);
});
