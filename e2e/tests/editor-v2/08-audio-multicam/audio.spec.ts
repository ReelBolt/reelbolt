import { test, expect, SEEDED_LAYOUT } from '../../../fixtures/editor-v2';
import { expectLargerScreenState, renderAndWait, runOn, waitRender } from '../../../fixtures/editor-v2/rows';
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

/** P8-DSP-08 (M), authoring half (cut on phone, R08): the DSP controls show the "needs a larger screen" state. */
test('P8-DSP-08 phone: authoring track processing shows the "needs a larger screen" state', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P8-DSP-08', 'authoring');
  const ed = await openEditor(seed.seededTimelineId);
  await ed.workspace('audio');
  await expectLargerScreenState(ed.page, /dynamics|equali[sz]er|\bEQ\b|processing|effects|denois/i, 'Track processing on a phone');
});

/** P8-AUTO-02 (M) (cut on phone, R08): Latch automation shows the "needs a larger screen" state. */
test('P8-AUTO-02 phone: latch automation shows the "needs a larger screen" state', async ({ seed, openEditor }) => {
  runOn('pixel7', 'iphone14webkit');
  knownFailure('P8-AUTO-02');
  const ed = await openEditor(seed.seededTimelineId);
  await ed.workspace('audio');
  await expectLargerScreenState(ed.page, /automation|latch/i, 'Automation on a phone');
});
