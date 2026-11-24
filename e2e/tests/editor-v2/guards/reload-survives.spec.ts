import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { knownFailure } from '../expected-failures';

/**
 * T0.4 "edit, reload, edit survives" after each risky action of audit top-10 #2 (report.md §3): the
 * action itself, then an ordinary edit (blade at the playhead), must both reach the server and survive
 * a reload. On origin/master the risky action makes autosave 400 and locks every later edit out.
 */

const itemCount = (tl: Timeline) => tl.document.tracks.reduce((n, t) => n + t.items.length, 0);

/** An ordinary follow-up edit that works on every layout: blade the V1 clip at frame 75. */
async function followUpEdit(ed: EditorPage, before: Timeline): Promise<void> {
  await ed.seek(75);
  await ed.splitAtPlayhead();
  await ed.expectServer((tl) => itemCount(tl) > itemCount(before), 'blade after the risky edit reached the server');
}

async function reloadAndCheck(ed: EditorPage, check: (tl: Timeline) => boolean, message: string): Promise<void> {
  await ed.reload();
  const tl = await ed.serverTimeline();
  expect(check(tl), message).toBe(true);
  await expect(ed.chip).toHaveText(/^\s*Saved/);
}

/**
 * The risky edit is cut on phone (it shows the "needs a larger screen" state, asserted by `row`'s phone
 * spec), so a phone cannot make it and there is nothing for this guard to check there. The desktop guard
 * is unchanged.
 */
function skipCutOnPhone(row: string, edit: string): void {
  test.skip(test.info().project.name !== 'desktop', `cut on phone: ${edit} needs a larger screen there (asserted by ${row}); the edit cannot be made from a phone`);
}

test.describe('guards: edit, reload, edit survives', () => {
  test('GUARD-ADD-VIDEO-TRACK P8-SEQ-01 +V then an edit both survive reload', async ({ freshTimeline, openEditor }) => {
    knownFailure('GUARD-ADD-VIDEO-TRACK');
    const ed = await openEditor(await freshTimeline('seeded'));
    const before = await ed.serverTimeline();
    await ed.tap(ed.testId('add-video-track'));
    await ed.expectServer((tl) => tl.document.tracks.length === before.document.tracks.length + 1, 'the new video track is stored');
    await followUpEdit(ed, before);
    await reloadAndCheck(ed, (tl) => tl.document.tracks.length === before.document.tracks.length + 1, 'track survives reload');
  });

  test('GUARD-ADD-AUDIO-TRACK P8-SEQ-01 +A then an edit both survive reload', async ({ freshTimeline, openEditor }) => {
    knownFailure('GUARD-ADD-AUDIO-TRACK');
    const ed = await openEditor(await freshTimeline('seeded'));
    const before = await ed.serverTimeline();
    await ed.tap(ed.testId('add-audio-track'));
    await ed.expectServer((tl) => tl.document.tracks.filter((t) => t.kind === 'audio').length === 2, 'the new audio track is stored');
    await followUpEdit(ed, before);
    await reloadAndCheck(ed, (tl) => tl.document.tracks.length === before.document.tracks.length + 1, 'track survives reload');
  });
});

