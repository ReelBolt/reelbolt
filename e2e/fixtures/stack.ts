/**
 * Helpers for specs that run against an isolated e2e stack (e2e/scripts/up-isolated.sh).
 *
 *   source e2e/.stack/<project>/env.sh   # BASE_URL, PADDLE_MOCK_URL, E2E_STACK_DIR
 *   npx playwright test tests/billing/ --project=desktop
 *
 * - `loadStack()` reads the stack's URLs and the seeded users (users.json).
 * - `loginAs(pageOrContext, label)` signs in as a seeded user through POST /api/v1/auth/token, in that
 *   context only: no storage-state file is shared between specs, so one spec's session never leaks
 *   into another's.
 * - `mock` drives the Paddle mock's control API (state, recorded calls, completing a checkout,
 *   renewing a period, reset).
 * - `stripeMock` drives the Stripe fake's control API the same way, on a stack started with
 *   `up-isolated.sh --provider stripe` (env.sh then also exports STRIPE_MOCK_URL and
 *   E2E_BILLING_PROVIDER=stripe); `isStripeStack()` says which provider the stack runs.
 * - `postToken` / `signIn` sign in with a 429 retry (nginx's auth limiter); `Throwaways` makes
 *   throwaway users (and a Team org with a Member) for specs that change billing state.
 * - `billingSummary` / `expectSummary` / `subscribeViaApi` read and wait for webhook-driven state.
 */
import { expect, request as pwRequest, type APIRequestContext, type APIResponse, type BrowserContext, type Page } from '@playwright/test';
import { execFileSync } from 'child_process';
import { createHash, randomBytes } from 'crypto';
import * as fs from 'fs';
import * as path from 'path';

export type SeededLabel = 'admin' | 'trial' | 'creator' | 'team' | 'canceled' | 'member';

export interface SeededUser {
  email: string;
  password: string;
  orgId: string;
  orgName: string;
  orgRole: string;
  planKey?: string;
  seats?: number;
  subscriptionId?: string;
  subscriptionStatus?: string;
  customerId?: string;
  platformAdmin?: boolean;
}

export interface Stack {
  baseUrl: string;
  mockUrl: string;
  /** The Stripe fake (empty on a Paddle stack). */
  stripeMockUrl: string;
  stackDir: string;
  users: Record<SeededLabel, SeededUser>;
}

/** Reads `KEY=value` / `export KEY=value` lines. */
function readEnvFile(file: string): Record<string, string> {
  const out: Record<string, string> = {};
  if (!fs.existsSync(file)) return out;
  for (const line of fs.readFileSync(file, 'utf8').split('\n')) {
    const m = /^(?:export\s+)?([A-Z0-9_]+)=(.*)$/.exec(line.trim());
    if (m) out[m[1]] = m[2];
  }
  return out;
}

let cached: Stack | undefined;

/**
 * The stack under test. `E2E_STACK_DIR` names it (env.sh exports it); BASE_URL and PADDLE_MOCK_URL
 * from the environment win over the values in env.sh.
 */
export function loadStack(): Stack {
  if (cached) return cached;
  const stackDir = process.env.E2E_STACK_DIR;
  if (!stackDir) {
    throw new Error('E2E_STACK_DIR is not set: run e2e/scripts/up-isolated.sh, then `source e2e/.stack/<project>/env.sh`.');
  }
  const fromFile = readEnvFile(path.join(stackDir, 'env.sh'));
  const usersFile = path.join(stackDir, 'users.json');
  if (!fs.existsSync(usersFile)) {
    throw new Error(`${usersFile} does not exist: the stack has not been seeded (e2e/scripts/seed-billing.mjs).`);
  }
  cached = {
    baseUrl: (process.env.BASE_URL || fromFile.BASE_URL || '').replace(/\/+$/, ''),
    mockUrl: (process.env.PADDLE_MOCK_URL || fromFile.PADDLE_MOCK_URL || '').replace(/\/+$/, ''),
    stripeMockUrl: (process.env.STRIPE_MOCK_URL || fromFile.STRIPE_MOCK_URL || '').replace(/\/+$/, ''),
    stackDir,
    users: JSON.parse(fs.readFileSync(usersFile, 'utf8')),
  };
  if (!cached.baseUrl || !cached.mockUrl) throw new Error(`BASE_URL / PADDLE_MOCK_URL missing for ${stackDir}.`);
  return cached;
}

/**
 * True on a stack started with `up-isolated.sh --provider stripe`. `E2E_BILLING_PROVIDER` from the
 * environment (env.sh exports it) wins; without it the stack's env.sh decides. Never throws, so a spec
 * can ask before `loadStack()` has a stack to load.
 */
export function isStripeStack(): boolean {
  const fromEnv = process.env.E2E_BILLING_PROVIDER;
  if (fromEnv) return fromEnv.toLowerCase() === 'stripe';
  const dir = process.env.E2E_STACK_DIR;
  return dir ? (readEnvFile(path.join(dir, 'env.sh')).E2E_BILLING_PROVIDER ?? '').toLowerCase() === 'stripe' : false;
}

export function user(label: SeededLabel): SeededUser {
  const u = loadStack().users[label];
  if (!u) throw new Error(`No seeded user "${label}" in ${loadStack().stackDir}/users.json.`);
  return u;
}

