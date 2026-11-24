import { apiFetch } from './client';
import type {
  BillingAdminOverview,
  BillingAdminOverviewParams,
  BillingReconcileList,
  BillingResyncRequest,
  BillingResyncResult,
  ReconcileOutcome,
} from '../types/billing-admin';

/**
 * Platform-admin only (403 for anyone else). Relative path only: the JWT is an httpOnly cookie that
 * nginx turns into an Authorization header.
 *
 * Under `/api/v1/billing/admin/` rather than `/api/v1/admin/`, which nginx sends to the Go API.
 */
export const BILLING_ADMIN_OVERVIEW_PATH = '/api/v1/billing/admin/overview';

/** The order the params are appended in. Asserted by the tests, so it stays stable. */
const PARAM_ORDER = ['from', 'to'] as const;

/**
 * Builds the (relative) request URL from only the supplied params — the backend's own 30-day
 * default window is never sent from here, so the server stays the single source of truth for it.
 *
 * Deliberately `=== undefined`/`null` rather than a falsy check, and an empty or whitespace-only
 * string counts as "not supplied": it would otherwise send a meaningless empty bound.
 */
export function getBillingAdminOverviewUrl(params: BillingAdminOverviewParams = {}): string {
  const parts: string[] = [];
  for (const key of PARAM_ORDER) {
    const value = params[key];
    if (value === undefined || value === null) continue;
    if (typeof value === 'string' && value.trim() === '') continue;
    parts.push(`${key}=${encodeURIComponent(value)}`);
  }
  return parts.length > 0
    ? `${BILLING_ADMIN_OVERVIEW_PATH}?${parts.join('&')}`
    : BILLING_ADMIN_OVERVIEW_PATH;
}

/** Fetches the revenue/cost overview. Admin-only on the backend (403 otherwise). */
export function getBillingAdminOverview(
  params: BillingAdminOverviewParams = {},
): Promise<BillingAdminOverview> {
  return apiFetch<BillingAdminOverview>(getBillingAdminOverviewUrl(params));
}

/** The reconcile attention list: workspaces whose subscription is waiting on Paddle. DB only. */
export const BILLING_ADMIN_RECONCILE_PATH = '/api/v1/billing/admin/reconcile';

/** Platform admin only; 404 when billing is disabled on this deployment. */
export function getBillingAdminReconcile(): Promise<BillingReconcileList> {
  return apiFetch<BillingReconcileList>(BILLING_ADMIN_RECONCILE_PATH);
}

export function getBillingAdminResyncUrl(organizationId: string): string {
  return `/api/v1/billing/admin/organizations/${encodeURIComponent(organizationId)}/resync`;
}

/**
 * Reads the workspace's subscription from Paddle and applies it (force: true on the server). An
 * empty or whitespace-only reason is not sent. Throws `ApiError`: 400 reason too long, 404 unknown
 * workspace or billing disabled, 409 `change_in_progress`, 502 `billing_provider_error`.
 */
export function resyncBillingOrganization(
  organizationId: string,
  request: BillingResyncRequest = {},
): Promise<BillingResyncResult> {
  const reason = request.reason?.trim();
  const body: BillingResyncRequest = reason ? { reason } : {};
  return apiFetch<BillingResyncResult>(getBillingAdminResyncUrl(organizationId), {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

const RECONCILE_OUTCOMES: readonly ReconcileOutcome[] = [
  'Healed',
  'InSync',
  'TooRecent',
  'NothingToReconcile',
  'ProviderUnavailable',
  'Stale',
  'Busy',
];

/**
 * Maps a serialized outcome to its `ReconcileOutcome` name whatever the casing (`Healed`, `healed`,
 * `inSync`, `in_sync`, `IN_SYNC`); null for anything else, so the caller can show the raw value.
 */
export function normalizeReconcileOutcome(raw: string | null | undefined): ReconcileOutcome | null {
  if (typeof raw !== 'string') return null;
  const folded = raw.replace(/[_\s-]/g, '').toLowerCase();
  return RECONCILE_OUTCOMES.find((name) => name.toLowerCase() === folded) ?? null;
}
