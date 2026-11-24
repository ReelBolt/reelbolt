import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, seekAndBlur } from '../../../fixtures/editor-v2/lane-a';
import type { Item, Timeline, TimelineDocument } from '../../../fixtures/editor-v2/api';
import { stubAnalysis } from '../../../fixtures/editor-v2/analysis-stub';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Wave 3 / T-008: the Captions sheet (RM.12, R09.5; x1 acceptance criteria 5 and 7). The dock's Captions item opens it.
 * Caption text edit saves, split / merge / delete, one-tap Remove silence (its summary matches the analysis) and Delete
 * words. Every outcome is read back from the SAVED document, never from the control.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const captionsOf = (tl: Timeline): Item[] => tl.document.tracks.flatMap((t) => t.items).filter((i) => i.type === 'caption').sort((a, b) => a.recordIn - b.recordIn);
const picture = (tl: Timeline): Item[] => tl.document.tracks.find((t) => t.kind === 'video' && t.items.some((i) => i.type === 'mediaClip'))!.items.filter((i) => i.type === 'mediaClip');
const pictureLength = (tl: Timeline): number => picture(tl).reduce((n, i) => n + (i.recordOut - i.recordIn), 0);
const tap = (ed: EditorPage, testId: string) => ed.tap(ed.testId(testId));

const caption = (id: string, recordIn: number, recordOut: number, text: string): Item => ({ type: 'caption', id, name: text.slice(0, 12), recordIn, recordOut, text, language: 'en', enabled: true, provenance: null });
const K1 = '0a000000-0000-4000-8000-000000000001';
const K2 = '0a000000-0000-4000-8000-000000000002';

/** The standard pair of clips plus a Subtitles track with two captions: [0,60) and [60,120). */
async function captionedTimeline(freshTimeline: Parameters<typeof clipsTimeline>[0], api: Parameters<typeof clipsTimeline>[1], seed: Parameters<typeof clipsTimeline>[2]): Promise<string> {
  return clipsTimeline(freshTimeline, api, seed, PAIR, {
    mutate: (doc: TimelineDocument) => {
      const v = doc.tracks.find((t) => t.kind === 'video')!;
      const subs = { ...structuredClone(v), id: crypto.randomUUID(), name: 'Subtitles', items: [caption(K1, 0, 60, 'Hello there my friend'), caption(K2, 60, 120, 'Second caption here')] };
      doc.tracks.splice(doc.tracks.indexOf(v) + 1, 0, subs);
    },
  });
}

const openCaptions = async (ed: EditorPage): Promise<void> => {
  await tap(ed, 'dock-captions');
  await expect(ed.testId('phone-captions-sheet')).toBeVisible();
};

