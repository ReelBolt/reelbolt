#!/usr/bin/env node
/**
 * Run page bandwidth under a throttled phone profile.
 *
 * Opens a run page in Chromium with a Pixel 7 viewport, throttles the network through the DevTools
 * protocol (default: 1.6 Mbit/s down, 750 kbit/s up, 150 ms round trip, "slow 4G"), lets it sit
 * for `--duration-ms` (default 2 minutes) with a video playing, and reports bytes on the wire and
 * request counts per kind of request. Bytes are `encodedDataLength` (what crossed the network,
 * after compression); a 304 counts as a request with only its headers.
 *
 * Credentials come from the environment (`REELBOLT_PERF_EMAIL` / `REELBOLT_PERF_PASSWORD`) and
 * never reach the output. Output goes to `--out` (default /tmp/reelbolt-perf-rpb/<label>), never the repo.
 *
 *   node e2e/perf/run-page-bandwidth.mjs --base http://localhost:28080 \
 *     --project <id> --workflow <id> --execution <id> --scenario app-finished --label before
 *
 * Scenarios (--scenario):
 *   app-finished  the app view of a workflow whose newest run finished; its video plays
 *   app-active    the same page while the newest run is Queued/Running (set by the caller in the
 *                 database of the throwaway stack); a finished run's video is streamed beside it
 *   run-detail    the full run page of one finished run, a step opened, its video playing
 */
import { chromium, devices } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

function parseArgs(argv) {
  const a = {
    base: 'http://localhost:28080', project: '', workflow: '', execution: '', videoStepResult: '',
    scenario: 'app-finished', label: 'run', durationMs: 120000, out: '',
    downKbps: 1600, upKbps: 750, rttMs: 150,
  };
  for (let i = 0; i < argv.length; i += 1) {
    const k = argv[i];
    const next = () => argv[++i];
    if (k === '--base') a.base = next().replace(/\/+$/, '');
    else if (k === '--project') a.project = next();
    else if (k === '--workflow') a.workflow = next();
    else if (k === '--execution') a.execution = next();
    else if (k === '--video-step-result') a.videoStepResult = next();
    else if (k === '--scenario') a.scenario = next();
    else if (k === '--label') a.label = next();
    else if (k === '--duration-ms') a.durationMs = Number(next());
    else if (k === '--out') a.out = next();
    else if (k === '--down-kbps') a.downKbps = Number(next());
    else if (k === '--up-kbps') a.upKbps = Number(next());
    else if (k === '--rtt-ms') a.rttMs = Number(next());
    else throw new Error(`unknown argument: ${k}`);
  }
  if (!a.out) a.out = path.join('/tmp/reelbolt-perf-rpb', a.label);
  return a;
}

/** A request URL reduced to its kind, so ids do not split one endpoint into many rows. */
export function classify(url) {
  if (url.startsWith('data:')) return 'STATIC data-uri';
  const u = new URL(url, 'http://x');
  const p = u.pathname
    .replace(/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi, ':id');
  if (/\/outputs\/:id\/download$/.test(p)) return 'VIDEO  ' + p;
  if (/\.(js|css|woff2?|png|svg|ico)$/.test(p) || p.startsWith('/app/_next/')) return 'STATIC ' + p.replace(/\/_next\/static\/[^/]+\/.+/, '/_next/static/*');
  return 'API    ' + p;
}

async function login(page, base, email, password) {
  await page.goto(`${base}/app/login`, { waitUntil: 'domcontentloaded' });
  await page.getByLabel('Email', { exact: true }).fill(email);
  await page.locator('input[type="password"]').fill(password);
  const resp = page.waitForResponse((r) => r.url().includes('/api/v1/auth/token'), { timeout: 60000 }).catch(() => null);
  await page.getByRole('button', { name: /sign in/i }).click();
  const r = await resp;
  if (!r || r.status() !== 200) throw new Error(`login failed: ${r ? r.status() : 'no response'}`);
  await page.waitForURL((u) => !u.pathname.endsWith('/login'), { timeout: 30000 }).catch(() => {});
}

