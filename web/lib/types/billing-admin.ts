import type { RateCardProviderKind, UsageBillingMode, UsageKind } from './rate-card';

/**
 * TS mirror of `BillingAdminController` (`GET /api/v1/billing/admin/overview`): the read-only
 * platform-admin view of money in and money out. Field names are exactly the serialized names.
 *
 * What the UI must carry rather than smooth over: `revenue.collected` is money the provider actually
 * charged (per currency, never summed across currencies), `revenue.committedMonthlyUsd` is the run
 * rate after each subscription's latest discount, and COGS is whatever the rate cards priced at
 * write time, so PLACEHOLDER cards (B13) make every cost and margin figure here a placeholder too.
 */

/** One cost roll-up row: an enum member name, a model, or a template name, with its window totals. */
export interface BillingCostBucket {
  key: string;
  costUsd: number;
  creditsMillis: number;
  events: number;
}

/** One organization's window cost, with its display name. */
export interface BillingCostOrganization {
  organizationId: string;
  name: string;
  costUsd: number;
  creditsMillis: number;
  events: number;
}

/** One plan's committed run rate against the cost its organizations actually incurred. */
export interface BillingPlanMargin {
  planKey: string;
  planName: string;
  organizations: number;
  /** After discounts. */
  committedMonthlyUsd: number;
  /** List price, before discounts. */
  listPriceMonthlyUsd: number;
  windowCommittedUsd: number;
  costUsd: number;
  marginUsd: number;
  /** Null when there is no committed revenue to take a percentage of (or the row has no plan). */
  marginPercent: number | null;
}

/** Money the provider charged in the window, in one currency. Amounts are minor units. */
export interface BillingCollectedCurrency {
  /** ISO-4217 as the provider sent it; null when it sent none. */
  currencyCode: string | null;
  minorUnitExponent: number;
  payments: number;
  zeroAmountPayments: number;
  grossMinor: number;
  subscriptionGrossMinor: number;
  topUpGrossMinor: number;
  discountMinor: number;
  taxMinor: number;
  /** Null when no payment reported a fee. */
  feeMinor: number | null;
  paymentsWithUnknownTax: number;
  refunds: number;
  refundedMinor: number;
  netMinor: number;
}

/** A provider other than the active one (e.g. Paddle test payments). Never part of revenue. */
export interface BillingExcludedProvider {
  provider: string;
  /** Active/PastDue subscriptions of that provider, not counted in the run rate. */
  payingSubscriptions: number;
  collected: BillingCollectedCurrency[];
}

export interface BillingAdminRevenue {
  /** Provider key the figures are for (e.g. "stripe"); null = billing not configured, all providers. */
  activeProvider: string | null;
  /** Every other provider seen in the window or with paying subscriptions. */
  excluded: BillingExcludedProvider[];
  /** List price of paying provider subscriptions, monthly, before discounts. */
  listPriceMonthlyUsd: number;
  /** Run rate after each subscription's latest recorded discount. */
  committedMonthlyUsd: number;
  /** `committedMonthlyUsd` prorated over the window at 30-day months. */
  windowCommittedUsd: number;
  payingSubscriptions: number;
  trialingSubscriptions: number;
  /** Subscriptions with no billing provider at all — the internal Trial plan. Never revenue. */
  internalTrialOrganizations: number;
  discountedSubscriptions: number;
  fullyDiscountedSubscriptions: number;
  /** Paying subscriptions with no payment row, counted at list price. */
  subscriptionsWithoutPayment: number;
  /** Sorted by currency code. */
  collected: BillingCollectedCurrency[];
  /** The USD row's net / 100; null when there is no USD row. */
  collectedNetUsd: number | null;
  /** Payments in a currency other than USD (not in `collectedNetUsd`). */
  otherCurrencyPayments: number;
  topUpCreditsInWindow: number;
  /** True: top-up money is in `collected[].topUpGrossMinor`. */
  topUpUsdRecorded: boolean;
  topUpCreditsRecorded: boolean;
}

