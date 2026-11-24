'use client';

import type { Plan } from '@/lib/pricing';
import { promoBadgeLabel, promotionsForPlan } from '@/lib/promotions/active';
import { usePromotions } from './usePromotions';

/**
 * Corner badge on a pricing card when a plan discount applies to that plan. It is absolutely positioned
 * over the card's top edge, so a badge arriving after load moves nothing. Renders nothing otherwise.
 * The width stops at the card's padding and a long headline truncates; the full text stays in
 * `title` and `aria-label`.
 */
export function PlanPromoBadge({ plan }: { plan: Plan }) {
  const first = promotionsForPlan(usePromotions(), plan)[0];
  if (!first) return null;
  const label = promoBadgeLabel(first);
  return (
    <span
      role="note"
      title={label}
      aria-label={label}
      className="absolute -top-3 left-6 z-10 max-w-[calc(100%-3rem)] truncate bg-accent px-2.5 py-1 font-mono text-[11px] uppercase tracking-eyebrow text-paper"
    >
      {label}
    </span>
  );
}
