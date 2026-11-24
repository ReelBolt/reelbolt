// The FAQ arrays in `./faqs` are the English source (and what the tests and llms.txt read). The
// pages render the same questions through the `faq` catalogue, entry for entry, so the visible
// answers and the FAQPage markup stay one list per locale.
import type { Faq } from './faqs.ts';
import { SUPPORT_EMAIL } from './legal-placeholders.ts';

export type FaqT = (key: string, values?: Record<string, string | number>) => string;

export function localizedFaqs(
  t: FaqT,
  set: 'home' | 'pricing',
  count: number,
  values: Record<string, string | number> = {},
): Faq[] {
  const vars = { policy: t('failedRunPolicy'), email: SUPPORT_EMAIL, ...values };
  return Array.from({ length: count }, (_, i) => ({
    question: t(`${set}.${i}.q`, vars),
    answer: t(`${set}.${i}.a`, vars),
  }));
}
