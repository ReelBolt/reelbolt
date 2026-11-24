import * as fs from 'fs';
import * as path from 'path';
import { execFileSync } from 'child_process';
import { test, expect, type EditorPage } from '../../../fixtures/editor-v2';
import { mediaClip, type Item, type Timeline } from '../../../fixtures/editor-v2/api';
import { stubAnalysis, type StubWord } from '../../../fixtures/editor-v2/analysis-stub';
import { probe, runOn } from '../../../fixtures/editor-v2/rows';
import { waitRender, type RenderSummary } from '../../../fixtures/editor-v2/rows';

/**
 * T-027 Journey 2 (Wave 2 exit): a derush timeline of TWO sources -> Analyse (free analyses run for real, the paid
 * transcript is stubbed at the analysis API) -> Remove silence (audition first) -> delete the filler words -> generate
 * captions -> burn-in render -> the downloaded film shows the captions (pixels in the caption band differ from a render
 * without them, the rest of the frame does not). No edit may cut across the two sources. Everything is read from the
 * stored document or the downloaded bytes.
 */

const DL_DIR = '/tmp/editor-remediation/journeys-downloads';
const FPS = 30;
/** Source A is cut to its first 9 s on the timeline: its last second (silent from 8.0 s) runs straight into source B. */
const A_END = 270;
const B_LEN = 240;

const WORDS_A: StubWord[] = [
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
const WORDS_B: StubWord[] = [
  { text: 'welcome', startSec: 0.4, endSec: 1.0 },
  { text: 'back', startSec: 1.0, endSec: 1.5 },
  { text: 'friends', startSec: 1.6, endSec: 2.3 },
];

const items = (tl: Timeline, kind: 'video' | 'audio'): Item[] => tl.document.tracks.find((t) => t.kind === kind)!.items;
const total = (xs: Item[]): number => xs.reduce((n, i) => n + (i.recordOut - i.recordIn), 0);
const captionsOf = (tl: Timeline): Item[] => tl.document.tracks.flatMap((t) => t.items).filter((i) => i.type === 'caption');

async function stateOf(ed: EditorPage, fileId: string, kind: string): Promise<string | null> {
  return ed.page.getByTestId(`analyse-kind-${fileId}-${kind}`).getAttribute('data-state');
}

/** The whole edit still reads as source A then source B, each clip inside its own source, nothing cut across them. */
function expectNoCrossSourceCuts(tl: Timeline, files: { a: string; b: string }, label: string): { aEnd: number } {
  for (const kind of ['video', 'audio'] as const) {
    const list = items(tl, kind);
    const owner = list.map((i) => (i.assetRef?.projectFileId === files.b ? 'B' : 'A'));
    const firstB = owner.indexOf('B');
    expect(firstB, `${label} ${kind}: source B is on the track`).toBeGreaterThan(0);
    expect(owner.slice(firstB).every((o) => o === 'B'), `${label} ${kind}: every B clip comes after every A clip (${owner.join('')})`).toBe(true);
    list.forEach((i, n) => {
      if (n > 0) expect(i.recordIn, `${label} ${kind}: clip ${n} follows the previous one with no gap and no overlap`).toBe(list[n - 1].recordOut);
      if (owner[n] === 'A') expect((i.sourceIn ?? 0) + (i.recordOut - i.recordIn), `${label} ${kind}: A clip ${n} stays inside A's first 9 s (never into B's time)`).toBeLessThanOrEqual(A_END);
    });
    const b = list.filter((_, n) => owner[n] === 'B');
    expect(b.map((i) => [i.sourceIn ?? 0, i.recordOut - i.recordIn]), `${label} ${kind}: B is whole and untouched by A's cuts`).toEqual([[0, B_LEN]]);
    expect(b[0].recordIn, `${label} ${kind}: B starts where A's last piece ends`).toBe(list[firstB - 1].recordOut);
  }
  const v = items(tl, 'video');
  const a = items(tl, 'audio');
  expect(v.map((i) => [i.recordIn, i.recordOut]), `${label}: picture and sound are cut at the same frames`).toEqual(a.map((i) => [i.recordIn, i.recordOut]));
  return { aEnd: v.filter((i) => i.assetRef?.projectFileId !== files.b).at(-1)!.recordOut };
}

/** One frame of a film as `w x h` rgb. */
function frame(file: string, seconds: number, w = 320, h = 180): Buffer {
  return execFileSync('ffmpeg', ['-v', 'error', '-ss', seconds.toFixed(3), '-i', file, '-frames:v', '1', '-vf', `scale=${w}:${h}`, '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-'], { maxBuffer: 1 << 24 });
}

/** Mean absolute difference of two frames over rows [y0, y1) and columns [x0, x1). */
function regionDiff(a: Buffer, b: Buffer, w: number, h: number, y0: number, y1: number, x0 = 0, x1 = 1): number {
  let sum = 0;
  let n = 0;
  for (let y = Math.floor(y0 * h); y < Math.floor(y1 * h); y++) {
    for (let x = Math.floor(x0 * w); x < Math.floor(x1 * w); x++) {
      for (let c = 0; c < 3; c++) {
        sum += Math.abs(a[(y * w + x) * 3 + c] - b[(y * w + x) * 3 + c]);
        n++;
      }
    }
  }
  return sum / n;
}

/** Clicks Render on the Deliver page that is already open (the page keeps its choices) and waits for the render to finish. */
async function renderHere(ed: EditorPage, timelineId: string): Promise<{ render: RenderSummary; body: { captions?: string } }> {
  const started = ed.page.waitForResponse((r) => r.request().method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(r.url()).pathname));
  await ed.tap(ed.testId('render-start'));
  const res = await started;
  expect(res.status(), 'the render was accepted').toBe(202);
  const body = JSON.parse(res.request().postData() ?? '{}') as { captions?: string };
  const { executionId } = (await res.json()) as { executionId: string };
  return { render: await waitRender(ed.seed.api, ed.seed.projectId, timelineId, executionId, 300_000), body };
}

