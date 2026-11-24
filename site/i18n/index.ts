// Client + server component API. See docs/i18n.md. Server-only helpers live in ./server.
import { useLocale, useTranslations } from 'next-intl';
import { useMemo } from 'react';
import { defaultLocale, isLocale, type Locale } from './config';
import { makeFormat, type Format } from './format';
import { decorate, type Base, type T } from './t';

export type { T } from './t';
export type { Format } from './format';
export type { Msg } from './msg';
export { msg } from './msg';

/** `useT('layout.footer')` — namespace = catalogue file name, then an optional path inside it. */
export function useT(namespace?: string): T {
  const t = useTranslations(namespace as never) as unknown as Base;
  const root = useTranslations() as unknown as Base;
  return decorate(t, root);
}

export function useCurrentLocale(): Locale {
  const locale = useLocale();
  return isLocale(locale) ? locale : defaultLocale;
}

export function useFormat(): Format {
  const locale = useCurrentLocale();
  return useMemo(() => makeFormat(locale), [locale]);
}