/**
 * Signs `target` in as the seeded user. The Go API sets the httpOnly session cookie and the readable
 * `reelbolt_user` cookie on the response, so the context's cookie jar is all a page needs afterwards.
 * Returns the login response body (accessToken, org, orgRole, ...).
 */
export async function loginAs(target: Page | BrowserContext, label: SeededLabel): Promise<Record<string, unknown>> {
  const u = user(label);
  return signIn(target, u.email, u.password, u.orgId, label);
}

const sleep = (ms: number) => new Promise((r) => setTimeout(r, ms));

function contextOf(target: Page | BrowserContext): BrowserContext {
  return 'context' in target && typeof target.context === 'function' ? (target as Page).context() : (target as BrowserContext);
}

/**
 * POST /api/v1/auth/token, retried on 429. nginx's auth limiter (`burst=5` in nginx/locations.conf,
 * only its rate is raised for e2e stacks) answers 429 when several workers sign in at the same
 * instant; that is the harness, not the app, so a 429 backs off (jittered, growing) and retries.
 */
export async function postToken(request: APIRequestContext, email: string, password: string): Promise<APIResponse> {
  const { baseUrl } = loadStack();
  for (let attempt = 1; ; attempt++) {
    const res = await request.post(`${baseUrl}/api/v1/auth/token`, { data: { email, password } });
    if (res.status() !== 429 || attempt >= 8) return res;
    await sleep(200 * attempt + Math.floor(Math.random() * 250));
  }
}

/**
 * Signs `target` in with an email and password (429-tolerant), then switches to `orgId` when the
 * session landed elsewhere. The context's cookie jar carries the session afterwards.
 */
export async function signIn(
  target: Page | BrowserContext,
  email: string,
  password: string,
  orgId?: string,
  what: string = email,
): Promise<Record<string, unknown>> {
  const { baseUrl } = loadStack();
  const context = contextOf(target);
  await context.clearCookies();
  const res = await postToken(context.request, email, password);
  expect(res.status(), `login as ${what} (${email})`).toBe(200);
  const body = (await res.json()) as Record<string, unknown>;
  // The org a user lands in is its last-active one; switch to the one the spec expects.
  if (orgId && body.org !== orgId) {
    const sw = await context.request.post(`${baseUrl}/api/v1/auth/switch-org`, { data: { organizationId: orgId } });
    expect(sw.status(), `switch ${what} to ${orgId}`).toBe(200);
    return (await sw.json()) as Record<string, unknown>;
  }
  return body;
}

// ------------------------------------------------------------------ billing helpers

/** GET /api/v1/billing/summary as the page's signed-in user. */
export async function billingSummary(page: Page): Promise<any> {
  const r = await page.request.get(`${loadStack().baseUrl}/api/v1/billing/summary`);
  expect(r.status(), 'billing summary').toBe(200);
  return r.json();
}

/** Polls the summary until `pick(summary)` equals `expected` (webhook-driven state; never a fixed sleep). */
export async function expectSummary<T>(page: Page, pick: (s: any) => T, expected: T, message?: string, timeout = 20_000): Promise<any> {
  await expect.poll(async () => pick(await billingSummary(page)), { message, timeout, intervals: [250, 500, 1000] }).toEqual(expected);
  return billingSummary(page);
}

/**
 * Buys a plan without the browser (setup only): POST /checkout as the page's user, pay it in the mock,
 * and wait for the webhook to land. Returns the summary and the mock's subscription id.
 */
export async function subscribeViaApi(page: Page, plan: string, seats?: number): Promise<{ summary: any; subscriptionId: string; transactionId: string }> {
  const r = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: seats ? { plan, seats } : { plan } });
  expect(r.status(), `checkout ${plan}: ${await r.text()}`).toBe(200);
  const { transactionId } = await r.json();
  const done = await mock.completeTransaction(transactionId);
  const summary = await expectSummary(page, (s) => `${s.subscription?.planKey}/${s.subscription?.status}${seats ? `/${s.subscription?.seats}` : ''}`,
    `${plan}/Active${seats ? `/${seats}` : ''}`, `${plan} active after the webhook`);
  return { summary, subscriptionId: done.subscription.id, transactionId };
}

/** The date as the billing page prints it (`longDate`: the browser's locale, long month). */
export async function longDateIn(page: Page, iso: string): Promise<string> {
  return page.evaluate((v) => new Date(v).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' }), iso);
}

// ------------------------------------------------------------------ throwaway users

export interface Throwaway {
  id: string;
  email: string;
  password: string;
  /** The org this user should act in (its Personal org unless `createTeamOrg` moved it). */
  orgId?: string;
}

function readStackEnv(): Record<string, string> {
  const { stackDir } = loadStack();
  return { ...readEnvFile(path.join(stackDir, '.env')), ...readEnvFile(path.join(stackDir, 'secrets.env')) };
}

/** Runs one SQL statement in this stack's postgres (only for what no endpoint does, e.g. an invite token). */
export function psql(sql: string): string {
  const { stackDir } = loadStack();
  const env = readStackEnv();
  const project = env.COMPOSE_PROJECT_NAME || process.env.E2E_PROJECT;
  if (!project) throw new Error(`COMPOSE_PROJECT_NAME missing from ${stackDir}/.env`);
  const repoRoot = path.resolve(stackDir, '../../..');
  return execFileSync('docker', [
    'compose', '-p', project, '--project-directory', repoRoot,
    '--env-file', path.join(stackDir, '.env'),
    '-f', path.join(repoRoot, 'docker-compose.yml'), '-f', path.join(repoRoot, 'docker-compose.e2e.yml'),
    'exec', '-T', 'postgres',
    'psql', '-U', env.POSTGRES_USER || 'postgres', '-d', env.POSTGRES_DB || 'reelforge',
    '-v', 'ON_ERROR_STOP=1', '-qtAX', '-c', sql,
  ], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] }).trim();
}

