import * as fs from 'fs';
import * as path from 'path';
import { test, expect, SEEDED_LAYOUT, type EditorPage } from '../../../fixtures/editor-v2';
import { mediaClip } from '../../../fixtures/editor-v2/api';
import { ARTIFACTS } from '../../../fixtures/editor-v2/env';
import { downloadRender, probe, runOn } from '../../../fixtures/editor-v2/rows';
import { integratedLufs, renderFromDeliver } from '../../../fixtures/editor-v2/ws';

/**
 * Lane C, DELIVER workspace (inventory/workspaces.md 2.7, WS-160..WS-174). Every test starts a render through the real
 * Deliver controls and asserts the produced file (download > 0 bytes, ffprobe streams and duration), never "the button exists".
 * No paid provider is involved: TimelineRender is deterministic ffmpeg.
 */

const DL_DIR = path.join(ARTIFACTS, 'c-downloads');

/** A 2 s timeline (clipA picture + tone sound) that renders in seconds. */
async function shortTimeline(freshTimeline: (from: 'blank') => Promise<string>, api: import('../../../fixtures/editor-v2/api').EditorApi, projectId: string, files: { clipA: string; tone: string }, frames = 60): Promise<string> {
  const id = await freshTimeline('blank');
  await api.putDocument(projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipA.mp4', files.clipA, 0, frames)];
    doc.tracks.find((t) => t.kind === 'audio')!.items = [mediaClip(crypto.randomUUID(), 'tone.wav', files.tone, 0, frames)];
  });
  return id;
}

/** Picks a Format option in the Deliver select by its visible name. */
async function pickPreset(ed: EditorPage, name: RegExp): Promise<void> {
  await ed.tap(ed.testId('render-preset'));
  await ed.tap(ed.page.getByRole('option', { name }));
}

/** WS-167 + WS-164 + WS-172 + WS-171: Render from Deliver, the queue row, and Download render SAVES the file (not a navigation). */
test('WS-171 Download render in the queue saves the rendered mp4 and leaves the editor open', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  test.setTimeout(240_000);
  const id = await shortTimeline(freshTimeline, api, seed.projectId, seed.files);
  const ed = await openEditor(id);
  await ed.workspace('deliver');
  const name = `ws171-${Date.now()}`;
  // On a phone the file name sits under "More options".
  if (!(await ed.testId('render-file-name').isVisible().catch(() => false))) await ed.tap(ed.testId('render-advanced-toggle'));
  await ed.testId('render-file-name').fill(name);
  const r = await renderFromDeliver(ed, id);
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');

  const row = ed.page.getByTestId(`render-row-${r.executionId}`);
  await expect(row.getByTestId('render-status'), 'WS-172 the queue row says Done (the render queue uses the activity words)').toHaveText('Done', { timeout: 30_000 });
  await expect(row, 'WS-164 the row carries the typed file name').toContainText(name);
  const link = row.getByTestId('render-download');
  await expect(link).toBeVisible();

  const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(link)]);
  expect(download.suggestedFilename(), 'the saved file is named as typed in Deliver, with the .mp4 extension').toBe(`${name}.mp4`);
  fs.mkdirSync(DL_DIR, { recursive: true });
  const saved = path.join(DL_DIR, `${r.executionId}-${download.suggestedFilename()}`);
  await download.saveAs(saved);
  expect(fs.statSync(saved).size, 'the saved file has bytes').toBeGreaterThan(10_000);
  const p = probe(saved);
  expect(p.video, 'a video stream').toBeTruthy();
  expect(p.audio, 'an audio stream').toBeTruthy();
  expect(p.durationSec, '60 frames at 30 fps').toBeGreaterThan(1.8);
  expect(p.durationSec).toBeLessThan(2.4);
  // The click did not navigate the editor tab to a bare player.
  expect(ed.page.url()).toContain(`/editor/${seed.projectId}/timeline/${id}`);
  await expect(ed.testId('deliver-workspace')).toBeVisible();
});

