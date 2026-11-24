// Starts the real web/ app (`next dev`) on a free port and tears it down completely. (A production build was
// tried and rejected: it type-checks every file, which fails on a checkout whose node_modules lag the lockfile,
// and it rewrites the tracked web/next-env.d.ts.)
import { spawn } from 'node:child_process';
import { closeSync, existsSync, openSync, readFileSync, rmSync, writeFileSync } from 'node:fs';
import { createServer } from 'node:net';
import { join } from 'node:path';
import { webDir } from './env.mjs';

function freePort() {
  return new Promise((resolvePort, reject) => {
    const srv = createServer();
    srv.once('error', reject);
    srv.listen(0, '127.0.0.1', () => {
      const { port } = srv.address();
      srv.close(() => resolvePort(port));
    });
  });
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/**
 * The server renders the dashboard greeting ("Good morning") with ITS clock, which the browser's frozen
 * clock cannot reach, and a text mismatch is not repaired on hydration. Put the server's local time at
 * 08:xx whatever the real time is, with a whole-hour zone (the greeting is "morning" from 05:00 to 11:59,
 * so even a long run cannot cross a boundary). The browser itself is frozen at 09:20 UTC.
 */
export function serverTimeZone(now = new Date()) {
  let offset = (8 - now.getUTCHours()) % 24; // local = UTC + offset
  if (offset > 12) offset -= 24;
  if (offset < -12) offset += 24;
  return offset === 0 ? 'Etc/UTC' : `Etc/GMT${offset > 0 ? '-' : '+'}${Math.abs(offset)}`;
}

// What this run started, so one teardown can stop it whatever stage an interruption arrives in.
const running = { children: [], hadNext: true, nextEnv: null };
const nextEnvFile = join(webDir, 'next-env.d.ts');

const groupAlive = (pid) => {
  try { process.kill(-pid, 0); return true; } catch { return false; }
};

/** Stops every process group this module started (only those), then removes the build output it created. */
export async function stopWeb() {
  for (const child of running.children.splice(0)) {
    if (!child.pid) continue;
    for (const sig of ['SIGTERM', 'SIGKILL']) {
      if (!groupAlive(child.pid)) break;
      try { process.kill(-child.pid, sig); } catch { /* already gone */ }
      for (let i = 0; i < 50 && groupAlive(child.pid); i++) await sleep(100);
    }
    if (groupAlive(child.pid)) throw new Error(`could not stop process group ${child.pid}`);
  }
  if (!running.hadNext) rmSync(join(webDir, '.next'), { recursive: true, force: true });
  // `next dev` rewrites this tracked file (its .next/dev import paths); put back what was there.
  if (running.nextEnv !== null && readFileSync(nextEnvFile, 'utf8') !== running.nextEnv) writeFileSync(nextEnvFile, running.nextEnv);
}

export async function startWeb({ logFile }) {
  const port = await freePort();
  running.hadNext = existsSync(join(webDir, '.next'));
  running.nextEnv = existsSync(nextEnvFile) ? readFileSync(nextEnvFile, 'utf8') : null;
  const out = openSync(logFile, 'w');
  const next = join(webDir, 'node_modules/next/dist/bin/next');
  const env = {
    ...process.env, TZ: serverTimeZone(), NEXT_TELEMETRY_DISABLED: '1', NODE_ENV: 'development', FORCE_COLOR: '0',
    WORKFLOW_APPS_ENABLED: 'true', SIGNUP_ENABLED: 'true', GO_API_URL: 'http://127.0.0.1:9', OAUTH_GOOGLE_ENABLED: 'false', OAUTH_GITHUB_ENABLED: 'false',
  };
  try {
    // detached: the dev server and its workers share one process group we can signal as a unit (and only that unit).
    const child = spawn(process.execPath, [next, 'dev', '--turbopack', '-p', String(port), '-H', '127.0.0.1'], { cwd: webDir, detached: true, stdio: ['ignore', out, out], env });
    running.children.push(child);
    let exited = false;
    child.once('exit', () => { exited = true; });
    const base = `http://127.0.0.1:${port}`;
    const deadline = Date.now() + 120_000;
    for (;;) {
      if (exited) throw new Error(`next dev exited early; see ${logFile}`);
      try {
        const res = await fetch(`${base}/app/login`, { redirect: 'manual' });
        if (res.status < 500) break;
      } catch { /* not up yet */ }
      if (Date.now() > deadline) throw new Error(`next dev did not become ready in 120 s; see ${logFile}`);
      await sleep(500);
    }
    return { base, port };
  } finally {
    closeSync(out);
  }
}
