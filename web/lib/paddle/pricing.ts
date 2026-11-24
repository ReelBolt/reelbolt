import type { PricePreviewItem, PricePreviewResponse } from '@paddle/paddle-js';
import type { BillingInterval, BillingPlan, BillingSummary, PaddleClientConfig } from '../types/billing';

/**
 * Pure helpers for the embedded checkout: what to ask Paddle to price, what it answered, and where
 * to send the buyer afterwards. Nothing here touches the network or the browser — the calls
 * themselves live in `sdk.ts` — so every rule below is directly testable.
 */

/**
 * The app's base path. `web/next.config.ts` sets `basePath: '/app'`, so the dashboard's pages are
 * all served under it.
 */
export const APP_BASE_PATH = '/app';

/** Where a completed checkout lands: the page that explains the payment is not confirmed here. */
export const CHECKOUT_PATH = `${APP_BASE_PATH}/checkout`;

/**
 * The absolute URL to hand Paddle as `settings.successUrl`.
 *
 * Absolute because Paddle navigates the top-level browser to it from its own origin, so a relative
 * path would resolve against Paddle's domain and 404. The origin is the one the app is currently
 * served from — the same reasoning the rest of this app's client code uses when it calls the API by
 * relative path, so the dashboard talks to whichever origin the buyer is actually on (nginx in
 * production, localhost in dev) rather than to a value baked into the image.
 */
export function checkoutSuccessUrl(origin: string): string {
  return `${origin.replace(/\/+$/, '')}${CHECKOUT_PATH}`;
}

/**
 * The Paddle.js settings to initialize with, or null when this deployment has none: the summary
 * names a different provider, or the operator has not set a publishable token. Callers treat null
 * as "no embedded checkout" and fall back to the provider's hosted page for the same transaction.
 */
export function paddleClientOf(summary: BillingSummary | undefined): PaddleClientConfig | null {
  const config = summary?.paddle;
  if (!config || typeof config.clientToken !== 'string' || config.clientToken.trim() === '') return null;
  return config;
}

/**
 * The items to ask Paddle to price: one of each sellable plan, quantity 1.
 *
 * Quantity 1 because the card shows the plan's own price — per month, and for a per-seat plan per
 * seat — which is what the catalogue's list price means too. The seat count the buyer chooses is
 * applied by the server when it creates the transaction, and Paddle shows that exact total in the
 * checkout itself.
 */
export function previewItems(plans: BillingPlan[], interval: BillingInterval = 'monthly'): PricePreviewItem[] {
  const ids = plans.map((plan) => priceIdFor(plan, interval));
  return ids
    .filter((id): id is string => typeof id === 'string' && id.trim() !== '')
    .map((priceId) => ({ priceId, quantity: 1 }));
}

/** The provider price id a checkout for this plan carries in this interval; null when there is none. */
export function priceIdFor(plan: BillingPlan, interval: BillingInterval): string | null {
  const id = interval === 'annual' ? plan.annualPriceId : plan.priceId;
  return typeof id === 'string' && id.trim() !== '' ? id : null;
}

/**
 * Paddle's formatted total per price id, keyed the way the page looks it up.
 *
 * The strings are Paddle's own — already localized, already formatted, tax included where Paddle
 * could determine it — and this function only moves them. It never parses, converts, rounds or
 * re-formats one: a number this app computed would be a second, worse price, and the one the buyer
 * is actually charged is Paddle's.
 */
export function totalsByPriceId(preview: PricePreviewResponse | null | undefined): Map<string, string> {
  const totals = new Map<string, string>();
  for (const line of preview?.data?.details?.lineItems ?? []) {
    const formatted = line?.formattedTotals?.total;
    if (typeof line?.price?.id === 'string' && typeof formatted === 'string' && formatted.trim() !== '') {
      totals.set(line.price.id, formatted);
    }
  }
  return totals;
}
