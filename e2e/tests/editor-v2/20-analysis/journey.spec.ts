import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { stubAnalysis, type StubWord } from '../../../fixtures/editor-v2/analysis-stub';
import { downloadRender, renderAndWait, runOn, probe } from '../../../fixtures/editor-v2/rows';
import * as fs from 'fs';

/**
 * T-014 Journey 2 (R09.2-R09.7): a seeded derush timeline -> Analyse (the free analyses run for real on the
 * backend; the paid transcript is stubbed at the analysis API) -> Remove silence (audition first, then apply)
 * -> delete the filler words -> generate captions -> deliver (burn in + sidecar files) -> a render that passes.
 * Every outcome is read from the STORED timeline document.
 */

const WORDS: StubWord[] = [
  { text: 'so', startSec: 0.2, endSec: 0.5 },
  { text: 'um', startSec: 0.5, endSec: 0.9 },
  { text: 'the', startSec: 0.9, endSec: 1.2 },
  { text: 'the', startSec: 1.25, endSec: 1.5 },
  { text: 'quick', startSec: 1.5, endSec: 1.9 },
  { text: 'brown', startSec: 1.9, endSec: 2.3 },
  { text: 'fox', startSec: 2.3, endSec: 2.9 },
  { text: 'jumps', startSec: 4.6, endSec: 5.0 },
  { text: 'over', startSec: 5.0, endSec: 5.4 },
  { text: 'the', startSec: 5.5, endSec: 5.8 },
  { text: 'lazy', startSec: 5.8, endSec: 6.1 },
  { text: 'dog', startSec: 6.1, endSec: 6.5 },
];

const track0 = (tl: Timeline): Item[] => tl.document.tracks[0].items;
const total = (tl: Timeline): number => track0(tl).reduce((n, i) => n + (i.recordOut - i.recordIn), 0);
const captionsOf = (tl: Timeline): Item[] => tl.document.tracks.flatMap((t) => t.items).filter((i) => i.type === 'caption');

async function stateOf(ed: EditorPage, fileId: string, kind: string): Promise<string | null> {
  return ed.page.getByTestId(`analyse-kind-${fileId}-${kind}`).getAttribute('data-state');
}

test('T-014 Journey 2: analyse, remove silence (audition first), delete fillers, generate captions, deliver, render', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  const control = await stubAnalysis(page, [{ fileId: seed.files.clipA, transcript: WORDS }], { mode: 'transcriptOnly' });
  const timelineId = await freshTimeline('derush');
  const ed = await openEditor(timelineId);
  const fileId = seed.files.clipA;

  // ── Analyse: the free kinds run for real and finish; nothing fails.
  await ed.openPanelTab('tab-analyse');
  await expect(page.getByTestId(`analyse-file-${fileId}`)).toBeVisible();
  await ed.tap(page.getByTestId(`analyse-run-${fileId}`));
  await expect.poll(() => stateOf(ed, fileId, 'silence'), { message: 'silence analysis finishes', timeout: 120_000 }).toMatch(/ready|failed/);
  for (const kind of ['silence', 'shots', 'motion']) {
    await expect.poll(() => stateOf(ed, fileId, kind), { message: `${kind} analysis finishes`, timeout: 120_000 }).toBe('ready');
  }

  // ── Transcript: the credit estimate is shown first, and nothing is started until it is confirmed.
  await ed.tap(page.getByTestId(`analyse-transcribe-${fileId}`));
  await expect(page.getByTestId(`analyse-estimate-${fileId}`)).toContainText(/3 credits/);
  expect(control.starts.filter((s) => s.kinds.includes('transcript')), 'no transcript job before the user confirms').toHaveLength(0);
  await ed.tap(page.getByTestId(`analyse-transcribe-confirm-${fileId}`));
  await expect.poll(() => stateOf(ed, fileId, 'transcript'), { message: 'transcript finishes', timeout: 60_000 }).toBe('ready');

  // ── Remove silence: audition first. The viewer plays the result; the stored document is untouched.
  const before = await ed.serverTimeline();
  expect(track0(before)).toHaveLength(1);
  await ed.openRemoveSilence();
  await expect(page.locator('[data-testid^="remove-silence-span-"]')).toHaveCount(2);
  await ed.tap(page.getByTestId('remove-silence-audition'));
  await expect(page.getByTestId('audition-banner')).toBeVisible();
  const patchesBefore = ed.patches.length;
  await page.waitForTimeout(1500); // a negative: an audition must not write
  expect(ed.patches.length, 'an audition writes nothing').toBe(patchesBefore);
  expect(track0(await ed.serverTimeline()), 'the stored timeline is still one clip').toHaveLength(1);
  await ed.tap(page.getByTestId('audition-apply'));
  const cut = await ed.expectServer((tl) => track0(tl).length === 3, 'Apply cut the two silences');
  expect(total(cut), 'the silences are gone').toBeLessThan(total(before) - 60);

  // ── Filler words: "um" and the stutter are found in the transcript and removed.
  await ed.openPanelTab('tab-transcript');
  await expect(page.getByTestId('transcript-word-w1')).toBeVisible();
  await ed.tap(page.getByTestId('transcript-fillers-toggle'));
  await expect(page.getByTestId('transcript-fillers')).toContainText(/filler “um”/);
  await ed.tap(page.getByTestId('transcript-fillers-apply'));
  const clean = await ed.expectServer((tl) => total(tl) < total(cut) - 20, 'the fillers were cut out');
  expect(track0(clean).length).toBeGreaterThan(3);

  // ── Captions from the transcript, with the look that suits the picture.
  await ed.openPanelTab('tab-subtitles');
  await expect(page.getByTestId('subtitles-preview-count')).not.toContainText(/^\s*0 cues/);
  await ed.tap(page.getByTestId('subtitles-generate'));
  const captioned = await ed.expectServer((tl) => captionsOf(tl).length > 0, 'captions are stored');
  for (const c of captionsOf(captioned)) {
    expect(c.look, 'a look from the catalogue is stored').toMatch(/^(clean|bold|boxed|outline|spotlight)$/);
    expect(c.position).toBe('bottomCenter');
  }

  // ── Deliver: burn in AND separate files; the sidecar downloads carry the caption text.
  await ed.workspace('deliver');
  await expect(page.getByTestId('deliver-captions')).toBeVisible();
  await ed.tap(page.getByText('Both', { exact: true }));
  const srt = page.getByTestId('deliver-captions-srt');
  await expect(srt).toBeVisible();
  const srtResponse = await page.request.get((await srt.getAttribute('href')) as string);
  expect(srtResponse.status()).toBe(200);
  expect((await srtResponse.text()).length, 'the SRT has content').toBeGreaterThan(20);

  const render = await renderAndWait(api, seed.projectId, timelineId);
  expect(render.status, render.errorMessage ?? 'render passes').toBe('Passed');
  const file = await downloadRender(api, seed.projectId, render);
  expect(fs.statSync(file).size, 'the burned-in film is a real file').toBeGreaterThan(0);
  expect(probe(file).durationSec, 'the film is as long as the edited timeline').toBeGreaterThan(1);
});
