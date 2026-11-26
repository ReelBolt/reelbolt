import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline, type ClipSpec } from '../../../fixtures/editor-v2/lane-a';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { stubAnalysis, type StubWord } from '../../../fixtures/editor-v2/analysis-stub';
import { runOn, desktopLayoutOnly } from '../../../fixtures/editor-v2/rows';

/**
 * T-014 analysis-driven cuts: R09.2 (linked picture and sound are cut together), R09.3 (audition without
 * writing), R09.4 (rough-timed words need a yes), R09.5 (two sources on one track: no cross-source cuts),
 * R09.8 (shots are never cut through speech), R09.9 (repeated takes: keep one, set the rest aside).
 * Analysis is stubbed at the per-file analysis API; every assertion reads the STORED document.
 */

const items = (tl: Timeline, kind?: 'video' | 'audio'): Item[] => tl.document.tracks.filter((t) => !kind || t.kind === kind).flatMap((t) => t.items);
const lengthOf = (list: Item[]): number => list.reduce((n, i) => n + (i.recordOut - i.recordIn), 0);
const mainVideo = (tl: Timeline): Item[] => tl.document.tracks.find((t) => t.kind === 'video' && !t.name.startsWith('Other takes'))!.items;

const L1 = '11111111-1111-4111-8111-111111111111';
const PICTURE: ClipSpec[] = [{ name: 'pic-a.mp4', file: 'clipA', recordIn: 0, recordOut: 60, extra: { linkId: L1 } as never }];
const SOUND: ClipSpec[] = [{ name: 'snd-a.mp4', file: 'clipA', recordIn: 0, recordOut: 60, extra: { linkId: L1 } as never }];

async function openSilence(ed: EditorPage): Promise<void> {
  await ed.openRemoveSilence();
  await expect(ed.page.getByRole('dialog')).toBeVisible();
}

test('R09.5 two sources on one track: a silence measured in file A never cuts file B', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  // A is quiet at 0.5-1.0 s of ITS file, B at 1.4-1.9 s of ITS file. Both clips show their file's first 2 s, so a
  // matcher that ignored the source would also cut B at 0.5-1.0 s.
  await stubAnalysis(page, [
    { fileId: seed.files.clipA, silence: [{ startSec: 0.5, endSec: 1.0 }] },
    { fileId: seed.files.clipB, silence: [{ startSec: 1.4, endSec: 1.9 }] },
  ], { readyAtStart: true });
  const id = await clipsTimeline(freshTimeline, api, seed, PAIR);
  const ed = await openEditor(id);
  await openSilence(ed);
  await expect(page.locator('[data-testid^="remove-silence-span-"]'), 'one silence per file').toHaveCount(2);
  await ed.tap(page.getByTestId('remove-silence-apply'));
  const cut = await ed.expectServer((tl) => items(tl).length > 2, 'the silences were cut');
  const fileOf = (i: Item) => (i.assetRef as { projectFileId?: string }).projectFileId;
  const a = items(cut).filter((i) => fileOf(i) === seed.files.clipA);
  const b = items(cut).filter((i) => fileOf(i) === seed.files.clipB);
  // Default padding keeps 2 frames each side: each loses 15-4 = 11 frames. Nothing else.
  expect(lengthOf(a), 'A lost exactly its own silence').toBe(60 - 11);
  expect(lengthOf(b), 'B lost exactly its own silence, not A\'s').toBe(60 - 11);
  expect(a.every((i) => i.recordOut <= 49), 'A\'s pieces stay inside A\'s part of the timeline').toBe(true);
});

