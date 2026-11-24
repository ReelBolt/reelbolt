import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, countItems, itemNamed, seekAndBlur } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
import type { Item } from '../../../fixtures/editor-v2/api';

/**
 * Wave 3 / T-004: the phone action bar, Select mode and the trim sheet (RM.5; x1 acceptance criterion 4).
 * No drag and no keyboard: every step is a tap (pixel7 has real CDP touch, iphone14webkit taps are synthetic),
 * and every outcome is read back from the SAVED document through the API.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

type Clip = Item & { linkId?: string | null; playbackRate?: number; sourceIn?: number };
const clipNamed = (tl: Parameters<typeof itemNamed>[0], name: string, track = 0): Clip | undefined => itemNamed(tl, name, track) as Clip | undefined;

const tap = (ed: EditorPage, testId: string) => ed.tap(ed.testId(testId));

test.describe('RM.5 action bar', () => {
  test('RM.5 the bar offers the right actions for nothing selected and for a selected clip', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const bar = ed.testId('phone-action-bar');
    await expect(bar).toHaveAttribute('data-mode', 'none');
    for (const id of ['add-media', 'add-text', 'marker', 'select']) await expect(ed.testId(`phone-action-${id}`)).toBeVisible();
    await ed.selectClipAt(30);
    await expect(bar).toHaveAttribute('data-mode', 'clip');
    for (const id of ['split', 'duplicate', 'trim', 'nudge', 'more', 'delete']) await expect(ed.testId(`phone-action-${id}`)).toBeVisible();
    const box = (await bar.boundingBox())!;
    expect(box.height, 'the bar is 52 px').toBeGreaterThanOrEqual(51.5);
    expect(box.height).toBeLessThanOrEqual(52.5);
    const del = (await ed.testId('phone-action-delete').boundingBox())!;
    const more = (await ed.testId('phone-action-more').boundingBox())!;
    expect(del.height).toBeGreaterThanOrEqual(43.5);
    expect(del.x - (more.x + more.width), 'Delete is at least 8 px from its neighbour').toBeGreaterThanOrEqual(8);
  });

  test('RM.5 split by tap is stored in the document', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await seekAndBlur(ed, 30);
    await ed.selectClipAt(15);
    await tap(ed, 'phone-action-split');
    const tl = await ed.expectServer((t) => countItems(t) === 3, 'clip A was split in two in the stored document');
    const names = tl.document.tracks[0].items.map((i) => [i.recordIn, i.recordOut]);
    expect(names).toContainEqual([0, 30]);
    expect(names).toContainEqual([30, 60]);
  });

  test('RM.5 ripple delete closes the gap and the 6 second Undo brings the clip back', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-delete');
    await expect(ed.testId('undo-snackbar')).toContainText('Clip deleted');
    const tl = await ed.expectServer((t) => countItems(t) === 1, 'clip A is gone from the stored document');
    expect(clipNamed(tl, 'clipB.mp4')!.recordIn, 'the gap closed (ripple delete)').toBe(0);
    await tap(ed, 'undo-snackbar-undo');
    const back = await ed.expectServer((t) => countItems(t) === 2, 'Undo restored clip A');
    expect(clipNamed(back, 'clipA.mp4')!.recordIn).toBe(0);
    expect(clipNamed(back, 'clipB.mp4')!.recordIn).toBe(60);
  });

  test('RM.5 lift leaves a gap', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-more');
    await tap(ed, 'phone-more-lift');
    const tl = await ed.expectServer((t) => countItems(t) === 1, 'clip A was lifted out');
    expect(clipNamed(tl, 'clipB.mp4')!.recordIn, 'the gap stays').toBe(60);
  });

  test('RM.5 duplicate adds a copy', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-duplicate');
    await ed.expectServer((t) => countItems(t) === 3, 'a copy of the clip is stored');
  });

  test('RM.5 nudge moves the clip by the chosen frames', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(75); // clip B (inside the 20 px edge dead zone of a 390 px screen at frame 90)
    await tap(ed, 'phone-action-nudge');
    await tap(ed, 'phone-nudge+10');
    const tl = await ed.expectServer((t) => clipNamed(t, 'clipB.mp4')?.recordIn === 70, 'clip B moved 10 frames later');
    expect(clipNamed(tl, 'clipB.mp4')!.recordOut).toBe(130);
  });

  test('RM.5 move later and move earlier trade places with the neighbour', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-more');
    await tap(ed, 'phone-more-move-later');
    const tl = await ed.expectServer((t) => clipNamed(t, 'clipB.mp4')?.recordIn === 0, 'clip B now comes first');
    expect(clipNamed(tl, 'clipA.mp4')!.recordIn).toBe(60);
    await tap(ed, 'phone-action-more');
    await tap(ed, 'phone-more-move-earlier');
    await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.recordIn === 0, 'clip A is first again');
  });

  test('RM.5 speed chip retimes the clip', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-more');
    await tap(ed, 'phone-more-speed');
    await tap(ed, 'phone-speed-2');
    const tl = await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.playbackRate === 2, 'clip A runs at 2x');
    expect(clipNamed(tl, 'clipA.mp4')!.recordOut).toBe(30);
  });
});

test.describe('RM.5 trim sheet', () => {
  test('RM.5 trim via the sheet is stored, one undo step per session', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-trim');
    await expect(ed.testId('trim-sheet')).toBeVisible();
    await tap(ed, 'trim-end-10');
    await tap(ed, 'trim-end-1');
    await tap(ed, 'trim-done');
    const tl = await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.recordOut === 49, 'clip A ends 11 frames earlier');
    expect(clipNamed(tl, 'clipB.mp4')!.recordIn, 'ripple: the next clip followed').toBe(49);
    await tap(ed, 'phone-undo');
    const back = await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.recordOut === 60, 'ONE undo took back the whole trim session');
    expect(clipNamed(back, 'clipB.mp4')!.recordIn).toBe(60);
  });

  test('RM.5 the start stepper and a typed value both move the in-point', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-trim');
    await tap(ed, 'trim-start+10');
    await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.sourceIn === 10, 'the in-point moved 10 frames into the source');
    const field = ed.testId('inspector-in');
    await field.fill('20');
    await field.press('Enter');
    await tap(ed, 'trim-done');
    await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.sourceIn === 20, 'a typed in-point of 20 is stored');
  });

  test('RM.5 undo and redo from the header, checked in the stored document', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-duplicate');
    await ed.expectServer((t) => countItems(t) === 3, 'duplicate stored');
    await tap(ed, 'phone-undo');
    await ed.expectServer((t) => countItems(t) === 2, 'undo took the copy away');
    await tap(ed, 'phone-redo');
    await ed.expectServer((t) => countItems(t) === 3, 'redo brought it back');
  });
});

test.describe('RM.5 Select mode', () => {
  test('RM.5 tapping clips builds a group and Delete removes the whole group', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [...PAIR, { name: 'clipC.mp4', file: 'clipA', recordIn: 120, recordOut: 180 }]));
    await tap(ed, 'phone-action-select');
    await expect(ed.testId('phone-action-bar')).toHaveAttribute('data-mode', 'select');
    await ed.selectClipAt(30);
    await ed.selectClipAt(75);
    await expect(ed.testId('phone-action-select-done')).toContainText('2');
    await tap(ed, 'phone-action-delete');
    const tl = await ed.expectServer((t) => countItems(t) === 1, 'both tapped clips are gone');
    expect(clipNamed(tl, 'clipC.mp4')!.recordIn, 'the third clip closed up').toBe(0);
  });

  test('RM.5 Clear empties the group and Done leaves Select mode', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await tap(ed, 'phone-action-select');
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-select-clear');
    await expect(ed.testId('phone-action-select-clear')).toBeDisabled();
    await tap(ed, 'phone-action-select-done');
    await expect(ed.testId('phone-action-bar')).toHaveAttribute('data-mode', 'none');
  });

  test('RM.5 a linked picture and sound pair is deleted together', async ({ freshTimeline, openEditor, api, seed }) => {
    const link = 'a1b2c3d4-0000-4000-8000-000000000001';
    const ed = await openEditor(
      await clipsTimeline(freshTimeline, api, seed, [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 60, extra: { linkId: link } as Partial<Item> }], {
        audioClips: [{ name: 'clipA sound', file: 'clipA', recordIn: 0, recordOut: 60, extra: { linkId: link } as Partial<Item> }],
      }),
    );
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-delete');
    const tl = await ed.expectServer((t) => t.document.tracks.every((tr) => tr.items.length === 0), 'the picture and its linked sound are both gone');
    expect(tl.document.tracks.flatMap((t) => t.items)).toHaveLength(0);
  });
});
