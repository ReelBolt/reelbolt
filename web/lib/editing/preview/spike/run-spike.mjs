/**
 * T-200 — Playwright driver.  REMOVABLE (see README.md).
 *
 * Starts `serve.mjs` on a free port, opens the harness page in real Chromium, calls
 * `window.runSpike()`, and writes the raw JSON report plus a human-readable summary. Runs
 * the whole browser session `--runs N` times (default 3) and reports the run-to-run MEDIAN
 * of each metric, because the cold-open numbers move by tens of percent between runs.
 *
 * Playwright is resolved from the `e2e/` install — the ticket forbids adding it to
 * `web/package.json` — and `e2e/**` is never written to. Both facts are load-bearing, so
 * the resolution is by absolute path through `createRequire`.
 *
 * Usage (from `web/`):
 *   node lib/editing/preview/spike/run-spike.mjs [--headed] [--runs N] [--out <path>]
 */
import { createRequire } from 'node:module';
import { spawn } from 'node:child_process';
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, resolve } from 'node:path';

const HERE = dirname(fileURLToPath(import.meta.url));
const WEB_ROOT = resolve(HERE, '..', '..', '..', '..');
const REPO_ROOT = resolve(WEB_ROOT, '..');

const argv = process.argv.slice(2);
const headed = argv.includes('--headed');
const outIdx = argv.indexOf('--out');
const runsIdx = argv.indexOf('--runs');
const RUNS = runsIdx >= 0 ? Math.max(1, Number(argv[runsIdx + 1])) : 3;
const OUT_PATH =
  outIdx >= 0
    ? resolve(argv[outIdx + 1])
    : resolve(HERE, 'measurements.t200.json');

const LAUNCH_ARGS = [
  '--no-sandbox',
  '--ozone-platform=headless',
  '--use-gl=angle',
  '--use-angle=swiftshader',
  '--enable-unsafe-swiftshader',
  // LOAD-BEARING. There is no /dev/dri and no X server here, and without this flag ANGLE
  // picks DisplayVkXcb, `xcb_connect()` fails with error 1, SwANGLE then returns
  // EGL_NOT_INITIALIZED, the GPU process exits, and `canvas.getContext('webgl2')` is null
  // in every other flag combination tried (angle=swiftshader, angle=vulkan,
  // ozone=headless, in-process-gpu, VK_ICD_FILENAMES, ...). With it, WebGL 2.0 comes up on
  // SwiftShader. See the report's `gl` block.
  '--disable-gpu-sandbox',
];

// ── Resolve Playwright from the e2e package, never from web/node_modules ────────────────
const e2eRequire = createRequire(resolve(REPO_ROOT, 'e2e', 'package.json'));
const { chromium } = e2eRequire('playwright');

// ── Start the static server on a free port and read back its bound origin ───────────────
const server = spawn(process.execPath, [resolve(HERE, 'serve.mjs'), '0'], {
  cwd: WEB_ROOT,
  env: { ...process.env, NODE_ENV: undefined },
  stdio: ['ignore', 'pipe', 'inherit'],
});

