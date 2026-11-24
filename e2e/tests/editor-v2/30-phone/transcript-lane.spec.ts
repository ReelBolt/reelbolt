import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline } from '../../../fixtures/editor-v2/lane-a';
import type { Item, Timeline, TimelineDocument } from '../../../fixtures/editor-v2/api';
import { stubAnalysis } from '../../../fixtures/editor-v2/analysis-stub';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * Phone: the transcript can be started, watched and used without a larger screen. With no words yet the Captions sheet
 * (and Delete words) offers "Find the words in this video" with the credit estimate; nothing starts before the
 * confirmation; once the words are ready Generate from speech works and the captions lane can be hidden and shown.
 * Outcomes are read from the SAVED document.
 */
test.use({ serviceWorkers: 'block' });
test.beforeEach(() => { runOn('pixel7', 'iphone14webkit'); });

const tap = (ed: EditorPage, testId: string) => ed.tap(ed.testId(testId));
const captionsOf = (tl: Timeline): Item[] => tl.document.tracks.flatMap((t) => t.items).filter((i) => i.type === 'caption');
const WORDS = [
  { text: 'Hello', startSec: 0.2, endSec: 0.5 },
  { text: 'there', startSec: 0.5, endSec: 0.9 },
  { text: 'world', startSec: 0.9, endSec: 1.3 },
];

test.describe('phone transcript lane', () => {
  test('no words yet: price first, then the words arrive and Generate adds captions that can be hidden', async ({ page, freshTimeline, openEditor, api, seed }) => {
    const control = await stubAnalysis(page, [{ fileId: seed.files.clipA, transcript: WORDS }, { fileId: seed.files.clipB, transcript: WORDS }]);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await tap(ed, 'dock-captions');
    await expect(ed.testId('phone-captions-sheet')).toBeVisible();
    await expect(ed.testId('phone-captions-generate')).toBeDisabled();
    const find = ed.testId('analyse-first');
    expect((await find.boundingBox())!.height, 'finger-sized').toBeGreaterThanOrEqual(43.5);
    await ed.tap(find);
    await expect(ed.testId('analyse-first-price')).toContainText(/credit|free/i);
    expect(control.starts, 'nothing started before the confirmation').toHaveLength(0);
    await tap(ed, 'analyse-first-confirm');
    await expect.poll(() => control.starts.length).toBe(2);
    expect(control.starts.every((s) => s.kinds.join() === 'transcript')).toBe(true);
    await expect(ed.testId('phone-captions-generate')).toBeEnabled({ timeout: 60_000 });
    await tap(ed, 'phone-captions-generate');
    await ed.expectServer((tl) => captionsOf(tl).length > 0, 'captions were generated from the words');
    const subs = (await ed.serverTimeline()).document.tracks.find((t: TimelineDocument['tracks'][number]) => t.items.some((i) => i.type === 'caption'))!;
    await tap(ed, `phone-captions-lane-${subs.id}`);
    await ed.expectServer((tl) => tl.document.tracks.find((t) => t.id === subs.id)?.enabled === false, 'the captions lane is hidden');
    await tap(ed, `phone-captions-lane-${subs.id}`);
    await ed.expectServer((tl) => tl.document.tracks.find((t) => t.id === subs.id)?.enabled === true, 'the captions lane is shown again');
  });
});
