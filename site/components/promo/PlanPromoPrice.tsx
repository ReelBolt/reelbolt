'use client';

import { offerOpenUntil } from '@/lib/promotions/active';
import { planPriceFor } from '@/lib/promotions/plan-prices';
import { useT } from '@/i18n';
import { usePlanPrices } from './usePromotions';

// Every slot is always rendered, so the block is the same height with or without a live promotion. The
// line-height and min-height match, so an empty slot holds one line and a long string truncates.
const SLOT = 'min-h-5 truncate font-mono text-xs leading-5 text-ink-muted';

/**
 * The price figure on a pricing card. Renders the server-side catalog price (`catalog`, `unit`) until
 * the live plan price for this plan is known; then the struck original, the discounted figure and
 * the offer's end date (never how long the discount lasts). Every amount is the provider's string, verbatim. `promoEligible` false (the trial) never shows
 * a promotion, but keeps the same block height.
 */
export function PlanPromoPrice({
  planId,
  catalog,
  unit,
  promoEligible = true,
}: {
  planId: string;
  catalog: string;
  unit: string;
  promoEligible?: boolean;
}) {
  const t = useT('promo.price');
  const live = planPriceFor(usePlanPrices(), planId);
  const price = promoEligible ? live : null;
  // Only the end date, from the one launch-offer constant; the coupon's duration is never read.
  const openUntil = price ? offerOpenUntil() : '';

  return (
    <div className="mt-4">
      <p className={SLOT} title={price?.original}>
        {price && (
          <s className="text-ink-muted">
            <span className="sr-only">{t('was')} </span>
            {price.original}
          </s>
        )}
      </p>
      <p className="flex flex-wrap items-baseline gap-x-2">
        <span className="font-display text-4xl font-bold tracking-tight text-ink">
          {price && <span className="sr-only">{t('now')} </span>}
          {price ? price.discounted : catalog}
        </span>
        <span className="font-mono text-xs uppercase tracking-eyebrow text-ink-muted">{unit}</span>
      </p>
      <p className={SLOT} title={openUntil || undefined}>
        {openUntil}
      </p>
    </div>
  );
}
