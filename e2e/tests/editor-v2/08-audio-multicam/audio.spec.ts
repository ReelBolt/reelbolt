import { test, expect, SEEDED_LAYOUT, type EditorPage } from '../../../fixtures/editor-v2';
import { renderAndWait, runOn, waitRender } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * 08 audio and multicam ❌ rows (remediation-plan.md W0.1 render, W0.2 latch keys, W0.3 original-media
 * URL; W0.7: DSP authoring and automation are cut on phone; verdict-table.md P5-MIX / P8 rows).
 */

/** P5-MIX-07 (W0.1): the mixer shows the loudness the finished render of this revision measured. */
test('P5-MIX-07 the mixer shows the measured loudness of the finished render', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('desktop');
  knownFailure('P5-MIX-07');
  const id = await freshTimeline('seeded');
  const r = await renderAndWait(api, seed.projectId, id);
  expect(r.status, `render ${r.errorMessage ?? ''}`).toBe('Passed');
  const ed = await openEditor(id);
  await ed.workspace('audio');
  const block = ed.testId('mixer-render-loudness');
  await expect(block).toBeVisible();
  await expect(block, 'the measured integrated loudness, not "No finished render"').toContainText(/-?\d+(\.\d+)?\s*LUFS/i, { timeout: 20_000 });
});

/** P8-MCAM-02 (W0.3, Seed 1): Sync by waveform reads both clips and reports an offset with its confidence. */
test('P8-MCAM-02 Sync by waveform reads both clips and reports offsets', async ({ freshTimeline, openEditor }) => {
  runOn('desktop');
  knownFailure('P8-MCAM-02');
  const ed = await openEditor(await freshTimeline('seeded'));
  const doc = (await ed.serverTimeline()).document;
  const tone = doc.tracks.find((t) => t.kind === 'audio')!.items[0];
  await ed.selectClipAt(60, 0);
  await ed.selectClipAt(SEEDED_LAYOUT.tone[0] + 60, 1, ['Control']);
  await ed.openPanelTab('tab-structure');
  await ed.tap(ed.testId('multicam-sync'));
  await expect(ed.testId(`multicam-sync-confidence-${tone.id}`), 'a sync result for tone.wav').toBeVisible({ timeout: 30_000 });
  await expect(ed.testId('structure-error'), 'no "Could not read the media" error').toBeHidden();
});

/** P8-DSP-08 (M), render half (W0.1): a track with EQ, dynamics, denoise, leveler and a channel-mapped clip renders, started from the phone. */
test('P8-DSP-08 phone: a timeline with every DSP stage and a channel map renders from Deliver', async ({ freshTimeline, openEditor, api, seed }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P8-DSP-08', 'render');
  test.setTimeout(300_000);
  const id = await freshTimeline('seeded');
  await api.putDocument(seed.projectId, id, (doc) => {
    const a = doc.tracks.find((t) => t.kind === 'audio')!;
    a.eq = { lowCutHz: 80, highCutHz: 12000, bands: [{ frequencyHz: 1000, gainDb: 3, q: 1 }] };
    a.dynamics = { compressor: { thresholdDb: -20, ratio: 3, attackMs: 10, releaseMs: 200, makeupDb: 2 }, limiter: { ceilingDb: -1 }, gate: { thresholdDb: -60, ratio: 2, attackMs: 5, releaseMs: 100 }, deesser: { intensity: 0.3 } };
    a.denoise = { kind: 'fft', strength: 0.2 + Math.random() * 0.3 };
    a.leveler = { kind: 'speech', targetDb: -18 };
    a.items[0].channelMap = 'left';
  });
  const ed = await openEditor(id);
  const posts: string[] = [];
  ed.page.on('response', async (res) => {
    if (res.request().method() === 'POST' && /\/edit-timelines\/[^/]+\/render$/.test(new URL(res.url()).pathname)) posts.push(`${res.status()} ${await res.text().catch(() => '')}`);
  });
  await ed.workspace('deliver');
  await ed.tap(ed.testId('render-start'));
  await expect.poll(() => posts.length, { message: 'the render was requested' }).toBeGreaterThan(0);
  expect(posts[0], 'the render starts').toMatch(/^202 /);
  const started = JSON.parse(posts[0].slice(4)) as { executionId: string };
  const r = await waitRender(api, seed.projectId, id, started.executionId);
  expect(r.status, `DSP render ${r.errorMessage ?? ''}`).toBe('Passed');
});

/*
 * The phone's Audio sheet is volume, mute, solo, Duck music, Clean voice and the voiceover recorder: no buses, no DSP
 * numbers, no automation lanes (docs/editor.md "Not on a phone"). It does not render a disabled stand-in, it does not offer them.
 */
async function openAudioSheetOnPhone(ed: EditorPage): Promise<void> {
  await ed.workspace('audio');
  await expect(ed.testId('phone-audio-sheet')).toBeVisible();
}

test('P8-DSP-08 phone: the Audio sheet offers volume, mute and solo but no track processing (EQ, dynamics, effects)', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openEditor(seed.seededTimelineId);
  await openAudioSheetOnPhone(ed);
  await expect(ed.page.locator('[data-testid^="phone-audio-gain-"]').first()).toBeVisible();
  await expect(ed.page.locator('[data-testid="advanced-audio"], [data-testid^="mixer-rumble-"], [data-testid^="mixer-lowcut-"], [data-testid="mixer-dsp-preview-note"]')).toHaveCount(0);
});

test('P8-AUTO-02 phone: the Audio sheet offers no automation or latch controls', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  const ed = await openEditor(seed.seededTimelineId);
  await openAudioSheetOnPhone(ed);
  await expect(ed.page.locator('[data-testid^="automation-mode-"], [data-testid^="automation-fader-"], [data-testid="advanced-audio"]')).toHaveCount(0);
});
