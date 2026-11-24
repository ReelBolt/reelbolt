import type { T } from '@/i18n/translator';
import { enT } from '@/lib/i18n-default';
import { apiFetch, ApiError } from './client';
import type { RunOutputResolution } from '../types/styles';
import type { RunBrief } from '../types/workflow';
import type {
  BillingInterval,
  BillingSummary,
  BillingUsage,
  CancelSubscriptionResult,
  PlanChangeResult,
  ResumeSubscriptionResult,
  InsufficientCredits,
  UsageGroupBy,
  WorkflowCostEstimate,
} from '../types/billing';
import { readableServerMessage } from './describe-error';

// Relative paths only: the JWT is an httpOnly cookie that nginx turns into an Authorization header.
export const BILLING_SUMMARY_PATH = '/api/v1/billing/summary';
export const BILLING_USAGE_PATH = '/api/v1/billing/usage';

export interface BillingUsageParams {
  /** Window start (inclusive). Omitted lets the backend default to the last 30 days. */
  from?: string;
  /** Window end (exclusive). */
  to?: string;
  groupBy?: UsageGroupBy;
  /** Restricts the roll-up to one run. Omitted keeps the whole organization. */
  executionId?: string;
}

/** The order the params are appended in. Asserted by the tests, so it stays stable. */
const PARAM_ORDER = ['from', 'to', 'groupBy', 'executionId'] as const;

/**
 * Appends `key=value` only when the caller actually supplied a value. An empty or
 * whitespace-only string counts as "not supplied" — it would otherwise send a meaningless
 * `?groupBy=` and get a 400 back.
 */
function appendParam(
  parts: string[],
  key: (typeof PARAM_ORDER)[number],
  value: string | undefined,
): void {
  if (value === undefined || value === null) return;
  if (value.trim() === '') return;
  parts.push(`${key}=${encodeURIComponent(value)}`);
}

/**
 * Builds the (relative) usage request URL from only the supplied params — the backend's own
 * defaults for the window and the axis are never sent from here.
 */
export function getBillingUsageUrl(params: BillingUsageParams = {}): string {
  const parts: string[] = [];
  for (const key of PARAM_ORDER) appendParam(parts, key, params[key]);
  return parts.length > 0 ? `${BILLING_USAGE_PATH}?${parts.join('&')}` : BILLING_USAGE_PATH;
}

/**
 * The organization's metered usage over a window. A 404 means billing is not enabled on this
 * deployment (`Billing:Enabled=false`), which the caller must render as such rather than as
 * a failure — see {@link isBillingUnavailable}.
 */
export function getBillingUsage(params: BillingUsageParams = {}): Promise<BillingUsage> {
  return apiFetch<BillingUsage>(getBillingUsageUrl(params));
}

/** The organization's plan, renewal date and credit balance. 404 when billing is not enabled. */
export function getBillingSummary(): Promise<BillingSummary> {
  return apiFetch<BillingSummary>(BILLING_SUMMARY_PATH);
}

/**
 * True when the endpoint answered 404. That is how `Billing:Enabled=false` presents itself:
 * every `/api/v1/billing/*` action returns NotFound, so the page says billing is off on this
 * deployment instead of showing a failure it cannot fix.
 */
export function isBillingUnavailable(error: unknown): boolean {
  return error instanceof ApiError && error.status === 404;
}

// -------------------------------------------------------------------- the pre-run estimate (B7)

/**
 * The run's estimate. It lives under the project's workflow route rather than under `/billing`
 * because that is where B7 publishes it, but it is a billing fact and it is parsed here with the
 * rest of them.
 *
 * Unlike the `/billing/*` endpoints this one does NOT 404 when billing is off: it answers 200 with
 * `billingEnabled: false` and every figure at zero, so a self-host install needs no special case
 * beyond gating the whole UI on that flag.
 */
