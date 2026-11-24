import { OG_SIZE, renderOgCard } from '@/lib/og-card';
import { localeStaticParams, resolveLocale, type LocaleParams } from '@/i18n/route';

// The Open Graph card in each non-default language (the English card is app/opengraph-image).
export const size = OG_SIZE;
// The brand name only: the visible text on the card is translated, the alt cannot vary per language here.
export const alt = 'ReelBolt';
export const contentType = 'image/png';
export const dynamic = 'force-static';
export const generateStaticParams = localeStaticParams;

export default async function OpengraphImage({ params }: LocaleParams) {
  return renderOgCard(await resolveLocale(params));
}