async function downloadFilm(ed: EditorPage, executionId: string, name: string): Promise<string> {
  await expect(ed.testId('film-card')).toBeVisible({ timeout: 30_000 });
  await expect(ed.testId('film-name'), 'the film card shows the render that just finished').toHaveText(name, { timeout: 30_000 });
  const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId('film-download'))]);
  fs.mkdirSync(DL_DIR, { recursive: true });
  const file = path.join(DL_DIR, `${executionId}-${download.suggestedFilename()}`);
  await download.saveAs(file);
  expect(fs.statSync(file).size, 'the downloaded film has bytes').toBeGreaterThan(10_000);
  return file;
}

test('J2 desktop: two-source derush: analyse, remove silence (audition first), delete fillers, captions, burn-in render', async ({ page, freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(900_000);
  const files = { a: seed.files.clipA, b: seed.files.clipB };
  const control = await stubAnalysis(page, [{ fileId: files.a, transcript: WORDS_A }, { fileId: files.b, transcript: WORDS_B }], { mode: 'transcriptOnly' });
  const timelineId = await freshTimeline('derush');
  // The derush timeline is source A (its render); source B follows it. A is cut to 9 s so its silent tail meets B.
  await api.putDocument(seed.projectId, timelineId, (doc) => {
    for (const t of doc.tracks) {
      t.items[0].recordOut = A_END;
      t.items.push(mediaClip(crypto.randomUUID(), 'clipB.mp4', files.b, A_END, A_END + B_LEN));
    }
  });
  const ed = await openEditor(timelineId);

  // ── Analyse: both sources; the free kinds run for real and finish.
  await ed.openPanelTab('tab-analyse');
  for (const id of [files.a, files.b]) {
    await expect(page.getByTestId(`analyse-file-${id}`)).toBeVisible();
    await ed.tap(page.getByTestId(`analyse-run-${id}`));
  }
  for (const id of [files.a, files.b]) {
    for (const kind of ['silence', 'shots', 'motion']) {
      await expect.poll(() => stateOf(ed, id, kind), { message: `${kind} analysis of ${id === files.a ? 'A' : 'B'} finishes`, timeout: 180_000 }).toBe('ready');
    }
  }
  // ── Transcripts: the estimate comes first, nothing starts before the confirm.
  for (const id of [files.a, files.b]) {
    await ed.tap(page.getByTestId(`analyse-transcribe-${id}`));
    await expect(page.getByTestId(`analyse-estimate-${id}`)).toContainText(/3 credits/);
  }
  expect(control.starts.filter((s) => s.kinds.includes('transcript')), 'no transcript job before the user confirms').toHaveLength(0);
  for (const id of [files.a, files.b]) {
    await ed.tap(page.getByTestId(`analyse-transcribe-confirm-${id}`));
    await expect.poll(() => stateOf(ed, id, 'transcript'), { message: 'transcript finishes', timeout: 60_000 }).toBe('ready');
  }

  // ── Remove silence: audition first (nothing is written), then Apply.
  const before = await ed.serverTimeline();
  expect(items(before, 'video'), 'A then B').toHaveLength(2);
  await ed.openRemoveSilence();
  await expect(page.locator('[data-testid^="remove-silence-span-"]')).toHaveCount(2);
  await ed.tap(page.getByTestId('remove-silence-audition'));
  await expect(page.getByTestId('audition-banner')).toBeVisible();
  const patchesBefore = ed.patches.length;
  await expect.poll(() => ed.patches.length, { message: 'an audition writes nothing', timeout: 2500 }).toBe(patchesBefore).catch(() => undefined);
  expect(ed.patches.length, 'an audition writes nothing').toBe(patchesBefore);
  expect(items(await ed.serverTimeline(), 'video'), 'the stored timeline is still A then B').toHaveLength(2);
  await ed.tap(page.getByTestId('audition-apply'));
  const cut = await ed.expectServer((tl) => items(tl, 'video').length >= 3, 'Apply cut the silences');
  expect(total(items(cut, 'video')), 'the silences are gone').toBeLessThan(total(items(before, 'video')) - 60);
  const afterSilence = expectNoCrossSourceCuts(cut, files, 'after Remove silence');
  expect(afterSilence.aEnd, 'A lost its two silent stretches (about 1.5 s and 1 s)').toBeLessThanOrEqual(A_END - 60);

  // ── Filler words: "um" and the stutter are found in A's transcript and removed; B's words are untouched.
  await ed.openPanelTab('tab-transcript');
  await ed.tap(page.getByTestId('transcript-fillers-toggle'));
  await expect(page.getByTestId('transcript-fillers')).toContainText(/filler “um”/);
  await ed.tap(page.getByTestId('transcript-fillers-apply'));
  const clean = await ed.expectServer((tl) => total(items(tl, 'video')) < total(items(cut, 'video')) - 20, 'the fillers were cut out');
  const afterFillers = expectNoCrossSourceCuts(clean, files, 'after the fillers');
  expect(afterFillers.aEnd, 'A got shorter again').toBeLessThan(afterSilence.aEnd - 20);

  // ── Captions from the transcripts: A's words land inside A's part, B's inside B's.
  await ed.openPanelTab('tab-subtitles');
  await expect(page.getByTestId('subtitles-preview-count')).not.toContainText(/^\s*0 cues/);
  await ed.tap(page.getByTestId('subtitles-generate'));
  const captioned = await ed.expectServer((tl) => captionsOf(tl).length > 0, 'captions are stored');
  const cues = captionsOf(captioned) as Array<Item & { text: string; look?: string; position?: string }>;
  const bStart = items(captioned, 'video').find((i) => i.assetRef?.projectFileId === files.b)!.recordIn;
  const textOf = (c: { text: string }) => c.text.toLowerCase();
  const aCues = cues.filter((c) => /quick|brown|fox|jumps|lazy|dog/.test(textOf(c)));
  const bCues = cues.filter((c) => /welcome|back|friends/.test(textOf(c)));
  expect(aCues.length, 'A\'s words became captions').toBeGreaterThan(0);
  expect(bCues.length, 'B\'s words became captions').toBeGreaterThan(0);
  for (const c of aCues) expect(c.recordOut, `caption "${c.text}" stays inside A's part`).toBeLessThanOrEqual(bStart);
  for (const c of bCues) expect(c.recordIn, `caption "${c.text}" starts inside B's part`).toBeGreaterThanOrEqual(bStart);
  for (const c of cues) {
    expect(c.look, 'a look from the catalogue is stored').toMatch(/^(clean|bold|boxed|outline|spotlight)$/);
    expect(c.position).toBe('bottomCenter');
  }
  const firstCue = [...aCues].sort((x, y) => x.recordIn - y.recordIn)[0];
  const probeAt = (firstCue.recordIn + Math.min(firstCue.recordOut, firstCue.recordIn + 20)) / 2 / FPS;

  // ── Deliver, burn in: render, download.
  await ed.workspace('deliver');
  await expect(page.getByTestId('deliver-captions')).toBeVisible();
  await ed.tap(page.getByText('Burn into the video', { exact: true }));
  await ed.testId('render-file-name').fill('j2-burned');
  const { render: burned, body: burnBody } = await renderHere(ed, timelineId);
  expect(burned.status, burned.errorMessage ?? 'the burn-in render passes').toBe('Passed');
  const burnedFile = await downloadFilm(ed, burned.executionId, 'j2-burned');
  const burnedFacts = probe(burnedFile);
  expect([burnedFacts.video?.width, burnedFacts.video?.height]).toEqual([1920, 1080]);
  const expectedSec = total(items(captioned, 'video')) / FPS;
  expect(Math.abs(burnedFacts.durationSec - expectedSec), `the film is as long as the edit (${expectedSec} s)`).toBeLessThan(0.6);

  // The same edit with the words NOT burned in: the caption band is the only place the two films differ.
  await ed.tap(page.getByText('Separate files only', { exact: true }));
  await ed.testId('render-file-name').fill('j2-plain');
  const { render: plain, body: plainBody } = await renderHere(ed, timelineId);
  expect(plainBody.captions, 'the two renders were asked for different caption handling').not.toBe(burnBody.captions);
  expect(plain.status, plain.errorMessage ?? 'the plain render passes').toBe('Passed');
  const plainFile = await downloadFilm(ed, plain.executionId, 'j2-plain');
  const w = 320;
  const h = 180;
  const withWords = frame(burnedFile, probeAt, w, h);
  const without = frame(plainFile, probeAt, w, h);
  const band = regionDiff(withWords, without, w, h, 0.68, 1, 0.1, 0.9);
  const rest = regionDiff(withWords, without, w, h, 0, 0.5);
  expect(band, `the burn-in changed the caption band at ${probeAt.toFixed(2)} s (mean difference ${band.toFixed(2)})`).toBeGreaterThan(3);
  expect(rest, `the rest of the picture is the same film (mean difference ${rest.toFixed(2)})`).toBeLessThan(band / 3);
});
