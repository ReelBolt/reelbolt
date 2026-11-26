import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { mediaClip, type Timeline } from '../../../fixtures/editor-v2/api';
import { runOn, skipSyntheticPointers } from '../../../fixtures/editor-v2/rows';
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
 * Selects the clip at `frame` by tap and runs one action of the phone's action bar (Delete is on the bar; Lift is in
 * its More sheet; Nudge opens the Nudge sheet). Every button is a 44 px target: the 30-phone suite measures that.
 */
async function tapClipThen(ed: EditorPage, frame: number, action: 'delete' | 'lift' | 'nudge-forward'): Promise<void> {
  await ed.selectClipAt(frame);
  await ed.clipAction(action);
}

test('P1-OPS-03 lift a clip by tap (gap stays)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P1-OPS-03');
  const ed = await openEditor(await twoClips(freshTimeline, api, seed));
  await tapClipThen(ed, 30, 'lift');
  await ed.expectServer((tl) => !item(tl, 'clipA.mp4') && item(tl, 'clipB.mp4')?.recordIn === 60, 'clipA is lifted and clipB keeps its place');
});

test('P1-OPS-04 delete a clip by tap closes the gap (extract)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P1-OPS-04');
  const ed = await openEditor(await twoClips(freshTimeline, api, seed));
  await tapClipThen(ed, 30, 'delete');
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
  await tapClipThen(ed, 30, 'nudge-forward');
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

/*
 * The phone cannot move a clip to another track (docs/editor.md "Not on a phone"), and picking a clip up to move it is
 * behind the per-device flag reelbolt:editor:touch-drag (default off). So the rows that guarded the desktop gestures
 * on touch now guard the phone's designed answer: a one-finger drag over a clip pans the strip and writes nothing.
 */
test('P1-OPS-09 phone: a touch drag over a clip never moves it to another track (the phone has no track move)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  skipSyntheticPointers();
  const ed = await openEditor(await twoClips(freshTimeline, api, seed, true));
  await ed.selectClipAt(30);
  const before = await ed.serverTimeline();
  const [y0, y1, x] = [await ed.laneY(0), await ed.laneY(1), await ed.frameX(30)];
  await ed.touchDrag(x, y0, x, y1, 16);
  await ed.page.waitForTimeout(1500);
  expect(ed.patches, 'the drag wrote nothing').toEqual([]);
  const after = await ed.serverTimeline();
  expect(after.revision).toBe(before.revision);
  expect(!!item(after, 'clipA.mp4', 0) && !item(after, 'clipA.mp4', 1), 'clipA stays on Video 1').toBe(true);
});

test('P3-CANVAS-03 phone: a one-finger drag pans the strip (the playhead follows) and writes nothing', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  skipSyntheticPointers();
  const ed = await openEditor(await twoClips(freshTimeline, api, seed));
  const frameNow = async () => {
    const [h, m, s, f] = ((await ed.testId('status-timecode').textContent()) ?? '0:0:0:0').split(':').map(Number);
    return ((h * 60 + m) * 60 + s) * 30 + f;
  };
  const y = await ed.laneY(0);
  const cv = await ed.canvasBox();
  const start = await frameNow();
  // The strip keeps the playhead at its centre: dragging the content to the left moves the playhead later.
  await ed.touchDrag(cv.x + cv.width - 60, y, cv.x + cv.width - 210, y, 16);
  await expect.poll(frameNow, { message: 'the pan moved the playhead later' }).toBeGreaterThan(start + 20);
  expect(ed.patches, 'panning writes nothing').toEqual([]);
});

/* ------------------------------------------------------------------ cut on phone */

test('P3-SEL-02 phone: several clips are selected in Select mode (there is no marquee on a phone)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openEditor(await twoClips(freshTimeline, api, seed));
  await ed.tap(ed.testId('phone-action-select'));
  await expect(ed.testId('phone-action-bar')).toHaveAttribute('data-mode', 'select');
  await ed.selectClipAt(30);
  await ed.selectClipAt(75);
  await expect(ed.testId('phone-action-select-done'), 'the group counts both tapped clips').toContainText('2');
});

test('P4-UX-02 phone: nudging a group moves every clip of the group (Select mode, Nudge sheet)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openEditor(await twoClips(freshTimeline, api, seed));
  await ed.tap(ed.testId('phone-action-select'));
  await ed.selectClipAt(30);
  await ed.selectClipAt(75);
  await ed.tap(ed.testId('phone-action-nudge'));
  await ed.tap(ed.testId('phone-nudge+10'));
  await ed.expectServer((tl) => item(tl, 'clipA.mp4')?.recordIn === 10 && item(tl, 'clipB.mp4')?.recordIn === 70, 'both clips moved 10 frames later');
});