test.describe('guards: risky edits keep autosave alive', () => {
  test('GUARD-BASELINE an ordinary blade saves and survives reload', async ({ freshTimeline, openEditor }) => {
    // Control row (passes on origin/master): proves the guard mechanics themselves work on this project.
    knownFailure('GUARD-BASELINE');
    const ed = await openEditor(await freshTimeline('seeded'));
    const before = await ed.serverTimeline();
    await followUpEdit(ed, before);
    await reloadAndCheck(ed, (tl) => itemCount(tl) === itemCount(before) + 1, 'blade survives reload');
  });

  test('GUARD-COMPOUND P8-SEQ-01 Make compound clip then an edit both survive reload', async ({ freshTimeline, openEditor }) => {
    skipCutOnPhone('P8-SEQ-01:phone', 'making a compound clip');
    knownFailure('GUARD-COMPOUND');
    const ed = await openEditor(await freshTimeline('seeded'));
    const before = await ed.serverTimeline();
    await ed.selectClipAt(60); // clipA, one clip (phones have no multi-select yet); frame 60 is on screen on a 412 px phone
    await ed.openPanelTab('tab-structure');
    await ed.tap(ed.testId('compound-create'));
    await ed.expectServer((tl) => (tl.document.sequences?.length ?? 0) > 0, 'the compound clip is stored');
    await ed.showTimeline();
    const afterCompound = await ed.serverTimeline();
    await followUpEdit(ed, afterCompound);
    await reloadAndCheck(ed, (tl) => (tl.document.sequences?.length ?? 0) > 0 && itemCount(tl) > itemCount(before) - 1, 'compound survives reload');
  });

  test('GUARD-FIRST-KEYFRAME P5-KEY-01 first opacity keyframe then an edit both survive reload', async ({ freshTimeline, openEditor }) => {
    knownFailure('GUARD-FIRST-KEYFRAME');
    const ed = await openEditor(await freshTimeline('seeded'));
    const before = await ed.serverTimeline();
    await ed.seek(60);
    await ed.selectClipAt(60); // clipA under the playhead, on screen on every layout
    await ed.openInspector();
    await ed.tap(ed.testId('inspector-finetune-toggle'));
    await ed.tap(ed.testId('inspector-key-opacity'));
    const keyed = (tl: Timeline) => tl.document.tracks[0].items.some((i) => JSON.stringify(i.transform ?? {}).includes('"keys"'));
    await ed.expectServer(keyed, 'the first keyframe is stored');
    await ed.showTimeline();
    await followUpEdit(ed, before);
    await reloadAndCheck(ed, keyed, 'keyframe survives reload');
  });

  test('GUARD-LATCH P8-AUTO-02 a latch automation pass then an edit both survive reload', async ({ freshTimeline, openEditor }) => {
    skipCutOnPhone('P8-AUTO-02', 'a latch automation pass');
    knownFailure('GUARD-LATCH');
    const ed = await openEditor(await freshTimeline('seeded'));
    const before = await ed.serverTimeline();
    const audio = before.document.tracks.find((t) => t.kind === 'audio')!;
    await ed.workspace('audio');
    await ed.openAdvancedAudio(); // automation lives under Advanced audio
    const mode = ed.testId(`automation-mode-${audio.id}`);
    await mode.scrollIntoViewIfNeeded();
    await ed.tap(mode.locator('label').filter({ hasText: 'Latch' }).first());
    const fader = ed.testId(`automation-fader-${audio.id}`);
    await fader.scrollIntoViewIfNeeded();
    const box = (await fader.boundingBox())!;
    await ed.tap(ed.testId('transport-play'));
    await ed.page.waitForTimeout(600);
    const [x0, x1, y] = [box.x + box.width * 0.6, box.x + box.width * 0.3, box.y + box.height / 2];
    if (ed.isPhone && ed.projectName === 'pixel7') await ed.touchDrag(x0, y, x1, y, 6);
    else await ed.mouseDrag(x0, y, x1, y, 6);
    await ed.page.waitForTimeout(600);
    await ed.tap(ed.testId('transport-play'));
    const automated = (tl: Timeline) => JSON.stringify(tl.document.tracks.find((t) => t.id === audio.id)?.gainDb ?? {}).includes('"keys"');
    await ed.expectServer(automated, 'the latch pass is stored as gain keys');
    await ed.workspace('edit');
    await followUpEdit(ed, before);
    await reloadAndCheck(ed, automated, 'automation survives reload');
  });

  test('GUARD-BLADE-NEAR-FADE eng-D6 blade near the end of a faded clip keeps saving', async ({ freshTimeline, openEditor, api, seed }) => {
    knownFailure('GUARD-BLADE-NEAR-FADE');
    const id = await freshTimeline('seeded');
    await api.putDocument(seed.projectId, id, (doc) => {
      doc.tracks[0].items[0].fadeOutFrames = 60; // clipA [0,150): the cut at 140 leaves a 10-frame tail
    });
    const ed = await openEditor(id);
    const before = await ed.serverTimeline();
    await ed.seek(140);
    await ed.splitAtPlayhead();
    await ed.expectServer((tl) => itemCount(tl) === itemCount(before) + 1, 'the blade near the fade is stored');
    await reloadAndCheck(ed, (tl) => itemCount(tl) === itemCount(before) + 1, 'blade near fade survives reload');
  });

  test('GUARD-DELETE-NEXT-TO-TRANSITION eng-D7 deleting the clip next to a transition keeps saving', async ({ freshTimeline, openEditor, api, seed }) => {
    knownFailure('GUARD-DELETE-NEXT-TO-TRANSITION');
    const id = await freshTimeline('seeded');
    // A transition is its own span between its neighbours: clipA [0,60), dissolve [60,80), clipB [80,210),
    // vertical [210,360) (clipB keeps its picture aligned: sourceIn 20; clipA has 10 s of tail handle).
    // Everything sits in the first 360 frames so the clip to delete is on screen on a 412 px phone.
    await api.putDocument(seed.projectId, id, (doc) => {
      const [a, b, v] = doc.tracks[0].items;
      Object.assign(a, { recordIn: 0, recordOut: 60 });
      Object.assign(b, { recordIn: 80, recordOut: 210, sourceIn: 20 });
      Object.assign(v, { recordIn: 210, recordOut: 360 });
      doc.tracks[0].items = [a, { type: 'transition', id: crypto.randomUUID(), name: 'Dissolve', recordIn: 60, recordOut: 80, transitionType: 'dissolve', enabled: true }, b, v];
    });
    const ed = await openEditor(id);
    const before = await ed.serverTimeline();
    expect(before.document.tracks[0].items.some((i) => i.type === 'transition'), 'fixture holds a transition').toBe(true);
    await ed.selectClipAt(120); // clipB, the clip AFTER the transition (engine.md D7 recipe: [A][T][B][C], delete B)
    // Keyboard Delete on every project: phones have no touch delete control yet (top-10 #8, W0.7).
    await ed.page.keyboard.press('Delete');
    const gone = (tl: Timeline) => !tl.document.tracks[0].items.some((i) => i.name === 'clipB.mp4');
    await ed.expectServer(gone, 'the delete is stored');
    await reloadAndCheck(ed, gone, 'delete survives reload');
  });
});
