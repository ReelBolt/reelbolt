import registry from './locales.json';

/**
 * The language registry (i18n/locales.json at the repo root, vendored byte-identically by
 * `node i18n/sync.mjs`). A language is data: this module never names one.
 */
export interface LocaleInfo {
  code: string;
  label: string;
  htmlLang: string;
  ogLocale: string;
  dir: 'ltr' | 'rtl';
}

export const LOCALE_INFOS: readonly LocaleInfo[] = registry.locales as LocaleInfo[];
export const LOCALES: readonly string[] = LOCALE_INFOS.map((l) => l.code);
export const DEFAULT_LOCALE: string = registry.defaultLocale;

/** Visitor-chosen language; readable by script on purpose (the language switcher writes it). */
export const LOCALE_COOKIE = 'rb_locale';
/** Field of the readable `reelbolt_user` cookie that carries the saved user locale. */
export const USER_LOCALE_FIELD = 'locale';

/** Pseudo-locale for layout/truncation checks. Never in the registry, never shipped. */
export const PSEUDO_LOCALE = 'en-XA';

/** True only under `NEXT_PUBLIC_I18N_PSEUDO=1` or Vitest. */
export function pseudoEnabled(): boolean {
  return process.env.NEXT_PUBLIC_I18N_PSEUDO === '1' || process.env.VITEST !== undefined;
}

/**
 * The allowlist. EVERY externally supplied locale (cookie, header, saved setting, form field) goes
 * through this before it is used for anything, and a path is never built from an unvalidated value.
 */
export function isLocale(value: unknown): value is string {
  if (typeof value !== 'string') return false;
  if (LOCALES.includes(value)) return true;
  return value === PSEUDO_LOCALE && pseudoEnabled();
}

export function localeInfo(code: string): LocaleInfo {
  const found = LOCALE_INFOS.find((l) => l.code === code);
  if (found) return found;
  // The pseudo-locale reads as English, left to right.
  return LOCALE_INFOS.find((l) => l.code === DEFAULT_LOCALE) ?? LOCALE_INFOS[0];
}

/** The best allowlisted locale for an Accept-Language header (q-values honoured, region stripped), else null. */
export function fromAcceptLanguage(header: string | null | undefined): string | null {
  if (!header || header.length > 1000) return null;
  const ranked = header
    .split(',')
    .map((part, index) => {
      const [tag, ...params] = part.trim().split(';');
      const q = params
        .map((p) => p.trim())
        .find((p) => p.toLowerCase().startsWith('q='));
      const weight = q === undefined ? 1 : Number(q.slice(2));
      return { tag: tag.trim().toLowerCase(), weight: Number.isFinite(weight) ? weight : 0, index };
    })
    .filter((x) => x.tag && x.tag !== '*' && x.weight > 0)
    .sort((a, b) => b.weight - a.weight || a.index - b.index);
  for (const { tag } of ranked) {
    const base = tag.split('-')[0];
    // Match the full tag first so a registered regional locale would win over its base language.
    const hit = LOCALES.find((code) => code.toLowerCase() === tag) ?? LOCALES.find((code) => code.toLowerCase() === base);
    if (hit) return hit;
  }
  return null;
}

export interface LocaleSignals {
  /** `locale` from the `reelbolt_user` cookie (the saved account setting). */
  user?: unknown;
  /** The `rb_locale` cookie. */
  cookie?: unknown;
  acceptLanguage?: string | null;
}

/** saved user locale -> `rb_locale` cookie -> Accept-Language -> default. Every step allowlisted. */
export function negotiateLocale(signals: LocaleSignals): string {
  if (isLocale(signals.user)) return signals.user;
  if (isLocale(signals.cookie)) return signals.cookie;
  return fromAcceptLanguage(signals.acceptLanguage) ?? DEFAULT_LOCALE;
}
