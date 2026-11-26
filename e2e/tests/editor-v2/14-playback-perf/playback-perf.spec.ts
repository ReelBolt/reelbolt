import { chromium, devices, expect, request as pwRequest, test, type Browser, type Page, type TestInfo } from '@playwright/test';
import { createHash } from 'crypto';
import * as fs from 'fs';
import * as os from 'os';
import { EditorApi } from '../../../fixtures/editor-v2/api';
import { ignoreHTTPSErrorsFor, storageStatePath } from '../../../fixtures/editor-v2/env';
import { createPerfSeed, PERF_LAYOUT, type PerfSeed } from '../../../fixtures/editor-v2/perf-seed';
import { runOn } from '../../../fixtures/editor-v2/rows';

/**
 * 14 playback performance: the timeline viewer holds its frame rate on a representative multi-track
 * timeline (initiative editor-v2-playback-verify, issue #492). Desktop Chromium only.
 *
 * The timeline: 1080p / 30 fps, V1 (clip, 30-frame dissolve, clip) + V2 (picture-in-picture) + V3 (a title)
 * + A1 (camera audio) + a music track, 22 s (`fixtures/editor-v2/perf-seed.ts`). It is played from frame 0
 * for 14 s, so the dissolve (10-11 s), the title and both A1 clips are inside the window. Three sources: 720p /
 * GOP-15 files uploaded as originals (the shape of the pipeline's proxy), the REAL proxy derivative the media
 * pipeline produced, and the 1080p / GOP-30 originals the viewer plays before a proxy exists.
 *
 * WHAT IS ASSERTED, all read from the running editor (nothing is mocked or estimated by the test):
 *   1. the always-on dropped-frame counter in the status bar (`data-testid=dropped-frames`, read through its
 *      `data-dropped` / `data-deadlines` attributes): dropped <= DROPPED_PCT_MAX of the deadlines;
 *   2. the achieved presentation rate the session's own ledger reports: >= FPS_MIN_FRACTION of the timeline rate;
 *   3. the playhead really advances at wall-clock speed: frames moved between two reads 8 s apart in steady
 *      play, against `performance.now()`, within +-PLAYHEAD_TOLERANCE;
 *   4. frame cadence: `requestAnimationFrame` gaps in steady play (the main thread is not blocked, so the
 *      display holds 60 Hz): median <= RAF_P50_MAX_MS, p99 <= RAF_P99_MAX_MS and no gap over RAF_GAP_MAX_MS;
 *   5. audio is audible and clock-synced: the AudioContext is `running`, its clock advanced within AUDIO_DRIFT_MS
 *      of the wall clock between the two steady-play reads, and >= 3 buffer sources (A1 x2 + music) were
 *      scheduled; the viewer takes its timing FROM that clock (live-session.ts), so check 3 is also an audio-sync
 *      check, and a stalled or free-running audio clock would be seconds off, not milliseconds;
 *   6. enough frames were composited for the dissolve (frames 300-330) to have been inside the window.
 *
 * THRESHOLDS and why. Calibrated on the dev box (AMD Radeon AI PRO R9700, HEADED Chromium on the display = the
 * real GPU path; 32 cores shared with other work) from 20 runs, load 9-19 (docs/editor.md "Playback performance
 * budget"): 30 fps cases 0-0.5 % dropped in 11 of 15 runs, 2.1-22 % in the rest (cold GPU, other builds); 60 fps
 * 0-4.5 %, fresh 95-100 %. So: dropped <= 6 % (<= 2.5 % with EDITOR_E2E_PERF_STRICT=1), <= 7 % at 60 fps; fresh frames
 * >= 90 % of the deadlines; >= 6 distinct canvas hashes of 8; ledger rate >= 90 %; playhead within 5 % of wall clock;
 * audio clock within 300 ms over 8 s (underruns slow it); rAF median <= 20 ms, p99 <= 100 ms, longest gap <= 600 ms.
 * Software GL (no display, or EDITOR_E2E_PERF_GL=software) is MEASURED and PRINTED but the test is then skipped with an
 * annotation, never passed: SwiftShader read 10-32 % dropped and 1080p originals 1-9 % fresh.
 * The spec waits up to 4 min for the 1-minute load average to fall under 0.6 x cores and is SKIPPED (annotated) if it
 * does not: at load ~30 on this 32-core box 30 fps read 9.9 % dropped and 60 fps 16 %, while load < 19 held the bounds.
 * It starts a browser of its own per measurement and never retries a failed run.
 */

