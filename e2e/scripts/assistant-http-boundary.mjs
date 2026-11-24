#!/usr/bin/env node
/**
 * T-QA-04 condition 2 — the assistant request-body seam at the REAL HTTP BOUNDARY.
 *
 * WHY THIS EXISTS, AND WHY THE C# SEAM TEST IS NOT ENOUGH
 *
 * `AssistantChatSeamContractTests` binds the shared fixture and drives the controller action
 * directly. Its own remarks disclaim what that leaves uncovered: there is no Kestrel and no
 * `TestServer`/`WebApplicationFactory`, so the body never travels through the ASP.NET Core input
 * formatter, `[ApiController]`'s automatic model-state validation, the `[Authorize]` filter or the
 * route table. QA's live reproduction of the critical defect came from exactly that layer — a
 * `threadId` of `__LOCALID_kLSMtGS` answered
 *
 *   HTTP 400  {"$.threadId":["The JSON value could not be converted to …AssistantChatRequest…"]}
 *
 * a response NO in-process test can produce, because the rejection happens before the action is
 * ever entered. This script is that missing round trip.
 *
 * WHAT IT DOES
 *
 *   1. creates a SCRATCH database (never the live `reelbolt` database) and starts the Inference API
 *      built from this checkout against it;
 *   2. POSTs the shared seam fixture — the same file both halves of
 *      `e2e/tests/assistant-seam.contract.spec.ts` are pinned to — over real HTTP, and asserts 200
 *      plus an `X-Assistant-Thread-Id` header naming a non-empty GUID;
 *   3. asserts the NEGATIVE CONTROL: a genuinely malformed `threadId` is still refused, so a green
 *      run cannot be a rubber stamp that would pass against an endpoint that accepts anything;
 *   4. stops the server and drops the scratch database, whatever happened.
 *
 * INVOCATION — a MANUALLY-RUN gate, not a CI job
 *
 *   node e2e/scripts/assistant-http-boundary.mjs
 *
 * (or `npm run test:assistant-http-boundary` from `e2e/`). It is deliberately NOT wired into
 * `.github/**`: it needs a local Docker PostgreSQL and a spare port, neither of which a CI runner
 * for this initiative has. Run it before declaring the seam closed.
 *
 * Requirements: Docker with the `reelbolt-postgres` container running, `dotnet` on PATH, and a
 * built `inference/` tree (`cd inference && dotnet build --no-restore`). Nothing else — no browser,
 * no web server, no provider credential: the turn is persisted before the assistant's model call is
 * reached, and the assertions are on the HTTP status and headers.
 *
 * ENVIRONMENT (all optional; the defaults are this checkout's LOCAL DEVELOPMENT values. They are
 * not all inert — the signing key in particular is a real one, so read the two notes below before
 * copying this block anywhere.)
 *
 *   ASSISTANT_HTTP_PORT        port to serve on                      (default 5099)
 *   ASSISTANT_HTTP_DB          scratch database name                 (default, per run: see below)
 *   POSTGRES_CONTAINER         docker container running PostgreSQL   (default reelbolt-postgres)
 *   PGUSER / PGPASSWORD        PostgreSQL credentials                (default postgres / postgres —
 *                                                                     the same development pair
 *                                                                     docker-compose.yml ships as
 *                                                                     its `:-` fallback)
 *   PG_HOST / PG_PORT          PostgreSQL address                    (default 127.0.0.1 / 5432)
 *   JWT_SIGNING_KEY / JWT_ISSUER / JWT_AUDIENCE                      (required, no default; see the note below)
 *   RABBITMQ_HOST / RABBITMQ_USERNAME / RABBITMQ_PASSWORD            (default guest / guest — the
 *                                                                     broker's own factory default)
 *
 * JWT_SIGNING_KEY is REQUIRED and has no default: the script exits with a message when it is unset.
 * Use the same value the local stack runs with (the one in your untracked `.env`), or any random
 * string of at least 32 characters, because this script starts its own API process with it.
 *
 * ASSISTANT_HTTP_DB is validated before use and on the cleanup path: it must be a plain SQL
 * identifier and must not be the live database. There is no override for that.
 */

