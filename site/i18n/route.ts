import { notFound } from 'next/navigation';
import { defaultLocale, isLocale, prefixedLocales, type Locale } from './config';

export type LocaleParams = { params: Promise<{ locale: string }> };

/** The `[locale]` segment of a thin route file: an unregistered code (or `en`, which is unprefixed) is a 404. */
export async function resolveLocale(params: LocaleParams['params']): Promise<Locale> {
  const { locale } = await params;
  if (!isLocale(locale) || locale === defaultLocale) notFound();
  return locale;
}

export function localeStaticParams(): { locale: string }[] {
  return prefixedLocales.map((locale) => ({ locale }));
}
