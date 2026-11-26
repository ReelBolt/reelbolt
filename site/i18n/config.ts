// The language registry is data: i18n/locales.json (repo root, vendored here byte-identically by
// `node i18n/sync.mjs`). Adding a language needs no code change; see docs/i18n.md.
import registry from './locales.json' with { type: 'json' };
import { isAudiencePath } from '../lib/audiences/registry.ts';

export interface LocaleMeta {
  code: string;
  label: string;
  htmlLang: string;
  ogLocale: string;
  dir: 'ltr' | 'rtl';
}

/** A registered locale code. Kept as `string` because the set is data, not a union in code. */
export type Locale = string;

export const defaultLocale: Locale = registry.defaultLocale;
export const localeList: readonly LocaleMeta[] = registry.locales as LocaleMeta[];
export const locales: readonly Locale[] = localeList.map((l) => l.code);
/** Every locale served under a `/<code>/` prefix. */
export const prefixedLocales: readonly Locale[] = locales.filter((l) => l !== defaultLocale);

/**
 * The allowlist check. Every path that turns a request value into a catalogue file name goes
 * through this first, so a crafted segment ("../..") can never reach the file system.
 */
export function isLocale(value: unknown): value is Locale {
  return typeof value === 'string' && locales.includes(value);
}

export function localeMeta(locale: Locale): LocaleMeta {
  return localeList.find((l) => l.code === locale) ?? localeList[0];
}

/**
 * Pages that exist in every registered language. Everything else (legal documents, the blog, the
 * dashboard under /app, API routes, files) lives at one URL and is never prefixed.
 */
const TRANSLATED_ROUTES = ['/', '/features', '/pricing', '/examples', '/about', '/open-source', '/contact', '/contact/thank-you', '/runner', '/recipes'];

/** One recipe's page, `/recipes/<id>`: a family of translated pages rather than one fixed route. */
const RECIPE_DETAIL = /^\/recipes\/[^/]+$/;

export function isTranslatedPath(path: string): boolean {
  const clean = stripTrailingSlash(path.split(/[?#]/)[0] || '/');
  // The audience pages, `/for/<slug>`, are a closed family: only the six slugs of
  // `lib/audiences/registry` exist, so `/for/anything-else` is not a translated path.
  return TRANSLATED_ROUTES.includes(clean) || RECIPE_DETAIL.test(clean) || isAudiencePath(clean);
}

function stripTrailingSlash(path: string): string {
  return path.length > 1 ? path.replace(/\/+$/, '') : path;
}

/**
 * `localizePath('it', '/pricing')` is `/it/pricing`; the default locale is unprefixed and a path
 * that has no translation (legal, blog, /app, external URLs) comes back unchanged. The query and
 * hash survive.
 */
export function localizePath(locale: Locale, path: string): string {
  if (locale === defaultLocale || !isLocale(locale) || !path.startsWith('/')) return path;
  if (!isTranslatedPath(path)) return path;
  const [pathOnly, ...rest] = path.split(/(?=[?#])/);
  const suffix = rest.join('');
  return (pathOnly === '/' ? `/${locale}` : `/${locale}${pathOnly}`) + suffix;
}

/** Parts of the origin that are not pages of this app: the dashboard, the APIs, the docs, tracked links. */
const OTHER_APPS = /^\/(app|api|docs|l)(\/|$)/;

/**
 * True for a root-relative link to a page of THIS site (`/blog`, `/it/pricing?x=1`): not an
 * external or protocol-relative URL, not a fragment, and not another app on the same origin.
 */
export function isSitePagePath(path: string): boolean {
  return path.startsWith('/') && !path.startsWith('//') && !OTHER_APPS.test(path.split(/[?#]/)[0]);
}

/**
 * True when a link on a page of a PREFIXED language leads to a page of this site that exists only
 * in English (legal, blog). The two are different root layouts (`app/[locale]` and `app/(en)`), so
 * the browser opens the target as a new document; `@/i18n/link` uses this to carry the visitor's
 * campaign across that load. Never true in the default language, for a translated path, for an
 * external URL or for another app on the same origin.
 */
export function leavesLocaleLayout(locale: Locale, path: string): boolean {
  if (locale === defaultLocale || !isLocale(locale)) return false;
  return isSitePagePath(path) && !isTranslatedPath(path);
}

/**
 * The published user guide in a language: `/docs/` for the default locale, `/docs/<code>/` otherwise
 * (docs-site/docusaurus.config.js). `page` is a guide path such as `runner/`. It sits outside this
 * app, so link it with a plain `<a>`; `next/link` would 404 and `localizePath` would give `/it/docs/`.
 */
export function docsHref(locale: string, page = ''): string {
  return locale === defaultLocale || !isLocale(locale) ? `/docs/${page}` : `/docs/${locale}/${page}`;
}

/** The unprefixed twin of a path that may carry a locale prefix, and the locale it carried. */
export function splitLocalePath(path: string): { locale: Locale; path: string } {
  const m = /^\/([^/]+)(\/.*)?$/.exec(path);
  if (m && m[1] !== defaultLocale && isLocale(m[1])) return { locale: m[1], path: m[2] || '/' };
  return { locale: defaultLocale, path };
}

/** hreflang map for a translated path: every registered locale plus x-default (English). */
export function alternateLanguages(path: string): Record<string, string> {
  const out: Record<string, string> = {};
  for (const l of localeList) out[l.htmlLang] = localizePath(l.code, path);
  out['x-default'] = localizePath(defaultLocale, path);
  return out;
}
