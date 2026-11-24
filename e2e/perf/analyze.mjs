#!/usr/bin/env node
/**
 * Read the artefacts `measure.mjs` wrote and print the waterfall summary that
 * matters for a latency investigation:
 *
 *   - request count and bytes by endpoint (a page that fires 60 requests to
 *     load one screen is the finding, not the footnote);
 *   - negotiated protocol per request (h3 vs h2) and the navigation protocol;
 *   - time to first byte, both as a percentile distribution and per endpoint;
 *   - the slowest requests, including ones that never finished (long-lived
 *     streams are expected for SSE; every other unfinished request is not);
 *   - polled endpoints: the same path requested more than twice in one run;
 *   - achieved throughput for each download the scenario measured.
 *
 *   node e2e/perf/analyze.mjs /tmp/reelbolt-perf/<stamp>
 *   node e2e/perf/analyze.mjs /tmp/reelbolt-perf/<stamp>/run-1.har
 *
 * Reads either a directory of `run-*.requests.json` or one HAR file.
 */
import fs from 'node:fs';
import path from 'node:path';
import { redactUrl } from './sanitize.mjs';

const UUID = /[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/gi;
const DIGITS = /\/\d+(?=\/|$)/g;

/** Collapse ids so repeated calls to one endpoint aggregate together. */
function pattern(url) {
  let u = url;
  try {
    const parsed = new URL(url, 'https://placeholder.invalid');
    u = parsed.origin === 'https://placeholder.invalid' ? parsed.pathname : parsed.origin + parsed.pathname;
  } catch { /* leave as-is */ }
  return redactUrl(u).replace(UUID, ':id').replace(DIGITS, '/:n');
}

/**
 * Path only. Query strings are never printed: they carry output-link capability tokens and
 * presigned S3 credentials (`X-Amz-Signature`, `X-Amz-Credential`), which must not land in
 * stdout or in a report pasted from it.
 */
function shortUrl(url) {
  try {
    return redactUrl(new URL(url).pathname);
  } catch {
    return redactUrl(String(url).split('?')[0]);
  }
}

function percentile(values, p) {
  if (!values.length) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const index = Math.min(sorted.length - 1, Math.max(0, Math.ceil((p / 100) * sorted.length) - 1));
  return sorted[index];
}

function loadRuns(target) {
  const stat = fs.statSync(target);
  if (stat.isFile() && target.endsWith('.har')) {
    return [{ file: target, requests: fromHar(JSON.parse(fs.readFileSync(target, 'utf8'))) }];
  }
  const files = fs.readdirSync(target).filter((f) => /^run-\d+\.requests\.json$/.test(f)).sort();
  return files.map((f) => {
    const body = JSON.parse(fs.readFileSync(path.join(target, f), 'utf8'));
    return {
      file: f,
      summary: body.summary,
      connection: body.summary?.connection,
      requests: fromCdp(body.requests || []),
    };
  });
}

function fromCdp(rows) {
  return rows.map((r) => {
    const ttfb = r.responseAt && r.startedAt ? Math.round((r.responseAt - r.startedAt) * 1000) : null;
    return {
      url: r.url,
      method: r.method,
      status: r.status,
      protocol: r.protocol,
      bytes: r.bytes || 0,
      ttfbMs: ttfb,
      durationMs: r.durationMs ?? null,
      failed: r.failed || null,
      resourceType: r.resourceType,
      finished: Boolean(r.finishedAt),
    };
  });
}

function fromHar(har) {
  const out = [];
  for (const entry of har.log?.entries || []) {
    const request = entry.request || {};
    const response = entry.response || {};
    const started = Date.parse(entry.startedDateTime);
    const ttfb = Number.isFinite(started) ? Math.round(started + (entry.timings?.wait ?? 0) - started) : null;
    out.push({
      url: request.url,
      method: request.method,
      status: response.status,
      protocol: response.httpVersion,
      bytes: response.content?.size ?? 0,
      ttfbMs: entry.timings ? Math.round(entry.timings.wait) : null,
      durationMs: entry.time != null ? Math.round(entry.time) : null,
      failed: entry._error || (response.status === 0 ? 'failed' : null),
      resourceType: entry._resourceType,
      finished: true,
    });
  }
  return out;
}

function reportRun(run) {
  const requests = run.requests.filter((r) => !/^data:/.test(r.url));
  const totalBytes = requests.reduce((a, r) => a + (r.bytes || 0), 0);
  const protocols = {};
  for (const r of requests) protocols[r.protocol || '?'] = (protocols[r.protocol || '?'] || 0) + 1;
  const ttfbValues = requests.map((r) => r.ttfbMs).filter((v) => v != null && v >= 0);

  console.log(`\n=== ${run.file} — ${requests.length} requests, ${(totalBytes / 1024 / 1024).toFixed(2)} MiB ===`);
  if (run.connection) {
    console.log(`navigation protocol: ${run.connection.navigationProtocol || '?'} | resources: ${(run.connection.resourceProtocols || []).join(', ')}`);
  }
  console.log(`protocols: ${Object.entries(protocols).map(([p, n]) => `${p || '?'}=${n}`).join(' ')}`);
  if (ttfbValues.length) {
    console.log(
      `TTFB ms: p50=${percentile(ttfbValues, 50)} p90=${percentile(ttfbValues, 90)} p99=${percentile(ttfbValues, 99)} max=${Math.max(...ttfbValues)}`,
    );
  }

  const byPattern = new Map();
  for (const r of requests) {
    const key = pattern(r.url);
    const row = byPattern.get(key) || { count: 0, bytes: 0, ttfb: [], duration: [], statuses: new Set() };
    row.count += 1;
    row.bytes += r.bytes || 0;
    if (r.ttfbMs != null) row.ttfb.push(r.ttfbMs);
    if (r.durationMs != null) row.duration.push(r.durationMs);
    row.statuses.add(r.status ?? '?');
    byPattern.set(key, row);
  }

  const rows = [...byPattern.entries()]
    .map(([key, v]) => ({
      key,
      count: v.count,
      bytes: v.bytes,
      avgTtfb: v.ttfb.length ? Math.round(v.ttfb.reduce((a, b) => a + b, 0) / v.ttfb.length) : 0,
      maxDuration: v.duration.length ? Math.max(...v.duration) : 0,
      statuses: [...v.statuses].join(','),
    }))
    .sort((a, b) => (b.avgTtfb * b.count) - (a.avgTtfb * a.count));

  console.log('\nendpoint                                 count   bytes      avgTTFB  maxDur  status');
  for (const row of rows.slice(0, 30)) {
    console.log(
      `${row.key.slice(0, 40).padEnd(40)} ${String(row.count).padStart(5)} ${String(row.bytes).padStart(9)} ${String(row.avgTtfb).padStart(8)}ms ${String(row.maxDuration).padStart(6)}ms  ${row.statuses}`,
    );
  }

  const slowest = [...requests]
    .filter((r) => r.durationMs != null)
    .sort((a, b) => b.durationMs - a.durationMs)
    .slice(0, 12);
  console.log('\nslowest requests:');
  for (const r of slowest) {
    console.log(`  ${String(r.durationMs).padStart(6)}ms ttfb=${String(r.ttfbMs).padStart(5)}ms ${String(r.status).padStart(3)} ${r.protocol || '?'} ${shortUrl(r.url).slice(0, 80)}`);
  }

  const unfinished = requests.filter((r) => !r.finished);
  if (unfinished.length) {
    console.log(`\nunfinished (still open at capture): ${unfinished.length}`);
    for (const r of unfinished.slice(0, 10)) console.log(`  ${r.resourceType || '?'} ${shortUrl(r.url).slice(0, 90)}`);
  }

  const polled = [...byPattern.entries()].filter(([, v]) => v.count > 2 && v.ttfb.length > 1);
  if (polled.length) {
    console.log('\npolled / repeated endpoints (>2 calls in one run):');
    for (const [key, v] of polled.sort((a, b) => b[1].count - a[1].count).slice(0, 10)) {
      console.log(`  ${String(v.count).padStart(4)}x ${(v.bytes / 1024).toFixed(0).padStart(6)} KiB  ${key.slice(0, 70)}`);
    }
  }

  for (const d of run.summary?.downloads || []) {
    console.log(`\ndownload ${d.label}: ${d.kbps ?? '?'} KB/s — ${d.bytes ?? '?'} B in ${d.totalMs ?? '?'} ms (ttfb ${d.firstByteMs ?? '?'} ms, status ${d.status ?? '?'})`);
  }
  for (const key of ['video', 'renderVideo']) {
    const samples = run.summary?.[key];
    if (samples?.length) {
      const last = samples[samples.length - 1];
      console.log(`${key} after ${last.atMs} ms: currentTime=${last.currentTime}s buffered=${last.bufferedEnd}s readyState=${last.readyState}${last.error ? ' error=' + last.error.code : ''}`);
    }
  }
}

const target = process.argv[2];
if (!target) {
  console.error('usage: node e2e/perf/analyze.mjs <out-dir|run-N.har>');
  process.exit(1);
}
for (const run of loadRuns(target)) reportRun(run);
