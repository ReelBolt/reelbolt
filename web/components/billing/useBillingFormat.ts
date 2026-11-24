import { useFormat, useT, type T } from '@/i18n';
import type { BillingSummary } from '@/lib/types/billing';
import { providerDisplayName } from './subscription';

/** A date as "October 10, 2026" in the active locale. */
export function useLongDate(): (iso: string) => string {
  const f = useFormat();
  return (iso) => f.date(new Date(iso), 'medium');
}

/**
 * The payment provider's name for the copy: the API's display name, else the key's, else a neutral phrase.
 * `providerStart` is the same name for the start of a sentence.
 */
export function useMerchant(
  summary: Pick<BillingSummary, 'billingProvider' | 'billingProviderDisplayName'> | null | undefined,
): { provider: string; providerStart: string } {
  const t = useT('billing.merchant');
  const name = providerDisplayName(summary?.billingProvider, summary?.billingProviderDisplayName);
  return name ? { provider: name, providerStart: name } : { provider: t('fallback'), providerStart: t('fallbackStart') };
}

/** A subscription status as the buyer reads it; `t` is the unscoped translator. An unknown status is shown as sent. */
export function subscriptionStatusLabel(tt: T, status: string): string {
  switch (status) {
    case 'Active': return tt('billing.status.active');
    case 'Trialing': return tt('billing.status.trialing');
    case 'PastDue': return tt('billing.status.pastDue');
    case 'Paused': return tt('billing.status.paused');
    case 'Canceled': return tt('billing.status.canceled');
    default: return status;
  }
}