test('R09.2 silence removal cuts the linked sound together with the picture; one Undo restores both', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  await stubAnalysis(page, [{ fileId: seed.files.clipA, silence: [{ startSec: 0.5, endSec: 1.0 }] }], { readyAtStart: true });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PICTURE, { audioClips: SOUND }));
  await openSilence(ed);
  await expect(page.getByTestId('remove-silence-summary')).toContainText(/linked sound/i);
  await ed.tap(page.getByTestId('remove-silence-apply'));
  const cut = await ed.expectServer((tl) => items(tl, 'video').length === 2, 'the picture was cut');
  expect(items(cut, 'audio'), 'the sound was cut at the same place').toHaveLength(2);
  expect(lengthOf(items(cut, 'video'))).toBe(49);
  expect(lengthOf(items(cut, 'audio')), 'picture and sound are the same length').toBe(49);
  const spans = (kind: 'video' | 'audio') => items(cut, kind).map((i) => [i.recordIn, i.recordOut]);
  expect(spans('audio'), 'the cut pieces line up').toEqual(spans('video'));
  const links = (kind: 'video' | 'audio') => items(cut, kind).map((i) => i.linkId ?? null);
  expect(links('audio'), 'each piece is still linked to its own sound').toEqual(links('video'));
  expect(new Set(links('video')).size, 'two halves are two pairs').toBe(2);
  await ed.tap(ed.testId('undo'));
  await ed.expectServer((tl) => items(tl, 'video').length === 1 && items(tl, 'audio').length === 1 && lengthOf(items(tl, 'audio')) === 60, 'one Undo restores picture and sound');
});

test('R09.3 audition plays the result without writing; Cancel leaves the stored timeline alone', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  await stubAnalysis(page, [{ fileId: seed.files.clipA, silence: [{ startSec: 0.5, endSec: 1.0 }] }], { readyAtStart: true });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await openSilence(ed);
  await ed.tap(page.getByTestId('remove-silence-audition'));
  await expect(page.getByTestId('audition-banner')).toContainText(/Nothing is saved yet/);
  const patches = ed.patches.length;
  await ed.tap(page.getByTestId('audition-cancel'));
  await expect(page.getByTestId('audition-banner')).toHaveCount(0);
  await page.waitForTimeout(1200); // a negative: Cancel must not write
  expect(ed.patches.length).toBe(patches);
  const stored = await ed.serverTimeline();
  expect(lengthOf(items(stored)), 'nothing was cut').toBe(120);
});

const ROUGH: StubWord[] = [
  { text: 'so', startSec: 0.2, endSec: 0.5 },
  { text: 'quick', startSec: 0.6, endSec: 1.0, estimated: true },
  { text: 'fox', startSec: 1.0, endSec: 1.4 },
];

test('R09.4 a word timed only roughly cannot be deleted without a yes', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  await stubAnalysis(page, [{ fileId: seed.files.clipA, transcript: ROUGH }], { readyAtStart: true });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.openPanelTab('tab-transcript');
  const quick = page.getByTestId('transcript-word-w1');
  await expect(quick).toContainText('≈');
  await ed.tap(quick);
  await ed.tap(page.getByTestId('transcript-delete'));
  await expect(page.getByTestId('transcript-estimate-guard')).toContainText(/timed roughly/i);
  await ed.tap(page.getByTestId('transcript-estimate-cancel'));
  await page.waitForTimeout(1200); // a negative: "Keep them" writes nothing
  expect(lengthOf(items(await ed.serverTimeline())), 'still uncut').toBe(120);
  await ed.tap(page.getByTestId('transcript-delete'));
  await ed.tap(page.getByTestId('transcript-estimate-confirm'));
  const cut = await ed.expectServer((tl) => lengthOf(items(tl)) < 120, 'after "Cut anyway" the word is cut');
  expect(lengthOf(items(cut))).toBeLessThan(120);
});

