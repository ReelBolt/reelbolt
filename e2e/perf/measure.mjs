#!/usr/bin/env node
/**
 * Webapp latency measurement — drives the real dashboard in Chromium and records
 * a HAR plus a per-request Chrome DevTools (CDP) network log.
 *
 * Why both: the HAR is the portable artefact (open it in any waterfall viewer);
 * the CDP log carries fields the HAR does not — the negotiated protocol
 * (`h3`/`h2`), per-request timing phases and the bytes actually received per
 * stream, which is what distinguishes "slow" from "queued".
 *
 * Credentials never enter this repo: they come from the environment
 * (`REELBOLT_PERF_EMAIL` / `REELBOLT_PERF_PASSWORD`) or, for local runs, from
 * the git-ignored `.local/perf-credentials.md`.
 *
 * Output goes to `--out` (default `/tmp/reelbolt-perf/<utc-stamp>`), never into
 * the repository — HAR files carry cookies and full response bodies.
 *
 *   node e2e/perf/measure.mjs --scenario dashboard
 *   node e2e/perf/measure.mjs --scenario render --repeat 3
 *   node e2e/perf/measure.mjs --base https://reelbolt.ai --out /tmp/run1
 *
 * Scenarios:
 *   dashboard  log in, open the dashboard
 *   project    open one project's page (Files tab) and let it settle
 *   video      open a project file video and play it for --video-wait-ms
 *   render     open a project's Renders tab (the output player) and play it
 *   full       dashboard → project → video → render
 *
 * A HAR is written per repetition (`run-<n>.har`), plus `run-<n>.requests.json`
 * (the CDP log + scenario summary) and `summary.json`. Analyse them with
 * `node e2e/perf/analyze.mjs <out-dir>`.
 */
import { chromium, firefox } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { redactUrl, sanitizeHar, assertSafeOutDir, writePrivate } from './sanitize.mjs';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '../..');

function parseArgs(argv) {
  const args = {
    base: process.env.REELBOLT_PERF_BASE_URL || 'https://reelbolt.ai',
    out: '',
    scenario: 'full',
    repeat: 1,
    project: process.env.REELBOLT_PERF_PROJECT || '',
    browser: 'chromium',
    headed: false,
    videoWaitMs: 15000,
    settleMs: 6000,
    authState: '',
    keepOpen: false,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const a = argv[i];
    const next = () => argv[++i];
    if (a === '--base') args.base = next();
    else if (a === '--out') args.out = next();
    else if (a === '--scenario') args.scenario = next();
    else if (a === '--repeat') args.repeat = Number(next());
    else if (a === '--project') args.project = next();
    else if (a === '--browser') args.browser = next();
    else if (a === '--headed') args.headed = true;
    else if (a === '--video-wait-ms') args.videoWaitMs = Number(next());
    else if (a === '--settle-ms') args.settleMs = Number(next());
    else if (a === '--auth') args.authState = next();
    else if (a === '--keep-open') args.keepOpen = true;
    else if (a === '--help' || a === '-h') { printHelp(); process.exit(0); }
    else throw new Error(`unknown argument: ${a}`);
  }
  args.base = args.base.replace(/\/+$/, '');
  if (!args.out) {
    const stamp = new Date().toISOString().replace(/[:.]/g, '-');
    args.out = path.join('/tmp/reelbolt-perf', stamp);
  }
  return args;
}

function printHelp() {
  console.log(`Usage: node e2e/perf/measure.mjs [options]

  --base <url>        Origin under test (default https://reelbolt.ai)
  --out <dir>         Output directory (default /tmp/reelbolt-perf/<stamp>)
  --scenario <name>   dashboard | project | video | render | full (default full)
  --repeat <n>        Run the scenario n times in fresh contexts (default 1)
  --project <id>      Project id (default: the account's first project)
  --browser <name>    chromium | firefox (default chromium)
  --auth <file>       Reuse a saved storageState instead of logging in again
  --headed            Show the browser
  --video-wait-ms <n> How long to watch playback (default 15000)
  --settle-ms <n>     Quiet time after each navigation (default 6000)

Credentials: REELBOLT_PERF_EMAIL / REELBOLT_PERF_PASSWORD, or the git-ignored
.local/perf-credentials.md.`);
}

function readCredentials() {
  let email = process.env.REELBOLT_PERF_EMAIL || '';
  let password = process.env.REELBOLT_PERF_PASSWORD || '';
  if (email && password) return { email, password };
  const file = path.join(REPO, '.local/perf-credentials.md');
  if (fs.existsSync(file)) {
    const text = fs.readFileSync(file, 'utf8');
    const e = text.match(/Account:\s*(\S+)/);
    const p = text.match(/Password:\s*(\S+)/);
    if (e && !email) email = e[1];
    if (p && !password) password = p[1];
  }
  if (!email || !password) {
    throw new Error('No credentials: set REELBOLT_PERF_EMAIL/REELBOLT_PERF_PASSWORD, or write .local/perf-credentials.md');
  }
  return { email, password };
}

