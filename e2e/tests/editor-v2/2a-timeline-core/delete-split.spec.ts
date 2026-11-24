import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, countItems, itemNamed, seekAndBlur, settledPatchCount, toastWith } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane A (editor-v2-playback-verify): delete, lift, close-a-gap and split. These are the controls most likely to be called
 * "useless" buttons, so each asserts the STORED document (GET after the
 * PATCH) and that undo restores it.
 *
 * Decision D7 (remediation-plan.md): Delete ripples (closes the gap); Shift+Delete lifts (leaves a gap);
 * the Clip menu and the keyboard agree; a gap can be closed.
 */

async function press(ed: EditorPage, chord: string): Promise<void> {
  await ed.page.keyboard.press(chord);
}

test('TL-518 Delete and Backspace ripple-delete the selected clip (the next clip closes the gap) and undo restores it', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  for (const key of ['Delete', 'Backspace']) {
    await ed.selectClipAt(30);
    await press(ed, key);
    await ed.expectServer((tl) => !itemNamed(tl, 'clipA.mp4') && itemNamed(tl, 'clipB.mp4')?.recordIn === 0, `${key}: clipA is gone and clipB closed the gap`);
    await expect(ed.chip).toHaveText(/^\s*Saved/);
    await press(ed, 'Control+z');
    await ed.expectServer((tl) => itemNamed(tl, 'clipA.mp4')?.recordIn === 0 && itemNamed(tl, 'clipB.mp4')?.recordIn === 60, `${key}: undo brings clipA back and clipB to 60`);
  }
  await ed.reload();
  const after = await ed.serverTimeline();
  expect(countItems(after), 'both clips survive reload').toBe(2);
});

test('TL-518b Shift+Delete lifts the selected clip and leaves the gap', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.selectClipAt(30);
  await press(ed, 'Shift+Delete');
  await ed.expectServer((tl) => !itemNamed(tl, 'clipA.mp4') && itemNamed(tl, 'clipB.mp4')?.recordIn === 60, 'clipA is lifted, clipB keeps its place');
  await press(ed, 'Control+z');
  await ed.expectServer((tl) => itemNamed(tl, 'clipA.mp4')?.recordIn === 0, 'undo puts clipA back');
});

test('TL-089 TL-090 the Clip menu agrees with the keyboard: Delete closes the gap, Lift leaves it (desktop and phone)', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.selectClipAt(30);
  await ed.clipAction('lift');
  await ed.expectServer((tl) => !itemNamed(tl, 'clipA.mp4') && itemNamed(tl, 'clipB.mp4')?.recordIn === 60, 'Lift: clipA gone, clipB stays at 60');
  // Undo through the on-screen button (a phone has no Ctrl+Z).
  await ed.undo();
  await ed.expectServer((tl) => itemNamed(tl, 'clipA.mp4')?.recordIn === 0, 'Undo button restores clipA');
  await ed.selectClipAt(30);
  await ed.clipAction('delete');
  await ed.expectServer((tl) => !itemNamed(tl, 'clipA.mp4') && itemNamed(tl, 'clipB.mp4')?.recordIn === 0, 'Delete: clipA gone, clipB moved to 0');
});

test('TL-gap a lifted gap can be closed from the Clip menu (one gap, or every gap on the track)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no gap tool; skip recorded in suite-skips.md
  // [A 0..60]  gap 60..90  [B 90..150]  gap 150..180  [C 180..240]
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, [
      { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
      { name: 'clipB.mp4', file: 'clipB', recordIn: 90, recordOut: 150 },
      { name: 'clipC.mp4', file: 'clipA', recordIn: 180, recordOut: 240 },
    ]),
  );
  await ed.selectClipAt(120);
  await ed.clipAction('close-gap');
  await ed.expectServer((tl) => itemNamed(tl, 'clipB.mp4')?.recordIn === 60 && itemNamed(tl, 'clipC.mp4')?.recordIn === 150, 'the gap before clipB is closed and clipC follows');
  await ed.undo();
  await ed.expectServer((tl) => itemNamed(tl, 'clipB.mp4')?.recordIn === 90 && itemNamed(tl, 'clipC.mp4')?.recordIn === 180, 'undo reopens the gap');

  await ed.selectClipAt(120);
  await ed.clipAction('close-gaps');
  await ed.expectServer((tl) => itemNamed(tl, 'clipB.mp4')?.recordIn === 60 && itemNamed(tl, 'clipC.mp4')?.recordIn === 120, 'every gap on the track is closed');
  await ed.reload();
  const tl = await ed.serverTimeline();
  expect(itemNamed(tl, 'clipC.mp4')?.recordIn, 'closed gaps survive reload').toBe(120);
});