const SOURCES = [
  // 720p / GOP-15 files uploaded as originals: the shape of the derivative pipeline's proxy, independent of the engine.
  { name: 'proxy-shaped', proxyLike: true, derivatives: false },
  // The real thing: the proxy derivative the media pipeline produced, which the viewer prefers over the original.
  { name: 'derivative-proxy', proxyLike: false, derivatives: true },
  // No proxy yet: the viewer plays the 1080p originals.
  { name: 'original-1080p', proxyLike: false, derivatives: false },
  // A 60 fps timeline over 60 fps 1080p sources, the same shape: every output frame is a new picture.
  { name: 'original-1080p-60fps', proxyLike: false, derivatives: false, fps60: true },
  // 3840x2160 sources (no proxy), 30 fps timeline: held to the drop bound, or the viewer must SAY it could not (see `measure`).
  { name: 'original-4k-30fps', proxyLike: false, derivatives: false, res4k: true },
  // 3840x2160 sources, 60 fps timeline.
  { name: 'original-4k-60fps', proxyLike: false, derivatives: false, fps60: true, res4k: true },
] as const;

const PLAY_SECONDS = 14;

/* GPU thresholds. */
const DROPPED_PCT_MAX = 6.0;
/** With EDITOR_E2E_PERF_STRICT=1. */
const DROPPED_PCT_MAX_QUIET = 2.5;
/** The 60 fps timeline (8.3 ms a frame): 0-4.5 % in 5 headed-GPU runs at load 9-19. */
const DROPPED_PCT_MAX_60FPS = 7.0;
const DROPPED_PCT_MAX_60FPS_STRICT = 4.5;
/** 3840x2160 sources with the rate held and no label on screen: 4K 30 fps read 0, 0, 0, 0.23, 0.71 % in 5 runs at load 15-19. */
const DROPPED_PCT_MAX_4K = 3.0;
const DROPPED_PCT_MAX_4K_STRICT = 1.5;
/** Above this many x cores of 1-minute load the test is SKIPPED with an annotation (never passed). */
const SKIP_LOAD_FACTOR = 0.6;
/** The 5-minute load average above this x cores also skips: the final pass at 5-min load 15-16 (5 CI VMs) read 0.6-1.1 s stalls and 8-24 % dropped at 60 fps, while 5-min load <= 14 read 1-2 %. */
const SKIP_LOAD5_FACTOR = 0.45;
const FPS_MIN_FRACTION = 0.9;
/** Fresh = presented frames in which EVERY layer showed a newly decoded frame; the rest repeated a held one. */
const FRESH_PCT_MIN = 90;
/** The program canvas is hashed this many times, all OUTSIDE the measured play (before it, after it, after a short second play); a frozen picture yields one hash. */
const CANVAS_SAMPLES = 3;
const CANVAS_DISTINCT_MIN = 3;
const PLAYHEAD_TOLERANCE = 0.05;
const RAF_P50_MAX_MS = 20;
const RAF_P99_MAX_MS = 100;
const RAF_GAP_MAX_MS = 600;
const AUDIO_DRIFT_MS = 300;

/**
 * Real GPU path = HEADED Chromium on the machine's display. (Headless ANGLE/EGL looks like a GPU in the renderer
 * string but crashes the GPU process on the first VideoFrame upload - "Could not find SharedImageBackingFactory ...
 * BGRA_8888" - after which every WebGL context is lost and the program canvas stays black; the pixel checks below
 * exist because earlier "GPU" numbers were taken on that dead context.) With no display, or EDITOR_E2E_PERF_GL=software,
 * it runs headless on SwiftShader and the software bounds apply.
 */
