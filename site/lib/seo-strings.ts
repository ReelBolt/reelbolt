import { loadMessages } from '@/i18n/request';
import type { Locale } from '@/i18n/config';

interface SeoMessages {
  description: string;
  plansName: string;
  featureList: string[];
  ogAlt: string;
  ogTitle: string;
  ogSubtitle: string;
}

/** The translated strings behind the JSON-LD blocks (`messages/<locale>/seo.json`). Server only. */
export function seoStrings(locale: Locale): SeoMessages {
  return (loadMessages(locale).seo ?? {}) as unknown as SeoMessages;
}