/**
 * Log in ONCE, in a context that records nothing, and save the browser state. The recording
 * context then starts from that state, so the login request — whose body is the account's
 * plaintext password — never enters the HAR.
 */
async function ensureAuthState(args, creds, statePath) {
  if (fs.existsSync(statePath)) {
    try { fs.chmodSync(statePath, 0o600); } catch { /* best effort */ }
    return;
  }
  const browserType = args.browser === 'firefox' ? firefox : chromium;
  const browser = await browserType.launch({ headless: !args.headed });
  try {
    const context = await browser.newContext();
    const page = await context.newPage();
    await loginOnce(page, args.base, creds, statePath);
    await context.close();
  } finally {
    await browser.close();
  }
  try { fs.chmodSync(statePath, 0o600); } catch { /* best effort */ }
}

/** CDP network recorder: one row per request, merged as events arrive. */
function attachCdpRecorder(page) {
  const rows = new Map();
  const order = [];
  let session;
  const ready = (async () => {
    session = await page.context().newCDPSession(page);
    await session.send('Network.enable', {
      maxTotalBufferSize: 64 * 1024 * 1024,
      maxResourceBufferSize: 8 * 1024 * 1024,
    });
    session.on('Network.requestWillBeSent', (e) => {
      const row = {
        requestId: e.requestId,
        url: e.request.url,
        method: e.request.method,
        resourceType: e.type,
        startedAt: e.timestamp,
        wallTime: e.wallTime,
        bytes: 0,
      };
      rows.set(e.requestId, row);
      order.push(e.requestId);
    });
    session.on('Network.requestServedFromCache', (e) => {
      const row = rows.get(e.requestId);
      if (row) row.fromCache = true;
    });
    session.on('Network.responseReceived', (e) => {
      const row = rows.get(e.requestId);
      if (!row) return;
      row.status = e.response.status;
      row.protocol = e.response.protocol || '';
      row.mimeType = e.response.mimeType || '';
      row.timing = e.response.timing;
      row.responseAt = e.timestamp;
    });
    session.on('Network.dataReceived', (e) => {
      const row = rows.get(e.requestId);
      if (row) row.bytes += e.dataLength || 0;
    });
    session.on('Network.loadingFinished', (e) => {
      const row = rows.get(e.requestId);
      if (!row) return;
      row.finishedAt = e.timestamp;
      row.encodedBodyLength = e.encodedDataLength;
      row.durationMs = Math.round((e.timestamp - row.startedAt) * 1000);
    });
    session.on('Network.loadingFailed', (e) => {
      const row = rows.get(e.requestId);
      if (!row) return;
      row.failed = e.errorText;
      row.blockedReason = e.blockedReason || '';
      row.finishedAt = e.timestamp;
      row.durationMs = Math.round((e.timestamp - row.startedAt) * 1000);
    });
  })();
  return {
    ready,
    async snapshot() {
      await ready;
      return order.map((id) => rows.get(id)).filter(Boolean);
    },
  };
}

/**
 * Log in and keep the session in a storageState file. Running the scenarios
 * repeatedly must not hammer `/api/v1/auth/token`: nginx rate-limits that route
 * at 5 requests/minute per address, and a measurement tool that gets a 429 has
 * measured the limiter instead of the app.
 */
export async function loginOnce(page, base, creds, statePath) {
  await page.goto(`${base}/app/login`, { waitUntil: 'domcontentloaded' });
  await page.getByLabel('Email', { exact: true }).fill(creds.email);
  // Mantine's PasswordInput renders a visibility toggle whose accessible name
  // also contains "Password"; target the input itself.
  await page.locator('input[type="password"]').fill(creds.password);
  const responsePromise = page
    .waitForResponse((r) => r.url().includes('/api/v1/auth/token'), { timeout: 60000 })
    .catch(() => null);
  await page.getByRole('button', { name: /sign in/i }).click();
  const response = await responsePromise;
  if (!response || response.status() !== 200) {
    throw new Error(`login failed: ${response ? response.status() : 'no response'}`);
  }
  await page.context().storageState({ path: statePath });
  try { fs.chmodSync(statePath, 0o600); } catch { /* best effort */ }
}

async function settle(page, ms) {
  // Not networkidle: the dashboard keeps a live SSE stream open forever, so
  // networkidle never fires. A fixed quiet period keeps runs comparable.
  await page.waitForTimeout(ms);
}

async function apiJson(page, url) {
  const res = await page.request.get(url);
  if (!res.ok()) throw new Error(`GET ${url.replace(/^https?:\/\/[^/]+/, '')} -> ${res.status()}`);
  return res.json();
}

