/**
 * T-201 — Playwright driver for the compositor budget.  REMOVABLE (see README.md).
 *
 * Starts `serve.mjs` on a free port, opens `playback.html` in real Chromium, calls
 * `window.runScenario()`, and writes the raw JSON report plus a human-readable summary.
 *
 * Modes (all are the SAME page and the same entry point — the page decides which path runs):
 *   --mode main      4 layers + 6 tracks, WebCodecs composite, 1280x720 canvas   (default)
 *   --mode native    4 layers + 6 tracks composited at the proxy's native 1920x1080
 *   --mode fallback  WebCodecs made unavailable in the page; expects the §3.5 path
 *   --mode pool      the main scenario under an overridden decoder pool (--lookahead N)
 *
 * Playwright is resolved from the `e2e/` install by ABSOLUTE path — this ticket forbids
 * adding `playwright` to `web/package.json`, and nothing under `e2e/**` is written.
 *
 * Usage (from `web/`):
 *   node lib/editing/preview/spike/run-playback.mjs [--mode main|native|fallback|pool]
 *        [--runs N] [--seconds N] [--warmup N] [--lookahead N] [--headed] [--out PATH]
 */
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const WEB_ROOT = resolve(HERE, '..', '..', '..', '..');
const REPO_ROOT = resolve(WEB_ROOT, '..');

const argv = process.argv.slice(2);
const arg = (name, dflt) => {
  const i = argv.indexOf(name);
  return i >= 0 ? argv[i + 1] : dflt;
};
const headed = argv.includes('--headed');
const MODE = arg('--mode', 'main');
const RUNS = Math.max(1, Number(arg('--runs', 3)));
const SECONDS = Number(arg('--seconds', 30));
const WARMUP = Number(arg('--warmup', 4));
const LOOKAHEAD = arg('--lookahead', null);
const OUT_PATH = resolve(arg('--out', resolve(HERE, 'measurements.t201.json')));

const COMPOSITE = MODE === 'native' ? { w: 1920, h: 1080 } : { w: 1280, h: 720 };
/** `sizes` re-composites the same 4 frames at five canvas areas -> the fill-rate curve. */
const SCALING = MODE === 'sizes';

const LAUNCH_ARGS = [
  '--no-sandbox',
  '--ozone-platform=headless',
  '--use-gl=angle',
  '--use-angle=swiftshader',
  '--enable-unsafe-swiftshader',
  // LOAD-BEARING (T-200's finding, reused verbatim). There is no /dev/dri and no X server
  // here; without this flag ANGLE picks DisplayVkXcb, xcb_connect() fails, SwANGLE returns
  // EGL_NOT_INITIALIZED and canvas.getContext('webgl2') is null under every other
  // combination tried. With it, WebGL 2.0 comes up on SwiftShader — SOFTWARE GL.
  '--disable-gpu-sandbox',
  // The six WebAudio tracks have to actually run: an AudioContext created without a user
  // gesture starts 'suspended', and a suspended context renders nothing, so the audio half
  // of the scenario would silently measure zero work.
  '--autoplay-policy=no-user-gesture-required',
  // A backgrounded/occluded page gets its rAF and its timers throttled, which would make
  // "achieved fps" a measurement of the throttler rather than of the compositor.
  '--disable-background-timer-throttling',
  '--disable-renderer-backgrounding',
  '--disable-backgrounding-occluded-windows',
];

// ── Resolve Playwright from the e2e package, never from web/node_modules ────────────────
const e2eRequire = createRequire(resolve(REPO_ROOT, 'e2e', 'package.json'));
const { chromium } = e2eRequire('playwright');

// ── Static server on a free port ────────────────────────────────────────────────────────
const server = spawn(process.execPath, [resolve(HERE, 'serve.mjs'), '0'], {
  cwd: WEB_ROOT,
  env: { ...process.env, NODE_ENV: undefined },
  stdio: ['ignore', 'pipe', 'inherit'],
});
const killServer = () => {
  try {
    server.kill('SIGKILL');
  } catch {
    /* already gone */
  }
};
process.on('exit', killServer);