export const sqlStr = (s: string) => `'${s.replace(/'/g, "''")}'`;

/**
 * Forgets the organization's pending reconcile watch (`change_requested_at = NULL`), as the sweep does once it
 * finds a subscription in sync. Creating a Checkout Session records such a watch and a provider with no
 * `updated_at` (Stripe) ages a drift from the watch's OLDEST due time, so a watch the checkout left behind
 * would make the next change look overdue at once. Specs that assert "too recent" (409 change_pending) call this
 * right after their setup purchase, so the change they test is the only thing the watch is counting from.
 */
export function clearReconcileWatch(orgId: string): void {
  psql(`UPDATE billing_reconcile_watches SET change_requested_at = NULL WHERE organization_id = ${sqlStr(orgId)}`);
}

/**
 * Throwaway users for one spec file: fresh verified Owners of their own Personal org on the Trial
 * plan, created through the admin API (as the seed does) and deleted by `cleanup()` in `afterAll`.
 * Mutating specs use these, never the seeded users.
 */
export class Throwaways {
  private readonly made: Throwaway[] = [];
  private api: APIRequestContext | undefined;
  private adminToken = '';

  constructor(private readonly prefix: string) {}

  private async admin(): Promise<{ api: APIRequestContext; headers: Record<string, string> }> {
    if (!this.api) {
      this.api = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
      const a = user('admin');
      const r = await postToken(this.api, a.email, a.password);
      expect(r.status(), 'admin sign-in').toBe(200);
      this.adminToken = (await r.json()).accessToken;
    }
    return { api: this.api, headers: { Authorization: `Bearer ${this.adminToken}` } };
  }

  async create(tag: string): Promise<Throwaway> {
    const { api, headers } = await this.admin();
    const email = `${this.prefix}-${tag}-${Date.now()}-${randomBytes(3).toString('hex')}@e2e.test`;
    const created = await api.post('/api/v1/admin/users', { headers, data: { email, displayName: `E2E ${this.prefix} ${tag}` } });
    expect(created.status(), await created.text()).toBeLessThan(300);
    const { user: u, temporaryPassword } = await created.json();
    const t: Throwaway = { id: u.id, email, password: `E2e-${randomBytes(9).toString('base64url')}-1a` };
    this.made.push(t);
    // Its OWN request context: nginx turns a sign-in's session cookie into the Authorization header,
    // so sharing the admin's context would make later admin calls run as this user.
    const own = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
    try {
      const r1 = await postToken(own, email, temporaryPassword);
      expect(r1.status()).toBe(200);
      const t1 = (await r1.json()).accessToken;
      const cp = await own.post('/api/v1/auth/change-password', { headers: { Authorization: `Bearer ${t1}` }, data: { currentPassword: temporaryPassword, newPassword: t.password } });
      expect(cp.status(), await cp.text()).toBeLessThan(300);
      const r2 = await postToken(own, email, t.password);
      expect(r2.status()).toBe(200);
      const s2 = await r2.json();
      const legal = await (await own.get('/api/v1/legal/documents')).json();
      const at = await own.post('/api/v1/auth/accept-terms', {
        headers: { Authorization: `Bearer ${s2.accessToken}` },
        data: { acceptTerms: true, termsVersion: legal.termsVersion, privacyVersion: legal.privacyVersion },
      });
      expect(at.status()).toBe(200);
      t.orgId = s2.org;
    } finally {
      await own.dispose();
    }
    return t;
  }

  /** Signs the page in as `t`, in `t.orgId`. */
  async login(target: Page | BrowserContext, t: Throwaway): Promise<Record<string, unknown>> {
    return signIn(target, t.email, t.password, t.orgId, `throwaway ${t.email}`);
  }

  /** Runs `fn` with a fresh request context and a Bearer token for `t` (in `orgId` if given). */
  private async as<T>(t: Throwaway, orgId: string | undefined, fn: (api: APIRequestContext, headers: Record<string, string>) => Promise<T>): Promise<T> {
    const api = await pwRequest.newContext({ baseURL: loadStack().baseUrl });
    try {
      const r = await postToken(api, t.email, t.password);
      expect(r.status(), `sign in ${t.email}`).toBe(200);
      let session = await r.json();
      if (orgId && session.org !== orgId) {
        const sw = await api.post('/api/v1/auth/switch-org', { headers: { Authorization: `Bearer ${session.accessToken}` }, data: { organizationId: orgId } });
        expect(sw.status(), `switch ${t.email} to ${orgId}`).toBe(200);
        session = { ...session, ...(await sw.json()) };
      }
      return await fn(api, { Authorization: `Bearer ${session.accessToken}` });
    } finally {
      await api.dispose();
    }
  }

  /** Creates a Team org owned by `owner` and makes it `owner`'s org from now on. */
  async createTeamOrg(owner: Throwaway, name: string): Promise<string> {
    return this.as(owner, undefined, async (api, headers) => {
      const r = await api.post('/api/v1/orgs', { headers, data: { name } });
      expect(r.status(), await r.text()).toBeLessThan(300);
      owner.orgId = (await r.json()).id;
      return owner.orgId!;
    });
  }

