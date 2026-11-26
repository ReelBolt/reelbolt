import { test, expect, type EditorPage } from '../../../fixtures/editor-v2/lane-a-test';
import { PAIR, clipsTimeline } from '../../../fixtures/editor-v2/lane-a';
import type { Item, Timeline } from '../../../fixtures/editor-v2/api';
import { stubAnalysis } from '../../../fixtures/editor-v2/analysis-stub';
import { renderAndWait, runOn } from '../../../fixtures/editor-v2/rows';

/**
 * T-014 captions (R09.6 looks, position, multiline, add by hand, nudge; R12.9 SRT/WebVTT import; R09.7 delivery).
 * Every assertion reads the STORED document.
 */

const captionsOf = (tl: Timeline): Item[] => tl.document.tracks.flatMap((t) => t.items).filter((i) => i.type === 'caption').sort((a, b) => a.recordIn - b.recordIn);
const SRT = '1\n00:00:01,000 --> 00:00:03,000\nHello <i>there</i>\nsecond line\n\n2\n00:00:04,000 --> 00:00:03,000\nbackwards\n\n3\n00:00:05,000 --> 00:00:06,500\nGoodbye\n';
const VTT = 'WEBVTT\n\n00:00:00.500 --> 00:00:01.500\nOnly one\n';

async function openSubtitles(ed: EditorPage): Promise<void> {
  await ed.openPanelTab('tab-subtitles');
  await expect(ed.page.getByTestId('subtitles-panel')).toBeVisible();
}

async function chooseFile(ed: EditorPage, name: string, text: string): Promise<void> {
  await ed.tap(ed.page.getByTestId('subtitles-import'));
  await expect(ed.page.getByRole('dialog', { name: 'Import captions' })).toBeVisible();
  await ed.page.locator('input[type="file"]').last().setInputFiles({ name, mimeType: 'text/plain', buffer: Buffer.from(text) });
}

test('R09.6 a caption added by hand keeps its two lines, its look and its place; the look can be set on all and the timing nudged', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await ed.seek(10); // before the panel opens: on a phone an open sheet hides the transport
  await openSubtitles(ed);
  // A wide 1920x1080 picture starts with the clean look at the bottom.
  await expect(ed.page.getByTestId('caption-look-clean')).toHaveAttribute('aria-checked', 'true');
  await ed.tap(ed.page.getByTestId('caption-look-boxed'));
  await ed.tap(ed.page.getByTestId('caption-position-topCenter'));
  await ed.page.getByTestId('subtitles-new-text').fill('Line one\nLine two');
  await ed.tap(ed.page.getByTestId('subtitles-add'));
  const added = await ed.expectServer((tl) => captionsOf(tl).length === 1, 'the caption is stored');
  const c = captionsOf(added)[0];
  expect(c.text, 'both lines are kept').toBe('Line one\nLine two');
  expect(c.look).toBe('boxed');
  expect(c.position).toBe('topCenter');
  expect(c.recordIn, 'it starts at the playhead').toBe(10);

  await ed.tap(ed.page.getByTestId(`subtitles-later-${c.id}`));
  const nudged = await ed.expectServer((tl) => captionsOf(tl)[0]?.recordIn === 13, 'nudged 3 frames later');
  expect(captionsOf(nudged)[0].recordOut - captionsOf(nudged)[0].recordIn, 'the whole caption moved, its length did not change').toBe(c.recordOut - c.recordIn);

  await ed.tap(ed.page.getByTestId('caption-look-spotlight'));
  await ed.tap(ed.page.getByTestId('subtitles-apply-look'));
  await ed.expectServer((tl) => captionsOf(tl)[0]?.look === 'spotlight', 'the look is now on every caption');
});

test('R09.6 a tall picture starts with the bold look', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const id = await clipsTimeline(freshTimeline, api, seed, PAIR, {
    mutate: (doc) => {
      (doc.settings as { width: number; height: number }).width = 1080;
      (doc.settings as { width: number; height: number }).height = 1920;
    },
  });
  const ed = await openEditor(id);
  await openSubtitles(ed);
  await expect(ed.page.getByTestId('caption-look-bold')).toHaveAttribute('aria-checked', 'true');
});