const origin = await new Promise((resolveOrigin, reject) => {
  let buf = '';
  const timer = setTimeout(() => reject(new Error('serve.mjs did not become ready in 15 s')), 15_000);
  server.stdout.on('data', (chunk) => {
    buf += chunk;
    const m = /SPIKE_SERVER_READY (\S+)/.exec(buf);
    if (m) {
      clearTimeout(timer);
      resolveOrigin(m[1]);
    }
  });
  server.on('exit', (code) => {
    clearTimeout(timer);
    reject(new Error(`serve.mjs exited early with code ${code}`));
  });
});
console.log(`[driver] static server: ${origin}`);
console.log(`[driver] mode=${MODE} runs=${RUNS} seconds=${SECONDS} warmup=${WARMUP} composite=${COMPOSITE.w}x${COMPOSITE.h}`);

const proxyPath = resolve(HERE, 'fixtures', 'playback', 'proxy-1080p-36s.mp4');
if (!existsSync(proxyPath)) {
  console.error(`[driver] missing fixture ${proxyPath} — run make-playback-fixture.sh`);
  killServer();
  process.exit(1);
}
const proxySha256 = createHash('sha256').update(readFileSync(proxyPath)).digest('hex');
const proxyBytes = readFileSync(proxyPath).byteLength;
console.log(`[driver] proxy sha256 ${proxySha256} (${proxyBytes} bytes)`);

// ── One browser session ─────────────────────────────────────────────────────────────────
async function runOnce(runIndex) {
  const browser = await chromium.launch({ headless: !headed, channel: 'chromium', args: LAUNCH_ARGS });
  try {
    const page = await (await browser.newContext()).newPage();
    const console_ = [];
    page.on('console', (m) => {
      const s = `${m.type()}: ${m.text()}`;
      console_.push(s);
      if (runIndex === 0) console.log(`[page] ${s}`);
    });
    page.on('pageerror', (e) => {
      const s = `pageerror: ${e.message}`;
      console_.push(s);
      console.error(`[page] ${s}`);
    });
    page.on('requestfailed', (r) => {
      const s = `requestfailed: ${r.url()} ${r.failure()?.errorText ?? ''}`;
      console_.push(s);
      console.error(`[page] ${s}`);
    });

    await page.goto(`${origin}/lib/editing/preview/spike/playback.html`, { waitUntil: 'load' });
    await page.waitForFunction(() => window.__playbackReady === true, null, { timeout: 30_000 });

    let killSwitch = null;
    if (MODE === 'fallback') {
      // The experiment: make WebCodecs unavailable INSIDE the page, then call the very
      // same entry point the primary path uses. What runs is observed, not assumed.
      killSwitch = await page.evaluate(() => window.__spikeDisableWebCodecs());
    }
    let poolOverride = null;
    if (MODE === 'pool' && LOOKAHEAD !== null) {
      // `--lookahead` is the experiment that probes the decoder-pool knob — including, at high
      // values, the silent 0.00 fps failure. It is therefore allowed to suspend the pool invariant,
      // and the value returned here reports whether the pool it actually set still satisfies it, so
      // a suspended run can never be read as a normal one.
      poolOverride = await page.evaluate(
        (n) => window.__spikeSetPool({ lookAheadFrames: n, allowUnsafeLookAhead: true }),
        Number(LOOKAHEAD),
      );
    }

    const report = await page.evaluate(
      ({ seconds, warmupSec, compositeWidth, compositeHeight, scalingProbeRequested }) =>
        window.runScenario({ seconds, warmupSec, compositeWidth, compositeHeight, scalingProbeRequested }),
      {
        seconds: SECONDS,
        warmupSec: WARMUP,
        compositeWidth: COMPOSITE.w,
        compositeHeight: COMPOSITE.h,
        scalingProbeRequested: SCALING,
      },
    );
    report.driver = {
      runIndex,
      mode: MODE,
      playwright: e2eRequire('playwright/package.json').version,
      playwrightFrom: 'e2e/node_modules (resolved by absolute path; web/package.json untouched)',
      browserVersion: browser.version(),
      launchedArgs: LAUNCH_ARGS,
      channel: 'chromium',
      headed,
      killSwitch,
      poolOverride,
      consoleMessages: console_,
    };
    return report;
  } finally {
    await browser.close();
  }
}