async function firstProjectId(page, base) {
  const body = await apiJson(page, `${base}/api/v1/projects`);
  const items = Array.isArray(body) ? body : body.items || body.projects || [];
  if (!items.length) throw new Error('account has no projects');
  return items[0].id;
}

/** Fetch a real project video file from page context, measuring achieved throughput. */
async function measureDownload(page, projectId, label) {
  return page.evaluate(async ({ projectId, label }) => {
    const list = await fetch(`/api/v1/projects/${projectId}/files`).then((r) => r.json());
    const files = Array.isArray(list) ? list : list.items || list.files || [];
    const video = files.find((f) => (f.mimeType || '').startsWith('video/') && (f.sizeBytes || 0) > 10_000_000)
      || files.find((f) => (f.mimeType || '').startsWith('video/'));
    if (!video) return { label, error: 'no video file' };
    const t0 = performance.now();
    let firstByte = null;
    let bytes = 0;
    const res = await fetch(`/api/v1/projects/${projectId}/files/${video.id}/download`, { cache: 'no-store' });
    const reader = res.body.getReader();
    for (;;) {
      const { done, value } = await reader.read();
      if (done) break;
      if (firstByte === null) firstByte = performance.now() - t0;
      bytes += value.byteLength;
    }
    const total = performance.now() - t0;
    return {
      label,
      name: video.originalFileName,
      size: video.sizeBytes,
      status: res.status,
      bytes,
      totalMs: Math.round(total),
      firstByteMs: firstByte === null ? null : Math.round(firstByte),
      kbps: total > 0 ? Math.round((bytes / 1024) / (total / 1000)) : 0,
    };
  }, { projectId, label });
}

async function watchVideo(page, ms) {
  const video = page.locator('video:visible').first();
  await video.waitFor({ state: 'visible', timeout: 30000 });
  await video.evaluate(async (v) => { try { await v.play(); } catch { /* autoplay policy */ } });
  const samples = [];
  const step = 4000;
  for (let waited = step; waited <= ms; waited += step) {
    await page.waitForTimeout(step);
    samples.push({
      atMs: waited,
      ...(await video.evaluate((v) => ({
        currentTime: +v.currentTime.toFixed(2),
        readyState: v.readyState,
        networkState: v.networkState,
        bufferedEnd: v.buffered.length ? +v.buffered.end(v.buffered.length - 1).toFixed(2) : 0,
        error: v.error ? { code: v.error.code, message: v.error.message } : null,
      })).catch(() => null)),
    });
  }
  return samples;
}

/** Select a Files-tab scope chip ("All 42") and kind chip ("Video"). */
async function filterFilesToVideos(page) {
  const scope = page.locator('label').filter({ hasText: /^All \d+/ }).first();
  if (await scope.count()) {
    await scope.click();
    await page.waitForTimeout(1200);
  }
  const kind = page.getByRole('button', { name: 'Video', exact: true }).first();
  if (await kind.count()) {
    await kind.click();
    await page.waitForTimeout(1200);
  }
}

