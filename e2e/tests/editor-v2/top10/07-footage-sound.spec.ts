import { test, expect, SEEDED_LAYOUT, type EditorPage, type Seed } from '../../../fixtures/editor-v2';
import { downloadRender, meanVolumeDb, renderAndWait } from '../../../fixtures/editor-v2/rows';
import { knownFailure } from '../expected-failures';

/**
 * Top-10 #7 (report.md §3): footage must play with its own sound. On origin/master video-track clips are
 * picture-only and nothing ever puts the footage's sound on a track (0 sources started; audit d2 aud.mjs).
 *
 * W0.3 ships step 1 of R08.1 (remediation-plan.md §2 W0.3 row 8): footage's sound travels as a companion
 * clip on the paired "Camera audio" track, put there by the seeder (a timeline made from a render), by a
 * pool insert, and by the clip's "Extract audio" action. A bare video-track clip stays picture-only until
 * linked A/V (R08.1 step 2): playing it as well would double the companion's sound and disagree with the
 * render. So this row checks the two ways footage on the user's timeline gets its sound:
 *   1. a timeline made from the derush render (clipA) plays clipA's sound with no action. The seeder gives
 *      a cut its camera audio only once the source's ingest probe is stored, so the timeline is made here
 *      after clipA's probe exists (the worker seed may have made its derush timeline before that; that
 *      race is reported separately, it is not this row);
 *   2. on a copy of the hand-built seeded timeline (clipA picture-only on V at 0-150), "Extract audio" on
 *      clipA, then play: clipA's sound starts.
 * The second test covers the other two legs of W0.3 row 8 (the QA verdict on #335, C-335-3):
 *   3. clipA inserted from the media pool ("Insert at playhead") onto a blank timeline plays its sound;
 *   4. (desktop) a render of that timeline has clipA's sound: the downloaded file's mean volume over 0.5-2.5 s
 *      (clipA's tone, before its first silent gap at 3.0 s) is above -60 dB (host ffmpeg volumedetect).
 * "clipA's sound started" = an AudioBufferSourceNode.start of a buffer as long as clipA (12 s; the seeded
 * tone.wav under it is 5 s, clipB 8 s), or an unmuted media element .play() of a 12 s source. clipA is
 * used, not clipB, because frames past ~150 are off screen on a phone's timeline.
 */
const INSTRUMENT = `(() => {
  const log = (window.__e2eAudio = { started: [] });
  const AC = window.AudioContext;
  if (AC) window.AudioContext = class extends AC {
    constructor(...a) { super(...a); const make = this.createBufferSource.bind(this);
      this.createBufferSource = () => { const n = make(); const s = n.start.bind(n); n.start = (...x) => { log.started.push(n.buffer ? n.buffer.duration : -1); return s(...x); }; return n; }; }
  };
  const play = HTMLMediaElement.prototype.play;
  HTMLMediaElement.prototype.play = function (...a) { if (!this.muted && this.volume > 0) log.started.push(this.duration); return play.apply(this, a); };
})();`;

const CLIP_A_SEC = 12;
/** Durations of every source started so far. */
const startedDurations = (ed: EditorPage) =>
  ed.page.evaluate(() => (window as unknown as { __e2eAudio: { started: number[] } }).__e2eAudio.started.slice());

/** Waits until the media list carries clipA's ingest probe (the pool and the seeder need it to know it has sound). */
async function waitClipAProbe(seed: Seed): Promise<void> {
  const ref = `file:${seed.files.clipA}`;
  await expect.poll(async () => {
    const media = await seed.api.json<Array<{ mediaRef: string; probe: unknown }>>('GET', `/api/v1/projects/${seed.projectId}/media`);
    return media.find((m) => m.mediaRef === ref)?.probe != null;
  }, { message: 'clipA carries its ingest probe', timeout: 120_000, intervals: [2000] }).toBe(true);
}

async function playExpectingClipASound(ed: EditorPage, frame: number, message: string): Promise<void> {
  await ed.seek(frame);
  const before = (await startedDurations(ed)).length;
  await ed.tap(ed.testId('transport-play'));
  try {
    await expect.poll(async () => (await startedDurations(ed)).slice(before).some((d) => Math.abs(d - CLIP_A_SEC) < 0.5), { message, timeout: 8000 }).toBe(true);
  } finally {
    await ed.tap(ed.testId('transport-play'));
  }
}