  /**
   * Adds `member` to `owner`'s current org with `role`, through the real invite + accept endpoints.
   * The invite token is only ever emailed, so (as the seed does) the hash of a known token is written
   * over the invite by SQL before accepting it.
   */
  async addMember(owner: Throwaway, member: Throwaway, role: 'Member' | 'Admin' | 'Billing' = 'Member'): Promise<void> {
    const orgId = owner.orgId!;
    const inviteId = await this.as(owner, orgId, async (api, headers) => {
      const inv = await api.post(`/api/v1/orgs/${orgId}/invites`, { headers, data: { email: member.email, role } });
      expect(inv.status(), await inv.text()).toBeLessThan(300);
      const id: string | undefined = (await inv.json().catch(() => ({})))?.id;
      return id || psql(`SELECT id FROM organization_invites WHERE organization_id = ${sqlStr(orgId)} AND lower(email) = ${sqlStr(member.email.toLowerCase())} ORDER BY created_at DESC LIMIT 1`);
    });
    const token = randomBytes(24).toString('base64url');
    psql(`UPDATE organization_invites SET token_hash = ${sqlStr(createHash('sha256').update(token).digest('hex'))} WHERE id = ${sqlStr(inviteId)}`);
    await this.as(member, undefined, async (api, headers) => {
      const acc = await api.post(`/api/v1/auth/invitations/${token}/accept`, { headers, data: {} });
      expect(acc.status(), await acc.text()).toBe(200);
    });
    member.orgId = orgId;
  }

  /** Deletes every throwaway made by this instance (members first, so their orgs' owners go last). */
  async cleanup(): Promise<void> {
    if (!this.api) return;
    const { api, headers } = await this.admin();
    for (const t of [...this.made].reverse()) {
      const r = await api.delete(`/api/v1/admin/users/${t.id}`, { headers });
      if (r.status() >= 300 && r.status() !== 404) console.warn(`could not delete throwaway ${t.email}: ${r.status()} ${await r.text()}`);
    }
    this.made.length = 0;
    await api.dispose();
    this.api = undefined;
  }
}

// ------------------------------------------------------------------ Paddle mock control client

export interface MockCall {
  at: string;
  method: string;
  path: string;
  query?: string;
  body: unknown;
  paddleVersion: string | null;
  status: number;
}

export interface MockWebhookDelivery {
  /** Delivery id (`dlv_...`), for `mock.redeliver`. */
  id: string;
  event_id: string;
  event_type: string;
  occurred_at: string;
  subscription_id: string | null;
  transaction_id: string | null;
  delivered: boolean;
  /** Lost on purpose by a `mock.dropWebhooks` filter: recorded, never sent. */
  dropped: boolean;
  drop_filter?: string;
  /** Set on a `mock.redeliver` resend: the delivery it repeats. */
  redelivery_of?: string;
  attempts: { at: string; status: number; body: string }[];
}

export interface MockDropFilter {
  id: string;
  event_type: string | null;
  subscription_id: string | null;
  transaction_id: string | null;
  count: number;
  remaining: number;
  dropped: number;
}

/** The Paddle API key the e2e stack gives the mock (docker-compose.e2e.yml). */
const MOCK_API_KEY = 'e2e-mock-key';

export interface MockState {
  customers: Record<string, any>[];
  transactions: Record<string, any>[];
  subscriptions: Record<string, any>[];
  portalSessions: Record<string, any>[];
  webhooks: MockWebhookDelivery[];
  dropFilters: MockDropFilter[];
}

export interface MockDiscount {
  id: string;
  status: 'active' | 'archived';
  description: string;
  type: 'percentage' | 'flat' | 'flat_per_seat';
  amount: string;
  currency_code: string | null;
  recur: boolean;
  maximum_recurring_intervals: number | null;
  usage_limit: number | null;
  restrict_to: string[] | null;
  expires_at: string | null;
  custom_data: Record<string, any> | null;
  times_used: number;
  [key: string]: unknown;
}

export interface MockTransaction {
  id: string;
  status: string;
  discount_id?: string;
  details?: { totals: { subtotal: string; discount: string; tax: string; total: string; grand_total: string; currency_code: string } };
  custom_data: Record<string, any> | null;
  /** The request body exactly as the Inference API sent it. */
  request: Record<string, any> | null;
  [key: string]: unknown;
}