/** WS-160: "Export only <clip>" must also switch the range on, otherwise Render exports the whole timeline. */
test('WS-160 Export only the selected clip renders just that clip', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop', 'pixel7');
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  const ed = await openEditor(id);
  // clipA on the seeded timeline spans frames 0..150 (5 s; on screen on a phone too); the whole timeline is 600 frames (20 s).
  await ed.selectClipAt(SEEDED_LAYOUT.clipA[0] + 60, 0);
  await ed.workspace('deliver');
  // R09.10: one action sets the range and starts the render.
  const r = await renderFromDeliver(ed, id, 240_000, 'deliver-use-clip-range');
  await expect(ed.testId('render-range'), 'the marked-range switch follows the clip export').toBeChecked();
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');
  const file = await downloadRender(api, seed.projectId, r);
  const p = probe(file);
  expect(p.durationSec, `output is the clip (5 s), not the 20 s timeline: ${p.durationSec}`).toBeGreaterThan(4.7);
  expect(p.durationSec).toBeLessThan(5.4);
  // The marks were persisted with the document.
  const stored = (await api.getTimeline(seed.projectId, id)).document.inOut as { inFrame: number; outFrame: number } | null | undefined;
  expect(stored, 'the clip range is stored as the in/out marks').toMatchObject({ inFrame: SEEDED_LAYOUT.clipA[0], outFrame: SEEDED_LAYOUT.clipA[1] });
});

/** WS-161: every Format the select offers renders the right shape (a 1 s timeline keeps the seven renders cheap). */
test('WS-161 each Format in Deliver renders its size; Audio only has no picture', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(480_000);
  const id = await shortTimeline(freshTimeline, api, seed.projectId, seed.files, 30);
  const ed = await openEditor(id);
  await ed.workspace('deliver');
  const cases: Array<[RegExp, [number, number] | 'master' | 'audio']> = [
    [/^Full HD landscape/, [1920, 1080]],
    [/^Vertical/, [1080, 1920]],
    [/^Square/, [1080, 1080]],
    [/^Portrait/, [1080, 1350]],
    [/^4K landscape/, [3840, 2160]],
    [/^Master/, 'master'], // the timeline's own size, checked against the document below
    [/^Audio only/, 'audio'],
  ];
  const own = (await api.getTimeline(seed.projectId, id)).document.settings;
  for (const [label, dims] of cases) {
    await pickPreset(ed, label);
    const r = await renderFromDeliver(ed, id);
    expect(r.status, `${label}: render ${r.errorMessage ?? ''}`).toBe('Passed');
    const p = probe(await downloadRender(api, seed.projectId, r, dims === 'audio' ? 'm4a' : 'mp4'));
    if (dims === 'master') expect([p.video?.width, p.video?.height], 'Master renders at the timeline size').toEqual([own.width, own.height]);
    else if (dims === 'audio') {
      expect(p.video, 'Audio only has no picture').toBeUndefined();
      expect(p.audio, 'Audio only has sound').toBeTruthy();
    } else expect([p.video?.width, p.video?.height], `${label} size`).toEqual(dims);
  }
});

/** WS-163/164/165/166: range, file name, loudness target and custom encoding all reach the produced file. */
test('WS-163 the Deliver settings (range, name, -16 LUFS, H.265 CRF 28) are in the file; plain H.264 of the same range is bigger', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(420_000);
  const id = await freshTimeline('blank');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.find((t) => t.kind === 'video')!.items = [mediaClip(crypto.randomUUID(), 'clipB.mp4', seed.files.clipB, 0, 150)];
    doc.tracks.find((t) => t.kind === 'audio')!.items = [mediaClip(crypto.randomUUID(), 'tone.wav', seed.files.tone, 0, 150)];
    doc.inOut = { inFrame: 30, outFrame: 120 };
  });
  const ed = await openEditor(id);
  await ed.workspace('deliver');
  await expect(ed.testId('render-range'), 'WS-163 the stored marks switch the range on').toBeChecked();

  // R1: named, normalised to -16 LUFS, H.265 at CRF 28.
  const stamp = Date.now();
  await ed.testId('render-file-name').fill(`ws163-hevc-${stamp}`);
  await ed.tap(ed.testId('render-advanced-toggle'));
  await ed.page.getByRole('switch', { name: 'Normalise loudness' }).check();
  await ed.testId('render-target-lufs').fill('-16');
  await ed.page.getByRole('switch', { name: /^Choose codec and speed myself/ }).check();
  await ed.tap(ed.page.getByRole('combobox', { name: 'Video codec' }));
  await ed.tap(ed.page.getByRole('option', { name: /^H\.265/ }));
  await ed.page.getByLabel(/^Picture quality/).fill('28');
  const hevc = await renderFromDeliver(ed, id);
  expect(hevc.status, `render ${hevc.errorMessage ?? ''}`).toBe('Passed');
  const rows = await api.json<Array<{ executionId: string; outputFileName: string }>>('GET', `${api.timelinesUrl(seed.projectId, id)}/renders`);
  expect(rows.find((x) => x.executionId === hevc.executionId)?.outputFileName, 'WS-164 the typed name').toContain(`ws163-hevc-${stamp}`);
  const hevcFile = await downloadRender(api, seed.projectId, hevc);
  const hp = probe(hevcFile);
  expect(hp.video?.codec, 'WS-166 H.265').toBe('hevc');
  expect(hp.durationSec, 'WS-163 3 s range').toBeGreaterThan(2.7);
  expect(hp.durationSec).toBeLessThan(3.4);
  const lufs = integratedLufs(hevcFile);
  expect(Math.abs(lufs - -16), `WS-165 integrated loudness ${lufs} LUFS within 1.5 LU of -16`).toBeLessThan(1.5);

  // R2: the same range as plain H.264.
  await ed.page.getByRole('switch', { name: 'Normalise loudness' }).uncheck();
  await ed.page.getByRole('switch', { name: /^Choose codec and speed myself/ }).uncheck();
  await ed.testId('render-file-name').fill(`ws163-avc-${stamp}`);
  const avc = await renderFromDeliver(ed, id);
  expect(avc.status, `render ${avc.errorMessage ?? ''}`).toBe('Passed');
  const avcFile = await downloadRender(api, seed.projectId, avc);
  expect(probe(avcFile).video?.codec).toBe('h264');
  expect(fs.statSync(hevcFile).size, 'H.265 CRF 28 is smaller than H.264 CRF 18').toBeLessThan(fs.statSync(avcFile).size);
});