import { spawn, spawnSync } from 'node:child_process';
import { createHmac, randomUUID } from 'node:crypto';
import { existsSync, readFileSync } from 'node:fs';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = path.dirname(fileURLToPath(import.meta.url));
const REPO = path.resolve(HERE, '..', '..');

/** The live database this script must NEVER touch. A guard, not a comment. */
const LIVE_DATABASE = 'reelbolt';

const CONFIG = {
  port: Number(process.env.ASSISTANT_HTTP_PORT ?? 5099),
  database:
    process.env.ASSISTANT_HTTP_DB ?? `reelbolt_assistant_httpgate_${process.pid}_${Date.now()}`,
  container: process.env.POSTGRES_CONTAINER ?? 'reelbolt-postgres',
  pgUser: process.env.PGUSER ?? 'postgres',
  pgPassword: process.env.PGPASSWORD ?? 'postgres',
  pgHost: process.env.PG_HOST ?? '127.0.0.1',
  pgPort: process.env.PG_PORT ?? '5432',
  signingKey: process.env.JWT_SIGNING_KEY ?? '',
  issuer: process.env.JWT_ISSUER ?? 'reelbolt-api',
  audience: process.env.JWT_AUDIENCE ?? 'reelbolt-inference',
  rabbitHost: process.env.RABBITMQ_HOST ?? '127.0.0.1',
  rabbitUser: process.env.RABBITMQ_USERNAME ?? 'guest',
  // Deliberately the broker's own factory default, never a real credential: this file is public and
  // an operator's live broker password must not be committed here. The API starts regardless of
  // whether it can reach the broker (the turn is persisted before any model call), so a wrong guess
  // costs nothing; point these at the real values via the environment if the local broker differs.
  rabbitPassword: process.env.RABBITMQ_PASSWORD ?? 'guest',
};

if (CONFIG.signingKey.length < 32) {
  console.error(
    'assistant-http-boundary: JWT_SIGNING_KEY is required (at least 32 characters) and has no default. ' +
      'Export the signing key your local stack uses, or any random 32+ character string, and run again.',
  );
  process.exit(2);
}

/** The shared seam fixture — the SAME file the e2e spec and the C# seam test both pin to. */
const FIXTURE = path.join(
  REPO,
  'inference',
  'tests',
  'ReelBolt.WorkflowEngine.Tests',
  'TestData',
  'assistant-chat-ai-sdk-body.json',
);

/** The route under test. Also asserted against the controller's own attributes — see below. */
const CHAT_PATH = '/api/v1/assistant/chat';

let serverProcess = null;
/** True only once THIS run has successfully created the scratch database — see dropScratchDatabase. */
let createdScratchDatabase = false;
const failures = [];
const results = [];

function report(ok, name, detail) {
  results.push({ ok, name, detail });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}\n      ${detail}`);
  if (!ok) failures.push(name);
}

function fail(message) {
  throw new Error(message);
}

// ---------------------------------------------------------------------------------------------
// JWT
// ---------------------------------------------------------------------------------------------

const base64url = (value) =>
  Buffer.from(value).toString('base64').replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

/**
 * Mints the HS256 token the Go API issues. Claim names are the ones `CurrentUser` reads
 * (`sub` → user id, `email`, `isAdmin`); `iss`/`aud` are what the JwtBearer validation requires.
 */
function mintJwt(userId) {
  const now = Math.floor(Date.now() / 1000);
  const header = { alg: 'HS256', typ: 'JWT' };
  const payload = {
    sub: userId,
    email: 'assistant-http-gate@example.com',
    isAdmin: false,
    mustChangePassword: false,
    iss: CONFIG.issuer,
    aud: CONFIG.audience,
    iat: now,
    exp: now + 3600,
  };

  const signingInput = `${base64url(JSON.stringify(header))}.${base64url(JSON.stringify(payload))}`;
  const signature = createHmac('sha256', CONFIG.signingKey)
    .update(signingInput)
    .digest('base64')
    .replace(/\+/g, '-')
    .replace(/\//g, '_')
    .replace(/=+$/, '');

  return `${signingInput}.${signature}`;
}

// ---------------------------------------------------------------------------------------------
// Scratch database
// ---------------------------------------------------------------------------------------------

function psql(sql, database) {
  const args = ['exec', CONFIG.container, 'psql', '-U', CONFIG.pgUser];
  if (database !== undefined) args.push('-d', database);
  args.push('-tAc', sql);
  return spawnSync('docker', args, { encoding: 'utf8' });
}

/**
 * The JWT's `sub` must name a row in `application_users` — `assistant_threads.user_id` carries a
 * real foreign key to it, and the Go API (which owns the user table) is not part of this gate. So
 * the gate seeds exactly one caller into the SCRATCH database after the API has migrated it.
 */
function seedCaller(userId) {
  const inserted = psql(
    `INSERT INTO application_users ` +
      `(id, email, display_name, created_at, password_hash, is_admin, must_change_password) ` +
      `VALUES ('${userId}', 'assistant-http-gate@example.com', 'Assistant HTTP Gate', now(), 'x', ` +
      `false, false);`,
    CONFIG.database,
  );
  if (inserted.status !== 0) {
    fail(
      `Could not seed the gate's caller into "${CONFIG.database}":\n` +
        `${inserted.stderr || inserted.stdout}`,
    );
  }
  console.log(`      seeded caller ${userId}`);
}