test('TOP10-07 P3-VIEW-03 playing a video clip with sound starts its audio', async ({ page, seed, openEditor, freshTimeline }) => {
  knownFailure('TOP10-07');
  test.setTimeout(240_000);
  await page.addInitScript(INSTRUMENT);

  // 1. Footage in a timeline made from a render carries its sound with no action.
  const { api, projectId, files } = seed;
  await waitClipAProbe(seed);
  const seedStep = (await api.getTimeline(projectId, seed.derushTimelineId)).seedStepResultId;
  expect(seedStep, 'the derush timeline names the render it was made from').toBeTruthy();
  const fromRender = await api.createTimeline(projectId, `rem-TOP10-07-${test.info().project.name}`, seedStep!);
  const derush = await openEditor(fromRender.id);
  await playExpectingClipASound(derush, 15, 'the render-seeded timeline plays clipA\'s own sound (a 12 s source started)');

  // 2. Footage placed picture-only: "Extract audio" on clipA, then clipA's own sound plays.
  const ed = await openEditor(await freshTimeline('seeded'));
  await ed.selectClipAt(SEEDED_LAYOUT.clipA[0] + 60, 0); // x = 120 px into the lanes: on screen on every layout
  await ed.openInspector();
  await ed.tap(ed.testId('inspector-extract-audio'));
  await ed.expectServer(
    (tl) => tl.document.tracks.filter((t) => t.kind === 'audio').some((t) => t.items.some((i) => i.assetRef?.projectFileId === files.clipA)),
    'Extract audio put clipA\'s sound on an audio track',
  );
  await playExpectingClipASound(ed, SEEDED_LAYOUT.clipA[0] + 15, 'clipA\'s own sound started after Extract audio (a 12 s source started)');
});

test('TOP10-07 P3-VIEW-03 footage inserted from the media pool plays its sound, and its render is not silent', async ({ page, seed, openEditor, freshTimeline }, testInfo) => {
  knownFailure('TOP10-07');
  test.setTimeout(420_000);
  await page.addInitScript(INSTRUMENT);
  const { api, projectId, files } = seed;
  await waitClipAProbe(seed);

  // 3. Media workspace: select clipA in the pool, "Insert at playhead" on a blank timeline (playhead at 0).
  const id = await freshTimeline('blank');
  const ed = await openEditor(id);
  await ed.workspace('media');
  const tile = page.locator(`[data-testid="media-clip-tile"][data-media-ref="file:${files.clipA}"]`).first();
  if (ed.isPhone && !(await tile.isVisible().catch(() => false))) {
    const pane = page.locator('label', { hasText: /^Media$/ }).first();
    if (await pane.isVisible().catch(() => false)) await pane.tap();
  }
  await ed.tap(tile);
  await expect(ed.testId('media-insert-length'), 'the pool says clipA goes in with its sound').toContainText(/with its sound/i);
  await ed.tap(ed.testId('media-insert-at-playhead'));
  const placed = await ed.expectServer(
    (tl) => tl.document.tracks.some((t) => t.kind === 'video' && t.items.some((i) => i.assetRef?.projectFileId === files.clipA))
      && tl.document.tracks.some((t) => t.kind === 'audio' && t.items.some((i) => i.assetRef?.projectFileId === files.clipA)),
    'the pool insert put clipA on a video track and its sound on an audio track',
  );
  const sound = placed.document.tracks.filter((t) => t.kind === 'audio').flatMap((t) => t.items).find((i) => i.assetRef?.projectFileId === files.clipA)!;
  await ed.workspace('edit');
  await ed.showTimeline();
  await playExpectingClipASound(ed, sound.recordIn + 15, 'clipA inserted from the pool plays its own sound (a 12 s source started)');

  // 4. The render of that timeline carries clipA's sound (a free ffmpeg render on the local stack).
  if (testInfo.project.name !== 'desktop') return;
  const r = await renderAndWait(api, projectId, id);
  expect(r.status, `render ${r.status}${r.errorMessage ? ` (${r.errorMessage})` : ''}`).toBe('Passed');
  const file = await downloadRender(api, projectId, r);
  const fps = 30;
  const from = sound.recordIn / fps + 0.5;
  const db = meanVolumeDb(file, from, from + 2);
  expect(db, `the render's audio over ${from.toFixed(1)}-${(from + 2).toFixed(1)} s (clipA's tone) is not silent: mean_volume ${db} dB`).toBeGreaterThan(-60);
});