// ── Run N sessions ──────────────────────────────────────────────────────────────────────
const runReports = [];
let exitCode = 0;
try {
  for (let i = 0; i < RUNS; i++) {
    const r = await runOnce(i);
    runReports.push(r);
    const a = r.achieved ?? {};
    console.log(
      `[driver] run ${i + 1}/${RUNS}: path=${r.path} achievedFps=${a.achievedFps ?? 'n/a'} ` +
        `dropped=${a.droppedFrames ?? 'n/a'}/${a.targetDeadlines ?? 'n/a'}`,
    );
    if ((r.errors ?? []).length > 0) {
      console.error('[driver] harness reported errors:');
      for (const e of r.errors) console.error('  ' + String(e).split('\n')[0]);
      exitCode = 2;
    }
  }
} catch (e) {
  console.error('[driver] FAILED:', e);
  exitCode = 1;
} finally {
  killServer();
}

const representative = runReports[0];

/** Cross-run median of one number picked out of each run. */
function crossRunMedian(pick) {
  const xs = runReports.map(pick).filter((v) => typeof v === 'number' && Number.isFinite(v));
  if (xs.length === 0) return null;
  const s = [...xs].sort((a, b) => a - b);
  const mid = s.length >> 1;
  return +(s.length % 2 ? s[mid] : (s[mid - 1] + s[mid]) / 2).toFixed(3);
}

/** [label, pick] — every figure is the cross-run median of a within-run figure. */
const METRICS = [
  ['achievedFps', (r) => r.achieved?.achievedFps],
  ['displayedFps', (r) => r.achieved?.displayedFps],
  ['droppedFrames', (r) => r.achieved?.droppedFrames],
  ['droppedFramesPct', (r) => r.achieved?.droppedFramesPct],
  ['targetDeadlines', (r) => r.achieved?.targetDeadlines],
  ['framesPresented', (r) => r.achieved?.framesPresented],
  ['rafHz', (r) => r.achieved?.rafHz],
  ['rafIntervalMs_p95', (r) => r.achieved?.rafIntervalMs?.p95],
  ['deadlineLatenessMs_p50', (r) => r.achieved?.deadlineLatenessMs?.p50],
  ['deadlineLatenessMs_p95', (r) => r.achieved?.deadlineLatenessMs?.p95],
  ['decodeLatencyUs_p50', (r) => r.decodeLatency?.overall?.p50],
  ['decodeLatencyUs_p95', (r) => r.decodeLatency?.overall?.p95],
  ['decodeLatencyUs_p99', (r) => r.decodeLatency?.overall?.p99],
  ['decodeLatencyUs_max', (r) => r.decodeLatency?.overall?.max],
  ['bufferedAheadFrames_min', (r) => r.decodeLatency?.bufferedAheadFrames?.overall?.min],
  ['bufferedAheadFrames_p50', (r) => r.decodeLatency?.bufferedAheadFrames?.overall?.p50],
  ['evictedUndisplayed_total', (r) =>
    (r.decodeLatency?.evictedUndisplayed ?? []).reduce((a, b) => a + b, 0)],
  ['glUploadMs_p50', (r) => r.glComposite?.uploadMs?.p50],
  ['glUploadMs_p95', (r) => r.glComposite?.uploadMs?.p95],
  ['glDrawMs_p50', (r) => r.glComposite?.drawMs?.p50],
  ['glDrawMs_p95', (r) => r.glComposite?.drawMs?.p95],
  ['glCompositeMs_p50', (r) => r.glComposite?.totalMs?.p50],
  ['glCompositeMs_p95', (r) => r.glComposite?.totalMs?.p95],
  ['layersVerified', (r) => r.compositeCheck?.layersVerified],
  ['audioDriftMs', (r) => r.audio?.driftMs],
  ['audioStemRms_min_of_means', (r) =>
    r.audio?.perStemRms ? Math.min(...r.audio.perStemRms.map((s) => s.mean ?? 0)) : null],
  ['controls_bareRafHz', (r) => r.controls?.bareRafHz],
  ['controls_canvasClearOnlyRafHz', (r) => r.controls?.canvasClearOnlyRafHz],
  ['controls_fullCompositeNoDecodersRafHz', (r) => r.controls?.fullCompositeNoDecodersRafHz],
  ['controls_fourDecodersFlatOutRafHz', (r) => r.controls?.fourDecodersFlatOutNoCompositeRafHz],
  ['glCompositeSyncedMs_p50', (r) => r.glComposite?.totalSyncedMs?.p50],
  ['glCompositeSyncedMs_p95', (r) => r.glComposite?.totalSyncedMs?.p95],
  ['decodeDeliveredFpsPerLayer', (r) => r.decodeLatency?.throughput?.effectiveDecodeFpsPerLayer?.[0]],
  ['openMs', (r) => r.openMs],
  ['fetchMs', (r) => r.fetchMs],
];