test('R09.8 a boring shot is cut only where nobody is talking; an all-speech shot is not suggested', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const quiet: StubWord[] = [{ text: 'hello', startSec: 1.0, endSec: 1.5 }];
  await stubAnalysis(page, [{
    fileId: seed.files.clipA,
    shots: [{ startSec: 0, endSec: 5 }],
    motion: [{ startSec: 0, endSec: 5, mean: 0.01, motionClass: 'still' }],
    transcript: quiet,
  }], { readyAtStart: true });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 150 }]));
  await ed.workspace('cut');
  const badge = page.locator('[data-testid^="cut-boring-speech-"]').first();
  await expect(badge).toContainText(/Cuts only the quiet 3\.\d s; speech kept/);
  await ed.tap(page.locator('[data-testid^="cut-boring-remove-"]').first());
  const cut = await ed.expectServer((tl) => lengthOf(items(tl)) < 150, 'the quiet part was cut');
  const removed = 150 - lengthOf(items(cut));
  expect(removed, 'about 3.25 s (97 frames) removed, not the 5 s shot').toBeGreaterThan(90);
  expect(removed).toBeLessThan(105);
  const first = [...items(cut)].sort((a, b) => a.recordIn - b.recordIn)[0];
  expect(first.recordIn, 'the opening, with the spoken word, is kept').toBe(0);
  expect(first.recordOut, 'up to just after the word (1.5 s + margin)').toBeGreaterThan(50);
});

test('R09.8 a shot that is all speech is not suggested', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const talking: StubWord[] = Array.from({ length: 12 }, (_, i) => ({ text: `w${i}`, startSec: i * 0.45, endSec: i * 0.45 + 0.4 }));
  await stubAnalysis(page, [{
    fileId: seed.files.clipA,
    shots: [{ startSec: 0, endSec: 5 }],
    motion: [{ startSec: 0, endSec: 5, mean: 0.01, motionClass: 'still' }],
    transcript: talking,
  }], { readyAtStart: true });
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, [{ name: 'clipA.mp4', file: 'clipA', recordIn: 0, recordOut: 150 }]));
  await ed.workspace('cut');
  await expect(page.locator('[data-testid^="cut-boring-speech-"]').first()).toContainText(/Someone is talking/);
  await expect(page.locator('[data-testid^="cut-boring-remove-"]').first()).toBeDisabled();
});

const sentence = (text: string, start: number): StubWord[] => text.split(' ').map((t, i) => ({ text: t, startSec: start + i * 0.3, endSec: start + i * 0.3 + 0.25 }));
const TAKES: StubWord[] = [
  ...sentence('the quick brown um fox jumps over the lazy dog.', 0.5),
  ...sentence('and now something else entirely.', 4.5),
  ...sentence('the quick brown fox jumps over the lazy dog.', 7.0),
];

test('R09.9 repeated takes are grouped, the clean one is suggested, the others are set aside (not lost)', async ({ page, freshTimeline, openEditor, seed }) => {
  desktopLayoutOnly('takes are not on a phone');
  runOn('desktop', 'pixel7');
  await stubAnalysis(page, [{ fileId: seed.files.clipA, transcript: TAKES }], { mode: 'transcriptOnly', readyAtStart: true });
  const ed = await openEditor(await freshTimeline('derush'));
  await ed.openPanelTab('tab-takes');
  const group = page.getByTestId('takes-group-r0');
  await expect(group).toContainText('quick brown fox');
  await expect(group).toContainText(/best take/i);
  await expect(group).toContainText(/1 stumble/);
  const before = await ed.serverTimeline();
  await ed.tap(page.getByTestId('takes-keep-apply'));
  const after = await ed.expectServer((tl) => tl.document.tracks.some((t) => t.name === 'Other takes'), 'the other take was parked');
  const holding = after.document.tracks.find((t) => t.name === 'Other takes')!;
  expect(holding.items.length, 'the set-aside take is kept on the holding track').toBeGreaterThan(0);
  expect((holding as { enabled?: boolean }).enabled, 'the holding track is switched off').toBe(false);
  expect(lengthOf(mainVideo(after)), 'the film got shorter by the set-aside take').toBeLessThan(lengthOf(mainVideo(before)) - 60);
});