/**
 * The scratch database name, validated ONCE and then reused verbatim.
 *
 * This is the only value the script ever interpolates into SQL, so it is checked here against both
 * the live name and a strict identifier pattern — and it is checked AGAIN on the drop path, because
 * the cleanup `finally` runs even when creation threw. Without that second check a name that create
 * refuses would still reach the `DROP`, which is how a guard that looks sufficient drops the very
 * database it exists to protect.
 */
function assertScratchDatabaseName(name) {
  if (name === LIVE_DATABASE) {
    throw new Error(
      `Refusing to run against the LIVE database "${LIVE_DATABASE}". This gate is destructive by ` +
        'construction: it migrates the schema and writes assistant turns. Pick a scratch name via ' +
        'ASSISTANT_HTTP_DB.',
    );
  }

  // Guard against SQL injection into the identifier before it is interpolated anywhere.
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(name)) {
    throw new Error(
      `ASSISTANT_HTTP_DB="${name}" is not a plain SQL identifier. It must match ` +
        '/^[A-Za-z_][A-Za-z0-9_]*$/ — nothing is interpolated into SQL otherwise.',
    );
  }

  return name;
}

function createScratchDatabase() {
  assertScratchDatabaseName(CONFIG.database);

  const created = psql(`CREATE DATABASE "${CONFIG.database}";`);
  if (created.status !== 0) {
    fail(
      `Could not create the scratch database "${CONFIG.database}" through the ` +
        `"${CONFIG.container}" container:\n${created.stderr || created.stdout}`,
    );
  }

  // Only ever drop what THIS run created. Without this flag, pointing ASSISTANT_HTTP_DB at an
  // existing database of one's own would drop it on the way out — creation failing (because it
  // already exists) is exactly when the cleanup must be most careful, not least.
  createdScratchDatabase = true;
  console.log(`      scratch database: ${CONFIG.database}`);
}

function dropScratchDatabase() {
  if (!createdScratchDatabase) return;

  // Re-validated, not assumed: this runs from the `finally` even when creation threw, so it must
  // make the same judgement on its own rather than trust that create got far enough to make it.
  assertScratchDatabaseName(CONFIG.database);

  // The server must be gone first, or PostgreSQL refuses to drop a database with open connections.
  psql(
    `SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = '${CONFIG.database}';`,
  );
  const dropped = psql(`DROP DATABASE IF EXISTS "${CONFIG.database}";`);
  if (dropped.status !== 0) {
    console.warn(
      `WARNING: could not drop the scratch database "${CONFIG.database}": ` +
        `${dropped.stderr || dropped.stdout}`,
    );
  } else {
    console.log(`      dropped scratch database: ${CONFIG.database}`);
  }
}

// ---------------------------------------------------------------------------------------------
// Server
// ---------------------------------------------------------------------------------------------