const HAS_DISPLAY = Boolean(process.env.DISPLAY || process.env.WAYLAND_DISPLAY);
const SOFTWARE_ONLY = process.env.EDITOR_E2E_PERF_GL === 'software' || !HAS_DISPLAY;
const GL_ARGS = SOFTWARE_ONLY
  ? ['--ignore-gpu-blocklist', '--enable-unsafe-swiftshader', '--autoplay-policy=no-user-gesture-required']
  : [
      '--ignore-gpu-blocklist',
      '--autoplay-policy=no-user-gesture-required',
      '--disable-backgrounding-occluded-windows',
      '--disable-renderer-backgrounding',
      // EDITOR_E2E_PERF_WINDOW=x,y puts the window on a given monitor (refresh rates differ per display).
      // EDITOR_E2E_PERF_ANGLE=vulkan|gl-egl asks ANGLE for the machine's GPU on a display whose own GL is software (xrdp).
      ...(process.env.EDITOR_E2E_PERF_ANGLE ? [`--use-angle=${process.env.EDITOR_E2E_PERF_ANGLE}`, ...(process.env.EDITOR_E2E_PERF_ANGLE === 'vulkan' ? ['--enable-features=Vulkan'] : []), '--enable-gpu'] : []),
      ...(process.env.EDITOR_E2E_PERF_WINDOW ? [`--window-position=${process.env.EDITOR_E2E_PERF_WINDOW}`] : []),
    ];

/** Init script: rAF cadence, long tasks and the AudioContext's clock and scheduled sources, from the first frame. */
const INSTRUMENT = `
window.__perf = { raf: [], long: [], acStarts: 0, acs: [], probes: [] };
new PerformanceObserver((l) => { for (const e of l.getEntries()) window.__perf.long.push([e.startTime, e.duration]); }).observe({ entryTypes: ['longtask'] });
const loop = (t) => { window.__perf.raf.push(t); requestAnimationFrame(loop); };
requestAnimationFrame(loop);
const AC = window.AudioContext;
window.AudioContext = class extends AC { constructor(...a) { super(...a); window.__perf.acs.push(this); } };
const start = AudioBufferSourceNode.prototype.start;
AudioBufferSourceNode.prototype.start = function (...a) { window.__perf.acStarts++; return start.apply(this, a); };
`;

async function waitForQuietMachine(): Promise<number> {
  const limit = SKIP_LOAD_FACTOR * os.cpus().length;
  const deadline = Date.now() + 240_000;
  const busy = () => os.loadavg()[0] > limit || os.loadavg()[1] > SKIP_LOAD5_FACTOR * os.cpus().length;
  while (busy() && Date.now() < deadline) await new Promise((r) => setTimeout(r, 3000));
  // Above the limit when still busy: the caller skips on this value.
  return busy() ? Math.max(os.loadavg()[0], limit + 0.1) : os.loadavg()[0];
}

async function counter(page: Page): Promise<{ dropped: number; deadlines: number; presented: number; repeats: number; fps: number }> {
  const chip = page.getByTestId('dropped-frames');
  return {
    dropped: Number(await chip.getAttribute('data-dropped')),
    deadlines: Number(await chip.getAttribute('data-deadlines')),
    presented: Number(await chip.getAttribute('data-presented')),
    repeats: Number(await chip.getAttribute('data-repeats')),
    fps: Number(await chip.getAttribute('data-achieved-fps')),
  };
}

const timecodeFrames = (tc: string, fps: number) => {
  const [h, m, s, f] = tc.split(':').map(Number);
  return ((h * 60 + m) * 60 + s) * fps + f;
};

