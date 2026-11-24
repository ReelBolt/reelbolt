import { useT } from '@/i18n';
import { isUnaffordable, type PictureEstimate } from '@/lib/editor/credit-estimate';

/** The credit wording of `lib/editor/credit-estimate` ("about 4.5 credits"), resolved through the catalogue. */
export function useRunLabelA() {
  const t = useT('editor-thumbnail-a.common');
  const credits = (n: number) => t('credits', { n: Math.round(n * 100) / 100 });
  /** "about 4.5 credits", or null when nothing is charged or no figure is known. */
  const estimateLine = (estimate: PictureEstimate | null | undefined): string | null => {
    if (!estimate || !estimate.billingEnabled || estimate.credits === undefined || !(estimate.credits > 0)) return null;
    return t('about', { credits: credits(estimate.credits) });
  };
  /** A Run button's words with its price; just the action when no price is known. */
  const runLabel = (action: string, estimate: PictureEstimate | null | undefined): string => {
    const line = estimateLine(estimate);
    return line && !isUnaffordable(estimate) ? t('runWithPrice', { action, line }) : action;
  };
  return { credits, estimateLine, runLabel };
}
