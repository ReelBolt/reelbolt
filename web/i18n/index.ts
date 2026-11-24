import { useLocale, useTranslations } from 'next-intl';
import { createFormat, type Format } from './format';
import { stableT, type BaseTranslator, type T } from './translator';

/**
 * Client + (non-async) server component hooks.
 *
 *   const t = useT('files.manager');   // namespace = catalogue file (+ optional sub path)
 *   t('uploadButton'); t('count', { count: 3 }); t.rich('terms', { link: (c) => <a>{c}</a> });
 *   t.msg(m)                           // resolve a Msg from non-component code
 *   const f = useFormat();             // f.number / f.date / f.credits / f.currency / f.relative
 */
export function useT(namespace?: string): T {
  const scoped = useTranslations(namespace as never) as unknown as BaseTranslator;
  const root = useTranslations() as unknown as BaseTranslator;
  return stableT(scoped, namespace ? root : scoped);
}

const formats = new Map<string, Format>();
export function useFormat(): Format {
  const locale = useLocale();
  let f = formats.get(locale);
  if (!f) formats.set(locale, (f = createFormat(locale)));
  return f;
}

export { useLocale } from 'next-intl';
export { msg, isMsg } from './msg';
export type { Msg, MsgValue } from './msg';
export type { Format, DateStyle } from './format';
export type { T } from './translator';
