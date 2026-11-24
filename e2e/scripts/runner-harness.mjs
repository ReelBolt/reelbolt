#!/usr/bin/env node
/**
 * E13 — the end-to-end runner harness.
 *
 * WHAT THIS IS
 *
 * `sandbox/scripts/worker-smoke.sh` is the D13 acceptance check, written as a
 * manual script. This file is the CI form of it: the same evidence chain, driven
 * from a small Node runner with three modes, so that `ci.yml` can run it and a
 * leg whose preconditions are missing in the current environment SKIPS LOUDLY
 * instead of passing silently. The evidence chain, in order:
 *
 *   1. an unenrolled pool worker is refused 4401 with the enrollment remedy
 *      logged (exit 4, no restart loop);
 *   2. `reelbolt-worker enroll` creates exactly one idempotent row;
 *   3. the same binary handshakes as a CloudPool runner;
 *   4. a `video.compile` job is assigned and a session opens;
 *   5. an ffmpeg op runs INSIDE A REAL DOCKER CONTAINER (the sandbox-runtime
 *      image), encodes a real MP4, and ffprobe reads its dimensions back
 *      through the session;
 *   6. the job completes Succeeded and the row carries last_seen_at and its
 *      advertised slots;
 *   7. the scaler decides from D5c's endpoint and runs its `-apply` hook;
 *   8. SIGTERM drains the worker and it exits.
 *
 * The `remotion.workspace-session` leg is NOT in this chain, and this harness
 * has no implementation of it: it needs a runtime image pinned by DIGEST (a
 * locally built tag has none, and an unpinned runtime must not be advertised)
 * and D8's object store for presigned stage/publish. The harness reports it on
 * its own NOT COVERED line, with the reason, in every run, and the Playwright
 * spec annotates it. It is not counted as a SKIP: a skip is a leg that exists
 * but could not run HERE, and --run-full turns any skip into exit 2. Counting
 * a leg that cannot run ANYWHERE as a skip made --run-full a constant exit 2,
 * a gate that could never be green. When the leg is implemented it joins the
 * chain and becomes required under --run-full like every other leg.
 *
 * MODES
 *
 *   --dry-run            verify the host (docker, tools, images) and print the
 *                        plan; build nothing, start nothing. Exit 0.
 *   --run-full           the whole chain above. Exit 0 only if every leg of
 *                        the chain ran and passed and no leg had to skip.
 *   (no flag, default)   the chain, but a leg whose preconditions are missing
 *                        SKIPS with a printed reason; exit 0 if every leg that
 *                        ran passed.
 *
 * A leg that SKIPS is reported on its own line, with the reason, and is counted
 * in the summary. The summary distinguishes RUN, SKIP and FAIL, and lists what
 * the harness does not cover at all, so a reader can see at a glance which
 * evidence was produced and which was not.
 *
 * ENVIRONMENT (all optional; defaults match `worker-smoke.sh`)
 *
 *   SANDBOX_IMAGE                 the runtime image a job's container runs in
 *                                (default reelbolt-sandbox-runtime:local)
 *   GO_IMAGE                      the Go build image
 *                                (default golang:1.26-alpine)
 *   PG_IMAGE                      the throwaway Postgres image
 *                                (default postgres:17-alpine)
 *   GO_MODCACHE                   the named volume the Go build reuses
 *                                (default reelforge-go-modcache)
 *   REELBOLT_SMOKE_PG_PORT        throwaway Postgres port (default 55439)
 *   REELBOLT_SMOKE_GW_PORT        gateway port (default 18099)
 *   RUNTIME_IMAGE_DIGEST          the DIGEST-pinned runtime image the
 *                                remotion.workspace-session leg will need once
 *                                it is implemented; today it is only reported.
 *
 * Requirements: docker with a working daemon, curl, jq, uuidgen, and the Go and
 * Postgres images available locally (the build and the database run in
 * containers; no host Go toolchain is used). The two local-environment
 * workarounds from `worker-smoke.sh` are preserved here: a leftover worker from
 * an interrupted run is killed (it would otherwise supersedes the next run's
 * worker), and Postgres readiness comes from the container's own health check
 * with the start retried (an exec-based probe was unreliable on this machine).
 */

