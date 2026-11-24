import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import type { Timeline } from '../../../fixtures/editor-v2/api';
import { expectLargerScreenState, extendAnalysis, runOn, TRANSCRIPT } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 09 analysis-driven editing: transcript-based editing and subtitles (remediation-plan.md W0.5; verdict-table
 * P6-TBE / P6-SUBS rows, audit d4 what-ifs). Transcription is paid and off on the local stack, so the run's
 * VideoAnalyze artifact is given a transcript by `extendAnalysis` (that artifact only): a panel that reads
 * the analysis sees words; a panel that reads the VideoCompile EDL (origin/master, ui1 D3) sees none.
 *
 * The derush timeline is one clip "Shot s0" [0,360) showing clipA source 0..12 s. Paragraphs: p0 0.2-2.9 s,
 * p1 4.6-7.9 s (a 1.6 s hole between "over" and "the"), p2 9.6-11.95 s (its last segment has no word timing:
 * an estimated word "≈").
 */

const ESTIMATED = { id: 't3', startSec: 11.85, endSec: 11.95, text: 'end' };
const ARTIFACT = { segments: [...TRANSCRIPT.segments, ESTIMATED], words: TRANSCRIPT.words };

const items = (tl: Timeline) => [...tl.document.tracks[0].items].sort((a, b) => a.recordIn - b.recordIn);
/** The piece that now plays where paragraph p0 started (record frame 6 = 0.2 s, after the lead-in). */
const atP0 = (tl: Timeline) => items(tl).find((i) => i.recordIn >= 4 && i.recordIn <= 8);
const word = (ed: EditorPage, id: string) => ed.page.getByTestId(`transcript-word-${id}`);

async function openTranscript(ed: EditorPage): Promise<void> {
  await ed.openPanelTab('tab-transcript');
  await expect(word(ed, 'w0'), 'the transcript lists the analysed words').toBeVisible();
}

async function derushWithTranscript(page: import('@playwright/test').Page, freshTimeline: (f: 'derush') => Promise<string>, openEditor: (id: string) => Promise<EditorPage>): Promise<EditorPage> {
  await extendAnalysis(page, ARTIFACT);
  return openEditor(await freshTimeline('derush'));
}

test('P6-TBE-01 the transcript panel shows the run\'s words, estimated ones marked ≈', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-TBE-01');
  const ed = await derushWithTranscript(page, freshTimeline, openEditor);
  await openTranscript(ed);
  await expect(ed.page.locator('[data-testid^="transcript-word-w"]')).toHaveCount(TRANSCRIPT.words.length);
  await expect(word(ed, 'w1')).toHaveText('um');
  await expect(ed.page.locator('[data-testid^="transcript-word-"]').filter({ hasText: '≈' }).first(), 'the estimated word is marked ≈').toBeVisible();
});

test('P6-TBE-02 select words then Delete words ripple-cuts them as one step', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-TBE-02');
  const ed = await derushWithTranscript(page, freshTimeline, openEditor);
  await openTranscript(ed);
  await word(ed, 'w4').click();
  await word(ed, 'w6').click({ modifiers: ['Shift'] });
  await ed.tap(ed.page.getByTestId('transcript-delete'));
  const cut = await ed.expectServer((tl) => items(tl).length === 2, '"quick brown fox" (1.5-2.9 s) is cut out');
  const [a, b] = items(cut);
  expect(Math.abs(a.recordOut - 45), `cut starts at ~frame 45 (got ${a.recordOut})`).toBeLessThan(4);
  expect(b.recordIn, 'rippled: no gap').toBe(a.recordOut);
  expect(Math.abs((b.sourceIn ?? 0) - 87), `the rest resumes after "fox" (source ~87, got ${b.sourceIn})`).toBeLessThan(4);
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((tl) => items(tl).length === 1 && items(tl)[0].recordOut === 360, 'ONE undo restores the clip');
});

test('P6-TBE-03 a selection across a pause longer than 0.35 s cuts two pieces', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-TBE-03');
  const ed = await derushWithTranscript(page, freshTimeline, openEditor);
  await openTranscript(ed);
  await word(ed, 'w7').click();
  await word(ed, 'w11').click({ modifiers: ['Shift'] });
  await ed.tap(ed.page.getByTestId('transcript-delete'));
  // "jumps over" 4.6-5.4 s and "the lazy dog" 7.0-7.9 s with a 1.6 s hole kept between them: three pieces.
  await ed.expectServer((tl) => items(tl).length === 3, 'two cuts around the kept 1.6 s pause');
});

test('P6-TBE-04 Find fillers lists um and the stutter; only the accepted one is removed', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-TBE-04');
  const ed = await derushWithTranscript(page, freshTimeline, openEditor);
  await openTranscript(ed);
  await ed.tap(ed.page.getByTestId('transcript-fillers-toggle'));
  const list = ed.page.getByTestId('transcript-fillers');
  const um = list.locator('label', { hasText: /filler “um”/ });
  const stutter = list.locator('label', { hasText: /stutter “the the”/ });
  await expect(um).toBeVisible();
  await expect(stutter).toBeVisible();
  await stutter.click(); // untick the stutter
  await ed.tap(ed.page.getByTestId('transcript-fillers-apply'));
  const cut = await ed.expectServer((tl) => items(tl).length === 2, 'only "um" is cut');
  expect(Math.abs(items(cut)[0].recordOut - 15), 'the cut sits at "um" (0.5 s)').toBeLessThan(4);
});