async function startServer() {
  const connectionString =
    `Host=${CONFIG.pgHost};Port=${CONFIG.pgPort};Database=${CONFIG.database};` +
    `Username=${CONFIG.pgUser};Password=${CONFIG.pgPassword}`;

  const env = {
    ...process.env,
    ASPNETCORE_ENVIRONMENT: 'Development',
    ConnectionStrings__DefaultConnection: connectionString,
    RabbitMQ__Host: CONFIG.rabbitHost,
    RabbitMQ__Username: CONFIG.rabbitUser,
    RabbitMQ__Password: CONFIG.rabbitPassword,
    Jwt__SigningKey: CONFIG.signingKey,
    Jwt__Issuer: CONFIG.issuer,
    Jwt__Audience: CONFIG.audience,
  };

  let log = '';
  serverProcess = spawn(
    'dotnet',
    [
      'run',
      '--no-restore',
      '--no-launch-profile',
      '--project',
      'src/ReelBolt.Inference.Api',
      '--urls',
      `http://127.0.0.1:${CONFIG.port}`,
    ],
    { cwd: path.join(REPO, 'inference'), env, stdio: ['ignore', 'pipe', 'pipe'] },
  );

  serverProcess.stdout.on('data', (chunk) => {
    log += String(chunk);
  });
  serverProcess.stderr.on('data', (chunk) => {
    log += String(chunk);
  });

  const health = `http://127.0.0.1:${CONFIG.port}/api/v1/health`;
  const deadline = Date.now() + 180_000;

  while (Date.now() < deadline) {
    if (serverProcess.exitCode !== null) {
      fail(
        `The Inference API exited with code ${serverProcess.exitCode} before becoming healthy.\n` +
          `--- server output ---\n${log}`,
      );
    }
    try {
      const response = await fetch(health);
      if (response.ok) {
        console.log(`      server healthy on http://127.0.0.1:${CONFIG.port}`);
        return;
      }
    } catch {
      // Not listening yet.
    }
    await new Promise((resolve) => setTimeout(resolve, 1000));
  }

  fail(`The Inference API did not become healthy within 180s.\n--- server output ---\n${log}`);
}

async function stopServer() {
  if (serverProcess === null || serverProcess.exitCode !== null) return;
  serverProcess.kill('SIGTERM');
  const deadline = Date.now() + 15_000;
  while (Date.now() < deadline && serverProcess.exitCode === null) {
    await new Promise((resolve) => setTimeout(resolve, 200));
  }
  if (serverProcess.exitCode === null) serverProcess.kill('SIGKILL');
  serverProcess = null;
}

// ---------------------------------------------------------------------------------------------
// The checks
// ---------------------------------------------------------------------------------------------

/**
 * Reads a bounded PREFIX of a response body and then cancels the stream.
 *
 * A successful turn is an open `text/event-stream` whose assistant half needs a configured model
 * provider this gate deliberately does not have, so waiting for the body to end would either hang
 * or fail on a stream the server tears down. The prefix is enough: this gate asserts on the HTTP
 * STATUS and the `X-Assistant-Thread-Id` HEADER, both of which are settled before the first frame
 * is written, and a refusal (400) is a small JSON body that arrives whole.
 */
async function readBoundedPrefix(response, ms = 5000, maxBytes = 4000) {
  if (response.body === null) return '';
  const reader = response.body.getReader();
  let text = '';
  const deadline = Date.now() + ms;

  try {
    while (text.length < maxBytes) {
      const remaining = deadline - Date.now();
      if (remaining <= 0) break;
      const chunk = await Promise.race([
        reader.read(),
        new Promise((resolve) => setTimeout(() => resolve({ done: true }), remaining)),
      ]);
      if (chunk.done) break;
      if (chunk.value !== undefined) text += Buffer.from(chunk.value).toString('utf8');
    }
  } catch {
    // A torn-down stream is not a gate failure: the status and headers were already observed.
  }

  try {
    await reader.cancel();
  } catch {
    // Already closed.
  }

  return text;
}

