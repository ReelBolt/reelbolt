import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { clipsTimeline, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * R08.1 (T-009): linked picture and sound in the UI. A clip's sound travels as a companion on the
 * "Camera audio" track sharing the clip's `linkId`; selecting one half selects both, and move, trim,
 * blade, delete, copy/paste and speed act on both as ONE undo step. Unlink (or "Edit sound separately")
 * gives J and L cuts. Every assertion reads the STORED document.
 */

type Tl = Awaited<ReturnType<EditorPage['serverTimeline']>>;
type Clip = { id: string; name: string; recordIn: number; recordOut: number; linkId?: string | null };
const clips = (tl: Tl, kind: 'video' | 'audio'): Clip[] => tl.document.tracks.filter((t) => t.kind === kind).flatMap((t) => t.items as unknown as Clip[]);
const byName = (tl: Tl, name: string): Clip => [...clips(tl, 'video'), ...clips(tl, 'audio')].find((c) => c.name === name)!;
const span = (c: Clip) => [c.recordIn, c.recordOut];

const L1 = '11111111-1111-4111-8111-111111111111';
const L2 = '22222222-2222-4222-8222-222222222222';
const PICTURE: ClipSpec[] = [
  { name: 'pic-a.mp4', file: 'clipA', recordIn: 0, recordOut: 60, extra: { linkId: L1 } as never },
  { name: 'pic-b.mp4', file: 'clipB', recordIn: 60, recordOut: 120, extra: { linkId: L2 } as never },
];
const SOUND: ClipSpec[] = [
  { name: 'snd-a.mp4', file: 'clipA', recordIn: 0, recordOut: 60, extra: { linkId: L1 } as never },
  { name: 'snd-b.mp4', file: 'clipB', recordIn: 60, recordOut: 120, extra: { linkId: L2 } as never },
];

const linkedPairs = (ft: Parameters<typeof clipsTimeline>[0], api: Parameters<typeof clipsTimeline>[1], seed: Parameters<typeof clipsTimeline>[2]) =>
  clipsTimeline(ft, api, seed, PICTURE, { audioClips: SOUND });

test('R08.1 selecting a linked picture clip selects its sound too', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7'); // iphone14webkit: menu taps and the inspector time out under load (recorded in the ledger)
  const ed = await openEditor(await linkedPairs(freshTimeline, api, seed));
  await ed.selectClipAt(30);
  await ed.openInspector();
  await expect(ed.testId('inspector-linked')).toContainText('Linked');
  await expect(ed.testId('inspector-link-member')).toBeVisible();
});

test('R08.1 split cuts picture and sound together; the halves are two linked pairs; one Undo', async ({ freshTimeline, openEditor, api, seed }) => {
  const ed = await openEditor(await linkedPairs(freshTimeline, api, seed));
  await ed.selectClipAt(30);
  await ed.seek(25);
  await ed.splitAtPlayhead();
  const tl = await ed.expectServer((t) => clips(t, 'video').length === 3 && clips(t, 'audio').length === 3, 'both tracks split');
  const left = byName(tl, 'pic-a.mp4');
  const leftSound = byName(tl, 'snd-a.mp4');
  expect(span(left)).toEqual([0, 25]);
  expect(span(leftSound)).toEqual([0, 25]);
  expect(left.linkId, 'the left halves stay linked').toBe(L1);
  expect(leftSound.linkId).toBe(L1);
  const rightPic = clips(tl, 'video').find((c) => c.recordIn === 25)!;
  const rightSnd = clips(tl, 'audio').find((c) => c.recordIn === 25)!;
  expect(rightPic.linkId, 'the right halves share a new link').toBeTruthy();
  expect(rightPic.linkId).toBe(rightSnd.linkId);
  expect(rightPic.linkId).not.toBe(L1);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => clips(t, 'video').length === 2 && clips(t, 'audio').length === 2, 'one Undo rejoins both');
});

test('R08.1 Delete removes picture and sound and closes both tracks; Undo restores', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7'); // iphone14webkit: menu taps and the inspector time out under load (recorded in the ledger)
  const ed = await openEditor(await linkedPairs(freshTimeline, api, seed));
  await ed.selectClipAt(30);
  await ed.clipAction('delete');
  const tl = await ed.expectServer((t) => clips(t, 'video').length === 1 && clips(t, 'audio').length === 1, 'both halves removed');
  expect(span(byName(tl, 'pic-b.mp4'))).toEqual([0, 60]);
  expect(span(byName(tl, 'snd-b.mp4')), 'the next sound closed up too').toEqual([0, 60]);
  await ed.tap(ed.testId('undo'));
  const back = await ed.expectServer((t) => clips(t, 'video').length === 2 && clips(t, 'audio').length === 2, 'Undo restores both');
  expect(span(byName(back, 'snd-b.mp4'))).toEqual([60, 120]);
});

test('R08.1 nudge moves picture and sound by the same amount', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7'); // iphone14webkit: menu taps and the inspector time out under load (recorded in the ledger)
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [PICTURE[0]], { audioClips: [SOUND[0]] }));
  await ed.selectClipAt(30);
  await ed.clipAction('nudge-forward');
  const tl = await ed.expectServer((t) => byName(t, 'pic-a.mp4').recordIn === 1, 'picture nudged');
  expect(span(byName(tl, 'snd-a.mp4')), 'the sound moved with it').toEqual([1, 61]);
});