/** WS-168: a refused start is shown as a sentence, never raw JSON, and the alert closes (the refusal is a stubbed 402: the suite plan has no cap). */
test('WS-168 a refused render shows a plain sentence in Deliver and the alert can be closed', async ({ freshTimeline, openEditor }) => {
  runOn('desktop', 'pixel7');
  // Deliver does not start a render of an empty timeline (Render is disabled), so the refusal needs a timeline with clips.
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.page.route(/\/edit-timelines\/[^/]+\/render$/, async (route) => {
    if (route.request().method() !== 'POST') return route.continue();
    return route.fulfill({ status: 402, contentType: 'application/json', body: JSON.stringify({ code: 'PLAN_NOT_ENTITLED', message: 'Your plan renders up to 1080p. Upgrade to render in 4K.' }) });
  });
  await ed.workspace('deliver');
  await ed.tap(ed.testId('render-start'));
  const alert = ed.testId('render-refusal');
  await expect(alert).toContainText('Your plan renders up to 1080p');
  await expect(alert, 'no raw JSON').not.toContainText('PLAN_NOT_ENTITLED');
  await alert.getByRole('button').first().click();
  await expect(alert).toHaveCount(0);
});

const FORMATS: Array<{ id: 'otio' | 'edl' | 'srt' | 'fcpxml' | 'vtt'; ext: string }> = [
  { id: 'otio', ext: 'otio' }, { id: 'edl', ext: 'edl' }, { id: 'srt', ext: 'srt' }, { id: 'fcpxml', ext: 'fcpxml' }, { id: 'vtt', ext: 'vtt' },
];

async function saveDownload(ed: EditorPage, linkTestId: string, name: string): Promise<{ file: string; text: string; filename: string }> {
  const [download] = await Promise.all([ed.page.waitForEvent('download'), ed.tap(ed.testId(linkTestId))]);
  fs.mkdirSync(DL_DIR, { recursive: true });
  const file = path.join(DL_DIR, `${Date.now()}-${name}`);
  await download.saveAs(file);
  return { file, text: fs.readFileSync(file, 'utf8'), filename: download.suggestedFilename() };
}

