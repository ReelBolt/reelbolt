import type { BillingPlan, BillingSummary, PlanChangeResult } from '@/lib/types/billing';
import { formatCredits } from '@/lib/utils/usage-labels';

/**
 * Pure rules for the Plan & billing page: whether the workspace has a live paid plan, which way a
 * plan change goes, how a provider key is shown, and what summary proves Paddle applied a change.
 * Nothing here fetches or touches the browser, so every rule is unit-tested directly.
 */

/** The catalogue id of the free plan (`plans.catalog.json`). A workspace with no live plan is on it. */
export const TRIAL_PLAN_KEY = 'trial';

/** Subscription statuses that still mean "a paid plan is running" (canceled is not among them). */
const LIVE_STATUSES = new Set(['Active', 'Trialing', 'PastDue', 'Paused']);

/**
 * True when the workspace is on a live paid plan, which changes through cancel / change-plan rather
 * than a second checkout. NOT `hasBillingCustomer`: a workspace that canceled, or that started and
 * abandoned a checkout, keeps its Paddle customer but has no plan to change, and must be able to
 * choose one again.
 */
export function hasLivePaidPlan(summary: Pick<BillingSummary, 'subscription' | 'plans'>): boolean {
  const sub = summary.subscription;
  if (!sub || sub.planKey === TRIAL_PLAN_KEY || !LIVE_STATUSES.has(sub.status)) return false;
  // The free plan never has a billing period; a sold plan is in the catalogue as `current`.
  return summary.plans.some((p) => p.current) || sub.periodEnd !== null;
}

/** What a plan costs per month for a seat count, as the server compares plans (`IsDowngrade`). */
export function monthlyPrice(plan: Pick<BillingPlan, 'kind' | 'priceMonthlyUsd'>, seats: number): number {
  return plan.priceMonthlyUsd * (plan.kind === 'PerSeat' ? seats : 1);
}

export type PlanDirection = 'upgrade' | 'downgrade';

/**
 * Upgrade or downgrade, by monthly price × seats exactly like the backend: strictly cheaper is a
 * downgrade (from the next billing period); equal or dearer is an upgrade (now).
 */
export function planDirection(
  current: Pick<BillingPlan, 'kind' | 'priceMonthlyUsd'>,
  currentSeats: number,
  target: Pick<BillingPlan, 'kind' | 'priceMonthlyUsd'>,
  targetSeats: number,
): PlanDirection {
  return monthlyPrice(target, targetSeats) < monthlyPrice(current, currentSeats) ? 'downgrade' : 'upgrade';
}

/** The merchant name for a provider key: `paddle` → "Paddle"; an unknown key is capitalised. */
export function providerDisplayName(key: string | null | undefined, apiName?: string | null): string | null {
  const named = apiName?.trim();
  if (named) return named;
  const trimmed = key?.trim();
  if (!trimmed) return null;
  if (trimmed.toLowerCase() === 'paddle') return 'Paddle';
  if (trimmed.toLowerCase() === 'stripe') return 'Stripe';
  return trimmed.charAt(0).toUpperCase() + trimmed.slice(1);
}

/** The merchant name for the copy: the API's display name, else the key's, else a neutral phrase. */
export function merchantLabel(summary: Pick<BillingSummary, 'billingProvider' | 'billingProviderDisplayName'> | null | undefined): string {
  return providerDisplayName(summary?.billingProvider, summary?.billingProviderDisplayName) ?? 'your payment provider';
}

/** `merchantLabel` for the start of a sentence ("your payment provider" -> "Your payment provider"). */
export function sentenceStart(label: string): string {
  return label.charAt(0).toUpperCase() + label.slice(1);
}

/** True for a configured provider that is not Paddle (its previews can omit tax). */
export function isNonPaddleProvider(summary: Pick<BillingSummary, 'billingProvider'>): boolean {
  const key = summary.billingProvider?.trim().toLowerCase();
  return !!key && key !== 'paddle';
}

/** The test-id suffix for a provider's portal buttons: `paddle` -> "paddle", "Stripe" -> "stripe". */
export function providerTestIdSuffix(summary: Pick<BillingSummary, 'billingProvider'>): string {
  const slug = (summary.billingProvider ?? '').toLowerCase().replace(/[^a-z0-9]+/g, '-').replace(/^-+|-+$/g, '');
  return slug || 'paddle';
}

