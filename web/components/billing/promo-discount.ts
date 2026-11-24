import { msg, type Msg } from '@/i18n/msg';
import type { BillingPlan, PromoDiscount } from '@/lib/types/billing';
import { formatMinorUnits } from '@/lib/utils/billing';

/**
 * Whether moving to `target` ends the subscription's promo discount. The server keeps the discount
 * only when the new plan's price id is one the promotion targets (the same rule as checkout), so
 * anything else ends it.
 */
export function promoEndsWith(target: Pick<BillingPlan, 'priceId'>, promo: PromoDiscount | null | undefined): boolean {
  return promo != null && !promo.targetPriceIds.includes(target.priceId ?? '');
}

/** The promo's value as the customer knows it: "20%" or "$5.00". Null when it cannot be printed. */
function promoValue(promo: PromoDiscount): string | null {
  if (promo.discountType === 'Percentage') {
    const percent = promo.amount.trim();
    return percent ? `${percent}%` : null;
  }
  return formatMinorUnits(promo.amount, promo.currencyCode);
}

/**
 * The confirm-modal sentence for a plan change that ends the promo. A downgrade adds that keeping
 * the current plan later (cancelling the scheduled change) does not restore it.
 */
export function promoEndsSentence(promo: PromoDiscount, downgrade: boolean, currentPlanName: string): string {
  const value = promoValue(promo);
  const head = value ? `Your ${value} promo ends with this change.` : 'Your promo ends with this change.';
  return downgrade ? `${head} Keeping ${currentPlanName} later does not bring it back.` : head;
}

/** The same sentence as {@link promoEndsSentence}, as a catalogue message for the active locale. */
export function promoEndsMsg(promo: PromoDiscount, downgrade: boolean, currentPlanName: string): Msg {
  const value = promoValue(promo);
  const head = value ? msg('billing.promo.endsWithValue', { value }) : msg('billing.promo.ends');
  return downgrade ? msg('billing.promo.endsDowngrade', { head, plan: currentPlanName }) : head;
}