const origin = await new Promise((resolveOrigin, reject) => {
  let buf = '';
  const timer = setTimeout(
    () => reject(new Error('serve.mjs did not become ready in 15 s')),
    15_000,
  );
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

const fixturePath = resolve(HERE, 'fixtures', 'proxy-1080p-12s.mp4');
const fixtureSha256 = createHash('sha256').update(readFileSync(fixturePath)).digest('hex');
const fixtureBytes = readFileSync(fixturePath).byteLength;
console.log(`[driver] fixture sha256 ${fixtureSha256} (${fixtureBytes} bytes)`);
console.log(`[driver] runs: ${RUNS}`);

// ── One full browser session ────────────────────────────────────────────────────────────
async function runOnce(runIndex) {
  const browser = await chromium.launch({
    headless: !headed,
    // `channel: 'chromium'` selects the full Chromium build rather than Playwright's
    // `chromium_headless_shell`; the full build is the one shipping ANGLE together with
    // the bundled SwiftShader Vulkan ICD (`libvk_swiftshader.so`, `vk_swiftshader_icd.json`).
    channel: 'chromium',
    args: LAUNCH_ARGS,
  });
  try {
    const page = await (await browser.newContext()).newPage();
    const console_ = [];

    // Echoed live as well as collected: when the harness fails to load, the browser console
    // is the only place the real reason (a module-resolution error, a bare `node:` import, a
    // missing named export) is stated.
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

    await page.goto(`${origin}/lib/editing/preview/spike/harness.html`, { waitUntil: 'load' });
    await page.waitForFunction(() => window.__spikeReady === true, null, { timeout: 30_000 });

    const report = await page.evaluate(() => window.runSpike());
    report.browser.playwrightBrowserVersion = browser.version();
    report.driver = {
      runIndex,
      playwright: e2eRequire('playwright/package.json').version,
      playwrightFrom: 'e2e/node_modules (resolved by absolute path; web/package.json untouched)',
      launchedArgs: LAUNCH_ARGS,
      channel: 'chromium',
      headed,
      consoleMessages: console_,
    };
    return report;
  } finally {
    await browser.close();
  }
}

// ── Run N sessions, then aggregate ──────────────────────────────────────────────────────
const runReports = [];
let exitCode = 0;
try {
  for (let i = 0; i < RUNS; i++) {
    const r = await runOnce(i);
    runReports.push(r);
    console.log(
      `[driver] run ${i + 1}/${RUNS}: ` +
        `mediabunny seek ${r.demuxers.mediabunny?.randomSeek.batchMeanUs} µs, ` +
        `mp4box seek ${r.demuxers.mp4box?.randomSeek.batchMeanUs} µs`,
    );
    if (r.errors.length > 0) {
      console.error('[driver] harness reported errors:');
      for (const e of r.errors) console.error('  ' + e.split('\n')[0]);
      exitCode = 2;
    }
  }
} catch (e) {
  console.error('[driver] FAILED:', e);
  exitCode = 1;
} finally {
  server.kill('SIGTERM');
}

const representative = runReports[0];

/** Median across runs of one number picked out of each run. */
function crossRunMedian(pick) {
  const xs = runReports.map(pick).filter((v) => typeof v === 'number' && Number.isFinite(v));
  if (xs.length === 0) return null;
  const s = [...xs].sort((a, b) => a - b);
  const mid = s.length >> 1;
  return +(s.length % 2 ? s[mid] : (s[mid - 1] + s[mid]) / 2).toFixed(3);
}

/**
 * Per-metric cross-run median. `firstTouch.medianUs` and friends are already within-run
 * medians, so the number below is a median of medians — stated plainly so nobody reads it
 * as a single-sample figure.
 */
const DEMUXERS = ['mediabunny', 'mp4box'];
const METRICS = [
  ['headerParseMs', (d) => d.headerParseMs],
  ['sampleTableMs', (d) => d.sampleTableMs],
  ['durationComputeMs', (d) => d.durationComputeMs],
  ['parseMs', (d) => d.parseMs],
  ['packetsParsed', (d) => d.packetsParsed],
  ['enumerateMs', (d) => d.enumerateMs],
  ['firstKeyframeSeek_medianUs', (d) => d.firstKeyframeSeek.medianUs],
  ['randomSeek_firstTouch_medianUs', (d) => d.randomSeek.firstTouch.medianUs],
  ['randomSeek_firstTouch_p95Us', (d) => d.randomSeek.firstTouch.p95Us],
  ['randomSeek_repeat_medianUs', (d) => d.randomSeek.repeat.medianUs],
  ['randomSeek_batchMeanUs', (d) => d.randomSeek.batchMeanUs],
  ['randomSeek_plannerOnly_medianUs', (d) => d.randomSeek.planOnly?.medianUs],
  ['openToFirstSample_medianUs', (d) => d.openToFirstSample?.medianUs],
  ['openToFirstSampleFast_medianUs', (d) => d.openToFirstSampleFast?.medianUs],
  ['openToFirstDecodedFrame_medianUs', (d) => d.openToFirstDecodedFrame?.medianUs],
  ['openToFirstDecodedFrameFast_medianUs', (d) => d.openToFirstDecodedFrameFast?.medianUs],
  ['seekIntegrityFailures', (d) => d.randomSeek.integrityFailures],
];

const aggregate = { runs: runReports.length, metrics: {}, crossRunMedianOfMedians: true };
for (const [name, pick] of METRICS) {
  aggregate.metrics[name] = {};
  for (const dem of DEMUXERS) {
    aggregate.metrics[name][dem] = crossRunMedian((r) => {
      const d = r.demuxers[dem];
      return d ? pick(d) : null;
    });
  }
}

const report = {
  harness: representative?.harness ?? 'T-200 demuxer spike',
  fixture: {
    path: 'web/lib/editing/preview/spike/fixtures/proxy-1080p-12s.mp4',
    url: representative?.fixture.url ?? null,
    bytes: fixtureBytes,
    sha256: fixtureSha256,
  },
  browser: representative
    ? {
        ...representative.browser,
        playwrightBrowserVersion: representative.browser.playwrightBrowserVersion,
      }
    : null,
  gl: representative?.gl ?? null,
  driver: representative?.driver ?? null,
  aggregate,
  /** Unaggregated detail from run 1, for the seek-by-seek evidence. */
  representativeRun: representative,
  /** Every run, unmodified. */
  runs: runReports,
  errors: runReports.flatMap((r) => r.errors ?? []),
};

writeFileSync(OUT_PATH, JSON.stringify(report, null, 2));
console.log(`[driver] report written to ${OUT_PATH}`);

// ── Human-readable summary ─────────────────────────────────────────────────────────────
if (representative) {
  const line = (s) => console.log(s);
  const g = aggregate.metrics;
  const fmt = (v) => (v === null || v === undefined ? 'n/a' : String(v));
  line('');
  line('════════ T-200 demuxer spike ════════');
  line(
    `Chromium        ${representative.browser.playwrightBrowserVersion} ` +
      `(${representative.browser.chromeVersion} from UA)`,
  );
  line(`GL renderer     ${report.gl.renderer ?? report.gl.reason}`);
  line(
    `GL software?    ${
      report.gl.available ? (report.gl.isSoftware ? 'YES — software GL (SwiftShader)' : 'no') : 'n/a (no context)'
    }`,
  );
  line(`GL version      ${report.gl.version ?? 'n/a'}`);
  line(`Timer res       ${representative.browser.timerResolutionUs} µs`);
  line(
    `WebCodecs       ${
      representative.browser.webCodecsVideoDecoder ? 'VideoDecoder present' : 'VideoDecoder MISSING'
    }`,
  );
  line(`Fixture         ${fixtureBytes} bytes sha256 ${fixtureSha256}`);
  line(`Runs            ${aggregate.runs} (each figure below is the median of ${aggregate.runs} runs)`);
  line('');

  const rows = [['metric', 'mediabunny', 'mp4box']];
  for (const [name] of METRICS) {
    rows.push([name, fmt(g[name].mediabunny), fmt(g[name].mp4box)]);
  }
  const widths = [0, 1, 2].map((i) => Math.max(...rows.map((r) => r[i].length)) + 2);
  for (const [i, r] of rows.entries()) {
    line(r.map((c, j) => c.padEnd(widths[j])).join(''));
    if (i === 0) line(widths.map((w) => '-'.repeat(w)).join(''));
  }
}

process.exit(exitCode);