test('R12.9 importing an SRT previews the cues, says what it skipped, and stores the captions', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await openSubtitles(ed);
  await chooseFile(ed, 'talk.srt', SRT);
  await expect(ed.page.getByTestId('subtitle-import-count')).toContainText('2 captions found');
  await expect(ed.page.getByTestId('subtitle-import-problems')).toContainText('Cue 2 ends before it starts and was skipped.');
  await expect(ed.page.getByTestId('subtitle-import-preview')).toContainText('Hello there / second line');
  await ed.tap(ed.page.getByTestId('subtitle-import-apply'));
  const stored = await ed.expectServer((tl) => captionsOf(tl).length === 2, 'two captions are stored');
  const [first, second] = captionsOf(stored);
  expect(first.text).toBe('Hello there\nsecond line');
  expect(first.recordIn, '1.0 s at 30 fps').toBe(30);
  expect(first.recordOut, '3.0 s at 30 fps').toBe(90);
  expect(second.recordIn).toBe(150);
  expect(first.look, 'the panel look is applied to imported captions').toBeTruthy();
});

test('R12.9 importing a WebVTT can replace what is already on the captions track', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await openSubtitles(ed);
  await chooseFile(ed, 'a.srt', SRT);
  await ed.tap(ed.page.getByTestId('subtitle-import-apply'));
  await ed.expectServer((tl) => captionsOf(tl).length === 2, 'the first import is stored');
  await chooseFile(ed, 'b.vtt', VTT);
  await expect(ed.page.getByTestId('subtitle-import-count')).toContainText('1 caption found (WebVTT)');
  await ed.tap(ed.page.getByText('Replace them', { exact: true }));
  await expect(ed.page.getByTestId('subtitle-import-summary')).toContainText('1 replaced'.replace('1 replaced', '2 replaced'));
  await ed.tap(ed.page.getByTestId('subtitle-import-apply'));
  const stored = await ed.expectServer((tl) => captionsOf(tl).length === 1 && captionsOf(tl)[0].text === 'Only one', 'the old captions were replaced');
  expect(captionsOf(stored)[0].recordIn).toBe(15);
});

test('R12.9 a file that is not subtitles is explained in plain words and nothing is added', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await openSubtitles(ed);
  await chooseFile(ed, 'notes.txt', 'just some words, no times at all');
  await expect(ed.page.getByTestId('subtitle-import-problems')).toContainText(/No captions were found/);
  await expect(ed.page.getByTestId('subtitle-import-apply')).toBeDisabled();
  expect(ed.patches.length).toBe(0);
});

test('R09.7 the delivery choice goes into the render request; the sidecar files carry the words', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
  await openSubtitles(ed);
  await chooseFile(ed, 'talk.srt', SRT);
  await ed.tap(ed.page.getByTestId('subtitle-import-apply'));
  await ed.expectServer((tl) => captionsOf(tl).length === 2, 'captions are stored');
  await ed.workspace('deliver');
  await expect(ed.page.getByTestId('deliver-captions-note')).toContainText(/part of the picture/);
  await ed.tap(ed.page.getByText('Separate files only', { exact: true }));
  const vtt = await ed.page.request.get((await ed.page.getByTestId('deliver-captions-vtt').getAttribute('href')) as string);
  expect(vtt.status()).toBe(200);
  expect(await vtt.text(), 'the sidecar carries the words').toContain('Goodbye');
  const sent = ed.page.waitForRequest((r) => r.method() === 'POST' && /\/render$/.test(r.url()));
  await ed.tap(ed.page.getByTestId('render-start'));
  const body = JSON.parse((await sent).postData() ?? '{}') as { captions?: string };
  expect(body.captions, 'sidecar only: no burn-in').toBe('sidecar');
});