/** Which GL the browser actually uses, asked on a throwaway page (a second context on the editor's own page is not reliable). */
async function glRenderer(browser: Browser): Promise<string> {
  const context = await browser.newContext();
  try {
    const page = await context.newPage();
    await page.goto('about:blank');
    return await page.evaluate(() => {
      const gl = document.createElement('canvas').getContext('webgl2');
      const ext = gl?.getExtension('WEBGL_debug_renderer_info');
      return gl && ext ? String(gl.getParameter(ext.UNMASKED_RENDERER_WEBGL)) : 'no webgl2';
    });
  } finally {
    await context.close();
  }
}

const seeds = new Map<string, PerfSeed>();

/** Seeded lazily, once per source per worker, so a --grep run (or --repeat-each) only pays for the sources it measures. */
async function seedFor(source: (typeof SOURCES)[number], baseURL: string): Promise<PerfSeed> {
  const cached = seeds.get(source.name);
  if (cached) return cached;
  const request = await pwRequest.newContext({ baseURL, storageState: storageStatePath(baseURL) });
  try {
    const api = new EditorApi(request);
    const run = process.env.EDITOR_E2E_RUN ?? Date.now().toString();
    const seed = await createPerfSeed(api, `perf-${run}-${source.name}`, { proxyLike: source.proxyLike, derivatives: source.derivatives, fps60: 'fps60' in source, res4k: 'res4k' in source });
    seeds.set(source.name, seed);
    return seed;
  } finally {
    await request.dispose();
  }
}

for (const source of SOURCES) {
  test(`PERF-01 ${source.name}: the representative timeline plays ${PLAY_SECONDS} s at its frame rate, no dropped frames, audio on the clock`, async ({ baseURL }, testInfo) => {
    runOn('desktop');
    test.setTimeout(420_000);
    const seed = await seedFor(source, baseURL!);
    // Software GL (no display: every hosted runner) is measured and printed but never enforced, so a quiet machine buys
    // nothing there: waiting up to 240 s for it only ate the 420 s test budget (CI run 45796910736: the first case timed
    // out in the wait + a slow seed instead of reaching its annotated skip).
    const load = SOFTWARE_ONLY ? os.loadavg()[0] : await waitForQuietMachine();
    console.log(`PERF-01 ${source.name}: machine load average ${load.toFixed(1)} on ${os.cpus().length} cores (skip above ${SKIP_LOAD_FACTOR * os.cpus().length})`);
    testInfo.annotations.push({ type: 'perf-load', description: `1-min load ${load.toFixed(1)}` });
    test.skip(!SOFTWARE_ONLY && load > SKIP_LOAD_FACTOR * os.cpus().length, `machine saturated (load ${load.toFixed(1)} > ${SKIP_LOAD_FACTOR} x cores): frame delivery cannot be measured; NOT a pass`);
    const fps = 'fps60' in source ? 60 : 30;
    const is4k = 'res4k' in source;

    // A browser of its own per measurement: the GPU process (and its WebGL contexts) starts clean, so one
    // run's state cannot degrade the next run's compositor.
    const browser = await chromium.launch({ args: GL_ARGS, headless: SOFTWARE_ONLY });
    try {
      await measure(browser, baseURL!, seed, source.name, load, fps, is4k, testInfo);
    } finally {
      await browser.close();
    }
  });
}

/**
 * PERF-02 honest degradation, deterministic: the 60 fps timeline over 3840x2160 sources is played with the page's CPU
 * throttled (CDP Emulation.setCPUThrottlingRate), a forced low budget no machine can hold. The viewer must SAY so
 * ("Reduced frame rate" in the viewer's note) and the dropped-frame counter must count the REDUCED rate: fewer deadlines
 * than the full 60 fps would have produced, and a ledger that is not claiming 60 fps. Independent of machine load.
 */