async function mockFetch<T>(method: string, route: string, body?: unknown): Promise<T> {
  const { mockUrl } = loadStack();
  const res = await fetch(`${mockUrl}/__mock/${route}`, {
    method,
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`mock ${method} /__mock/${route} -> ${res.status}: ${text}`);
  return JSON.parse(text) as T;
}

export const mock = {
  state: () => mockFetch<MockState>('GET', 'state'),
  /** Every Paddle API call the Inference API made; `filter.path` is a substring match. */
  calls: async (filter: { path?: string; method?: string } = {}) => {
    const q = new URLSearchParams();
    if (filter.path) q.set('path', filter.path);
    if (filter.method) q.set('method', filter.method);
    const r = await mockFetch<{ calls: MockCall[] }>('GET', `calls${q.size ? `?${q}` : ''}`);
    return r.calls;
  },
  webhooks: async () => (await mockFetch<{ webhooks: MockWebhookDelivery[] }>('GET', 'webhooks')).webhooks,
  /** Every discount the Inference API created (POST /discounts), with `status` and `times_used`. */
  discounts: async () => (await mockFetch<{ discounts: MockDiscount[] }>('GET', 'discounts')).discounts,
  /** Every transaction as Paddle shows it, plus `request`: the exact body the Inference API sent. */
  transactions: async () => (await mockFetch<{ transactions: MockTransaction[] }>('GET', 'transactions')).transactions,
  /** Pays a checkout without the browser; webhooks follow ~300 ms later (`delayMs` to change). */
  completeTransaction: (id: string, delayMs?: number) =>
    mockFetch<{ transaction: any; subscription: any | null }>('POST', `transactions/${encodeURIComponent(id)}/complete`, delayMs === undefined ? {} : { delayMs }),
  /** Renews the subscription (new period); a scheduled cancel takes effect instead. Waits for the webhook. */
  advancePeriod: (subscriptionId: string) =>
    mockFetch<{ subscription: any; webhook: MockWebhookDelivery }>('POST', `subscriptions/${encodeURIComponent(subscriptionId)}/advance-period`, {}),
  /** Cancels as Paddle would, without going through the app (e.g. `immediately`). */
  cancelSubscription: (subscriptionId: string, effectiveFrom: 'immediately' | 'next_billing_period' = 'immediately') =>
    mockFetch<{ data: any }>('POST', `subscriptions/${encodeURIComponent(subscriptionId)}/cancel`, { effective_from: effectiveFrom }),
  /**
   * What the subscription's next upgrade/seat-increase charges (origin `subscription_update`) do:
   * `pay` (default; transaction.completed follows subscription.updated), `hold` (stay billed until
   * `releasePayment`/`failPayment`), `fail` (transaction.payment_failed, never completed).
   */
  setPayments: (subscriptionId: string, mode: 'pay' | 'hold' | 'fail') =>
    mockFetch<{ mode: string }>('POST', `subscriptions/${encodeURIComponent(subscriptionId)}/payments`, { mode }),
  /** The subscription's proration charges as Paddle would send them, oldest first. */
  prorationTransactions: async (subscriptionId: string) =>
    (await mockFetch<{ transactions: any[] }>('GET', `subscriptions/${encodeURIComponent(subscriptionId)}/transactions`)).transactions,
  /** Pays a held proration charge: transaction.completed, delivered before this resolves. */
  releasePayment: (transactionId: string) =>
    mockFetch<{ transaction: any; webhook: MockWebhookDelivery | null }>('POST', `transactions/${encodeURIComponent(transactionId)}/complete`, {}),
  /** Fails a held proration charge: transaction.payment_failed (unless `notify` is false), never completed. */
  failPayment: (transactionId: string, notify = true) =>
    mockFetch<{ transaction: any; webhook: MockWebhookDelivery | null }>('POST', `transactions/${encodeURIComponent(transactionId)}/fail`, { notify }),
  /** Refunds a completed transaction: adjustment.created (refund, full, approved unless told otherwise), delivered. */
  refund: (transactionId: string, opts: { action?: 'refund' | 'chargeback'; type?: 'full' | 'partial'; status?: string } = {}) =>
    mockFetch<{ adjustment: any; webhook: MockWebhookDelivery }>('POST', `transactions/${encodeURIComponent(transactionId)}/refund`, opts),
  /** Sends a signed webhook (`secret`/`signature` override the signing for negative tests). */
  sendWebhook: (event: { event_type: string; data: unknown; event_id?: string; occurred_at?: string; secret?: string; signature?: string }) =>
    mockFetch<{ event: unknown; delivery: MockWebhookDelivery }>('POST', 'webhooks/send', event),
  /**
   * Loses the next `count` (default 1) webhooks matching every given field: each is recorded in
   * `webhooks()` with `dropped: true` and never sent. Arm it before the call that causes the webhook;
   * `reset()` clears filters.
   */
  dropWebhooks: (filter: { event_type?: string; subscription_id?: string; transaction_id?: string; count?: number } = {}) =>
    mockFetch<{ filter: MockDropFilter }>('POST', 'webhooks/drop', filter),
  /**
   * Sends a recorded delivery (dropped or not) again with its original event_id and occurred_at,
   * freshly signed; resolves with the receiver's HTTP status once it answered.
   */
  redeliver: (deliveryId: string, attempts?: number) =>
    mockFetch<{ event: any; delivery: MockWebhookDelivery; status: number }>(
      'POST',
      `webhooks/${encodeURIComponent(deliveryId)}/redeliver`,
      attempts === undefined ? {} : { attempts },
    ),
  /**
   * The subscription's transactions through the mock's Paddle API (`GET /transactions`), as the
   * reconciler reads them; `filter` adds Paddle's `status` / `origin` / `billed_at[GTE]` / `per_page`.
   */
  listTransactions: async (
    subscriptionId: string,
    filter: { status?: string; origin?: string; billedAtGte?: string; perPage?: number } = {},
  ) => {
    const q = new URLSearchParams({ subscription_id: subscriptionId });
    if (filter.status) q.set('status', filter.status);
    if (filter.origin) q.set('origin', filter.origin);
    if (filter.billedAtGte) q.set('billed_at[GTE]', filter.billedAtGte);
    if (filter.perPage !== undefined) q.set('per_page', String(filter.perPage));
    const { mockUrl } = loadStack();
    const res = await fetch(`${mockUrl}/transactions?${q}`, { headers: { authorization: `Bearer ${MOCK_API_KEY}` } });
    const text = await res.text();
    if (!res.ok) throw new Error(`mock GET /transactions?${q} -> ${res.status}: ${text}`);
    return (JSON.parse(text) as { data: any[] }).data;
  },
  /**
   * Forgets every customer, transaction and subscription. The database keeps its rows, so seeded
   * subscriptions become unknown to the mock: reset only in specs that create their own.
   */
  reset: () => mockFetch<{ ok: true }>('POST', 'reset', {}),
  /** The org's most recent subscription that is not canceled, as the mock holds it (or undefined). */
  liveSubscriptionOf: async (orgId: string) =>
    (await mockFetch<MockState>('GET', 'state')).subscriptions
      .filter((s) => s.custom_data?.organization_id === orgId && s.status !== 'canceled')
      .at(-1),
};

// ------------------------------------------------------------------ Stripe fake control client

/** A call the Inference API made to the Stripe fake (`/v1/...` only), with the parsed form body. */
export interface StripeMockCall {
  at: string;
  method: string;
  path: string;
  query: string;
  /** The form body (or query) parsed with Stripe's nested syntax: `items[0][price]` → `{items: [{price}]}`. */
  body: any;
  rawBody: string;
  stripeVersion: string | null;
  idempotencyKey: string | null;
  status: number;
  /** True when the fake answered from its idempotency cache. */
  replayed?: boolean;
}

export interface StripeMockWebhook {
  /** Delivery id (`dlv_...`), for `stripeMock.redeliver`. */
  id: string;
  event_id: string;
  event_type: string;
  created: number;
  object_id: string | null;
  subscription_id: string | null;
  delivered: boolean;
  /** Lost on purpose by a `dropNextWebhook` filter: recorded, never sent. */
  dropped: boolean;
  drop_filter?: string;
  redelivery_of?: string;
  attempts: { at: string; status: number; body: string }[];
}

export interface StripeMockDropFilter {
  id: string;
  event_type: string | null;
  subscription_id: string | null;
  object_id: string | null;
  count: number;
  remaining: number;
  dropped: number;
}

export interface StripeMockState {
  customers: any[];
  sessions: any[];
  subscriptions: any[];
  invoices: any[];
  invoicePayments: any[];
  paymentIntents: any[];
  charges: any[];
  refunds: any[];
  disputes: any[];
  coupons: any[];
  discounts: any[];
  portalSessions: any[];
  webhooks: StripeMockWebhook[];
  dropFilters: StripeMockDropFilter[];
}

async function stripeFetch<T>(method: string, route: string, body?: unknown): Promise<T> {
  const { stripeMockUrl } = loadStack();
  if (!stripeMockUrl) {
    throw new Error('STRIPE_MOCK_URL is not set: start the stack with `e2e/scripts/up-isolated.sh --provider stripe` and source its env.sh.');
  }
  const res = await fetch(`${stripeMockUrl}/__mock/${route}`, {
    method,
    headers: body === undefined ? undefined : { 'content-type': 'application/json' },
    body: body === undefined ? undefined : JSON.stringify(body),
  });
  const text = await res.text();
  if (!res.ok) throw new Error(`stripe mock ${method} /__mock/${route} -> ${res.status}: ${text}`);
  return JSON.parse(text) as T;
}

/** Control client for the Stripe fake (e2e/stripe-mock/server.mjs); same style as `mock` for Paddle. */
export const stripeMock = {
  state: () => stripeFetch<StripeMockState>('GET', 'state'),
  /** Every Stripe API call the Inference API made; `filter.path` is a substring match. */
  calls: async (filter: { path?: string; method?: string } = {}) => {
    const q = new URLSearchParams();
    if (filter.path) q.set('path', filter.path);
    if (filter.method) q.set('method', filter.method);
    return (await stripeFetch<{ calls: StripeMockCall[] }>('GET', `calls${q.size ? `?${q}` : ''}`)).calls;
  },
  webhooks: async () => (await stripeFetch<{ webhooks: StripeMockWebhook[] }>('GET', 'webhooks')).webhooks,
  /** Pays a Checkout Session without the browser; the webhooks follow ~300 ms apart and are delivered before this resolves. */
  completeCheckout: (sessionId: string) =>
    stripeFetch<{ session: any; subscription: any | null; webhooks: StripeMockWebhook[] }>('POST', 'complete-checkout', { session_id: sessionId }),
  /** Renewal: next period, a `subscription_cycle` invoice, `customer.subscription.updated` + `invoice.paid`; a scheduled cancel becomes `customer.subscription.deleted`. */
  advancePeriod: (subscriptionId: string) =>
    stripeFetch<{ subscription: any; invoice: any | null }>('POST', `subscriptions/${encodeURIComponent(subscriptionId)}/advance-period`, {}),
  /** Cancels at once (`customer.subscription.deleted`), without going through the app. */
  cancelNow: (subscriptionId: string) =>
    stripeFetch<{ subscription: any }>('POST', `subscriptions/${encodeURIComponent(subscriptionId)}/cancel-now`, {}),
  /** `past_due`, `unpaid`, `paused`, ...: a status change as Stripe would report it. */
  setStatus: (subscriptionId: string, status: string) =>
    stripeFetch<{ subscription: any }>('POST', `subscriptions/${encodeURIComponent(subscriptionId)}/set-status`, { status }),
  /**
   * What the subscription's next upgrade / seat-increase / renewal invoices do: `pay` (default; the invoice is
   * paid and `invoice.paid` follows), `hold` (stays open until `payInvoice`), `fail` (open, subscription past_due,
   * `invoice.payment_failed`).
   *
   * The app sends an immediate plan or seat change with `payment_behavior=error_if_incomplete`, and Stripe
   * applies such an update only if its invoice is paid. So for THAT call `hold` and `fail` both mean "not paid":
   * the call answers 402 (`card_declined` for `fail`, `invoice_payment_intent_requires_action` for `hold`),
   * nothing changes and no webhook is sent. `hold`/`fail` still act on renewals and on updates sent without
   * that behavior.
   */
  setPayments: (subscriptionId: string, mode: 'pay' | 'hold' | 'fail') =>
    stripeFetch<{ mode: string }>('POST', `subscriptions/${encodeURIComponent(subscriptionId)}/payments`, { mode }),
  /** Pays a held invoice: `invoice.paid`, delivered before this resolves. */
  payInvoice: (invoiceId: string) => stripeFetch<{ invoice: any }>('POST', `invoices/${encodeURIComponent(invoiceId)}/pay`, {}),
  failInvoice: (invoiceId: string) => stripeFetch<{ invoice: any }>('POST', `invoices/${encodeURIComponent(invoiceId)}/fail`, {}),
  /** Refunds a payment (`refund.created` then `charge.refunded`, delivered): by payment intent, invoice or checkout session; full unless `amount` (minor units). */
  refund: (target: { paymentIntent?: string; invoice?: string; session?: string }, amount?: number) =>
    stripeFetch<{ refund: any; charge: any }>('POST', 'issue-refund', {
      payment_intent: target.paymentIntent, invoice: target.invoice, session: target.session, ...(amount === undefined ? {} : { amount }),
    }),
  /**
   * A chargeback on a payment (by payment intent): `charge.dispute.created` (status `needs_response`), then, when
   * `status` is a closed one (`lost`, `won`, `warning_closed`, `charge_refunded`), `charge.dispute.closed` with it.
   * `amount` defaults to the whole charge (minor units); `emit_created: false` sends only the closed event. A lost
   * dispute does not touch the charge's `amount_refunded`. Delivered before this resolves.
   */
  dispute: (paymentIntent: string, opts: { status?: string; amount?: number; reason?: string; emit_created?: boolean } = {}) =>
    stripeFetch<{ dispute: any; charge: any }>('POST', 'disputes', { payment_intent: paymentIntent, ...opts }),
  /** Closes an open dispute: `charge.dispute.closed` with `status` (default `lost`). */
  closeDispute: (disputeId: string, status: 'won' | 'lost' | 'warning_closed' | 'charge_refunded' = 'lost') =>
    stripeFetch<{ dispute: any; charge: any }>('POST', `disputes/${encodeURIComponent(disputeId)}/close`, { status }),
  /** Creates a coupon in the fake the way the catalog sync would (`id`, `percent_off`, `duration`, ...). */
  createCoupon: async (spec: Record<string, unknown>) => (await stripeFetch<{ coupon: any }>('POST', 'coupons', spec)).coupon,
  /**
   * Loses the next `count` (default 1) webhooks matching every given field: each is recorded in `webhooks()`
   * with `dropped: true` and never sent. Arm it before the call that causes the webhook.
   */
  dropNextWebhook: (filter: { event_type?: string; subscription_id?: string; object_id?: string; count?: number } = {}) =>
    stripeFetch<{ filter: StripeMockDropFilter }>('POST', 'webhooks/drop-next', filter),
  /** Sends a recorded delivery (dropped or not) again with its original event id, freshly signed. */
  redeliver: (deliveryId: string) =>
    stripeFetch<{ event: any; delivery: StripeMockWebhook; status: number }>('POST', `webhooks/${encodeURIComponent(deliveryId)}/redeliver`, {}),
  /** Forgets every customer, subscription and invoice. The database keeps its rows: reset only in specs that make their own. */
  reset: () => stripeFetch<{ ok: true }>('POST', 'reset', {}),
  /** The org's most recent subscription that is not canceled, as the fake holds it (or undefined). */
  liveSubscriptionOf: async (orgId: string) =>
    (await stripeFetch<StripeMockState>('GET', 'state')).subscriptions
      .filter((s) => s.metadata?.organization_id === orgId && s.status !== 'canceled')
      .at(-1),
};

/**
 * Buys a plan without the browser (setup only), on a Stripe stack: POST /checkout as the page's user, pay the
 * Checkout Session in the fake, and wait for the webhooks to land. Returns the summary and the fake's ids.
 */
export async function subscribeViaStripeApi(page: Page, plan: string, seats?: number): Promise<{ summary: any; subscriptionId: string; sessionId: string }> {
  const r = await page.request.post(`${loadStack().baseUrl}/api/v1/billing/checkout`, { data: seats ? { plan, seats } : { plan } });
  expect(r.status(), `checkout ${plan}: ${await r.text()}`).toBe(200);
  const { transactionId } = await r.json(); // the Checkout Session id (cs_test_...)
  const done = await stripeMock.completeCheckout(transactionId);
  const summary = await expectSummary(page, (s) => `${s.subscription?.planKey}/${s.subscription?.status}${seats ? `/${s.subscription?.seats}` : ''}`,
    `${plan}/Active${seats ? `/${seats}` : ''}`, `${plan} active after the webhooks`);
  return { summary, subscriptionId: done.subscription.id, sessionId: transactionId };
}

// ------------------------------------------------------------------ catalog and compose helpers (Stripe specs)

let catalogCache: any;

/** plans.catalog.json, the single source of every price: specs read amounts from it, never type them. */
export function planCatalog(): any {
  if (!catalogCache) {
    const repoRoot = path.resolve(loadStack().stackDir, '../../..');
    catalogCache = JSON.parse(fs.readFileSync(path.join(repoRoot, 'inference/src/ReelBolt.Inference.Api/Billing/plans.catalog.json'), 'utf8'));
  }
  return catalogCache;
}

/** A plan's monthly list price per unit (seat), in minor units, the way the Stripe fake prices it. */
export function planPriceMinor(planId: string): number {
  const plan = planCatalog().plans.find((p: any) => p.id === planId);
  if (!plan?.priceMonthly) throw new Error(`plan ${planId} has no monthly price in the catalog`);
  return Math.round(plan.priceMonthly * 100);
}

/** The credits one unit (seat) of a plan refills every period. */
export function planIncludedCredits(planId: string): number {
  const plan = planCatalog().plans.find((p: any) => p.id === planId);
  if (!plan) throw new Error(`plan ${planId} is not in the catalog`);
  return plan.includedCredits;
}

/** A top-up pack's price in minor units. */
export function topUpPriceMinor(credits: number): number {
  const pack = planCatalog().topUp.packs.find((p: any) => p.credits === credits);
  if (!pack) throw new Error(`no ${credits}-credit pack in the catalog`);
  return Math.round(pack.price * 100);
}

/** `docker compose` on this stack's project, exactly as e2e/scripts/up-isolated.sh calls it. */
export function composeStack(args: string[], env: NodeJS.ProcessEnv = process.env): string {
  const { stackDir } = loadStack();
  const repoRoot = path.resolve(stackDir, '../../..');
  const project = process.env.E2E_PROJECT || path.basename(stackDir);
  const profile = isStripeStack() ? ['--profile', 'stripe'] : [];
  return execFileSync('docker', [
    'compose', '-p', project, '--project-directory', repoRoot,
    '--env-file', path.join(stackDir, '.env'),
    '-f', path.join(repoRoot, 'docker-compose.yml'), '-f', path.join(repoRoot, 'docker-compose.e2e.yml'),
    ...profile,
    ...args,
  ], { encoding: 'utf8', env, stdio: ['ignore', 'pipe', 'pipe'], timeout: 600_000 }).trim();
}

/** The sweep interval the compose file gives the Inference API (E2E_RECONCILE_SWEEP_SECONDS unset). */
export const DEFAULT_RECONCILE_SWEEP_SECONDS = '4';

/**
 * Recreates only the inference container with `Billing__Reconcile__SweepIntervalSeconds` set through
 * E2E_RECONCILE_SWEEP_SECONDS (undefined = the compose default), waits until it is healthy and the API
 * answers through nginx again. The API answers 502 for a few seconds: never run it beside other specs.
 */
export async function restartInference(page: Page, sweepSeconds: string | undefined): Promise<void> {
  const env = { ...process.env };
  if (sweepSeconds === undefined) delete env.E2E_RECONCILE_SWEEP_SECONDS;
  else env.E2E_RECONCILE_SWEEP_SECONDS = sweepSeconds;
  composeStack(['up', '-d', '--no-deps', '--no-build', '--wait', '--wait-timeout', '300', 'inference'], env);
  const effective = composeStack(['exec', '-T', 'inference', 'printenv', 'Billing__Reconcile__SweepIntervalSeconds']);
  expect(effective, 'sweep interval of the running inference container').toBe(sweepSeconds ?? DEFAULT_RECONCILE_SWEEP_SECONDS);
  await expect.poll(async () => (await page.request.get(`${loadStack().baseUrl}/api/v1/billing/summary`)).status(),
    { message: 'inference answers through nginx again', timeout: 60_000, intervals: [500, 1000] }).toBeLessThan(500);
}

/**
 * Runs `action` (a click that starts the hosted-checkout redirect) and returns the answer of POST
 * /api/v1/billing/checkout together with the request body. The body is read inside a route handler, because the
 * browser leaves the page for the hosted checkout immediately and a plain `waitForResponse(...).json()` then loses the
 * body ("Response body is not available for a response that was navigated away from").
 */
export async function captureCheckout(page: Page, action: () => Promise<void>): Promise<{ status: number; request: any; body: any }> {
  let captured: { status: number; request: any; body: any } | undefined;
  const handler = async (route: import('@playwright/test').Route) => {
    const response = await route.fetch();
    const text = await response.text();
    let body: any = null;
    try { body = JSON.parse(text); } catch { /* not JSON */ }
    let request: any = null;
    try { request = JSON.parse(route.request().postData() ?? 'null'); } catch { /* not JSON */ }
    captured = { status: response.status(), request, body };
    await route.fulfill({ response, body: text });
  };
  const match = (url: URL) => url.pathname.endsWith('/api/v1/billing/checkout');
  await page.route(match, handler);
  try {
    await action();
    await expect.poll(() => captured !== undefined, { message: 'POST /api/v1/billing/checkout answered', timeout: 30_000 }).toBe(true);
  } finally {
    await page.unroute(match, handler);
  }
  return captured!;
}
