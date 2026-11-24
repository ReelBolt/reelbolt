import type { BillingInterval, BillingPlan, BillingSubscriptionSummary } from '@/lib/types/billing';

/**
 * A plan sells yearly only when the catalogue has a yearly price AND the provider has the price id
 * to charge it with (Paddle with no yearly id cannot sell it, so the switch must not offer it).
 */
export function sellsAnnual(plan: BillingPlan): boolean {
  return (
    typeof plan.priceAnnualUsd === 'number' &&
    plan.priceAnnualUsd > 0 &&
    typeof plan.annualPriceId === 'string' &&
    plan.annualPriceId.trim() !== ''
  );
}

/** The yearly list price spread over twelve months, for display through the money helpers. */
export function annualPerMonth(plan: Pick<BillingPlan, 'priceAnnualUsd'>): number | null {
  return typeof plan.priceAnnualUsd === 'number' ? plan.priceAnnualUsd / 12 : null;
}

/**
 * How many months the yearly price saves: 12 minus yearly / monthly, and only when that is exactly a
 * whole number of months. Anything else shows no claim at all.
 */
export function monthsFree(plan: Pick<BillingPlan, 'priceAnnualUsd' | 'priceMonthlyUsd'>): number | null {
  if (typeof plan.priceAnnualUsd !== 'number' || plan.priceMonthlyUsd <= 0) return null;
  const free = 12 - plan.priceAnnualUsd / plan.priceMonthlyUsd;
  const whole = Math.round(free);
  if (whole < 1 || Math.abs(free - whole) > 1e-9) return null;
  return whole;
}

/** True when the workspace is on a yearly subscription. */
export function isAnnualSubscriber(subscription: Pick<BillingSubscriptionSummary, 'interval'> | null | undefined): boolean {
  return subscription?.interval === 'annual';
}

/** The interval a card's action is for: a yearly subscriber stays yearly whatever the switch says. */
export function effectiveInterval(
  picked: BillingInterval | null,
  subscription: Pick<BillingSubscriptionSummary, 'interval'> | null | undefined,
): BillingInterval {
  return isAnnualSubscriber(subscription) ? 'annual' : picked ?? 'monthly';
}