export function workflowEstimatePath(
  projectId: string,
  workflowId: string,
  outputResolution?: RunOutputResolution,
  videoFileIds?: readonly string[],
): string {
  const base = `/api/v1/projects/${projectId}/workflows/${workflowId}/estimate`;
  // Auto and no chosen clips add nothing, so a plain run's estimate keeps the exact URL (and SWR key) it had before.
  const query: string[] = [];
  if (outputResolution) query.push(`outputResolution=${encodeURIComponent(outputResolution)}`);
  // The clips the run page chose: the estimate prices them as the run will hold them (repeatable, bounded server side).
  for (const id of videoFileIds ?? []) query.push(`videoFileIds=${encodeURIComponent(id)}`);
  return query.length > 0 ? `${base}?${query.join('&')}` : base;
}

/** What this run is expected to cost, the balance it will be compared against, and the breakdown. */
export function getWorkflowCostEstimate(
  projectId: string,
  workflowId: string,
  outputResolution?: RunOutputResolution,
  videoFileIds?: readonly string[],
): Promise<WorkflowCostEstimate> {
  return apiFetch<WorkflowCostEstimate>(workflowEstimatePath(projectId, workflowId, outputResolution, videoFileIds));
}

/** The body of `POST .../estimate` (docs/design/app-inputs.md 11.4): the run's brief, so app inputs and their branch are priced. */
export interface WorkflowEstimateBody {
  brief?: RunBrief | null;
  outputResolution?: RunOutputResolution;
  videoFileIds?: readonly string[];
}