async function runOnce(args, runIndex, outDir, statePath) {
  const browserType = args.browser === 'firefox' ? firefox : chromium;
  const launchArgs = args.browser === 'firefox' ? {} : {
    args: ['--enable-quic', '--quic-version=h3'],
  };
  const browser = await browserType.launch({ headless: !args.headed, ...launchArgs });
  const context = await browser.newContext({
    viewport: { width: 1440, height: 900 },
    recordHar: { path: path.join(outDir, `run-${runIndex}.har`), mode: 'minimal' },
    storageState: statePath,
  });
  const page = await context.newPage();
  const recorder = attachCdpRecorder(page);

  const marks = [];
  const mark = (name) => marks.push({ name, at: Date.now() });
  const summary = { run: runIndex, base: args.base, scenario: args.scenario, steps: [], downloads: [] };
  const step = async (name, fn) => {
    const t0 = Date.now();
    try {
      const value = await fn();
      summary.steps.push({ name, ms: Date.now() - t0, ok: true, value });
      mark(`step:${name}:end`);
      return value;
    } catch (e) {
      summary.steps.push({ name, ms: Date.now() - t0, ok: false, error: redactUrl(String(e).slice(0, 500)) });
      mark(`step:${name}:failed`);
      throw e;
    }
  };

  try {
    const projectId = args.project || await step('resolve-project', async () => firstProjectId(page, args.base));
    summary.projectId = projectId;

    const wants = (s) => args.scenario === 'full' || args.scenario === s;

    if (wants('dashboard')) {
      await step('dashboard', async () => {
        await page.goto(`${args.base}/app/dashboard`, { waitUntil: 'domcontentloaded' });
        await settle(page, args.settleMs);
      });
    }

    if (wants('project') || wants('video') || wants('render')) {
      await step('project-page', async () => {
        await page.goto(`${args.base}/app/projects/${projectId}`, { waitUntil: 'domcontentloaded' });
        await settle(page, args.settleMs);
      });
    }

    if (wants('video')) {
      await step('files-tab', async () => {
        await page.goto(`${args.base}/app/projects/${projectId}?tab=files`, { waitUntil: 'domcontentloaded' });
        await settle(page, args.settleMs);
        await filterFilesToVideos(page);
      });
      const rows = page.locator('[role=tabpanel]:visible [role=listitem]');
      const count = await rows.count();
      if (!count) throw new Error('no video rows in the Files tab');
      await step('open-video', async () => {
        await rows.first().click();
        await page.locator('video:visible').first().waitFor({ state: 'visible', timeout: 30000 });
        await page.waitForTimeout(1500);
      });
      summary.downloads.push(await step('download-idle', () => measureDownload(page, projectId, 'idle')));
      summary.video = await step('play-video', () => watchVideo(page, args.videoWaitMs));
      summary.downloads.push(await step('download-while-playing', () => measureDownload(page, projectId, 'while-playing')));
    }

    if (wants('render')) {
      await step('renders-tab', async () => {
        await page.goto(`${args.base}/app/projects/${projectId}?tab=renders`, { waitUntil: 'domcontentloaded' });
        await settle(page, args.settleMs);
      });
      const videos = await page.locator('video:visible').count();
      if (videos) {
        summary.renderVideo = await step('play-render', () => watchVideo(page, Math.min(args.videoWaitMs, 12000)));
      } else {
        summary.renderVideo = [];
      }
    }

    summary.connection = await page.evaluate(() => ({
      navigationProtocol: performance.getEntriesByType('navigation')[0]?.nextHopProtocol || '',
      resourceProtocols: [...new Set(performance.getEntriesByType('resource').map((r) => r.nextHopProtocol))],
      resourceCount: performance.getEntriesByType('resource').length,
      navigation: (() => {
        const n = performance.getEntriesByType('navigation')[0];
        if (!n) return null;
        return {
          ttfb: Math.round(n.responseStart),
          domContentLoaded: Math.round(n.domContentLoadedEventEnd),
          load: Math.round(n.loadEventEnd),
          transferSize: n.transferSize,
          encodedBodySize: n.encodedBodySize,
        };
      })(),
    }));
  } finally {
    const requests = (await recorder.snapshot().catch(() => []))
      .map((r) => ({ ...r, url: redactUrl(r.url) }));
    writePrivate(path.join(outDir, `run-${runIndex}.requests.json`), JSON.stringify({
      base: args.base,
      scenario: args.scenario,
      marks,
      summary,
      requests,
    }, null, 2));

    // recordHar is flushed when the context closes; sanitize it before it can be copied anywhere.
    await context.close();
    sanitizeHar(path.join(outDir, `run-${runIndex}.har`));
    if (args.keepOpen) {
      console.log('--keep-open: leaving the browser running; press Ctrl+C to exit');
      await new Promise(() => {});
    }
    await browser.close();
  }

  return summary;
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const creds = readCredentials();
  assertSafeOutDir(args.out, REPO);
  fs.mkdirSync(args.out, { recursive: true, mode: 0o700 });
  try { fs.chmodSync(args.out, 0o700); } catch { /* best effort */ }
  const statePath = args.authState || path.join(args.out, 'state.json');
  assertSafeOutDir(statePath, REPO);
  await ensureAuthState(args, creds, statePath);
  const all = [];
  for (let i = 1; i <= args.repeat; i += 1) {
    console.log(`run ${i}/${args.repeat} — ${args.scenario} against ${args.base}`);
    const summary = await runOnce(args, i, args.out, statePath);
    all.push(summary);
    for (const s of summary.steps) {
      console.log(`  ${s.ok ? 'ok  ' : 'FAIL'} ${s.name} ${s.ms}ms${s.error ? ' — ' + s.error : ''}`);
    }
    for (const d of summary.downloads) {
      console.log(`  download ${d.label}: ${d.kbps ?? '?'} KB/s (${d.bytes ?? '?'} B in ${d.totalMs ?? '?'} ms, ttfb ${d.firstByteMs ?? '?'} ms)`);
    }
    if (summary.video?.length) {
      const last = summary.video[summary.video.length - 1];
      console.log(`  video after ${last.atMs} ms: currentTime=${last.currentTime}s buffered=${last.bufferedEnd}s readyState=${last.readyState}${last.error ? ' error=' + last.error.code : ''}`);
    }
  }
  writePrivate(path.join(args.out, 'summary.json'), JSON.stringify(all, null, 2));
  console.log(`\nartefacts: ${args.out}`);
  console.log(`analyse:  node e2e/perf/analyze.mjs ${args.out}`);
}

main().catch((e) => { console.error(e); process.exit(1); });