export interface BillingAdminCost {
  totalUsd: number;
  totalCreditsMillis: number;
  events: number;
  /** Platform rows that matched no rate card and were priced at zero by the fail-closed path. */
  unpricedPlatformEvents: number;
  /** Metered calls that ran outside any usage scope. */
  unattributedEvents: number;
  byProviderKind: BillingCostBucket[];
  byModel: BillingCostBucket[];
  byUsageKind: BillingCostBucket[];
  bySource: BillingCostBucket[];
  byBillingMode: BillingCostBucket[];
  byTemplate: BillingCostBucket[];
  topOrganizations: BillingCostOrganization[];
  topOrganizationsTruncated: boolean;
}

export interface BillingAdminOverview {
  from: string;
  to: string;
  windowDays: number;
  revenue: BillingAdminRevenue;
  cost: BillingAdminCost;
  plans: BillingPlanMargin[];
}

/** Query params; both are optional and the server's 30-day default applies when neither is sent. */
export interface BillingAdminOverviewParams {
  /** Inclusive start (ISO-8601). */
  from?: string;
  /** Exclusive end (ISO-8601). */
  to?: string;
}

/** The cost slices the overview carries, in the order the UI tabs present them. */
export type BillingCostSliceKey =
  | 'byProviderKind'
  | 'byModel'
  | 'byUsageKind'
  | 'bySource'
  | 'byBillingMode'
  | 'byTemplate';

/** Re-exported so a consumer of the overview needs one import, not two. */
export type { RateCardProviderKind, UsageBillingMode, UsageKind };

/**
 * Why a workspace is on the reconcile attention list (`GET /api/v1/billing/admin/reconcile`). The
 * UI maps each one to plain words; an unknown value from a newer backend is shown raw, not dropped.
 */
export type BillingReconcileReason =
  | 'change_pending'
  | 'renewal_overdue'
  | 'cancel_overdue'
  | 'checkout_unconfirmed'
  | 'reconcile_failing';

/**
 * What one reconcile attempt concluded (`ReconcileOutcome` on the server). Serialized as a string;
 * the casing is not pinned by the contract, so read it through `normalizeReconcileOutcome` rather
 * than comparing raw values.
 */
export type ReconcileOutcome =
  | 'Healed'
  | 'InSync'
  | 'TooRecent'
  | 'NothingToReconcile'
  | 'ProviderUnavailable'
  | 'Stale'
  | 'Busy';

/** The fields a resync can find out of step with Paddle. Unknown values are kept as strings. */
export type ReconcileDriftField =
  | 'plan'
  | 'seats'
  | 'period'
  | 'status'
  | 'cancel_at_period_end'
  | 'subscription_missing';

/** One workspace waiting on Paddle. Read from the DB only; listing it never calls Paddle. */
export interface BillingReconcileItem {
  organizationId: string;
  organizationName: string;
  planKey: string | null;
  status: string | null;
  /** One of `BillingReconcileReason`; typed as string so a newer reason still renders. */
  reason: BillingReconcileReason | (string & {});
  /** ISO-8601: when the change was requested, or when the renewal/cancellation fell due. */
  waitingSince: string;
  /** ISO-8601, or null when no reconcile has checked it yet. */
  lastCheckedAt: string | null;
  /** A `ReconcileOutcome` name, or null when never checked. */
  lastOutcome: string | null;
  lastError: string | null;
}

/** `GET /api/v1/billing/admin/reconcile`: at most 200 items, oldest `waitingSince` first. */
export interface BillingReconcileList {
  /** How long a change may go unconfirmed before it is listed here. */
  stuckAfterSeconds: number;
  items: BillingReconcileItem[];
}

/** Body of `POST /api/v1/billing/admin/organizations/{orgId}/resync`. */
export interface BillingResyncRequest {
  /** Free text for the audit row; at most 500 characters (the server answers 400 above that). */
  reason?: string;
}

/** 200 answer of the resync. 409 (`change_in_progress`) and 502 (`billing_provider_error`) throw. */
export interface BillingResyncResult {
  /** A `ReconcileOutcome` name; see `normalizeReconcileOutcome`. */
  outcome: string;
  drift: (ReconcileDriftField | (string & {}))[];
  subscriptionEventsApplied: number;
  transactionsApplied: number;
}