async function main() {
  const args = parseArgs(process.argv.slice(2));
  const email = process.env.REELBOLT_PERF_EMAIL;
  const password = process.env.REELBOLT_PERF_PASSWORD;
  if (!email || !password) throw new Error('set REELBOLT_PERF_EMAIL and REELBOLT_PERF_PASSWORD');
  fs.mkdirSync(args.out, { recursive: true, mode: 0o700 });

  const browser = await chromium.launch();
  const context = await browser.newContext({ ...devices['Pixel 7'] });
  const page = await context.newPage();
  await login(page, args.base, email, password);

  const cdp = await context.newCDPSession(page);
  await cdp.send('Network.enable');
  // Bypass nothing: the browser cache stays on, because conditional GET is the point.
  const rows = new Map(); // requestId -> { kind, status, bytes }
  cdp.on('Network.requestWillBeSent', (e) => {
    rows.set(e.requestId, { kind: classify(e.request.url), status: 0, bytes: 0 });
  });
  cdp.on('Network.responseReceived', (e) => {
    const r = rows.get(e.requestId);
    if (r) r.status = e.response.status;
  });
  // The page-level status reports a revalidated response as 200; the wire status is in the extra info.
  cdp.on('Network.responseReceivedExtraInfo', (e) => {
    const r = rows.get(e.requestId);
    if (r) r.wire = e.statusCode;
  });
  cdp.on('Network.dataReceived', (e) => {
    const r = rows.get(e.requestId);
    if (r) r.bytes += e.encodedDataLength;
  });
  cdp.on('Network.loadingFinished', (e) => {
    const r = rows.get(e.requestId);
    if (r) r.bytes = Math.max(r.bytes, e.encodedDataLength);
  });

  const t0 = Date.now();
  await cdp.send('Network.emulateNetworkConditions', {
    offline: false,
    latency: args.rttMs,
    downloadThroughput: (args.downKbps * 1000) / 8,
    uploadThroughput: (args.upKbps * 1000) / 8,
  });

  const { base, project, workflow, execution } = args;
  const videoUrl = args.videoStepResult
    ? `/api/v1/projects/${project}/outputs/${args.videoStepResult}/download`
    : '';
  if (args.scenario === 'run-detail') {
    await page.goto(`${base}/app/projects/${project}/workflows/${workflow}/executions/${execution}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(8000);
    // Open the first step, as a person reading the run would.
    await page.locator('.react-flow__node').first().click({ timeout: 15000 }).catch(() => {});
  } else {
    await page.goto(`${base}/app/projects/${project}/workflows/${workflow}`, { waitUntil: 'domcontentloaded' });
    await page.waitForTimeout(8000);
  }
  // A video playing beside the page is the competing load. On the finished app page the page's own
  // player is used; otherwise a muted element streams the named output.
  const own = page.locator('video').first();
  if (await own.count()) {
    await own.evaluate((v) => { v.muted = true; return v.play().catch(() => {}); });
  } else if (videoUrl) {
    await page.evaluate((src) => {
      const v = document.createElement('video');
      v.muted = true; v.playsInline = true; v.src = src;
      v.style.cssText = 'position:fixed;right:0;bottom:0;width:80px;height:80px;z-index:99999';
      document.body.appendChild(v);
      return v.play().catch(() => {});
    }, videoUrl);
  }
  const elapsed = Date.now() - t0;
  await page.waitForTimeout(Math.max(0, args.durationMs - elapsed));

  const byKind = new Map();
  for (const r of rows.values()) {
    const e = byKind.get(r.kind) ?? { kind: r.kind, requests: 0, bytes: 0, status304: 0, status200: 0, status206: 0 };
    e.requests += 1; e.bytes += r.bytes;
    if (r.wire === 304 || r.status === 304) e.status304 += 1;
    if (r.status === 200) e.status200 += 1;
    if (r.status === 206) e.status206 += 1;
    byKind.set(r.kind, e);
  }
  const table = [...byKind.values()].sort((x, y) => y.bytes - x.bytes);
  const sum = (pred) => table.filter(pred).reduce((s, e) => ({ requests: s.requests + e.requests, bytes: s.bytes + e.bytes }), { requests: 0, bytes: 0 });
  const summary = {
    label: args.label, scenario: args.scenario, durationMs: args.durationMs,
    profile: { downKbps: args.downKbps, upKbps: args.upKbps, rttMs: args.rttMs, device: 'Pixel 7' },
    total: sum(() => true),
    api: sum((e) => e.kind.startsWith('API')),
    video: sum((e) => e.kind.startsWith('VIDEO')),
    static: sum((e) => e.kind.startsWith('STATIC')),
    rows: table,
  };
  fs.writeFileSync(path.join(args.out, `${args.scenario}.json`), JSON.stringify(summary, null, 2), { mode: 0o600 });
  console.log(`\n== ${args.label} / ${args.scenario} (${args.durationMs / 1000}s, ${args.downKbps} kbit/s down, ${args.rttMs} ms) ==`);
  for (const k of ['total', 'api', 'video', 'static']) {
    console.log(`${k.padEnd(7)} ${String(summary[k].requests).padStart(5)} requests  ${(summary[k].bytes / 1024).toFixed(0).padStart(8)} KiB`);
  }
  console.log('API rows (requests, KiB, 304s):');
  for (const e of table.filter((r) => r.kind.startsWith('API'))) {
    console.log(`  ${String(e.requests).padStart(4)} ${(e.bytes / 1024).toFixed(1).padStart(8)} KiB  304x${e.status304}  ${e.kind.slice(7)}`);
  }
  await browser.close();
}

if (import.meta.url === `file://${process.argv[1]}`) {
  main().catch((err) => { console.error(err); process.exit(1); });
}