test('P6-TBE-05 dragging a paragraph above the first one reorders the clips', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-TBE-05');
  const ed = await derushWithTranscript(page, freshTimeline, openEditor);
  await openTranscript(ed);
  await ed.page.getByTestId('transcript-paragraph-p2').dragTo(ed.page.getByTestId('transcript-paragraph-p0'));
  await ed.expectServer((tl) => (atP0(tl)?.sourceIn ?? 0) > 250, 'paragraph p2 (source ~9.6 s) now plays where p0 started');
});

test.describe(() => {
  // WebKit sends the page's API requests through the dashboard's service worker (public/sw.js), where
  // `page.route` never sees them, so the transcript stub would not reach the panel ("The analysis carries
  // no transcript"). Blocking service workers lets the stub through; the app under test is unchanged.
  test.use({ serviceWorkers: 'block' });

  test('P6-TBE-05 phone: reordering paragraphs shows the "needs a larger screen" state', async ({ page, freshTimeline, openEditor }) => {
    runOn('pixel7', 'iphone14webkit');
    knownFailure('P6-TBE-05', 'phone');
    const ed = await derushWithTranscript(page, freshTimeline, openEditor);
    await ed.openPanelTab('tab-transcript');
    await expectLargerScreenState(ed.page, /paragraph|reorder/i, 'Reordering paragraphs on a phone', ed.testId('transcript-panel'));
  });
});

test('P6-TBE-06 moving a paragraph down with its arrow reorders the clips; one undo restores', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-TBE-06');
  const ed = await derushWithTranscript(page, freshTimeline, openEditor);
  await openTranscript(ed);
  await ed.tap(ed.page.getByTestId('transcript-move-down-p0'));
  await ed.expectServer((tl) => (atP0(tl)?.sourceIn ?? 0) > 60, 'what followed p0 now plays where p0 started');
  await ed.page.keyboard.press('Control+z');
  await ed.expectServer((tl) => items(tl).length === 1 && (items(tl)[0].sourceIn ?? 0) === 0, 'one undo restores the order');
});

test('P6-TBE-07 deleted words stop being offered and come back after undo', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-TBE-07');
  const ed = await derushWithTranscript(page, freshTimeline, openEditor);
  await openTranscript(ed);
  await word(ed, 'w4').click();
  await word(ed, 'w6').click({ modifiers: ['Shift'] });
  await ed.tap(ed.page.getByTestId('transcript-delete'));
  await expect(word(ed, 'w5'), 'a deleted word is no longer on the track').toHaveAttribute('data-on-track', 'false');
  await expect(word(ed, 'w5')).toBeDisabled();
  await ed.page.keyboard.press('Control+z');
  await expect(word(ed, 'w5'), 'undo brings it back').toHaveAttribute('data-on-track', 'true');
});

test('P6-SUBS-01 Generate captions makes one Subtitles track from the timed words, and a second run does not add another', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-SUBS-01');
  const ed = await derushWithTranscript(page, freshTimeline, openEditor);
  await ed.openPanelTab('tab-subtitles');
  await expect(ed.page.getByTestId('subtitles-preview-count')).not.toContainText(/^\s*0 cues/);
  await ed.tap(ed.page.getByTestId('subtitles-generate'));
  await expect(ed.page.getByTestId('subtitles-message')).toContainText(/caption/i);
  const captionTracks = (tl: Timeline) => tl.document.tracks.filter((t) => t.items.some((i) => i.type === 'caption'));
  const made = await ed.expectServer((tl) => captionTracks(tl).length === 1, 'one caption track is stored');
  const cues = captionTracks(made)[0].items.length;
  expect(cues, 'captions were made').toBeGreaterThan(0);
  await ed.tap(ed.page.getByTestId('subtitles-generate'));
  await ed.page.waitForTimeout(1500);
  const again = await ed.expectSaved();
  expect(captionTracks(again).length, 'generating again does not add a second caption track').toBe(1);
});

test('P6-SUBS-02 the cue budget (lines per cue, characters per line) changes the cues', async ({ page, freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P6-SUBS-02');
  const ed = await derushWithTranscript(page, freshTimeline, openEditor);
  await ed.openPanelTab('tab-subtitles');
  const count = async () => Number(/(\d+) cues?/.exec((await ed.page.getByTestId('subtitles-preview-count').textContent()) ?? '')?.[1] ?? 0);
  await expect.poll(count, { message: 'cues are previewed from the words' }).toBeGreaterThan(0);
  const two = await count();
  const lines = ed.page.getByTestId('subtitles-max-lines');
  await lines.fill('1');
  await lines.press('Tab');
  const chars = ed.page.getByTestId('subtitles-max-chars');
  await chars.fill('16');
  await chars.press('Tab');
  await expect.poll(count, { message: 'a tighter budget makes more cues' }).toBeGreaterThan(two);
});