test.describe('RM.12 caption list', () => {
  test('RM.12 AC 7 caption text edit saves: the stored text changes and a reload shows it', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await captionedTimeline(freshTimeline, api, seed));
    await openCaptions(ed);
    await expect(ed.testId(`phone-caption-text-${K1}`)).toContainText('Hello there my friend');
    await tap(ed, `phone-caption-edit-${K1}`);
    const field = ed.testId('caption-edit-text');
    const box = (await field.boundingBox())!;
    expect(box.width, 'a full-width field').toBeGreaterThan(280);
    await field.fill('Hello again, everyone');
    await tap(ed, 'caption-edit-done');
    await ed.expectServer((tl) => captionsOf(tl)[0]?.text === 'Hello again, everyone', 'the new text is stored');

    await ed.page.reload();
    await expect(ed.testId('phone-undo')).toBeVisible({ timeout: 30_000 });
    expect(captionsOf(await ed.serverTimeline())[0].text, 'the text survived the reload').toBe('Hello again, everyone');
    await openCaptions(ed);
    await expect(ed.testId(`phone-caption-text-${K1}`)).toContainText('Hello again, everyone');
  });

  test('RM.12 Cancel keeps the old text and an empty caption is refused in words', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await captionedTimeline(freshTimeline, api, seed));
    await openCaptions(ed);
    await tap(ed, `phone-caption-edit-${K2}`);
    await ed.testId('caption-edit-text').fill('   ');
    await tap(ed, 'caption-edit-done');
    await expect(ed.testId('caption-edit-problem')).toContainText(/cannot be empty/i);
    await tap(ed, 'caption-edit-cancel');
    expect(captionsOf(await ed.serverTimeline())[1].text, 'nothing was written').toBe('Second caption here');
  });

  test('RM.12 split at the playhead and merge with the next are stored, each one undo step', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await captionedTimeline(freshTimeline, api, seed));
    await seekAndBlur(ed, 30);
    await openCaptions(ed);
    await tap(ed, `phone-caption-split-${K1}`);
    const split = await ed.expectServer((tl) => captionsOf(tl).length === 3, 'the caption was split in two');
    expect(captionsOf(split)[0].recordOut, 'the first half ends at the playhead').toBe(30);
    expect(captionsOf(split)[1].recordIn, 'the second half starts there').toBe(30);
    await tap(ed, 'phone-undo');
    await ed.expectServer((tl) => captionsOf(tl).length === 2, 'one Undo joined the halves again');

    await tap(ed, `phone-caption-merge-${K1}`);
    const merged = await ed.expectServer((tl) => captionsOf(tl).length === 1, 'the two captions were merged');
    expect(captionsOf(merged)[0].text).toContain('Second caption here');
    await tap(ed, 'phone-undo');
    await ed.expectServer((tl) => captionsOf(tl).length === 2, 'one Undo split them again');
  });

  test('RM.12 Delete removes just that caption and the 6 second Undo brings it back', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await captionedTimeline(freshTimeline, api, seed));
    await openCaptions(ed);
    await tap(ed, `phone-caption-delete-${K1}`);
    const gone = await ed.expectServer((tl) => captionsOf(tl).length === 1, 'the first caption is gone');
    expect(captionsOf(gone)[0].recordIn, 'the other caption did not move').toBe(60);
    await expect(ed.testId('undo-snackbar')).toContainText('Caption deleted');
    await tap(ed, 'undo-snackbar-undo');
    await ed.expectServer((tl) => captionsOf(tl).length === 2, 'Undo restored the caption');
  });

  test('RM.12 Add at the playhead types a caption into the document', async ({ freshTimeline, openEditor, api, seed }) => {
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await seekAndBlur(ed, 20);
    await openCaptions(ed);
    await expect(ed.testId('phone-captions-none')).toBeVisible();
    await tap(ed, 'phone-captions-add');
    await ed.testId('caption-edit-text').fill('Typed on the phone');
    await tap(ed, 'caption-edit-done');
    const tl = await ed.expectServer((t) => captionsOf(t).length === 1, 'the caption is stored');
    expect(captionsOf(tl)[0].text).toBe('Typed on the phone');
    expect(captionsOf(tl)[0].recordIn, 'at the playhead').toBe(20);
  });
});

test.describe('R09.5 Remove silence from the Captions sheet', () => {
  test('R09.5 AC 5 Remove silence is reachable, its summary matches the analysis, and Remove is one undo step', async ({ page, freshTimeline, openEditor, api, seed }) => {
    // clipA is quiet at 0.5-1.0 s: 15 frames, 2 frames kept on each side by default, so 11 frames go.
    await stubAnalysis(page, [{ fileId: seed.files.clipA, silence: [{ startSec: 0.5, endSec: 1.0 }] }], { readyAtStart: true });
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const before = pictureLength(await ed.serverTimeline());
    await openCaptions(ed);
    await tap(ed, 'phone-captions-remove-silence');
    await expect(ed.page.getByRole('dialog')).toBeVisible();
    await expect(page.locator('[data-testid^="remove-silence-span-"]'), 'the one measured silence is listed').toHaveCount(1);
    await expect(ed.testId('remove-silence-summary')).toContainText('1 cut, 11 frames removed');
    await tap(ed, 'remove-silence-apply');
    const cut = await ed.expectServer((tl) => pictureLength(tl) === before - 11, 'exactly the summarised 11 frames were removed');
    expect(picture(cut).length, 'the clip was cut around the silence').toBeGreaterThan(2);
    await tap(ed, 'phone-undo');
    await ed.expectServer((tl) => pictureLength(tl) === before, 'one Undo put the silence back');
  });
});