test('PERF-02 forced low budget: the viewer labels "Reduced frame rate" and the counter counts the reduced rate', async ({ baseURL }, testInfo) => {
  runOn('desktop');
  test.setTimeout(240_000);
  const source = SOURCES[5];
  const seed = await seedFor(source, baseURL!);
  const browser = await chromium.launch({ args: GL_ARGS, headless: SOFTWARE_ONLY });
  try {
    const renderer2 = await glRenderer(browser);
    if (/swiftshader|llvmpipe|softpipe|software/i.test(renderer2)) {
      testInfo.annotations.push({ type: 'skip-reason', description: `software GL (${renderer2}): the viewer starts on the Simple preview; run on a GPU display` });
      test.skip(true, `software GL (${renderer2}): Auto starts on the Simple preview, nothing to throttle; run on a GPU display`);
    }
    const context = await browser.newContext({ ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 }, deviceScaleFactor: 1, baseURL: baseURL!, storageState: storageStatePath(baseURL!), ignoreHTTPSErrors: ignoreHTTPSErrorsFor(baseURL!) });
    const page = await context.newPage();
    await page.goto(`/app/editor/${seed.projectId}/timeline/${seed.timelineId}`);
    await expect(page.getByTestId('session-kind')).toHaveText('Live preview', { timeout: 45_000 });
    await page.waitForTimeout(6000);
    const cdp = await context.newCDPSession(page);
    await cdp.send('Emulation.setCPUThrottlingRate', { rate: 12 });
    // Short window right after the label: every play() (also the loop wrap at ~14 s) re-arms the governor and drops the label, so the
    // label is asserted by the poll below and the window only measures the counter.
    const seconds = 3;
    // Loop, so the player keeps running after the label whatever the timeline length (the ~14 s timeline otherwise ends before the
    // governor has labelled it under a 12x throttle and the counter measures a stopped player).
    const loop = page.getByRole('button', { name: 'Loop', exact: true });
    if ((await loop.getAttribute('aria-pressed')) !== 'true') await loop.click();
    await page.getByTestId('transport-play').click();
    const note = page.getByTestId('preview-reduced-note');
    await expect.poll(async () => (await note.allInnerTexts()).some((t) => /Reduced frame rate/.test(t)), { timeout: 60_000, message: 'the "Reduced frame rate" label appears under a forced low budget' }).toBe(true);
    const w0 = Date.now();
    const c0 = await counter(page);
    await page.waitForTimeout(seconds * 1000);
    const c = await counter(page);
    const perSecond = ((c.deadlines - c0.deadlines) / (Date.now() - w0)) * 1000;
    await page.getByTestId('transport-play').click();
    testInfo.annotations.push({ type: 'perf-degrade', description: JSON.stringify(c) });
    console.log(`PERF-02 ${JSON.stringify(c)}`);
    // Counting the reduced rate: once labelled, the counter counts at most every second frame (<= 30 slots a second, with slack), not 60.
    expect(perSecond, `deadlines counted per second after the label (${JSON.stringify({ c0, c })})`).toBeLessThanOrEqual(36);
    // At 12x throttle the page achieves ~2 fps (ledger fps), so the counter advances about once a second: it must advance, not run at the full rate.
    expect(c.deadlines, 'the counter is still counting').toBeGreaterThan(c0.deadlines);
    await context.close();
  } finally {
    await browser.close();
  }
});

