#!/usr/bin/env node
// Seeds an isolated e2e stack (e2e/scripts/up-isolated.sh) with one user per billing state, through the
// public API wherever one exists and SQL only where none does. Idempotent: re-running it converges on
// the same users, orgs and subscriptions, and rewrites users.json.
//
// Environment (up-isolated.sh sets all of them; `source e2e/.stack/<project>/env.sh` gives the first four):
//   BASE_URL, PADDLE_MOCK_URL, E2E_PROJECT, E2E_STACK_DIR, E2E_REPO_ROOT (default: two dirs up)
//
// Options:
//   --provider paddle|stripe  (default paddle) which provider's mock completes the checkouts and which
//                             price ids the plans get. With stripe the env also needs STRIPE_MOCK_URL, the
//                             plans' `provider_price_ids_json` gains a `stripe` key (a `paddle` key is kept),
//                             and every subscription is a Stripe one (checkout completed in the Stripe fake).
//
// Users (label → state), written to $E2E_STACK_DIR/users.json:
//   admin     platform admin (Personal org, Trial)
//   trial     Personal org on the Trial plan
//   creator   Personal org subscribed to Creator through the mock checkout
//   team      Owner of the Team org "E2E Team", Team plan with 3 seats
//   canceled  Personal org that subscribed to Creator, then was canceled immediately → back on Trial
//   member    Member (not Owner/Billing) of "E2E Team"; active org is E2E Team
//
// SQL is used for: the plans' provider price ids (no endpoint sets them), the invite token for `member`
// (the API only ever emails it), and reading subscription ids for users.json.

import { execFileSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { randomBytes } from 'node:crypto';

const here = dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = process.env.E2E_REPO_ROOT || resolve(here, '../..');
const PROVIDER = providerArg();
const PROJECT = required('E2E_PROJECT');
const STACK_DIR = process.env.E2E_STACK_DIR || join(REPO_ROOT, 'e2e/.stack', PROJECT);
const BASE_URL = required('BASE_URL').replace(/\/+$/, '');
const MOCK_URL = required(PROVIDER === 'stripe' ? 'STRIPE_MOCK_URL' : 'PADDLE_MOCK_URL').replace(/\/+$/, '');

const env = { ...readEnvFile(join(STACK_DIR, '.env')), ...readEnvFile(join(STACK_DIR, 'secrets.env')) };
const ADMIN_EMAIL = env.ADMIN_EMAIL || 'admin@e2e.test';
const ADMIN_INITIAL_PASSWORD = env.ADMIN_PASSWORD;
const PASSWORD_SALT = env.JWT_SIGNING_KEY;
if (!ADMIN_INITIAL_PASSWORD || !PASSWORD_SALT) die(`ADMIN_PASSWORD / JWT_SIGNING_KEY missing from ${STACK_DIR}; run up-isolated.sh first.`);

const TEAM_ORG_NAME = 'E2E Team';
const PRICE_IDS = PROVIDER === 'stripe'
  ? { creator: 'price_e2e_creator', pro: 'price_e2e_pro', team: 'price_e2e_team' }
  : { creator: 'pri_e2e_creator', pro: 'pri_e2e_pro', team: 'pri_e2e_team' };
// The yearly price ids (stripe-mock serves them with a yearly recurring interval), stored as the `annual` key.
const ANNUAL_PRICE_IDS = PROVIDER === 'stripe'
  ? { creator: 'price_e2e_creator_annual', pro: 'price_e2e_pro_annual', team: 'price_e2e_team_annual' }
  : { creator: 'pri_e2e_creator_annual', pro: 'pri_e2e_pro_annual', team: 'pri_e2e_team_annual' };

// ------------------------------------------------------------------ helpers

/** `--provider paddle|stripe` (or `--provider=...`); paddle when absent. */
function providerArg() {
  const argv = process.argv.slice(2);
  let value = 'paddle';
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === '--provider') value = argv[++i] ?? '';
    else if (argv[i].startsWith('--provider=')) value = argv[i].slice('--provider='.length);
    else die(`unknown argument ${argv[i]}`);
  }
  value = value.toLowerCase();
  if (value !== 'paddle' && value !== 'stripe') die(`--provider must be paddle or stripe (got '${value}')`);
  return value;
}