const WORDS = [
  { text: 'Hello', startSec: 0.2, endSec: 0.5 },
  { text: 'there', startSec: 0.5, endSec: 0.9 },
  { text: 'brave', startSec: 0.9, endSec: 1.3 },
  { text: 'new', startSec: 1.3, endSec: 1.6 },
  { text: 'world', startSec: 1.6, endSec: 1.9 },
];

test.describe('RM.12 Delete words', () => {
  test('RM.12 Select words, tap the first and last word, Delete: the words are cut out, one undo step', async ({ page, freshTimeline, openEditor, api, seed }) => {
    await stubAnalysis(page, [{ fileId: seed.files.clipA, transcript: WORDS }], { readyAtStart: true });
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    const before = pictureLength(await ed.serverTimeline());
    await openCaptions(ed);
    await tap(ed, 'phone-captions-delete-words');
    await expect(ed.testId('word-picker')).toBeVisible();
    await expect(ed.testId('word-0')).toHaveText('Hello', { timeout: 30_000 });
    await tap(ed, 'word-picker-select');
    await tap(ed, 'word-1');
    await tap(ed, 'word-3');
    await expect(ed.testId('word-picker-summary')).toContainText('3 words selected');
    for (const id of ['word-handle-start', 'word-handle-end']) {
      const box = (await ed.testId(id).boundingBox())!;
      expect(Math.min(box.width, box.height), `${id} hit area`).toBeGreaterThanOrEqual(43.5);
    }
    // The arrow buttons are the no-gesture way to change the range: drop the last word.
    await tap(ed, 'word-end-earlier');
    await expect(ed.testId('word-picker-summary')).toContainText('2 words selected');
    await tap(ed, 'word-picker-delete');
    const cut = await ed.expectServer((tl) => pictureLength(tl) < before, 'the words were cut out of the stored timeline');
    // "there" and "brave" are 0.5 s to 1.3 s: about 24 frames (rounding of the frame edges allowed).
    expect(before - pictureLength(cut), 'about 24 frames removed').toBeGreaterThanOrEqual(20);
    expect(before - pictureLength(cut)).toBeLessThanOrEqual(28);
    // The Delete words sheet is 90% tall: on a 664 px screen it covers the header, so Done first, then the header Undo.
    await tap(ed, 'word-picker-done');
    await tap(ed, 'phone-undo');
    await ed.expectServer((tl) => pictureLength(tl) === before, 'one Undo put every word back');
  });

  test('RM.12 a 500 ms press on a word starts the selection with two draggable handles', async ({ page, freshTimeline, openEditor, api, seed }) => {
    test.skip(test.info().project.name === 'iphone14webkit', 'a held finger needs real CDP touch; Playwright WebKit taps are synthetic (D8). The Select words button covers the no-gesture path.');
    await stubAnalysis(page, [{ fileId: seed.files.clipA, transcript: WORDS }], { readyAtStart: true });
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await openCaptions(ed);
    await tap(ed, 'phone-captions-delete-words');
    const word = ed.testId('word-2');
    await expect(word).toHaveText('brave', { timeout: 30_000 });
    const box = (await word.boundingBox())!;
    const x = box.x + box.width / 2;
    const y = box.y + box.height / 2;
    const cdp = await page.context().newCDPSession(page);
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchStart', touchPoints: [{ x, y }] });
    // The picker's own timer is 500 ms: the selection must appear while the finger is still down.
    await expect(ed.testId('word-handle-start'), 'handles appear once the press is long enough').toBeVisible({ timeout: 3_000 });
    await cdp.send('Input.dispatchTouchEvent', { type: 'touchEnd', touchPoints: [] });
    await expect(ed.testId('word-picker-summary')).toContainText('1 word selected');
    await tap(ed, 'word-end-later');
    await expect(ed.testId('word-picker-summary')).toContainText('2 words selected');
  });

  test('RM.12 paragraph reordering says it needs a larger screen', async ({ page, freshTimeline, openEditor, api, seed }) => {
    await stubAnalysis(page, [{ fileId: seed.files.clipA, transcript: WORDS }], { readyAtStart: true });
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await openCaptions(ed);
    await tap(ed, 'phone-captions-delete-words');
    await expect(ed.page.locator('[data-testid="needs-larger-screen"][data-feature="paragraph-drag"]')).toBeVisible();
  });
});
