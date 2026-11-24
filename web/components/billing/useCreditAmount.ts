import { useFormat, useT } from '@/i18n';
import { creditRangeMsg } from '@/lib/billing-msg';

/**
 * A credit figure in the active locale, with the same rounding as `formatCreditAmount`
 * (two decimals; `roundUp` for a "needs N to start" figure).
 */
export function useCreditAmount(): (credits: number, options?: { roundUp?: boolean }) => string {
  const f = useFormat();
  return (credits, options = {}) => {
    const value = Number.isFinite(credits) ? credits : 0;
    const shown = options.roundUp ? Math.ceil(Math.round(value * 1000) / 1000) : value;
    return f.number(shown, { maximumFractionDigits: 2 });
  };
}

/** "≈ 12.5–20 credits" for a range, collapsing to "≈ 20 credits" when both ends agree. */
export function useCreditRange(): (low: number, high: number) => string {
  const t = useT();
  return (low, high) => t.msg(creditRangeMsg(low, high));
}