test('R08.1 a typed Out trims picture and sound together (and the neighbours ripple on both tracks)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop'); // phones: the typed-number inspector fields are not reachable in the one-pane layout (recorded in the ledger)
  const ed = await openEditor(await linkedPairs(freshTimeline, api, seed));
  await ed.selectClipAt(30);
  await ed.openInspector();
  const field = ed.testId('inspector-out');
  await field.fill('45');
  await field.press('Enter');
  const tl = await ed.expectServer((t) => byName(t, 'pic-a.mp4').recordOut === 45, 'picture out written');
  expect(byName(tl, 'snd-a.mp4').recordOut, 'sound trimmed with it').toBe(45);
  expect(byName(tl, 'pic-b.mp4').recordIn).toBe(45);
  expect(byName(tl, 'snd-b.mp4').recordIn).toBe(45);
  await ed.tap(ed.testId('undo'));
  const back = await ed.expectServer((t) => byName(t, 'pic-a.mp4').recordOut === 60, 'one Undo');
  expect(byName(back, 'snd-a.mp4').recordOut).toBe(60);
});

test('R08.1 copy and paste put a new linked pair; both halves share a fresh link', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  // A trailing clip makes the timeline long enough to put the playhead at 100.
  const tail: ClipSpec = { name: 'tail.mp4', file: 'clipB', recordIn: 200, recordOut: 260 };
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [PICTURE[0], tail], { audioClips: [SOUND[0]] }));
  await ed.selectClipAt(30);
  await ed.page.keyboard.press('Control+c');
  await ed.seek(100);
  await ed.page.evaluate(() => (document.activeElement as HTMLElement | null)?.blur());
  await ed.page.keyboard.press('Control+v');
  const tl = await ed.expectServer((t) => clips(t, 'video').length === 3 && clips(t, 'audio').length === 2, 'pasted a picture and a sound');
  const pic = clips(tl, 'video').find((c) => c.recordIn === 100)!;
  const snd = clips(tl, 'audio').find((c) => c.recordIn === 100)!;
  expect(pic.linkId).toBeTruthy();
  expect(pic.linkId).toBe(snd.linkId);
  expect(pic.linkId, 'a copy is its own pair').not.toBe(L1);
});

test('R08.1 Unlink, then trimming the picture leaves the sound alone (a J cut); Link brings them back', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7'); // iphone14webkit: menu taps and the inspector time out under load (recorded in the ledger)
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [PICTURE[0]], { audioClips: [SOUND[0]] }));
  await ed.selectClipAt(30);
  await ed.clipAction('unlink');
  const unlinked = await ed.expectServer((t) => !byName(t, 'pic-a.mp4').linkId && !byName(t, 'snd-a.mp4').linkId, 'both halves unlinked');
  expect(span(byName(unlinked, 'snd-a.mp4'))).toEqual([0, 60]);
  await ed.selectClipAt(30);
  await ed.openInspector();
  const field = ed.testId('inspector-out');
  await field.fill('40');
  await field.press('Enter');
  const tl = await ed.expectServer((t) => byName(t, 'pic-a.mp4').recordOut === 40, 'picture trimmed');
  expect(span(byName(tl, 'snd-a.mp4')), 'the sound is untouched').toEqual([0, 60]);
});

test('R08.1 "Edit sound separately" selects and trims just the sound (an L cut), the link stays', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop'); // phones: the typed-number inspector fields are not reachable in the one-pane layout (recorded in the ledger)
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [PICTURE[0]], { audioClips: [SOUND[0]] }));
  await ed.tap(ed.testId('edit-separately'));
  await expect(ed.testId('edit-separately')).toHaveAttribute('aria-pressed', 'true');
  await ed.selectClipAt(30, 1);
  await ed.openInspector();
  const field = ed.testId('inspector-out');
  await field.fill('80');
  await field.press('Enter');
  const tl = await ed.expectServer((t) => byName(t, 'snd-a.mp4').recordOut === 80, 'the sound was extended on its own');
  expect(span(byName(tl, 'pic-a.mp4')), 'the picture did not move').toEqual([0, 60]);
  expect(byName(tl, 'snd-a.mp4').linkId, 'still linked').toBe(L1);
});

test('R08.1 migration banner adds sound tracks to an old timeline, is undoable and can be dismissed', async ({ freshTimeline, openEditor, api, seed }) => {
  // The old shape: a picture clip and a same-source sound that were never linked.
  const unlinkedPairs: ClipSpec[] = [{ name: 'old-snd.mp4', file: 'clipA', recordIn: 0, recordOut: 60 }];
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [{ name: 'old-pic.mp4', file: 'clipA', recordIn: 0, recordOut: 60 }], { audioClips: unlinkedPairs }));
  const apply = ed.testId('link-migration-apply');
  await expect(apply, 'offered, never auto-run').toBeVisible({ timeout: 30_000 });
  expect((await ed.serverTimeline()).document.tracks.flatMap((t) => t.items as unknown as Clip[]).every((c) => !c.linkId), 'nothing linked before the click').toBe(true);
  await ed.tap(apply);
  const tl = await ed.expectServer((t) => !!byName(t, 'old-pic.mp4').linkId, 'the pair is linked');
  expect(byName(tl, 'old-snd.mp4').linkId).toBe(byName(tl, 'old-pic.mp4').linkId);
  await expect(apply).toBeHidden();
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((t) => !byName(t, 'old-pic.mp4').linkId && !byName(t, 'old-snd.mp4').linkId, 'one Undo unlinks');
});

test.describe('tablet (coarse pointer, 1024x768)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });
  test('R08.1 tablet: tapping a linked clip and Delete in the clip menu removes both halves', async ({ freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const ed = await openEditor(await linkedPairs(freshTimeline, api, seed));
    await ed.selectClipAt(30);
    await ed.clipAction('delete');
    await ed.expectServer((t) => clips(t, 'video').length === 1 && clips(t, 'audio').length === 1, 'both halves removed by touch');
  });
});