function required(name) {
  const v = process.env[name];
  if (!v) die(`${name} is not set (source e2e/.stack/<project>/env.sh, or run up-isolated.sh).`);
  return v;
}

function die(msg) {
  console.error(`seed-billing: ${msg}`);
  process.exit(1);
}

function log(msg) {
  console.log(`seed-billing: ${msg}`);
}

function readEnvFile(path) {
  const out = {};
  if (!existsSync(path)) return out;
  for (const line of readFileSync(path, 'utf8').split('\n')) {
    const m = /^([A-Z0-9_]+)=(.*)$/.exec(line.trim());
    if (m) out[m[1]] = m[2];
  }
  return out;
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** The password a seeded user ends up with: derived from this stack's own secret, so re-runs know it. */
function finalPassword(label) {
  return `E2e-${label}-${createHash('sha256').update(`${PASSWORD_SALT}:${label}`).digest('hex').slice(0, 16)}`;
}

function psql(sql) {
  const args = [
    'compose', '-p', PROJECT, '--project-directory', REPO_ROOT,
    '--env-file', join(STACK_DIR, '.env'),
    '-f', join(REPO_ROOT, 'docker-compose.yml'), '-f', join(REPO_ROOT, 'docker-compose.e2e.yml'),
    'exec', '-T', 'postgres',
    'psql', '-U', env.POSTGRES_USER || 'postgres', '-d', env.POSTGRES_DB || 'reelforge',
    '-v', 'ON_ERROR_STOP=1', '-qtAX', '-c', sql,
  ];
  return execFileSync('docker', args, { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

const sqlStr = (s) => `'${String(s).replace(/'/g, "''")}'`;

async function http(method, path, { token, body, base = BASE_URL, expect } = {}) {
  const headers = { accept: 'application/json' };
  if (body !== undefined) headers['content-type'] = 'application/json';
  if (token) headers.authorization = `Bearer ${token}`;
  const res = await fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text();
  let json = null;
  try {
    json = text ? JSON.parse(text) : null;
  } catch {
    json = null;
  }
  if (expect && !expect.includes(res.status)) {
    throw new Error(`${method} ${path} -> ${res.status}: ${text.slice(0, 400)}`);
  }
  return { status: res.status, json, text };
}

async function waitFor(what, fn, { timeoutMs = 60_000, intervalMs = 500 } = {}) {
  const until = Date.now() + timeoutMs;
  let last;
  while (Date.now() < until) {
    try {
      last = await fn();
      if (last) return last;
    } catch (err) {
      last = err;
    }
    await sleep(intervalMs);
  }
  throw new Error(`timed out waiting for ${what}${last instanceof Error ? `: ${last.message}` : ''}`);
}

// ------------------------------------------------------------------ auth

async function login(email, password) {
  const r = await http('POST', '/api/v1/auth/token', { body: { email, password } });
  return r.status === 200 ? r.json : null;
}

let legal;
async function acceptTermsIfNeeded(session) {
  if (session.termsVersion && session.termsVersion === legal.termsVersion) return session;
  const r = await http('POST', '/api/v1/auth/accept-terms', {
    token: session.accessToken,
    body: { acceptTerms: true, termsVersion: legal.termsVersion, privacyVersion: legal.privacyVersion },
    expect: [200],
  });
  return r.json;
}

/** Signs in as `label`, creating the account (as the admin) and finishing first-login chores if needed. */
async function ensureUser(label, email, adminToken) {
  const password = finalPassword(label);
  let session = await login(email, password);
  if (!session) {
    if (!adminToken) die(`cannot sign in as ${email} and no admin token to create it`);
    let temp;
    const created = await http('POST', '/api/v1/admin/users', { token: adminToken, body: { email, displayName: `E2E ${label}` } });
    if (created.status === 201 || created.status === 200) {
      temp = created.json.temporaryPassword;
    } else {
      // Left over from an interrupted seed with an unknown password: delete and recreate.
      const list = await http('GET', '/api/v1/admin/users', { token: adminToken, expect: [200] });
      const users = Array.isArray(list.json) ? list.json : list.json?.users ?? list.json?.items ?? [];
      const existing = users.find((u) => u.email?.toLowerCase() === email);
      if (!existing) throw new Error(`could not create ${email}: ${created.status} ${created.text}`);
      await http('DELETE', `/api/v1/admin/users/${existing.id}`, { token: adminToken, expect: [200, 204] });
      const again = await http('POST', '/api/v1/admin/users', { token: adminToken, body: { email, displayName: `E2E ${label}` }, expect: [200, 201] });
      temp = again.json.temporaryPassword;
    }
    session = await login(email, temp);
    if (!session) throw new Error(`temporary password for ${email} did not sign in`);
    await http('POST', '/api/v1/auth/change-password', {
      token: session.accessToken,
      body: { currentPassword: temp, newPassword: password },
      expect: [200, 204],
    });
    session = await login(email, password);
    if (!session) throw new Error(`new password for ${email} did not sign in`);
    log(`created ${email}`);
  }
  if (session.mustChangePassword) throw new Error(`${email} still has mustChangePassword`);
  if (!session.emailVerified) {
    psql(`UPDATE application_users SET email_verified_at = now() WHERE lower(email) = ${sqlStr(email)} AND email_verified_at IS NULL`);
    session = await login(email, password);
  }
  session = await acceptTermsIfNeeded(session);
  return { ...session, password };
}

async function ensureAdmin() {
  const password = finalPassword('admin');
  let session = await login(ADMIN_EMAIL, password);
  if (!session) {
    session = await login(ADMIN_EMAIL, ADMIN_INITIAL_PASSWORD);
    if (!session) die(`cannot sign in as ${ADMIN_EMAIL} with either the seeded or the initial password`);
    await http('POST', '/api/v1/auth/change-password', {
      token: session.accessToken,
      body: { currentPassword: ADMIN_INITIAL_PASSWORD, newPassword: password },
      expect: [200, 204],
    });
    session = await login(ADMIN_EMAIL, password);
    log(`admin password changed`);
  }
  session = await acceptTermsIfNeeded(session);
  return { ...session, password };
}

async function switchOrg(session, orgId) {
  if (session.org === orgId) return session;
  const r = await http('POST', '/api/v1/auth/switch-org', { token: session.accessToken, body: { organizationId: orgId }, expect: [200] });
  return { ...session, ...r.json };
}

// ------------------------------------------------------------------ billing

async function summary(session) {
  const r = await http('GET', '/api/v1/billing/summary', { token: session.accessToken, expect: [200] });
  return r.json;
}

async function subscribe(session, plan, seats) {
  const r = await http('POST', '/api/v1/billing/checkout', {
    token: session.accessToken,
    body: seats ? { plan, seats } : { plan },
    expect: [200],
  });
  const { checkoutUrl, transactionId } = r.json;
  if (!checkoutUrl?.startsWith(MOCK_URL)) throw new Error(`checkout URL ${checkoutUrl} does not point at the mock (${MOCK_URL})`);
  if (PROVIDER === 'stripe') {
    // transactionId is the Checkout Session id.
    await http('POST', '/__mock/complete-checkout', { base: MOCK_URL, body: { session_id: transactionId }, expect: [200] });
  } else {
    await http('POST', `/__mock/transactions/${transactionId}/complete`, { base: MOCK_URL, body: {}, expect: [200] });
  }
  return waitFor(`${plan} subscription for ${session.email}`, async () => {
    const s = await summary(session);
    const sub = s.subscription;
    return sub?.planKey === plan && sub.status === 'Active' && sub.hasBillingCustomer && (!seats || sub.seats === seats) ? s : null;
  });
}

function latestProviderSubscription(orgId) {
  const row = psql(`SELECT provider_subscription_id || '|' || status FROM subscriptions WHERE organization_id = ${sqlStr(orgId)} AND provider = ${sqlStr(PROVIDER)} ORDER BY created_at DESC LIMIT 1`);
  if (!row) return null;
  const [id, status] = row.split('|');
  return { id, status };
}

function customerId(orgId) {
  return psql(`SELECT provider_customer_id FROM billing_customers WHERE organization_id = ${sqlStr(orgId)} AND provider = ${sqlStr(PROVIDER)} LIMIT 1`) || null;
}

// ------------------------------------------------------------------ main

async function main() {
  await waitFor('the stack (/health, /api/v1/health)', async () => {
    const a = await http('GET', '/health');
    const b = await http('GET', '/api/v1/health');
    return a.status === 200 && b.status === 200;
  }, { timeoutMs: 180_000, intervalMs: 2000 });
  await waitFor(`the ${PROVIDER === 'stripe' ? 'Stripe' : 'Paddle'} mock`, async () => (await http('GET', '/__mock/health', { base: MOCK_URL })).status === 200);

  // Plans carry no provider price ids in the catalog; the seeder never touches the column, so this sticks.
  // Paddle keeps its original statement (it replaces the column); Stripe merges its key into whatever is
  // there, so a `paddle` key survives.
  for (const [key, price] of Object.entries(PRICE_IDS)) {
    if (PROVIDER === 'stripe') {
      psql(`UPDATE plans SET provider_price_ids_json = COALESCE(provider_price_ids_json, '{}'::jsonb) || ${sqlStr(JSON.stringify({ stripe: { monthly: price, annual: ANNUAL_PRICE_IDS[key] } }))}::jsonb WHERE key = ${sqlStr(key)}`);
    } else {
      psql(`UPDATE plans SET provider_price_ids_json = ${sqlStr(JSON.stringify({ paddle: { monthly: price, annual: ANNUAL_PRICE_IDS[key] } }))}::jsonb WHERE key = ${sqlStr(key)}`);
    }
  }
  log(`plan price ids set (${Object.values(PRICE_IDS).join(', ')})`);

  legal = (await http('GET', '/api/v1/legal/documents', { expect: [200] })).json;

  const users = {};
  const record = (label, session, extra = {}) => {
    const sub = latestProviderSubscription(session.org);
    users[label] = {
      email: session.email,
      password: session.password,
      orgId: session.org,
      orgName: session.orgName,
      orgRole: session.orgRole,
      ...(sub ? { subscriptionId: sub.id, subscriptionStatus: sub.status } : {}),
      ...(customerId(session.org) ? { customerId: customerId(session.org) } : {}),
      ...extra,
    };
  };

  const admin = await ensureAdmin();
  record('admin', admin, { platformAdmin: true });

  // trial
  const trial = await ensureUser('trial', 'trial@e2e.test', admin.accessToken);
  const trialSummary = await summary(trial); // also lands the one-time trial grant
  if (trialSummary.subscription?.planKey !== 'trial') throw new Error(`trial user is on ${trialSummary.subscription?.planKey}`);
  record('trial', trial, { planKey: 'trial' });

  // creator
  const creator = await ensureUser('creator', 'creator@e2e.test', admin.accessToken);
  if ((await summary(creator)).subscription?.planKey !== 'creator') {
    await subscribe(creator, 'creator');
    log('creator subscribed');
  }
  record('creator', creator, { planKey: 'creator' });

  // team: its own Team org, 3 seats
  let team = await ensureUser('team', 'team@e2e.test', admin.accessToken);
  const orgs = (await http('GET', '/api/v1/orgs', { token: team.accessToken, expect: [200] })).json;
  const orgList = Array.isArray(orgs) ? orgs : orgs?.organizations ?? orgs?.items ?? [];
  let teamOrg = orgList.find((o) => o.kind === 'Team' && o.name === TEAM_ORG_NAME);
  if (!teamOrg) {
    teamOrg = (await http('POST', '/api/v1/orgs', { token: team.accessToken, body: { name: TEAM_ORG_NAME }, expect: [200, 201] })).json;
    log(`created org ${TEAM_ORG_NAME}`);
  }
  team = await switchOrg(team, teamOrg.id);
  const teamState = (await summary(team)).subscription;
  if (teamState?.planKey !== 'team' || teamState.seats !== 3) {
    await subscribe(team, 'team', 3);
    log('team subscribed with 3 seats');
  }
  record('team', team, { planKey: 'team', seats: 3 });

  // canceled: subscribed, then canceled immediately in the mock → falls back to Trial
  const canceled = await ensureUser('canceled', 'canceled@e2e.test', admin.accessToken);
  let prior = latestProviderSubscription(canceled.org);
  if (!prior || prior.status !== 'Canceled') {
    if ((await summary(canceled)).subscription?.planKey !== 'creator') {
      await subscribe(canceled, 'creator');
    }
    prior = latestProviderSubscription(canceled.org);
    if (PROVIDER === 'stripe') {
      await http('POST', `/__mock/subscriptions/${prior.id}/cancel-now`, { base: MOCK_URL, body: {}, expect: [200] });
    } else {
      await http('POST', `/__mock/subscriptions/${prior.id}/cancel`, { base: MOCK_URL, body: { effective_from: 'immediately' }, expect: [200] });
    }
    await waitFor('the canceled user to fall back to trial', async () => (await summary(canceled)).subscription?.planKey === 'trial');
    log('canceled user subscribed and canceled');
  }
  record('canceled', canceled, { planKey: 'trial' });

  // member: Member of E2E Team. The invite token is only ever emailed, so the seed writes the hash of a
  // token it knows over the invite it just created, then accepts through the API.
  let member = await ensureUser('member', 'member@e2e.test', admin.accessToken);
  const members = (await http('GET', `/api/v1/orgs/${teamOrg.id}/members`, { token: team.accessToken, expect: [200] })).json;
  const memberList = Array.isArray(members) ? members : members?.members ?? members?.items ?? [];
  if (!memberList.some((m) => m.email?.toLowerCase() === 'member@e2e.test')) {
    const invite = await http('POST', `/api/v1/orgs/${teamOrg.id}/invites`, {
      token: team.accessToken,
      body: { email: 'member@e2e.test', role: 'Member' },
      expect: [200, 201, 409],
    });
    let inviteId = invite.json?.id;
    if (!inviteId) {
      inviteId = psql(`SELECT id FROM organization_invites WHERE organization_id = ${sqlStr(teamOrg.id)} AND lower(email) = 'member@e2e.test' ORDER BY created_at DESC LIMIT 1`);
    }
    const token = randomBytes(32).toString('base64url');
    psql(`UPDATE organization_invites SET token_hash = ${sqlStr(createHash('sha256').update(token).digest('hex'))} WHERE id = ${sqlStr(inviteId)}`);
    await http('POST', `/api/v1/auth/invitations/${token}/accept`, { token: member.accessToken, body: {}, expect: [200] });
    log('member joined E2E Team');
    member = { ...(await login('member@e2e.test', member.password)), password: member.password };
  }
  member = await switchOrg(member, teamOrg.id);
  record('member', member, { planKey: 'team' });

  writeFileSync(join(STACK_DIR, 'users.json'), `${JSON.stringify(users, null, 2)}\n`, { mode: 0o600 });
  log(`wrote ${join(STACK_DIR, 'users.json')}`);
  for (const [label, u] of Object.entries(users)) {
    log(`  ${label.padEnd(9)} ${u.email.padEnd(20)} ${String(u.orgName).padEnd(28)} ${u.orgRole}${u.subscriptionId ? ` ${u.subscriptionId} (${u.subscriptionStatus})` : ''}`);
  }
}

main().catch((err) => die(err.stack || String(err)));