test('TL-084 TL-088 split at the playhead: cuts when the playhead is inside a clip, explains when it is not (toolbar button and Clip menu)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // phone splits from the action bar on a selected clip (30-phone/actions.spec.ts); no toolbar scissors, no Clip menu hint
  // clipA [0,60), a gap [60,90), clipB [90,150)
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, [
      { name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
      { name: 'clipB.mp4', file: 'clipB', recordIn: 90, recordOut: 150 },
    ]),
  );
  // 1. Toolbar scissors with the playhead inside clipA (nothing selected: the targeted video track is cut).
  await ed.seek(30);
  await ed.splitAtPlayhead();
  await ed.expectServer((tl) => countItems(tl) === 3 && tl.document.tracks[0].items.some((i) => i.recordIn === 30), 'the toolbar split cut clipA at frame 30');
  await ed.undo();
  await ed.expectServer((tl) => countItems(tl) === 2, 'undo joins the halves');

  // 2. The playhead on a clip edge cannot cut: no PATCH, and the editor says why.
  await ed.seek(60);
  const sent = await settledPatchCount(ed, 2);
  await ed.splitAtPlayhead();
  await expect(toastWith(ed, /nothing to split/i), 'the editor explains why nothing was split').toBeVisible();
  expect(ed.patches.length, 'no edit was sent').toBe(sent);

  // 3. Over the gap: same answer.
  await ed.seek(75);
  await ed.splitAtPlayhead();
  expect(ed.patches.length, 'still no edit').toBe(sent);

  // 4. The Clip menu: with clipB selected and the playhead outside it, Split is disabled and says why; inside it works.
  await ed.selectClipAt(120);
  await ed.tap(ed.testId('clip-actions-open'));
  await expect(ed.testId('clip-action-split')).toBeDisabled();
  await expect(ed.testId('clip-action-split-hint')).toBeVisible();
  await ed.page.keyboard.press('Escape');
  await ed.seek(120);
  await ed.selectClipAt(120);
  await ed.clipAction('split');
  await ed.expectServer((tl) => countItems(tl) === 3 && tl.document.tracks[0].items.some((i) => i.recordIn === 120), 'the Clip menu split clipB at 120');
});

test('TL-519 Ctrl+B splits at the playhead and says why when it cannot', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await seekAndBlur(ed, 90);
  await press(ed, 'Control+b');
  await ed.expectServer((tl) => countItems(tl) === 3 && tl.document.tracks[0].items.some((i) => i.recordIn === 90), 'Ctrl+B split clipB at 90');
  const sent = await settledPatchCount(ed, 1);
  await seekAndBlur(ed, 60);
  await press(ed, 'Control+b');
  await expect(toastWith(ed, /nothing to split/i), 'Ctrl+B on a clip edge explains itself').toBeVisible();
  expect(ed.patches.length).toBe(sent);
});

test('TL-084b Split reaches a clip on a track that is not targeted when it is selected, and the audio track when it is targeted', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no per-track target buttons: x1-mobile-model verdict
  const ed = await openEditor(
    await clipsTimeline(freshTimeline, api, seed, PAIR, { audioClips: [{ name: 'tone.wav', file: 'tone', recordIn: 0, recordOut: 120 }] }),
  );
  const doc = (await ed.serverTimeline()).document;
  const audio = doc.tracks.find((t) => t.kind === 'audio')!;
  // Target A1 only (V1 is targeted by default): untarget V1, target A1.
  await ed.tap(ed.testId(`track-target-${doc.tracks.find((t) => t.kind === 'video')!.id}`));
  await ed.tap(ed.testId(`track-target-${audio.id}`));
  await ed.seek(30);
  await ed.splitAtPlayhead();
  await ed.expectServer((tl) => tl.document.tracks.find((t) => t.kind === 'audio')!.items.length === 2 && countItems(tl) === 2, 'only the audio clip was cut');
});