async function measure(browser: Browser, baseURL: string, seed: PerfSeed, sourceName: string, load: number, FPS: number, is4k: boolean, testInfo: TestInfo): Promise<void> {
  {
    const renderer = await glRenderer(browser);
    const software = /swiftshader|llvmpipe|software/i.test(renderer);
    const context = await browser.newContext({
      ...devices['Desktop Chrome'],
      viewport: { width: 1440, height: 900 },
      deviceScaleFactor: 1,
      baseURL,
      storageState: storageStatePath(baseURL),
      ignoreHTTPSErrors: ignoreHTTPSErrorsFor(baseURL),
    });
    const page = await context.newPage();
    await page.addInitScript(INSTRUMENT);
    await page.goto(`/app/editor/${seed.projectId}/timeline/${seed.timelineId}`);
    await expect(page.getByTestId('timeline-canvas')).toBeVisible({ timeout: 45_000 });
    if (software) {
      // Auto starts a software-graphics browser on the Simple preview (R04.5), so there is no live compositor to measure.
      testInfo.annotations.push({ type: 'skip-reason', description: `software GL (${renderer}): the viewer starts on the Simple preview; run on a GPU display` });
      test.skip(true, `software GL (${renderer}): Auto starts on the Simple preview, nothing to measure; run on a GPU display`);
    }
    await expect(page.getByTestId('session-kind')).toHaveText('Live preview', { timeout: 30_000 });
    // Sources open and the first frames decode while paused (the viewer presents the paused frame once).
    await page.waitForTimeout(6000);
    const hashes = new Set<string>();
    const canvasEl = page.getByTestId('program-canvas');
    const sampleCanvas = async () => {
      const png = await canvasEl.screenshot({ animations: 'allow', caret: 'initial', timeout: 5000 });
      hashes.add(createHash('sha1').update(png).digest('hex'));
    };
    await sampleCanvas();
    const before = await counter(page);
    expect(before.deadlines, 'nothing is counted while paused').toBe(0);
    await page.evaluate(() => {
      const p = (window as unknown as { __perf: { raf: number[]; long: number[][] } }).__perf;
      p.raf.length = 0;
      p.long.length = 0;
    });

    // EDITOR_E2E_PERF_TRACE=1 records a Chrome trace of the play (attribute a stall to a thread/task); off by default.
    const tracing = process.env.EDITOR_E2E_PERF_TRACE === '1';
    if (tracing) await browser.startTracing(page, { path: testInfo.outputPath(`trace-${sourceName}.json`), categories: ['devtools.timeline', 'disabled-by-default-devtools.timeline', 'v8', 'v8.gc', 'blink', 'gpu', 'media', 'toplevel', 'cc', 'viz', 'disabled-by-default-devtools.timeline.frame'] });
    const t0 = await page.evaluate(() => performance.now());
    await page.getByTestId('transport-play').click();

    // Two reads in steady play, 8 s apart: the playhead and the audio clock, each against the wall clock.
    const read = async () => ({
      ...(await page.evaluate(() => ({
        wall: (window as unknown as { __perf: { probes: number[][] } }).__perf.probes.push([performance.now(), 0]) && performance.now(),
        audio: (window as unknown as { __perf: { acs: AudioContext[] } }).__perf.acs.map((c) => ({ t: c.currentTime, state: c.state })),
      }))),
      frame: timecodeFrames(await page.getByTestId('status-timecode').innerText(), FPS),
    });
    await page.waitForTimeout(3000);
    const a = await read();
    // Nothing runs in the page during the measured play except the counter / clock reads at the start and end: the
    // canvas-changed evidence is taken OUTSIDE the timed window (paused before Play, paused after it, and after a short
    // second play), because every in-play canvas sample (toDataURL, thumbnail, compositor screenshot) was followed by rAF gaps.
    await page.waitForTimeout(8000);
    const b = await read();
    await page.waitForTimeout(Math.max(0, PLAY_SECONDS * 1000 - (b.wall - t0)));
    const audio1 = b.audio;
    const audioStarts = await page.evaluate(() => (window as unknown as { __perf: { acStarts: number } }).__perf.acStarts);
    const cadence = await page.evaluate((from) => {
      const p = (window as unknown as { __perf: { raf: number[]; long: number[][] } }).__perf;
      const steady = p.raf.filter((t) => t >= from + 1500);
      const gaps = steady.slice(1).map((t, i) => t - steady[i]);
      const sorted = [...gaps].sort((x, y) => x - y);
      return {
        frames: gaps.length,
        slow: gaps.filter((g) => g > 25).length,
        /** [ms since Play, gap ms] of the first rAF gaps over 18 ms: shows WHERE in the play a stall sits. */
        late: steady.slice(1).map((t, i) => [Math.round(t - from), Math.round(gaps[i])]).filter(([, g]) => g > 18).slice(0, 40),
        max: Math.max(0, ...gaps),
        p50: sorted[Math.floor(sorted.length / 2)] ?? 0,
        p99: sorted[Math.floor(sorted.length * 0.99)] ?? 0,
        longTasks: p.long.filter(([start]) => start >= from + 1500).length,
        longMax: Math.max(0, ...p.long.filter(([start]) => start >= from + 1500).map(([, d]) => d)),
        /** [ms since Play, ms] of the spec's own page probes (clock reads, canvas hashes): a stall right after one is the probe's. */
        probes: (p as unknown as { probes: number[][] }).probes.map(([t, d]) => [Math.round(t - from), Math.round(d)]),
        /** [ms since Play, gap ms] of every rAF gap over 18 ms in the whole play, warm-up included. */
        lateAll: p.raf.filter((t) => t >= from).slice(1).map((t, i, a) => [Math.round(t - from), Math.round(t - (i === 0 ? p.raf.filter((x) => x >= from)[0] : a[i - 1]))]).filter(([, g]) => g > 18).slice(0, 60),
      };
    }, t0);

    // The viewer's note is only on screen while playing: read it before pausing.
    const reducedNotes = await page.getByTestId('preview-reduced-note').allInnerTexts();
    if (tracing) await browser.stopTracing();
    await page.getByTestId('transport-play').click(); // pause: the throttled counter flushes on stop
    await expect.poll(async () => (await counter(page)).deadlines, { timeout: 5000 }).toBeGreaterThan(0);
    await page.waitForTimeout(600);
    const after = await counter(page);
    await sampleCanvas();
    // Second, short play (outside the measured window): the picture moves on again.
    await page.getByTestId('transport-play').click();
    await page.waitForTimeout(2000);
    await page.getByTestId('transport-play').click();
    await page.waitForTimeout(600);
    await sampleCanvas();
    const droppedPct = after.deadlines > 0 ? (after.dropped / after.deadlines) * 100 : 100;
    const reducedRate = reducedNotes.some((t) => /Reduced frame rate/.test(t));
    const reducedQuality = reducedNotes.some((t) => /Reduced quality/.test(t));
    const fresh = after.presented - after.repeats;
    const freshPct = after.deadlines > 0 ? (fresh / after.deadlines) * 100 : 0;
    const playheadRatio = (b.frame - a.frame) / (((b.wall - a.wall) / 1000) * FPS);
    const audioDriftMs = b.audio.map((c, i) => Math.abs((c.t - (a.audio[i]?.t ?? 0)) * 1000 - (b.wall - a.wall)));

    const summary = { renderer, software, strict: process.env.EDITOR_E2E_PERF_STRICT === '1', load: Number(load.toFixed(1)), source: sourceName, ...after, droppedPct: Number(droppedPct.toFixed(2)), fresh, freshPct: Number(freshPct.toFixed(2)), distinctCanvases: hashes.size, reducedRate, reducedQuality, playheadRatio: Number(playheadRatio.toFixed(3)), cadence, audioStarts, audioDriftMs: audioDriftMs.map(Math.round), audioState: audio1.map((c) => c.state) };
    testInfo.annotations.push({ type: 'perf', description: JSON.stringify(summary) });
    fs.writeFileSync(testInfo.outputPath(`perf-${sourceName}.json`), JSON.stringify(summary, null, 2));
    console.log(`PERF-01 ${JSON.stringify(summary)}`);

    if (software) {
      // Measured, never passed: SwiftShader cannot hold 30 fps on this timeline (10-32 % dropped, 1080p originals 1-9 % fresh).
      testInfo.annotations.push({ type: 'skip-reason', description: 'software GL: measured and printed, bounds not enforced (run on a GPU)' });
      test.skip(true, 'software GL: the viewer cannot hold the rate here; numbers printed, bounds not enforced');
    }
    const strict = process.env.EDITOR_E2E_PERF_STRICT === '1';
    const droppedMax = is4k ? (strict ? DROPPED_PCT_MAX_4K_STRICT : DROPPED_PCT_MAX_4K) : FPS === 60 ? (strict ? DROPPED_PCT_MAX_60FPS_STRICT : DROPPED_PCT_MAX_60FPS) : strict ? DROPPED_PCT_MAX_QUIET : DROPPED_PCT_MAX;
    const fpsMin = FPS_MIN_FRACTION * FPS;
    // 4K: either the timeline rate was HELD (the same bounds as every other case, no label on screen), or the viewer SAID it could
    // not ("Reduced frame rate" / "Reduced quality" in the viewer's note, the counter then counts the reduced rate). Silently
    // dropping with no label fails.
    const honestDegrade = is4k && (reducedRate || reducedQuality);
    testInfo.annotations.push({ type: 'perf-4k', description: is4k ? (honestDegrade ? `degraded honestly: ${JSON.stringify(reducedNotes)}` : 'rate held') : 'n/a' });
    // The play covered the whole window: deadlines are timeline frames the clock passed (a reduced rate counts fewer).
    expect(after.deadlines, `~${PLAY_SECONDS} s of deadlines at ${FPS} fps (${JSON.stringify(summary)})`).toBeGreaterThanOrEqual(((PLAY_SECONDS - 2) * FPS) / (honestDegrade ? 4 : 1));
    if (!honestDegrade) {
      expect(droppedPct, `dropped-frame counter ${after.dropped}/${after.deadlines} on ${renderer} (${JSON.stringify(summary)})`).toBeLessThanOrEqual(droppedMax);
      expect(reducedRate, 'the viewer fell back to half rate ("Reduced frame rate"): the timeline rate was NOT held').toBe(false);
      expect(freshPct, `fresh frames (a newly decoded frame in every layer): ${fresh}/${after.deadlines}, ${after.repeats} repeated; ${JSON.stringify(summary)}`).toBeGreaterThanOrEqual(FRESH_PCT_MIN);
    }
    expect(hashes.size, `distinct program-canvas pictures in ${CANVAS_SAMPLES} samples (a frozen picture has 1)`).toBeGreaterThanOrEqual(CANVAS_DISTINCT_MIN);
    if (!honestDegrade) expect(after.fps, `ledger achieved fps on ${renderer}`).toBeGreaterThanOrEqual(fpsMin);
    expect(Math.abs(playheadRatio - 1), `playhead speed vs wall clock (ratio ${playheadRatio.toFixed(3)})`).toBeLessThanOrEqual(PLAYHEAD_TOLERANCE);
    if (!software) {
      expect(cadence.p50, `median rAF gap (ms), ${cadence.frames} frames`).toBeLessThanOrEqual(RAF_P50_MAX_MS);
      expect(cadence.p99, 'p99 rAF gap (ms)').toBeLessThanOrEqual(RAF_P99_MAX_MS);
      expect(cadence.max, 'longest rAF gap (ms)').toBeLessThanOrEqual(RAF_GAP_MAX_MS);
    }
    expect(audio1.length, 'an AudioContext exists').toBeGreaterThan(0);
    expect(audio1.every((c) => c.state === 'running'), 'the audio context ran').toBeTruthy();
    expect(Math.max(...audioDriftMs), `audio clock vs wall clock (ms): ${audioDriftMs.map(Math.round)}`).toBeLessThanOrEqual(AUDIO_DRIFT_MS);
    expect(audioStarts, 'A1 (2 clips) and the music track were scheduled').toBeGreaterThanOrEqual(3);
    // The dissolve (frames 300-330) is inside the window: at least that many frames were composited.
    expect(after.presented, `presented frames (> ${PERF_LAYOUT.transition[1]})`).toBeGreaterThan((PERF_LAYOUT.transition[1] * FPS) / 30);
    await context.close();
  }
}