async function postChat(body, token) {
  const response = await fetch(`http://127.0.0.1:${CONFIG.port}${CHAT_PATH}`, {
    method: 'POST',
    headers: {
      'content-type': 'application/json',
      authorization: `Bearer ${token}`,
    },
    body: JSON.stringify(body),
  });

  const status = response.status;
  const headers = response.headers;
  const text = await readBoundedPrefix(response);
  return { status, headers, text };
}

/**
 * The route this script posts to must be the route the controller declares, or a green run would
 * only prove that some URL happens to answer 200.
 */
function assertRouteMatchesController() {
  const source = readFileSync(
    path.join(
      REPO,
      'inference',
      'src',
      'ReelBolt.Inference.Api',
      'Controllers',
      'AssistantChatController.cs',
    ),
    'utf8',
  );
  const controllerRoute = /\[Route\("([^"]+)"\)\]/.exec(source)?.[1];
  const actionRoute = /\[HttpPost\("([^"]+)"\)\]/.exec(source)?.[1];
  const declared = `/${controllerRoute}/${actionRoute}`;
  report(
    declared === CHAT_PATH,
    'the route under test is the one the controller declares',
    `controller declares ${declared}; this script posts to ${CHAT_PATH}`,
  );
}

async function run() {
  if (!existsSync(FIXTURE)) fail(`The shared seam fixture is missing at ${FIXTURE}.`);

  const fixture = JSON.parse(readFileSync(FIXTURE, 'utf8'));
  console.log(`ASSISTANT HTTP-BOUNDARY GATE`);
  console.log(`  fixture: ${path.relative(REPO, FIXTURE)}`);
  console.log(`  body:    ${JSON.stringify(fixture)}\n`);

  assertRouteMatchesController();

  createScratchDatabase();
  await startServer();

  // The caller is seeded AFTER the server is healthy, because it is the server's startup migration
  // that creates `application_users` in the scratch database in the first place.
  const userId = randomUUID();
  seedCaller(userId);
  const token = mintJwt(userId);

  // ---- The seam itself -----------------------------------------------------------------------
  const seam = await postChat(fixture, token);
  const threadHeader = seam.headers.get('x-assistant-thread-id');

  report(
    seam.status === 200,
    'the shared fixture is ACCEPTED over real HTTP (200, not 400)',
    `HTTP ${seam.status}${seam.status === 200 ? '' : ` — ${seam.text.slice(0, 400)}`}`,
  );

  report(
    typeof threadHeader === 'string' &&
      /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(threadHeader),
    'X-Assistant-Thread-Id names a non-empty GUID',
    threadHeader === null
      ? 'header absent — the endpoint did not report the thread it allocated'
      : threadHeader,
  );

  // ---- The negative control ------------------------------------------------------------------
  const malformed = { ...fixture, threadId: '__LOCALID_kLSMtGS' };
  const control = await postChat(malformed, token);

  report(
    control.status === 400,
    'NEGATIVE CONTROL: a genuinely malformed threadId is still refused',
    `HTTP ${control.status}${control.status === 400 ? '' : ` — ${control.text.slice(0, 400)}`}. ` +
      'Without this, the check above would also pass against an endpoint that had stopped ' +
      'validating the body at all.',
  );

  report(
    control.text.includes('$.threadId'),
    'NEGATIVE CONTROL: the refusal is the formatter rejecting the body, not an unrelated error',
    control.text.slice(0, 300) || '(empty body)',
  );
}

// ---------------------------------------------------------------------------------------------
// Entry point
// ---------------------------------------------------------------------------------------------

let exitCode = 0;
try {
  await run();
} catch (error) {
  console.error(`\nERROR: ${error instanceof Error ? error.message : String(error)}`);
  exitCode = 1;
} finally {
  await stopServer();
  dropScratchDatabase();
}

console.log(
  `\n${failures.length === 0 && exitCode === 0 ? 'GREEN' : 'RED'} — ` +
    `${results.filter((r) => r.ok).length}/${results.length} checks passed` +
    (failures.length > 0 ? `; failed: ${failures.join(', ')}` : ''),
);

process.exit(failures.length > 0 || exitCode !== 0 ? 1 : 0);