/** A date as "October 10, 2026" in the reader's locale. */
export function longDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, { year: 'numeric', month: 'long', day: 'numeric' });
}

/** Credits from the server's millicredit figure, formatted the way the rest of the page shows them. */
export function creditsFromMillis(millis: number): string {
  return formatCredits(millis / 1000);
}

/** A test the refreshed summary passes once Paddle's webhook has applied the change asked for. */
export type SummaryCheck = (summary: BillingSummary) => boolean;

export const cancelConfirmed: SummaryCheck = (s) => s.subscription?.cancelAtPeriodEnd === true;

export const resumeConfirmed: SummaryCheck = (s) => s.subscription?.cancelAtPeriodEnd === false;

/**
 * What proves a 202 from change-plan or seats landed, from the server's own `direction`:
 * an upgrade changes the live plan and seats; a downgrade is held as `scheduledChange`;
 * "keep" clears that scheduled change.
 */
export function planChangeConfirmed(planKey: string, result: Pick<PlanChangeResult, 'direction' | 'seats'>): SummaryCheck {
  return (s) => {
    const sub = s.subscription;
    if (!sub) return false;
    switch (result.direction) {
      case 'upgrade':
        return sub.planKey === planKey && sub.seats === result.seats;
      case 'downgrade':
        return sub.scheduledChange?.planKey === planKey && sub.scheduledChange.seats === result.seats;
      case 'keep':
        return !sub.scheduledChange;
      default:
        return false;
    }
  };
}

/**
 * Refreshes the summary until `isConfirmed` passes or the time runs out: the first refresh right
 * away, then one per `intervalMs`. Resolves to the confirming summary, or null on timeout (or when
 * `isCancelled` says the page went away). A failed refresh is just another miss.
 */
export async function pollSummary(
  refresh: () => Promise<BillingSummary | undefined>,
  isConfirmed: SummaryCheck,
  { intervalMs = 1000, timeoutMs = 20000, isCancelled = () => false }: {
    intervalMs?: number;
    timeoutMs?: number;
    isCancelled?: () => boolean;
  } = {},
): Promise<BillingSummary | null> {
  const deadline = Date.now() + timeoutMs;
  for (;;) {
    if (isCancelled()) return null;
    const fresh = await refresh().catch(() => undefined);
    if (fresh && isConfirmed(fresh)) return fresh;
    if (Date.now() + intervalMs > deadline || isCancelled()) return null;
    await new Promise((resolve) => setTimeout(resolve, intervalMs));
  }
}

/** The query parameter that asks the billing page to open the checkout of the picked plan. */
export const CHECKOUT_START_PARAM = 'start';

/**
 * Where a person who chose a plan before they had an account goes once they are signed in, or null
 * when the choice cannot go straight to a checkout and the plan stays a notice instead.
 *
 * The plan key is only ever taken from the workspace's own billing summary (the closed list of
 * plans on sale there), never from the stored value itself: an unknown key, the plan already held,
 * a per-seat plan (it needs a seat count first), a live paid plan (that changes through
 * change-plan), a member who cannot manage billing and an unverified address all answer null. No
 * price travels in the link; the checkout is created on the server for the plan key alone.
 */
export function pickedPlanCheckoutHref(
  planKey: string | undefined,
  summary: Pick<BillingSummary, 'subscription' | 'plans' | 'canManageBilling'> | undefined,
  emailVerified: boolean | undefined,
): string | null {
  if (!planKey || !summary || emailVerified === false || !summary.canManageBilling) return null;
  const plan = summary.plans.find((p) => p.key === planKey);
  if (!plan || plan.current || plan.kind === 'PerSeat' || plan.key === TRIAL_PLAN_KEY) return null;
  if (hasLivePaidPlan(summary)) return null;
  return `/settings/billing?plan=${encodeURIComponent(plan.key)}&${CHECKOUT_START_PARAM}=1#plans`;
}

/** sessionStorage key: the dashboard leaves the plan it is sending to checkout; the billing page consumes it. */
export const CHECKOUT_START_TOKEN_KEY = 'rb-plan-start:v1';