const aggregate = { runs: runReports.length, crossRunMedianOfSameRunFigures: true, metrics: {} };
for (const [name, pick] of METRICS) aggregate.metrics[name] = crossRunMedian(pick);

const report = {
  harness: 'T-201 compositor budget',
  mode: MODE,
  ticket: 'T-201',
  fixture: {
    path: 'web/lib/editing/preview/spike/fixtures/playback/proxy-1080p-36s.mp4',
    bytes: proxyBytes,
    sha256: proxySha256,
  },
  gl: representative?.capabilities?.gl ?? null,
  browser: representative?.capabilities
    ? {
        userAgent: representative.capabilities.gl?.userAgent ?? null,
        timerResolutionUs: representative.capabilities.timerResolutionUs,
        webCodecs: representative.capabilities.webCodecs,
        decoderConfig: representative.capabilities.decoderConfig,
        hardwareConcurrency: representative.hardwareConcurrency ?? null,
      }
    : null,
  path: representative?.path ?? null,
  driver: representative?.driver ?? null,
  aggregate,
  representativeRun: representative,
  runs: runReports,
  errors: runReports.flatMap((r) => r.errors ?? []),
};

writeFileSync(OUT_PATH, JSON.stringify(report, null, 2));
console.log(`[driver] report written to ${OUT_PATH}`);

// ── Human-readable summary ──────────────────────────────────────────────────────────────
if (representative) {
  const L = (s) => console.log(s);
  const g = aggregate.metrics;
  const f = (v) => (v === null || v === undefined ? 'n/a' : String(v));
  L('');
  L(`════════ T-201 compositor budget — mode=${MODE} ════════`);
  L(`path              ${representative.path}`);
  L(`GL renderer       ${report.gl?.renderer ?? 'n/a'}`);
  L(`GL software?      ${report.gl?.isSoftware ? 'YES — software GL (SwiftShader)' : 'no'}`);
  L(`timer resolution  ${report.browser?.timerResolutionUs} µs`);
  if (MODE !== 'fallback') {
    L(`scenario          ${representative.scenario.videoLayers} video layers + ` +
      `${representative.scenario.audioTracks} audio tracks, ${representative.scenario.sourceResolution}, ` +
      `${representative.scenario.targetFps} fps, canvas ${representative.scenario.compositeWidth}x` +
      `${representative.scenario.compositeHeight}, measured ${representative.scenario.measuredWindowSec}s`);
    L(`pool              ${JSON.stringify(representative.pool)}`);
  }
  L(`runs              ${aggregate.runs} (each figure is the median of ${aggregate.runs} runs)`);
  if (representative.controls) {
    L('');
    L('controls (rAF Hz, ~2 s each, same page and flags):');
    L(`  bare page                                  ${representative.controls.bareRafHz}`);
    L(`  canvas attached, gl.clear() only           ${representative.controls.canvasClearOnlyRafHz}`);
    L(`  4-frame composite, decoders idle           ${representative.controls.fullCompositeNoDecodersRafHz}`);
    L(`  4 decoders flat out, no composite          ${representative.controls.fourDecodersFlatOutNoCompositeRafHz}`);
  }
  if (representative.compositeScaling) {
    L('');
    L('composite cost vs canvas area (decoders idle, 4 held frames re-composited):');
    L('  canvas      Mpx     ms/frame   ms/Mpx   meets 30 fps');
    for (const s of representative.compositeScaling) {
      L(
        `  ${String(s.width + 'x' + s.height).padEnd(10)}  ${String(s.megapixels).padEnd(6)}  ` +
          `${String(s.msPerFrame).padEnd(9)}  ${String(s.msPerMegapixel).padEnd(7)}  ${s.meetsFps30}`,
      );
    }
  }
  L('');
  const rows = [['metric', 'median across runs']];
  for (const [name] of METRICS) rows.push([name, f(g[name])]);
  const w = Math.max(...rows.map((r) => r[0].length)) + 2;
  for (const [i, r] of rows.entries()) {
    L(r[0].padEnd(w) + r[1]);
    if (i === 0) L('-'.repeat(w + 8));
  }
}

process.exit(exitCode);