test('R09.7 a render with captions "both" lists the sidecar files on the film card; the downloads carry the cues', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(300_000);
  const id = await clipsTimeline(freshTimeline, api, seed, PAIR, {
    mutate: (doc) => {
      const v = doc.tracks.find((t) => t.kind === 'video')!;
      doc.tracks.splice(1, 0, { ...structuredClone(v), id: crypto.randomUUID(), name: 'Subtitles', items: [{ type: 'caption', id: crypto.randomUUID(), name: 'c', recordIn: 5, recordOut: 50, text: 'Sidecar words here', language: 'en', enabled: true } as never] });
    },
  });
  const render = await renderAndWait(api, seed.projectId, id, { captions: 'both' });
  expect(render.status, render.errorMessage ?? 'render passes').toBe('Passed');
  const ed = await openEditor(id);
  await ed.workspace('deliver');
  const srt = ed.page.getByTestId('film-caption-srt');
  await expect(srt).toBeVisible({ timeout: 30_000 });
  const res = await ed.page.request.get((await srt.getAttribute('href')) as string);
  expect(res.status()).toBe(200);
  const body = await res.text();
  expect(body.length).toBeGreaterThan(10);
  expect(body, 'the SRT is the cue list').toContain('Sidecar words here');
  const vtt = await ed.page.request.get((await ed.page.getByTestId('film-caption-vtt').getAttribute('href')) as string);
  expect(await vtt.text()).toContain('Sidecar words here');
});

test.describe('tablet (coarse pointer, 1024x768)', () => {
  test.use({ viewport: { width: 1024, height: 768 }, hasTouch: true });

  test('R12.9 tablet: import an SRT and R09.3 audition a cut, by touch', async ({ page, freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    await stubAnalysis(page, [{ fileId: seed.files.clipA, silence: [{ startSec: 0.5, endSec: 1.0 }] }], { readyAtStart: true });
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await openSubtitles(ed);
    await chooseFile(ed, 'talk.srt', SRT);
    await ed.tap(ed.page.getByTestId('subtitle-import-apply'));
    await ed.expectServer((tl) => captionsOf(tl).length === 2, 'captions imported by touch');

    await ed.openRemoveSilence();
    const audition = page.getByTestId('remove-silence-audition');
    expect((await audition.boundingBox())!.height, 'finger-sized').toBeGreaterThanOrEqual(43);
    await ed.tap(audition);
    await expect(page.getByTestId('audition-banner')).toBeVisible();
    await ed.tap(page.getByTestId('audition-apply'));
    await ed.expectServer((tl) => tl.document.tracks[0].items.length > 2, 'the cut was applied by touch');
  });

  test('R09.1 tablet: the Analyse panel shows the transcript estimate before anything starts, with finger-sized buttons', async ({ page, freshTimeline, openEditor, api, seed }) => {
    runOn('desktop');
    const control = await stubAnalysis(page, [{ fileId: seed.files.clipA, silence: [], shots: [], motion: [], transcript: [] }]);
    const ed = await openEditor(await clipsTimeline(freshTimeline, api, seed, PAIR));
    await ed.openPanelTab('tab-analyse');
    const run = page.getByTestId(`analyse-run-${seed.files.clipA}`);
    expect((await run.boundingBox())!.height).toBeGreaterThanOrEqual(43);
    await ed.tap(page.getByTestId(`analyse-transcribe-${seed.files.clipA}`));
    await expect(page.getByTestId(`analyse-estimate-${seed.files.clipA}`)).toContainText(/3 credits/);
    expect(control.starts, 'nothing started before the confirmation').toHaveLength(0);
    await ed.tap(page.getByTestId(`analyse-transcribe-confirm-${seed.files.clipA}`));
    await expect.poll(() => control.starts.length).toBe(1);
    await expect(page.getByTestId(`analyse-kind-${seed.files.clipA}-transcript`)).toHaveAttribute('data-state', /running|queued|ready/);
    await expect.poll(() => page.getByTestId(`analyse-kind-${seed.files.clipA}-transcript`).getAttribute('data-state'), { timeout: 30_000 }).toBe('ready');
  });
});
