import { getLocale, getTranslations } from 'next-intl/server';
import { createFormat, type Format } from './format';
import { stableT, type BaseTranslator, type T } from './translator';

/** Async server components, `generateMetadata`, route handlers: `const t = await getT('settings');`. */
export async function getT(namespace?: string): Promise<T> {
  const scoped = (await getTranslations(namespace)) as unknown as BaseTranslator;
  const root = namespace ? ((await getTranslations()) as unknown as BaseTranslator) : scoped;
  return stableT(scoped, root);
}

export async function getFormat(): Promise<Format> {
  return createFormat(await getLocale());
}

export { getLocale } from 'next-intl/server';
