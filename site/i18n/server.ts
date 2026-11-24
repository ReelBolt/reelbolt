// Server-only helpers: async server components, route handlers and generateMetadata.
import { getLocale, getTranslations, setRequestLocale } from 'next-intl/server';
import { defaultLocale, isLocale, type Locale } from './config';
import { makeFormat, type Format } from './format';
import { decorate, type Base, type T } from './t';

export { setRequestLocale };

/**
 * `await getT('layout.footer')`. Pass the locale explicitly in `generateMetadata` and route
 * handlers (no `setRequestLocale` has run there); pages and layouts call `setRequestLocale` first.
 */
export async function getT(namespace?: string, locale?: Locale): Promise<T> {
  const l = locale && isLocale(locale) ? locale : undefined;
  const get = async (ns?: string) =>
    (l ? await getTranslations({ locale: l, namespace: ns }) : await getTranslations(ns)) as unknown as Base;
  return decorate(await get(namespace), await get());
}

export async function getCurrentLocale(): Promise<Locale> {
  const locale = await getLocale();
  return isLocale(locale) ? locale : defaultLocale;
}

export async function getFormat(locale?: Locale): Promise<Format> {
  return makeFormat(locale && isLocale(locale) ? locale : await getCurrentLocale());
}