/** WS-169: every export link downloads a real file with the right extension and content. */
test('WS-169 the five export links download files whose content matches the timeline (captions included)', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    doc.tracks.push({
      id: crypto.randomUUID(), kind: 'video', name: 'Subtitles', height: 40, locked: false, enabled: true, mute: false, solo: false,
      items: [
        { type: 'caption', id: crypto.randomUUID(), name: 'c1', recordIn: 0, recordOut: 60, text: 'First caption line', language: 'en', enabled: true },
        { type: 'caption', id: crypto.randomUUID(), name: 'c2', recordIn: 90, recordOut: 150, text: 'Second caption line', language: 'en', enabled: true },
      ],
    });
  });
  const ed = await openEditor(id);
  await ed.workspace('deliver');
  const got: Record<string, { text: string; filename: string }> = {};
  for (const f of FORMATS) {
    const d = await saveDownload(ed, `deliver-export-${f.id}`, `export.${f.ext}`);
    expect(d.filename, `${f.id} extension`).toMatch(new RegExp(`\\.${f.ext}$`));
    expect(d.text.length, `${f.id} is not empty`).toBeGreaterThan(20);
    got[f.id] = d;
  }
  const otio = JSON.parse(got.otio.text) as { OTIO_SCHEMA: string };
  expect(otio.OTIO_SCHEMA).toMatch(/^Timeline\./);
  expect(got.otio.text).toContain('clipA.mp4');
  expect(got.edl.text).toMatch(/^TITLE:/m);
  expect(got.edl.text).toContain('FROM CLIP NAME: clipA.mp4');
  expect((got.edl.text.match(/^\d{3}\s+AX/gm) ?? []).length, 'an EDL event per cut on the first video track').toBeGreaterThanOrEqual(4);
  expect(got.srt.text).toContain('First caption line');
  expect(got.srt.text).toContain('Second caption line');
  expect(got.srt.text).toMatch(/00:00:00,000 --> 00:00:02,000/);
  expect(got.vtt.text.startsWith('WEBVTT')).toBe(true);
  expect(got.vtt.text).toContain('Second caption line');
  expect(got.fcpxml.text.trimStart().startsWith('<?xml')).toBe(true);
  expect(got.fcpxml.text).toContain('fcpxml');
});

/** WS-169 (empty case): SRT/WebVTT of a timeline with no captions are not offered as empty files. */
test('WS-169 SRT and WebVTT say there are no captions yet instead of downloading an empty file', async ({ openEditor, seed }) => {
  runOn('desktop');
  const ed = await openEditor(seed.seededTimelineId);
  await ed.workspace('deliver');
  for (const f of ['srt', 'vtt']) {
    const el = ed.testId(`deliver-export-${f}`);
    await expect(el).toContainText('no captions yet');
    expect(await el.evaluate((n) => n.tagName), 'not a link').not.toBe('A');
  }
  // The other formats are still links.
  for (const f of ['otio', 'edl', 'fcpxml']) expect(await ed.testId(`deliver-export-${f}`).evaluate((n) => n.tagName)).toBe('A');
});

/** WS-173: what a render left out is listed under the row by the name the user gave the item. */
test('WS-173 the render notes name an item the render left out', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  test.setTimeout(240_000);
  const id = await shortTimeline(freshTimeline, api, seed.projectId, seed.files, 30);
  await api.putDocument(seed.projectId, id, (doc) => {
    const top = structuredClone(doc.tracks.find((t) => t.kind === 'video')!);
    top.id = crypto.randomUUID();
    top.name = 'Inserts';
    top.items = [{ type: 'insertClip', id: crypto.randomUUID(), name: 'Lower line', recordIn: 0, recordOut: 30, assetRef: { kind: 'projectFile', projectFileId: seed.files.still, stepResultId: null }, enabled: true }];
    doc.tracks.splice(1, 0, top);
  });
  const ed = await openEditor(id);
  const r = await renderFromDeliver(ed, id);
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');
  const row = ed.page.getByTestId(`render-row-${r.executionId}`);
  await ed.tap(row.getByTestId('render-notes-toggle'));
  await expect(row, 'the notes name the item as the user named it').toContainText('Lower line');
});

/** T-003: Deliver is a page in the shared frame — settings 420 | result (rest), no centre, no timeline. */
test('T-003 Deliver fills the width with two columns and offers no timeline', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  const ed = await openEditor(await freshTimeline('blank'));
  await ed.workspace('deliver');
  await expect(ed.testId('deliver-controls-zone')).toBeVisible();
  await expect(ed.testId('deliver-result-zone')).toBeVisible();
  await expect(ed.testId('panel-left').getByTestId('deliver-controls')).toBeVisible();
  await expect(ed.testId('panel-right').getByTestId('deliver-result')).toBeVisible();
  await expect(ed.testId('panel-viewer'), 'no centre zone').toHaveCount(0);
  await expect(ed.testId('panel-timeline'), 'no timeline').toHaveCount(0);
});
