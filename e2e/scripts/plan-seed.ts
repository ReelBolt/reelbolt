/**
 * Plan + credit seeding for billing-gated e2e specs (render-resolution.spec.ts).
 *
 * SAFETY: every statement runs through `docker exec <container> psql` against ONE postgres
 * container, and only after proving that container belongs to the isolated compose project
 * named by E2E_COMPOSE_PROJECT (default `reelbolt-saas-render-resolution`). The default stack
 * (`reelbolt-postgres` / project `reelbolt`) is refused outright, so this can never write to it.
 *
 * How the active plan is resolved (ReelBolt.Shared/Billing/PlanBackedOrganizationEntitlements.cs):
 * the newest `subscriptions` row of the org whose status is Trialing/Active/PastDue names the
 * plan; without one the org falls back to the `trial` plan. Entitlements are cached per org for
 * Billing:EntitlementCacheSeconds (default 30 s) in EACH process (inference API and engine), so
 * set the plan before the org's first gated request, or wait SEED_CACHE_TTL_MS after changing it.
 *
 * Credits (ReelBolt.Shared/Billing/CreditLedger.cs GrantAsync): one `credit_accounts` row per
 * org (balance/reserved in millis), one `credit_grants` bucket per grant, and one
 * `credit_ledger_entries` row of kind `Grant` with a unique idempotency key.
 */
import { execFileSync, spawnSync } from 'child_process';
import { randomUUID } from 'crypto';

export const COMPOSE_PROJECT = process.env.E2E_COMPOSE_PROJECT || 'reelbolt-saas-render-resolution';
export const PG_CONTAINER = process.env.E2E_PG_CONTAINER || 'srr-postgres';
const PG_USER = process.env.E2E_PG_USER || 'postgres';
const PG_DB = process.env.E2E_PG_DB || 'reelforge';

/** Billing:EntitlementCacheSeconds default (30 s) plus a margin. */
export const SEED_CACHE_TTL_MS = 32_000;

export type PlanKey = 'trial' | 'creator' | 'pro' | 'team' | 'enterprise';

let verified = false;

/** Refuses to run unless PG_CONTAINER is the isolated project's postgres. */
function assertIsolatedPostgres(): void {
  if (verified) return;
  if (PG_CONTAINER === 'reelbolt-postgres' || COMPOSE_PROJECT === 'reelbolt') {
    throw new Error('plan-seed refuses the default stack (reelbolt-postgres / project reelbolt)');
  }
  const project = execFileSync(
    'docker',
    ['inspect', '-f', '{{index .Config.Labels "com.docker.compose.project"}}', PG_CONTAINER],
    { encoding: 'utf-8' },
  ).trim();
  if (project !== COMPOSE_PROJECT) {
    throw new Error(`plan-seed: container ${PG_CONTAINER} belongs to project '${project}', expected '${COMPOSE_PROJECT}'`);
  }
  verified = true;
}

/** Runs SQL (tuples only, unaligned) and returns stdout trimmed. ON_ERROR_STOP makes failures throw. */
export function sql(statement: string): string {
  assertIsolatedPostgres();
  return execFileSync(
    'docker',
    ['exec', '-i', PG_CONTAINER, 'psql', '-U', PG_USER, '-d', PG_DB, '-v', 'ON_ERROR_STOP=1', '-tA', '-q'],
    { input: statement, encoding: 'utf-8' },
  ).trim();
}

function uuidLiteral(id: string): string {
  if (!/^[0-9a-f-]{36}$/i.test(id)) throw new Error(`not a uuid: ${id}`);
  return `'${id}'::uuid`;
}

/**
 * Makes `planKey` the org's active plan: cancels every entitling subscription it has and inserts
 * a fresh Active one (newest updated_at wins in EfPlanSource).
 */
export function setOrgPlan(orgId: string, planKey: PlanKey): void {
  if (!/^[a-z]+$/.test(planKey)) throw new Error(`bad plan key ${planKey}`);
  const org = uuidLiteral(orgId);
  sql(`
BEGIN;
UPDATE subscriptions SET status = 'Canceled', updated_at = now()
 WHERE organization_id = ${org} AND status IN ('Trialing','Active','PastDue');
INSERT INTO subscriptions (id, organization_id, plan_id, provider, provider_subscription_id, status,
                           period_start, period_end, seats, cancel_at_period_end, created_at, updated_at)
SELECT '${randomUUID()}'::uuid, ${org}, p.id, 'e2e', 'e2e-${randomUUID()}', 'Active',
       now(), now() + interval '30 days', 1, false, now(), now()
  FROM plans p WHERE p.key = '${planKey}';
COMMIT;`);
  const active = sql(`
SELECT p.key FROM subscriptions s JOIN plans p ON p.id = s.plan_id
 WHERE s.organization_id = ${org} AND s.status = 'Active' ORDER BY s.updated_at DESC LIMIT 1;`);
  if (active !== planKey) throw new Error(`setOrgPlan: expected active plan ${planKey}, found '${active}'`);
}

/** Grants `credits` (whole credits) to the org as a Promo bucket, creating the account if needed. */
export function grantCredits(orgId: string, credits: number): void {
  const org = uuidLiteral(orgId);
  const millis = Math.round(credits * 1000);
  const grantId = randomUUID();
  sql(`
BEGIN;
INSERT INTO credit_accounts (id, organization_id, balance_millis, reserved_millis, row_version, updated_at)
VALUES ('${randomUUID()}'::uuid, ${org}, 0, 0, 0, now())
ON CONFLICT (organization_id) DO NOTHING;
INSERT INTO credit_grants (id, account_id, organization_id, source, amount_millis, remaining_millis, created_at)
SELECT '${grantId}'::uuid, a.id, ${org}, 'Promo', ${millis}, ${millis}, now()
  FROM credit_accounts a WHERE a.organization_id = ${org};
UPDATE credit_accounts SET balance_millis = balance_millis + ${millis}, row_version = row_version + 1, updated_at = now()
 WHERE organization_id = ${org};
INSERT INTO credit_ledger_entries (id, account_id, organization_id, kind, balance_delta_millis, reserved_delta_millis,
                                   balance_after_millis, grant_id, note, idempotency_key, created_at)
SELECT '${randomUUID()}'::uuid, a.id, ${org}, 'Grant', ${millis}, 0, a.balance_millis, '${grantId}'::uuid,
       'e2e seed', 'e2e-seed:${grantId}', now()
  FROM credit_accounts a WHERE a.organization_id = ${org};
COMMIT;`);
}

/** The generated initial admin password printed once in the isolated project's go-api logs. */
export function initialAdminPasswordFromLogs(): string | null {
  // Go's log package writes to stderr, so read both streams.
  const res = spawnSync('docker', ['logs', `${COMPOSE_PROJECT}-go-api-1`], { encoding: 'utf-8' });
  const all = `${res.stdout ?? ''}\n${res.stderr ?? ''}`;
  const m = /Initial admin user created[\s\S]*?Password:\s*(\S+) \(generated/.exec(all);
  return m ? m[1] : null;
}
