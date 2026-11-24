import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, itemNamed } from '../../../fixtures/editor-v2/lane-a';
import { runOn } from '../../../fixtures/editor-v2/rows';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';

/**
 * Wave 3 / T-007: the dock's "Look and fix" sheet (RM.10, R10.6; x1 acceptance criterion 7) and the phone clip
 * sheet (R07.7). Everything is a tap or a typed number (the numeric fallback of each slider), nothing is
 * dragged, and every outcome is read back from the SAVED document through the API.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

type Clip = Item & { grade?: { nodes?: { name?: string }[] } | null; playbackRate?: number; transform?: { opacity?: { value?: number }; positionX?: unknown } | null };
const clipNamed = (tl: Timeline, name: string, track = 0): Clip | undefined => itemNamed(tl, name, track) as Clip | undefined;
const allItems = (tl: Timeline): Item[] => tl.document.tracks.flatMap((t) => t.items);
const lookNode = (c: Clip | undefined) => c?.grade?.nodes?.find((n) => typeof n.name === 'string' && n.name.startsWith('Look: '))?.name;

const tap = (ed: EditorPage, testId: string) => ed.tap(ed.testId(testId));
/** The numeric fallback of a slider field: type the value, leave the box. */
async function typeNumber(ed: EditorPage, field: string, value: string): Promise<void> {
  const box = ed.testId(`${field}-number`);
  await box.scrollIntoViewIfNeeded();
  await box.fill(value);
  await box.blur();
}

test.describe('RM.10 Look and fix sheet', () => {
  test('RM.10 AC7 Look sheet applies a look to all clips, stored in the saved document', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'dock-look');
    await expect(ed.testId('look-sheet')).toBeVisible();
    await tap(ed, 'look-warm');
    await ed.expectServer((t) => lookNode(clipNamed(t, 'clipA.mp4')) !== undefined, 'clip A has the Warm look');
    expect(lookNode(clipNamed(await ed.expectSaved(), 'clipB.mp4')), 'clip B is untouched until Apply to all').toBeUndefined();
    await tap(ed, 'look-apply-all');
    const tl = await ed.expectServer((t) => lookNode(clipNamed(t, 'clipB.mp4')) !== undefined, 'clip B has the look too');
    expect(lookNode(clipNamed(tl, 'clipA.mp4'))).toContain('Warm');
    expect(lookNode(clipNamed(tl, 'clipB.mp4'))).toContain('Warm');
  });

  test('RM.10 a fix slider (typed) is stored as a colour correction on the clip', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'dock-look');
    await typeNumber(ed, 'fix-saturation', '1.5');
    await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.grade?.nodes?.some((n) => n.name === 'Fix') === true, 'clip A stores a Fix correction');
  });

  test('RM.10 a title is added with a template and its words can be edited', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await tap(ed, 'dock-look');
    await ed.testId('look-title-text').scrollIntoViewIfNeeded();
    await ed.testId('look-title-text').fill('Coming soon');
    await tap(ed, 'look-title-style-lowerThird');
    await tap(ed, 'look-title-add');
    const tl = await ed.expectServer((t) => allItems(t).some((i) => i.type === 'titleClip' && i.text === 'Coming soon'), 'the title is stored');
    expect(allItems(tl).find((i) => i.type === 'titleClip')!.template).toBe('lowerThird');
    // The new title is selected: its words are editable from the same sheet.
    const edit = ed.testId('look-title-edit-text');
    await edit.scrollIntoViewIfNeeded();
    await edit.fill('Out now');
    await edit.blur();
    await ed.expectServer((t) => allItems(t).some((i) => i.type === 'titleClip' && i.text === 'Out now'), 'the new words are stored');
  });
});

test.describe('R07.7 phone clip sheet', () => {
  test('R07.7 opacity, fade and speed from the clip sheet are stored', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-more');
    await tap(ed, 'phone-more-clip');
    await expect(ed.testId('clip-sheet')).toBeVisible();
    await typeNumber(ed, 'clip-opacity', '40');
    await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.transform?.opacity?.value === 0.4, 'opacity 40% is stored as 0.4');
    await typeNumber(ed, 'clip-fade-in', '0.5');
    await ed.expectServer((t) => (clipNamed(t, 'clipA.mp4')?.fadeInFrames ?? 0) > 0, 'a fade in is stored');
    await tap(ed, 'phone-speed-2');
    const tl = await ed.expectServer((t) => clipNamed(t, 'clipA.mp4')?.playbackRate === 2, 'clip A runs at 2x');
    expect(clipNamed(tl, 'clipA.mp4')!.recordOut).toBe(30);
  });

  test('R07.7 the strip Clip chip opens the same sheet, and Fill and the position pad write the transform', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.selectClipAt(30);
    await tap(ed, 'strip-clip-chip');
    await expect(ed.testId('clip-sheet')).toBeVisible();
    await typeNumber(ed, 'clip-scale', '50');
    await tap(ed, 'clip-pad-1--1');
    const tl = await ed.expectServer((t) => typeof (clipNamed(t, 'clipA.mp4')?.transform as { positionX?: { value?: number } } | null | undefined)?.positionX?.value === 'number', 'the position is stored');
    const x = (clipNamed(tl, 'clipA.mp4')!.transform as { positionX: { value: number } }).positionX.value;
    expect(x, 'top right: the picture moved right of centre').toBeGreaterThan(0);
  });

  test('R07.7 an animated value is read-only on the phone', async ({ freshTimeline, openEditor, api, seed }) => {
    const id = await clipsTimeline(freshTimeline, api, seed, PAIR, {
      mutate: (doc) => {
        const clip = doc.tracks[0].items[0];
        clip.transform = { positionX: { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' }, { frame: 30, value: 0.2, interp: 'linear' }] } };
      },
    });
    const ed = await openEditor(id);
    await ed.selectClipAt(30);
    await tap(ed, 'phone-action-more');
    await tap(ed, 'phone-more-clip');
    await expect(ed.testId('clip-x-readonly')).toContainText('Animated on desktop');
    await expect(ed.testId('clip-x-slider')).toHaveCount(0);
  });
});