/** The estimate of a run with its brief: the same answer as `GET`, over the steps as the brief's app inputs bind them. */
export function postWorkflowCostEstimate(projectId: string, workflowId: string, body: WorkflowEstimateBody): Promise<WorkflowCostEstimate> {
  return apiFetch<WorkflowCostEstimate>(`/api/v1/projects/${projectId}/workflows/${workflowId}/estimate`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** The machine code `POST .../execute` refuses an unaffordable run with, alongside 402. */
export const INSUFFICIENT_CREDITS_CODE = 'INSUFFICIENT_CREDITS';

function finiteNumber(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

/**
 * The 402 an unaffordable run gets, or null for anything else. The caller renders it as its own
 * state — a shortfall with a way to fix it — rather than as a generic failure, so it must not be
 * confused with the other 402 on this platform (a plan limit: "bring your own providers", which is
 * a different remedy entirely). The status AND the machine code are both required, and a refusal
 * whose body did not survive the trip answers null: the caller then falls back to the server's own
 * sentence in the error, which is still true, rather than inventing a shortfall it never read.
 */
export function insufficientCredits(error: unknown): InsufficientCredits | null {
  if (!(error instanceof ApiError) || error.status !== 402) return null;
  const body = error.body;
  if (typeof body !== 'object' || body === null) return null;
  const b = body as Record<string, unknown>;
  if (b.code !== INSUFFICIENT_CREDITS_CODE) return null;

  const required = finiteNumber(b.required);
  const balance = finiteNumber(b.balance);
  if (required === null || balance === null) return null;

  return {
    required,
    balance,
    shortfall: Math.max(0, required - balance),
    low: finiteNumber(b.low),
    high: finiteNumber(b.high),
    hasHistory: b.hasHistory === true,
    missing: finiteNumber(b.missing),
    typical: finiteNumber(b.typical),
    fixedMax: finiteNumber(b.fixedMax),
    message: typeof b.message === 'string' && b.message.trim() !== '' ? b.message : null,
  };
}

// ---------------------------------------------------------------------------- purchases

export const BILLING_CHECKOUT_PATH = '/api/v1/billing/checkout';
export const BILLING_PORTAL_PATH = '/api/v1/billing/portal';
export const BILLING_SEATS_PATH = '/api/v1/billing/seats';
export const BILLING_CANCEL_PATH = '/api/v1/billing/subscription/cancel';
export const BILLING_RESUME_PATH = '/api/v1/billing/subscription/resume';
export const BILLING_CHANGE_PLAN_PATH = '/api/v1/billing/subscription/change-plan';

/**
 * Body of `POST /api/v1/billing/checkout`. The endpoint requires exactly one of `plan` or
 * `topUpCredits` and answers 400 `choose_plan_or_topup` when both — or neither — are sent, so the
 * two helpers below are the only shape callers should build.
 *
 * `seats` is only meaningful for a per-seat plan; the server defaults it to the plan's minimum.
 */
export interface CheckoutRequestBody {
  plan?: string;
  seats?: number;
  topUpCredits?: number;
  /**
   * A promo code the buyer applied. An invalid one is refused (400 `promo_code_invalid`), never
   * silently dropped; with no code the server applies any automatic discount itself.
   */
  promoCode?: string;
  /** Sent only for a yearly plan; a monthly body stays exactly as it was. */
  interval?: 'annual';
}

/** What a checkout answers with: where to send the browser. It grants nothing by itself. */
export interface CheckoutSession {
  /** The provider's hosted checkout page. */
  checkoutUrl: string;
  /** The provider's transaction id, for support reference. */
  transactionId: string;
  /** Present when the server attached a discount to the transaction; null or absent otherwise. */
  discount?: { headline: string | null } | null;
}

/**
 * A customer-portal session: the overview (invoices, payment method) and, for a live subscription,
 * its deep links. Plan changes are not made there — the Plan & billing page changes plans itself.
 * Every link is short-lived: fetch a fresh session per click, never cache one.
 */
export interface PortalSession {
  url: string;
  /** The subscription's cancel page in the portal; null when Paddle returned none. */
  cancelUrl?: string | null;
  /** The subscription's update-payment-method page; null when Paddle returned none. */
  updatePaymentMethodUrl?: string | null;
}

/**
 * Whether a URL the API handed back is safe to navigate to. The link is built by the payment
 * provider, but it is still untrusted input: assigning a `javascript:` or `data:` URL to
 * `location` would run it in our own origin. Only http(s) is ever followed.
 */
export function isSafeRedirectUrl(url: unknown): url is string {
  if (typeof url !== 'string' || url.trim() === '') return false;
  try {
    const parsed = new URL(url);
    return parsed.protocol === 'https:' || parsed.protocol === 'http:';
  } catch {
    return false;
  }
}

/**
 * Starts a hosted checkout for a plan (optionally with a seat count) or a top-up pack. Needs the
 * Owner or Billing role; anyone else is refused.
 */
export function startPlanCheckout(
  plan: string,
  seats?: number,
  promoCode?: string,
  interval?: BillingInterval,
): Promise<CheckoutSession> {
  return startCheckout({
    plan,
    ...(seats === undefined ? {} : { seats }),
    ...(promoCode ? { promoCode } : {}),
    ...(interval === 'annual' ? { interval } : {}),
  });
}

/** Starts a hosted checkout for a top-up pack, in credits. */
export function startTopUpCheckout(topUpCredits: number, promoCode?: string): Promise<CheckoutSession> {
  return startCheckout(promoCode ? { topUpCredits, promoCode } : { topUpCredits });
}

function startCheckout(body: CheckoutRequestBody): Promise<CheckoutSession> {
  return apiFetch<CheckoutSession>(BILLING_CHECKOUT_PATH, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/**
 * A short-lived customer-portal link. Answers 409 `no_billing_customer` for an organization that
 * has never bought anything, which has no portal to open.
 */
export function openBillingPortal(): Promise<PortalSession> {
  return apiFetch<PortalSession>(BILLING_PORTAL_PATH, { method: 'POST' });
}

/**
 * Sets the seat count of a per-seat subscription, through the same path as a plan change: more
 * seats apply now (prorated), fewer seats from the next billing period. The local row — and with
 * it the credits that scale per seat — changes only when the provider's webhook arrives (202).
 */
export function changeBillingSeats(seats: number): Promise<PlanChangeResult> {
  return apiFetch<PlanChangeResult>(BILLING_SEATS_PATH, { method: 'POST', body: JSON.stringify({ seats }) });
}

/** Schedules the live plan to cancel at the end of its billing period. Undo with {@link resumeSubscription}. */
export function cancelSubscription(): Promise<CancelSubscriptionResult> {
  return apiFetch<CancelSubscriptionResult>(BILLING_CANCEL_PATH, { method: 'POST' });
}

/** Removes a scheduled cancellation ("Keep my plan"). */
export function resumeSubscription(): Promise<ResumeSubscriptionResult> {
  return apiFetch<ResumeSubscriptionResult>(BILLING_RESUME_PATH, { method: 'POST' });
}

/**
 * Moves the live plan to another self-serve plan (and seat count, for a per-seat plan). Choosing the
 * current plan while a downgrade is scheduled removes it (`direction: "keep"`). Omitting `seats`
 * lets the server keep the current count, or use the plan's minimum.
 */
export function changeSubscriptionPlan(plan: string, seats?: number, interval?: BillingInterval): Promise<PlanChangeResult> {
  return apiFetch<PlanChangeResult>(BILLING_CHANGE_PLAN_PATH, {
    method: 'POST',
    body: JSON.stringify({ plan, ...(seats === undefined ? {} : { seats }), ...(interval === 'annual' ? { interval } : {}) }),
  });
}

/**
 * The refusal codes of the billing actions (checkout, portal, seats, cancel, resume, change plan),
 * mapped to what the person clicking actually needs to do about it. A function receives the decoded
 * body, for the codes whose sentence needs a fact from it.
 */
const ACTION_CODES: ReadonlySet<string> = new Set([
  'choose_plan_or_topup', 'plan_not_purchasable', 'already_subscribed', 'below_min_seats', 'too_many_seats',
  'topup_requires_paid_plan', 'unknown_topup_pack', 'no_billing_customer', 'not_a_per_seat_subscription',
  'no_subscription', 'cancel_scheduled', 'no_change', 'unknown_plan', 'plan_not_self_serve', 'billing_provider_refused',
  'subscription_pending', 'change_pending', 'downgrade_scheduled', 'billing_not_configured', 'billing_provider_error',
  'promo_code_invalid', 'annual_unavailable', 'interval_change_not_supported', 'invalid_interval',
]);

/** Codes whose sentence the server writes itself (fixed text, safe to show); the catalogue entry is the fallback. */
const SERVER_WORDED: ReadonlySet<string> = new Set(['billing_provider_refused', 'subscription_pending', 'change_pending', 'downgrade_scheduled']);

function serverMessage(body: Record<string, unknown>): string | null {
  return typeof body.message === 'string' && body.message.trim() ? body.message : null;
}

/**
 * A sentence to show for a failed billing action. A known refusal code (from the body's `error`,
 * else the message the client fell back to) gets its own explanation; anything else falls back to
 * whatever the server said, and only then to a generic apology — never to a silent failure.
 */
export function billingActionMessage(error: unknown, t: T = enT): string {
  if (error instanceof ApiError) {
    const body = typeof error.body === 'object' && error.body !== null ? (error.body as Record<string, unknown>) : {};
    const message = error.message.trim();
    const code = error.code ?? message;
    if (ACTION_CODES.has(code)) {
      const key = `libLabels.billingActions.${code}`;
      if (SERVER_WORDED.has(code)) return serverMessage(body) ?? t(key);
      if (code === 'below_min_seats') {
        return typeof body.minSeats === 'number' ? t(key, { count: body.minSeats }) : t(`${key}_unknown`);
      }
      if (code === 'too_many_seats') {
        return typeof body.maxSeats === 'number' ? t(key, { count: body.maxSeats }) : t(`${key}_unknown`);
      }
      return t(key);
    }
    if (message) return message;
  }
  const own = readableServerMessage(error);
  if (own) return own;
  return t('libLabels.billingActions.fallback');
}