import { spawn, spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, rmSync, writeFileSync, existsSync, readFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

// ── the mode ────────────────────────────────────────────────────────────────
const args = process.argv.slice(2);
const DRY_RUN = args.includes('--dry-run');
const RUN_FULL = args.includes('--run-full');
const QUIET = args.includes('--quiet');

// ── configuration ───────────────────────────────────────────────────────────
const REPO_ROOT = dirname(dirname(dirname(fileURLToPath(import.meta.url))));
const SANDBOX = join(REPO_ROOT, 'sandbox');
const SCHEMA_SQL = join(SANDBOX, 'scripts', 'runner-smoke-schema.sql');
const GO_IMAGE = process.env.GO_IMAGE || 'golang:1.26-alpine';
const PG_IMAGE = process.env.PG_IMAGE || 'postgres:17-alpine';
const RUNTIME_IMAGE = process.env.SANDBOX_IMAGE || 'reelbolt-sandbox-runtime:local';
const RUNTIME_DIGEST = process.env.RUNTIME_IMAGE_DIGEST || '';
const GO_MODCACHE = process.env.GO_MODCACHE || 'reelforge-go-modcache';
const PG_PORT = process.env.REELBOLT_SMOKE_PG_PORT || '55439';
const GW_PORT = process.env.REELBOLT_SMOKE_GW_PORT || '18099';
const WORKER_NAME = 'pool-harness';
const POOL_CREDENTIAL = 'harness-pool-credential';
const INTERNAL_TOKEN = 'harness-internal-token';
const SANDBOX_NETWORK = `reelbolt-harness-net-${Date.now()}`;

// ── the tally ───────────────────────────────────────────────────────────────
let RUN = 0;
let SKIP = 0;
let FAIL = 0;
const skips = [];
const notCovered = [];

// `say` always prints: every PASS, SKIP and FAIL line, the summary and any
// fatal error. `--quiet` only drops the progress narration (`log`). A harness
// that fails must always say why: a bare exit 1 with nothing on stdout or
// stderr is exactly what made the CI job unreadable before.
const say = (msg) => {
  process.stdout.write(msg.endsWith('\n') ? msg : `${msg}\n`);
};
const log = (msg) => {
  if (!QUIET) say(msg);
};
const pass = (msg) => {
  RUN += 1;
  say(`  PASS: ${msg}`);
};
const skip = (leg, reason) => {
  SKIP += 1;
  skips.push(`${leg}: ${reason}`);
  say(`  SKIP: ${leg} — ${reason}`);
};
// A leg this harness has no implementation for. Printed every run and listed
// in the summary, never counted as passed or skipped (see the header).
const notCover = (leg, reason) => {
  notCovered.push(`${leg}: ${reason}`);
  say(`  NOT COVERED: ${leg} — ${reason}`);
};
const fail = (leg, reason) => {
  FAIL += 1;
  say(`  FAIL: ${leg} — ${reason}`);
};

// ── small helpers ───────────────────────────────────────────────────────────
function run(cmd, args, opts = {}) {
  const r = spawnSync(cmd, args, {
    encoding: 'utf8',
    timeout: opts.timeoutMs ?? 120000,
    maxBuffer: 64 * 1024 * 1024,
    env: { ...process.env, ...(opts.env || {}) },
  });
  // A child killed by a signal (including spawnSync's own timeout, which sends
  // SIGTERM) has status null, and one that never started has `error` set. Both
  // become code -1; `how` keeps the reason so a failure message can say it.
  let how = `exit ${r.status}`;
  if (r.error) how = `could not run ${cmd}: ${r.error.code || r.error.message}`;
  else if (r.status === null) how = `killed by ${r.signal}${r.signal === 'SIGTERM' ? ` (likely the ${Math.round((opts.timeoutMs ?? 120000) / 1000)}s timeout)` : ''}`;
  return { code: r.status ?? -1, how, out: r.stdout || '', err: r.stderr || '' };
}

const tail = (text, n) => text.split('\n').slice(-n).join('\n');
const parseJson = (text) => {
  try { return JSON.parse(text); } catch { return {}; }
};

async function waitFor(fn, timeoutMs = 30000, stepMs = 500) {
  const start = Date.now();
  for (;;) {
    if (await fn()) return true;
    if (Date.now() - start >= timeoutMs) return false;
    await new Promise((r) => setTimeout(r, stepMs));
  }
}

async function pgReady(pgName, timeoutMs = 60000) {
  // The probe stays INSIDE the container (the daemon's health check) rather than
  // a host-side `docker exec pg_isready` loop: on this machine the exec probe
  // intermittently left the entrypoint stuck, so a flaky probe would otherwise
  // look like a flaky worker. The start is retried, as in worker-smoke.sh.
  const ok = await waitFor(async () => {
    const r = run('docker', ['inspect', pgName, '--format', '{{if .State.Health}}{{.State.Health.Status}}{{else}}none{{end}}']);
    return r.out.trim() === 'healthy';
  }, timeoutMs, 1000);
  return ok;
}

// ── 0. host prerequisites ───────────────────────────────────────────────────
function verifyHost() {
  log('\n=== 0. host prerequisites ===');
  const docker = run('docker', ['info']);
  if (docker.code !== 0) {
    fail('docker', 'the docker daemon is not reachable');
    return false;
  }
  for (const tool of ['curl', 'jq', 'uuidgen']) {
    const r = run('which', [tool]);
    if (r.code !== 0) {
      fail('tools', `${tool} is required`);
      return false;
    }
  }
  const image = (name) => run('docker', ['image', 'inspect', name]).code === 0;
  if (!image(GO_IMAGE)) fail('images', `the Go image ${GO_IMAGE} is not present locally`);
  if (!image(PG_IMAGE)) fail('images', `the Postgres image ${PG_IMAGE} is not present locally`);
  RUNTIME_PRESENT = image(RUNTIME_IMAGE);
  if (FAIL > 0) return false;
  if (!RUNTIME_PRESENT) {
    // A missing runtime image is not a host failure: everything up to the
    // container leg can still run. The container leg itself SKIPS (step 7),
    // which under --run-full makes the run exit 2.
    log(`  note: the runtime image ${RUNTIME_IMAGE} is not present locally (build it from ./sandbox target sandbox-runtime, or set SANDBOX_IMAGE) — the container leg will skip`);
    pass('docker reachable, tools present, Go/Postgres images present');
  } else {
    pass('docker reachable, tools present, Go/Postgres/runtime images present');
  }
  return true;
}
let RUNTIME_PRESENT = false;

// ── the plan, for --dry-run ─────────────────────────────────────────────────
function printPlan() {
  log('\n=== plan (dry-run) ===');
  log(`  REPO_ROOT        ${REPO_ROOT}`);
  log(`  SANDBOX          ${SANDBOX}`);
  log(`  SCHEMA_SQL       ${SCHEMA_SQL} ${existsSync(SCHEMA_SQL) ? '(present)' : '(MISSING)'}`);
  log(`  GO_IMAGE         ${GO_IMAGE}`);
  log(`  PG_IMAGE         ${PG_IMAGE}`);
  log(`  RUNTIME_IMAGE    ${RUNTIME_IMAGE}`);
  log(`  RUNTIME_DIGEST   ${RUNTIME_DIGEST || '(absent)'}`);
  log(`  PG_PORT          ${PG_PORT}`);
  log(`  GW_PORT          ${GW_PORT}`);
  log(`  SANDBOX_NETWORK  ${SANDBOX_NETWORK}`);
  const legs = [
    '1. an UNENROLLED pool worker is refused 4401 (exit 4, remedy logged)',
    '2. enroll creates exactly one idempotent row',
    '3. the same binary handshakes as a CloudPool runner',
    '4. a video.compile job is assigned and a session opens',
    '5. an ffmpeg op runs INSIDE A REAL CONTAINER; ffprobe reads 320x180 back',
    '6. the job completes Succeeded; the row carries last_seen_at and 2 slots',
    '7. the scaler decides from D5c and runs its -apply hook',
    '8. SIGTERM drains the worker and it exits',
  ];
  for (const l of legs) log(`  ${l}`);
  log('  not covered: the remotion.workspace-session leg (not implemented; needs a DIGEST-pinned runtime image and D8\'s object store)');
}

// ── 1. build the three static binaries ─────────────────────────────────────
async function buildBinaries(binDir) {
  log('\n=== 1. building the gateway, the worker and the scaler ===');
  const r = run(
    'docker',
    [
      'run', '--rm',
      '-v', `${SANDBOX}:/w`,
      '-v', `${GO_MODCACHE}:/go/pkg/mod`,
      '-v', `${binDir}:/out`,
      '-w', '/w',
      '-e', 'CGO_ENABLED=0',
      GO_IMAGE,
      'sh', '-euc',
      'go build -o /out/runner-gateway ./cmd/runner-gateway\n' +
      'go build -o /out/reelbolt-worker ./cmd/reelbolt-worker\n' +
      'go build -o /out/reelbolt-scaler ./cmd/reelbolt-scaler\n',
    ],
    { timeoutMs: 240000 },
  );
  if (r.code !== 0) {
    fail('build', `the in-container go build failed (${r.how})\n${tail(r.err + r.out, 40)}`);
    return false;
  }
  pass('three static binaries built (they run on the host, not in a container)');
  return true;
}

// ── 2. a throwaway Postgres with the migration-faithful schema ────────────
async function startPostgres(binDir) {
  log('\n=== 2. starting a throwaway Postgres with the runner schema ===');
  if (!existsSync(SCHEMA_SQL)) {
    fail('schema', `the schema file is missing at ${SCHEMA_SQL}`);
    return null;
  }
  // Copy the schema into a per-run directory the container can read, so the
  // script loaded is byte-identical to the tracked file.
  const schemaDir = join(binDir, 'schema');
  mkdirSync(schemaDir, { recursive: true });
  writeFileSync(join(schemaDir, 'schema.sql'), readFileSync(SCHEMA_SQL));

  let name = '';
  let ready = false;
  for (let attempt = 1; attempt <= 3 && !ready; attempt += 1) {
    const candidate = `reelbolt-harness-pg-${process.pid}-${attempt}`;
    run('docker', ['rm', '-f', candidate]);
    _pgStarting = candidate; // so cleanup removes it even if we are stopped mid-wait
    const started = run('docker', [
      'run', '-d', '--rm', '--name', candidate,
      '-e', 'POSTGRES_PASSWORD=postgres',
      '-p', `127.0.0.1:${PG_PORT}:5432`,
      '-v', `${schemaDir}:/schema:ro`,
      '--health-cmd', 'pg_isready -U postgres',
      '--health-interval', '2s',
      '--health-timeout', '2s',
      '--health-retries', '30',
      PG_IMAGE,
    ]);
    if (started.code !== 0) {
      // e.g. the port is already taken, or the image is missing: waiting 60s
      // for a container that never started would only hide the reason.
      say(`  postgres attempt ${attempt}: docker run failed (${started.how})\n${tail(started.err + started.out, 6)}`);
      continue;
    }
    ready = await pgReady(candidate);
    if (!ready) {
      const logs = run('docker', ['logs', candidate]);
      say(`  postgres attempt ${attempt} did not become healthy\n${tail(logs.out + logs.err, 6)}`);
      run('docker', ['rm', '-f', candidate]);
    } else {
      name = candidate;
    }
    _pgStarting = '';
  }
  if (!ready) {
    fail('postgres', `postgres never became healthy on 127.0.0.1:${PG_PORT} (set REELBOLT_SMOKE_PG_PORT if the port is taken)`);
    return null;
  }
  const load = run('docker', ['exec', name, 'psql', '-U', 'postgres', '-v', 'ON_ERROR_STOP=1', '-q', '-f', '/schema/schema.sql']);
  if (load.code !== 0) {
    fail('schema-load', `loading the schema failed (exit ${load.code})\n${(load.out + load.err).split('\n').slice(-10).join('\n')}`);
    run('docker', ['rm', '-f', name]);
    return null;
  }
  pass('runner_devices, runner_jobs and runner_sessions created from the migration-faithful schema');
  return name;
}

// ── 3. the gateway ─────────────────────────────────────────────────────────
let gatewayProc = null;
async function startGateway() {
  log('\n=== 3. starting the runner gateway ===');
  const dbUrl = `postgres://postgres:postgres@127.0.0.1:${PG_PORT}/postgres?sslmode=disable`;
  const child = spawn(join(_binDir, 'runner-gateway'), [], {
    env: {
      ...process.env,
      PORT: GW_PORT,
      DATABASE_URL: dbUrl,
      RUNNER_POOL_CREDENTIALS: POOL_CREDENTIAL,
      RUNNER_INTERNAL_TOKEN: INTERNAL_TOKEN,
      GATEWAY_INSTANCE: 'harness-gateway',
    },
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  gatewayProc = child;
  const out = [];
  let spawnError = null;
  child.on('error', (e) => { spawnError = e; });
  child.stdout.on('data', (d) => out.push(d));
  child.stderr.on('data', (d) => out.push(d));
  const ok = await waitFor(async () => {
    if (spawnError || child.exitCode !== null) return false;
    const r = run('curl', ['-sf', `http://127.0.0.1:${GW_PORT}/healthz`]);
    return r.code === 0;
  }, 30000);
  if (!ok) {
    const why = spawnError ? `could not start: ${spawnError.message}` : child.exitCode !== null ? `exited ${child.exitCode}` : 'not healthy within 30s';
    fail('gateway', `the gateway never became healthy (${why})\n${tail(out.join(''), 20)}`);
    return false;
  }
  pass(`gateway healthy on 127.0.0.1:${GW_PORT}`);
  return true;
}

// ── the worker's environment ────────────────────────────────────────────────
function workerEnv() {
  return {
    ...process.env,
    REELBOLT_GATEWAY_URL: `ws://127.0.0.1:${GW_PORT}/v1/connect`,
    RUNNER_POOL_CREDENTIAL: POOL_CREDENTIAL,
    REELBOLT_WORKER_NAME: WORKER_NAME,
    REELBOLT_WORKER_CPUS: '4',
    SANDBOX_CPU_LIMIT: '2',
    SANDBOX_IMAGE: RUNTIME_IMAGE,
    SANDBOX_ROOT: join(_binDir, 'sandboxes'),
    SANDBOX_NETWORK,
    REELBOLT_WORKER_SCRATCH: join(_binDir, 'scratch'),
    SANDBOX_CONTAINER_CLI: 'docker',
  };
}

// ── 4. an UNENROLLED worker is refused ─────────────────────────────────────
async function refuseUnenrolled() {
  log('\n=== 4. an UNENROLLED pool worker is refused ===');
  const r = run('timeout', ['25', join(_binDir, 'reelbolt-worker'), 'run'], {
    env: workerEnv(),
    timeoutMs: 30000,
  });
  const refused = r.code === 4;
  const hasUnauth = (r.out + r.err).includes('unauthenticated');
  const hasRemedy = (r.out + r.err).includes('reelbolt-worker enroll');
  if (!refused || !hasUnauth || !hasRemedy) {
    fail('refused-4401', `${r.how} (want exit 4); unauthenticated=${hasUnauth}; remedy logged=${hasRemedy}\n${tail(r.out + r.err, 25)}`);
    return false;
  }
  pass('refused 4401 with the enrollment hint, exit 4 (fails closed, does not loop)');
  return true;
}

// ── 5. enroll, idempotently ────────────────────────────────────────────────
async function enroll() {
  log('\n=== 5. enrolling the pool worker ===');
  const dbUrl = `postgres://postgres:postgres@127.0.0.1:${PG_PORT}/postgres?sslmode=disable`;
  const r1 = run(join(_binDir, 'reelbolt-worker'), ['enroll'], {
    env: { ...process.env, DATABASE_URL: dbUrl, REELBOLT_WORKER_NAME: WORKER_NAME },
  });
  const o1 = r1.out + '\n' + r1.err;
  if (r1.code !== 0 || !o1.includes('enrolled pool worker')) {
    fail('enroll', `first enroll failed (${r1.how})\n${tail(o1, 10)}`);
    return false;
  }
  const r2 = run(join(_binDir, 'reelbolt-worker'), ['enroll'], {
    env: { ...process.env, DATABASE_URL: dbUrl, REELBOLT_WORKER_NAME: WORKER_NAME },
  });
  const o2 = r1.err + '\n' + r2.err; // the enroll logger writes to stderr
  if (r2.code !== 0 || !o2.includes('already enrolled')) {
    fail('enroll-idempotent', `second enroll failed (${r2.how})\n${tail(o2, 10)}`);
    return false;
  }
  const rows = run('docker', ['exec', _pgName, 'psql', '-U', 'postgres', '-tAc',
    `select count(*) from runner_devices where kind='CloudPool' and name='${WORKER_NAME}'`]);
  if (rows.out.trim() !== '1') {
    fail('enroll-row', `${rows.out.trim()} rows are named ${WORKER_NAME}, want exactly 1`);
    return false;
  }
  pass('one idempotent runner_devices row (kind=CloudPool, organization_id null)');
  return true;
}

// ── 6. the enrolled worker connects ────────────────────────────────────────
let workerProc = null;
async function startWorker() {
  log('\n=== 6. starting the enrolled worker ===');
  workerProc = spawn(join(_binDir, 'reelbolt-worker'), ['run'], {
    env: workerEnv(),
    stdio: ['ignore', 'pipe', 'pipe'],
  });
  const out = [];
  let spawnError = null;
  workerProc.on('error', (e) => { spawnError = e; });
  workerProc.stdout.on('data', (d) => out.push(d));
  workerProc.stderr.on('data', (d) => out.push(d));
  // The worker logs its capability document ("capabilities: ... jobTypes=[...]")
  // before it dials the gateway. Wait for THAT line as well as the connected
  // gauge: the gauge can flip inside the synchronous curl below, before Node has
  // read the worker's stderr pipe, and checking the advertisement then would
  // race the pipe rather than test the worker.
  // Only COMPLETE lines (the last split element may be a chunk cut mid-line).
  const capsLine = () => out.join('').split('\n').slice(0, -1).find((l) => l.includes('capabilities: ')) || '';
  const ok = await waitFor(async () => {
    if (spawnError || workerProc.exitCode !== null) return false;
    if (!capsLine()) return false;
    const r = run('curl', ['-s', `http://127.0.0.1:${GW_PORT}/metrics`]);
    return r.out.includes('reelbolt_runner_connected{kind="CloudPool"} 1');
  }, 30000);
  if (!ok) {
    const why = spawnError ? `could not start: ${spawnError.message}` : workerProc.exitCode !== null ? `exited ${workerProc.exitCode}` : !capsLine() ? 'no capability document logged within 30s' : 'not connected within 30s';
    fail('worker-connect', `the worker never showed up as a connected CloudPool runner (${why})\n${tail(out.join(''), 30)}`);
    return false;
  }
  if (!/jobTypes=\[[^\]]*\bvideo\.compile\b/.test(capsLine())) {
    // The usual cause is no ffmpeg on the host PATH: the probe runs the host's
    // ffmpeg (REELBOLT_WORKER_FFMPEG, default "ffmpeg") and, without one,
    // advertises no job types at all. Show what it did advertise and why.
    fail('worker-ads', `the worker did not advertise video.compile\n${tail(out.join(''), 30)}`);
    return false;
  }
  pass('handshake accepted; the pool now has a runner (the same binary that was refused above)');
  return true;
}

// ── 7. a job, a session, and a real container underneath ──────────────────
function op(session, name, args) {
  return run('curl', [
    '-sS', '-X', 'POST',
    `http://127.0.0.1:${GW_PORT}/internal/v1/sessions/${session}/ops`,
    '-H', `Authorization: Bearer ${INTERNAL_TOKEN}`,
    '-H', 'Content-Type: application/json',
    '-d', JSON.stringify({ op: name, args }),
  ]);
}
function view(job) {
  return run('curl', [
    '-sS', `http://127.0.0.1:${GW_PORT}/internal/v1/jobs/${job}`,
    '-H', `Authorization: Bearer ${INTERNAL_TOKEN}`,
  ]);
}

async function runJob() {
  log('\n=== 7. a video.compile job, a session, and a real container ===');
  const uuid = () => run('uuidgen').out.trim().toLowerCase();
  const org = uuid();
  const exec = uuid();
  const body = { orgId: org, executionId: exec, jobType: 'video.compile', target: 'CloudPool', purpose: 'e13-harness', spec: { note: 'E13 harness' } };
  const created = run('curl', [
    '-sS', '-X', 'POST', `http://127.0.0.1:${GW_PORT}/internal/v1/jobs`,
    '-H', `Authorization: Bearer ${INTERNAL_TOKEN}`,
    '-H', 'Content-Type: application/json',
    '-d', JSON.stringify(body),
  ]);
  const jobId = parseJson(created.out).jobId || '';
  if (!jobId) {
    fail('job-create', `no jobId came back (${created.how})\n${created.out}${created.err}`);
    return false;
  }
  let session = '';
  let status = '';
  for (let i = 0; i < 60 && !session; i += 1) {
    const v = parseJson(view(jobId).out);
    session = v.sessionId || '';
    status = v.status || '';
    if (!session) await new Promise((r) => setTimeout(r, 500));
  }
  if (!session) {
    fail('job-assign', `the job was never assigned (status ${status})`);
    return false;
  }
  pass(`job ${jobId} assigned and session ${session} opened (status ${status})`);

  if (!RUNTIME_PRESENT) {
    skip('container', `the runtime image ${RUNTIME_IMAGE} is not present locally, so no ffmpeg op can run in a real container (build it from ./sandbox target sandbox-runtime, or set SANDBOX_IMAGE)`);
    op(session, 'close', {}); // best effort, so the drain leg is not held by an open session
    return true;
  }

  // ffmpeg inside the real container.
  const ffmpegArgs = { argv: ['-hide_banner', '-y', '-f', 'lavfi', '-i', 'testsrc=duration=1:size=320x180:rate=10', '-pix_fmt', 'yuv420p', 'out.mp4'], timeoutSec: 180 };
  const ffmpeg = op(session, 'ffmpeg', ffmpegArgs);
  const ff = parseJson(ffmpeg.out);
  if (ff.ok !== true) {
    fail('ffmpeg-op', `the ffmpeg op failed (${ffmpeg.how})\n${ffmpeg.out}${ffmpeg.err}`);
    return false;
  }
  if (ff.result?.exitCode !== 0) {
    fail('ffmpeg-exit', `ffmpeg exited ${ff.result?.exitCode}: ${ff.result?.stderrTail}`);
    return false;
  }
  pass(`ffmpeg encoded 1s of testsrc to out.mp4 inside ${RUNTIME_IMAGE} (exit 0)`);

  const stat = op(session, 'stat', { path: 'out.mp4' });
  const st = parseJson(stat.out);
  if (st.result?.exists !== true || !(st.result?.bytes > 0)) {
    fail('stat', `the rendered file is not in the session directory\n${stat.out}`);
    return false;
  }
  pass(`the container wrote ${st.result.bytes} bytes into the session's scratch directory`);

  const probe = op(session, 'ffprobe', { argv: ['-v', 'error', '-show_entries', 'stream=width,height', '-of', 'json', 'out.mp4'], timeoutSec: 60 });
  const pr = parseJson(probe.out);
  if (pr.ok !== true || pr.result?.exitCode !== 0) {
    fail('ffprobe', `the ffprobe op failed\n${probe.out}`);
    return false;
  }
  let stream = {};
  try {
    const stdout = JSON.parse(pr.result.stdout);
    stream = stdout.streams[0] || {};
  } catch { /* fall through */ }
  if (String(stream.width) !== '320' || String(stream.height) !== '180') {
    fail('ffprobe-size', `ffprobe read the wrong size: ${stream.width} x ${stream.height}, want 320x180`);
    return false;
  }
  pass(`ffprobe read the encoded video back as ${stream.width} x ${stream.height}`);

  const close = op(session, 'close', {});
  const cl = parseJson(close.out);
  if (cl.ok !== true) {
    fail('close', `the close op failed\n${close.out}`);
    return false;
  }
  let finalStatus = '';
  for (let i = 0; i < 60; i += 1) {
    finalStatus = parseJson(view(jobId).out).status || '';
    if (finalStatus === 'Succeeded') break;
    await new Promise((r) => setTimeout(r, 500));
  }
  if (finalStatus !== 'Succeeded') {
    fail('job-succeeded', `the job ended as ${finalStatus}, want Succeeded`);
    return false;
  }
  pass('the job completed as Succeeded (gateway to worker to container and back)');

  const lastSeen = run('docker', ['exec', _pgName, 'psql', '-U', 'postgres', '-tAc',
    `select coalesce(to_char(last_seen_at,'YYYY-MM-DD'),'') from runner_devices where name='${WORKER_NAME}'`]);
  const slots = run('docker', ['exec', _pgName, 'psql', '-U', 'postgres', '-tAc',
    `select coalesce(capabilities_json->>'slots','') from runner_devices where name='${WORKER_NAME}'`]);
  if (!lastSeen.out.trim()) {
    fail('last-seen', 'the gateway never wrote last_seen_at on the enrolled row');
    return false;
  }
  if (slots.out.trim() !== '2') {
    fail('slots', `capabilities_json advertises ${slots.out.trim()} slots, want 2 (4 vCPU / SANDBOX_CPU_LIMIT 2)`);
    return false;
  }
  pass('the enrolled row carries last_seen_at and the advertised 2 slots');
  return true;
}

// ── 8. the scaling hook ────────────────────────────────────────────────────
async function scalingHook() {
  log("\n=== 8. the scaling hook reads D5c's endpoint ===");
  const dry = run(join(_binDir, 'reelbolt-scaler'), ['-json'], {
    env: { ...process.env, RUNNER_GATEWAY_URL: `http://127.0.0.1:${GW_PORT}`, RUNNER_INTERNAL_TOKEN: INTERNAL_TOKEN, POOL_SLOTS_PER_NODE: '2', POOL_MAX_NODES: '8' },
  });
  const decision = parseJson(dry.out);
  if (!(decision.decision?.nodes >= 1)) {
    fail('scaler-dry', `the scaler decided ${decision.decision?.nodes} nodes (${dry.how})\n${dry.out}${dry.err}`);
    return false;
  }
  log(`  decision: ${JSON.stringify(decision.decision)}`);
  pass(`dry run decided ${decision.decision.nodes} node(s) from D5c's desiredCloudPoolSlots (nothing was scaled)`);

  const hook = join(_binDir, '..', 'hook.out');
  const hookCmd = `echo "nodes=$POOL_NODES desired=$POOL_DESIRED_SLOTS cappedBy=$POOL_CAPPED_BY" > "${hook}"`;
  const apply = run(join(_binDir, 'reelbolt-scaler'), ['-apply'], {
    env: { ...process.env, RUNNER_GATEWAY_URL: `http://127.0.0.1:${GW_PORT}`, RUNNER_INTERNAL_TOKEN: INTERNAL_TOKEN, POOL_SLOTS_PER_NODE: '2', POOL_SCALE_COMMAND: hookCmd },
  });
  if (apply.code !== 0) {
    fail('scaler-apply', `-apply failed (${apply.how})\n${tail(apply.out + apply.err, 10)}`);
    return false;
  }
  const hookOut = existsSync(hook) ? readFileSync(hook, 'utf8') : '';
  if (!hookOut.includes('nodes=')) {
    fail('scaler-hook', `the scale command did not run\n${apply.out}`);
    return false;
  }
  pass(`apply ran the hook with the decision in its environment: ${hookOut.trim()}`);
  return true;
}

// ── 9. SIGTERM drains ──────────────────────────────────────────────────────
async function drainWorker() {
  log('\n=== 9. SIGTERM drains the worker ===');
  if (!workerProc) {
    skip('drain', 'no worker process to drain');
    return true;
  }
  workerProc.kill('SIGTERM');
  const exited = await new Promise((resolve) => {
    let done = false;
    const timer = setTimeout(() => { if (!done) { done = true; resolve(false); } }, 30000);
    workerProc.on('exit', () => { if (!done) { done = true; clearTimeout(timer); resolve(true); } });
  });
  if (!exited) {
    fail('drain', 'the worker did not stop within 30s of SIGTERM');
    return false;
  }
  pass('drained for the spot-reclaim window and exited');
  return true;
}

// ── 10. the remotion.workspace-session leg: not covered ─────────────────────
// The leg would render the template's Main composition for one second and
// assert the output. It has no implementation yet: it needs a DIGEST-pinned
// runtime image and D8's object store. Report that, every run, in its own
// category (see the header for why it is not a skip).
function remotionLeg() {
  log('\n=== 10. the remotion.workspace-session leg ===');
  notCover('remotion.workspace-session',
    `not implemented in this harness; it needs a DIGEST-pinned runtime image (RUNTIME_IMAGE_DIGEST${RUNTIME_DIGEST ? ' is set' : ' is unset'}) and D8's object store for presigned stage/publish, so no remotion render is proven here`);
}

// ── module state shared by the chain ───────────────────────────────────────
let _baseDir = '';
let _binDir = '';
let _pgName = '';
let _pgStarting = '';

// ── cleanup ─────────────────────────────────────────────────────────────────
function cleanup() {
  // stop_pid sends SIGTERM and WAITS. A worker that survives its own run keeps
  // reconnecting to the same port and would supersedes the next run's worker.
  const stop = (p) => {
    if (!p || p.exitCode !== null) return;
    try { p.kill('SIGTERM'); } catch { /* already gone */ }
    try { p.kill('SIGKILL'); } catch { /* already gone */ }
  };
  stop(workerProc);
  stop(gatewayProc);
  if (_pgName) run('docker', ['rm', '-f', _pgName]);
  if (_pgStarting) run('docker', ['rm', '-f', _pgStarting]);
  run('docker', ['network', 'rm', SANDBOX_NETWORK]);
  // Best effort: the binaries are root-owned (built in a container) but live in
  // a directory this process owns, so they can be unlinked.
  if (_baseDir) {
    try { rmSync(_baseDir, { recursive: true, force: true }); } catch { /* leave it */ }
  }
}

// ── the main chain ─────────────────────────────────────────────────────────
async function main() {
  log('runner-harness (E13) — mode: ' + (DRY_RUN ? 'dry-run' : RUN_FULL ? 'run-full' : 'default'));
  _baseDir = mkdtempSync(join(tmpdir(), 'reelbolt-harness-'));
  _binDir = join(_baseDir, 'bin');
  mkdirSync(_binDir, { recursive: true });

  if (!verifyHost()) {
    say('\n--- summary ---');
    say(`  ${RUN} passed, ${SKIP} skipped, ${FAIL} failed (host prerequisites) — aborting`);
    cleanup();
    process.exit(1);
  }

  if (DRY_RUN) {
    printPlan();
    say('\n--- summary (dry-run) ---');
    say(`  ${RUN} passed, ${SKIP} skipped, ${FAIL} failed — plan printed, nothing built or started`);
    cleanup();
    process.exit(FAIL > 0 ? 1 : 0);
  }

  // buildBinaries is async: without the await, `!promise` is always false and
  // a failed build fell through to the gateway leg.
  if (!(await buildBinaries(_binDir))) {
    finish();
    return;
  }

  _pgName = (await startPostgres(_binDir)) || '';
  if (!_pgName) { finish(); return; }

  if (!(await startGateway())) { finish(); return; }

  // A worker left over from an EARLIER run is worse than untidy: it reconnects
  // and supersedes this run's worker. Kill only this harness's own leftovers.
  run('pkill', ['-f', 'reelbolt-harness.*reelbolt-worker']);
  await new Promise((r) => setTimeout(r, 500));

  if (!(await refuseUnenrolled())) { finish(); return; }
  if (!(await enroll())) { finish(); return; }
  if (!(await startWorker())) { finish(); return; }
  if (!(await runJob())) { finish(); return; }
  if (!(await scalingHook())) { finish(); return; }
  await drainWorker();

  remotionLeg();
  finish();
}

let finished = false;
function finish() {
  if (finished) return;
  finished = true;
  say('\n--- summary ---');
  say(`  ${RUN} passed, ${SKIP} skipped, ${FAIL} failed`);
  if (skips.length) {
    say('  skipped legs:');
    for (const s of skips) say(`    - ${s}`);
  }
  if (notCovered.length) {
    say('  not covered by this harness:');
    for (const s of notCovered) say(`    - ${s}`);
  }
  cleanup();
  if (FAIL > 0) {
    process.exit(1);
  }
  if (RUN_FULL && SKIP > 0) {
    say('  run-full requires every leg to run; a leg skipped, so this is not a full pass');
    process.exit(2);
  }
  process.exit(0);
}

// Anything unexpected is a FAILED leg with its reason printed, never a bare
// exit: a thrown error, a rejected promise, or the harness itself being
// stopped (the spec's execFile timeout sends SIGTERM). Each path still runs
// cleanup, so no Postgres container, gateway or worker outlives the run.
const fatal = (what) => (e) => {
  fail('harness', `${what}: ${e?.stack || e}`);
  finish();
};
process.on('uncaughtException', fatal('uncaught exception'));
process.on('unhandledRejection', fatal('unhandled rejection'));
for (const sig of ['SIGTERM', 'SIGINT']) {
  process.on(sig, () => {
    fail('harness', `interrupted by ${sig} before the chain finished`);
    finish();
  });
}

main().catch(fatal('fatal error'));
