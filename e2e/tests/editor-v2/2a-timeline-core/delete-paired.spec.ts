import { test, expect } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, countItems, itemNamed, toastWith, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Lane A QA follow-up: Delete (ripple) must not slip the sound out of sync. A picture clip and its
 * paired sound (same source, same sourceIn, same span: what a media-pool insert makes) are removed
 * together and BOTH tracks close up. A paired clip on a locked track stays and the editor says so.
 * Eye / mute / solo on a locked track are disabled with a tooltip instead of being refused.
 */

const PICTURE: ClipSpec[] = [
  { name: 'pic-a.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
  { name: 'pic-b.mp4', file: 'clipB', recordIn: 60, recordOut: 120 },
];
const SOUND: ClipSpec[] = [
  { name: 'snd-a.mp4', file: 'clipA', recordIn: 0, recordOut: 60 },
  { name: 'snd-b.mp4', file: 'clipB', recordIn: 60, recordOut: 120 },
];
const audioTrack = (tl: { document: { tracks: Array<{ kind: string; items: Array<{ name: string; recordIn: number }> }> } }) => tl.document.tracks.find((t) => t.kind === 'audio')!;

async function deleteSelected(ed: Parameters<typeof toastWith>[0]): Promise<void> {
  await ed.clipAction('delete');
}

test('TL-090 TL-518 deleting a picture clip also removes its paired sound and both tracks close up (undo restores both)', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PICTURE, { audioClips: SOUND }));
  await ed.selectClipAt(30);
  await deleteSelected(ed);
  const tl = await ed.expectServer((t) => !itemNamed(t, 'pic-a.mp4') && !audioTrack(t).items.some((i) => i.name === 'snd-a.mp4'), 'the picture and its paired sound are both removed');
  expect(itemNamed(tl, 'pic-b.mp4')!.recordIn, 'the next picture closed up').toBe(0);
  expect(audioTrack(tl).items.map((i) => [i.name, i.recordIn]), 'and so did the next sound: still in sync').toEqual([['snd-b.mp4', 0]]);
  await expect(ed.chip).toHaveText(/^\s*Saved/);
  await ed.undo();
  const back = await ed.expectServer((t) => countItems(t) === 2 && audioTrack(t).items.length === 2, 'one Undo brings the picture and the sound back');
  expect(itemNamed(back, 'pic-b.mp4')!.recordIn).toBe(60);
  expect(audioTrack(back).items.find((i) => i.name === 'snd-b.mp4')!.recordIn).toBe(60);
});

test('TL-518 the Delete key does the same as the menu (desktop)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PICTURE, { audioClips: SOUND }));
  await ed.selectClipAt(30);
  await ed.page.keyboard.press('Delete');
  const tl = await ed.expectServer((t) => !itemNamed(t, 'pic-a.mp4') && !audioTrack(t).items.some((i) => i.name === 'snd-a.mp4'), 'Delete removed both');
  expect(audioTrack(tl).items[0].recordIn).toBe(0);
});

test('TL-518b Shift+Delete lifts only the selected clip (the sound stays where it is)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PICTURE, { audioClips: SOUND }));
  await ed.selectClipAt(30);
  await ed.page.keyboard.press('Shift+Delete');
  const tl = await ed.expectServer((t) => !itemNamed(t, 'pic-a.mp4'), 'the picture is lifted');
  expect(itemNamed(tl, 'pic-b.mp4')!.recordIn, 'picture gap stays').toBe(60);
  expect(audioTrack(tl).items.map((i) => i.name), 'the sound is untouched').toEqual(['snd-a.mp4', 'snd-b.mp4']);
});

test('TL-090 a paired sound on a LOCKED track is left in place and the editor says why', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PICTURE, { audioClips: SOUND, mutate: (doc) => { doc.tracks.find((t) => t.kind === 'audio')!.locked = true; } }));
  await ed.selectClipAt(30);
  await deleteSelected(ed);
  await ed.expectServer((t) => !itemNamed(t, 'pic-a.mp4'), 'the picture is deleted');
  await expect(toastWith(ed, /paired sound left in place/i)).toBeVisible();
  expect(audioTrack(await ed.serverTimeline()).items.map((i) => i.name), 'the locked sound is untouched').toEqual(['snd-a.mp4', 'snd-b.mp4']);
});

test('TL-115..118 eye, mute and solo on a locked track are disabled with "Unlock the track first"; unlocking re-enables them', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'tablet'); // the phone has no track headers (no lock, eye, mute or solo buttons per track): x1-mobile-model verdict
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PICTURE, { audioClips: SOUND }));
  const doc = (await ed.serverTimeline()).document;
  const v = doc.tracks.find((t) => t.kind === 'video')!;
  const a = doc.tracks.find((t) => t.kind === 'audio')!;
  const patchesBefore = ed.patches.length;
  for (const id of [v.id, a.id]) {
    await ed.tap(ed.testId(`track-lock-${id}`));
    await ed.expectServer((t) => t.document.tracks.find((x) => x.id === id)?.locked === true, 'locked');
  }
  for (const id of [`track-enable-${v.id}`, `track-mute-${a.id}`, `track-solo-${a.id}`]) {
    await expect(ed.testId(id), `${id} is disabled on a locked track`).toBeDisabled();
  }
  if (!ed.isPhone) {
    await ed.testId(`track-mute-${a.id}`).locator('xpath=..').hover();
    await expect(ed.page.getByText('Unlock the track first')).toBeVisible();
  }
  // Lock itself keeps working: unlock both and the controls act again, with no refusal toast in between.
  for (const id of [v.id, a.id]) {
    await ed.tap(ed.testId(`track-lock-${id}`));
    await ed.expectServer((t) => t.document.tracks.find((x) => x.id === id)?.locked === false, 'unlocked');
  }
  await expect(ed.testId(`track-mute-${a.id}`)).toBeEnabled();
  await ed.tap(ed.testId(`track-mute-${a.id}`));
  await ed.expectServer((t) => t.document.tracks.find((x) => x.id === a.id)?.mute === true, 'mute works again once unlocked');
  await expect(ed.page.locator('.mantine-Notification-root').filter({ hasText: /refused/i })).toHaveCount(0);
  expect(ed.patches.length).toBeGreaterThan(patchesBefore);
});
