import { sameEnum, type CheckoutPreview, type CheckoutPreviewRequest } from '@/lib/api/promotions';
import type { BillingInterval, BillingPlan } from '@/lib/types/billing';

/** One thing a promo code could discount, keyed the way the page keys its pending actions. */
export interface PreviewTarget {
  id: string;
  label: string;
  request: CheckoutPreviewRequest;
}

/** The preview request a plan card makes, so the code check and the card share one cache entry. */
export function planPreviewRequest(plan: BillingPlan, seats: number | undefined, promoCode: string | null, interval: BillingInterval = 'monthly'): CheckoutPreviewRequest {
  return {
    plan: plan.key,
    ...(seats === undefined ? {} : { seats }),
    ...(promoCode ? { promoCode } : {}),
    ...(interval === 'annual' ? { interval } : {}),
  };
}

export function packPreviewRequest(pack: number, promoCode: string | null): CheckoutPreviewRequest {
  return promoCode ? { topUpCredits: pack, promoCode } : { topUpCredits: pack };
}

/** Whether a preview carries a price discount (a pack bonus adds credits but leaves the price alone). */
export function hasPriceDiscount(preview: CheckoutPreview | undefined): preview is CheckoutPreview {
  return !!preview?.promotion && !sameEnum(preview.promotion.kind, 'packBonus');
}
