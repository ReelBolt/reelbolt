'use client';

import type { ReactNode } from 'react';
import { useCarryHref } from '@/components/analytics/useSignupHref';
import { useBasePath } from '@/i18n/use-base-path';
import { useCurrentLocale, useT } from '@/i18n';
import { isTranslatedPath, localeList, localizePath, type LocaleMeta } from '@/i18n/config';
import { localeCookie } from '@/i18n/cookie';

/**
 * One language's link. The href is the equivalent URL in that language, plus the audience and the
 * campaign parameters of the visitor's landing: switching language is a full document load, and
 * the landing is held in memory only, so without them a visitor who has not accepted cookies would
 * lose the campaign they arrived with (`useCarryHref`; the server-rendered href is the plain one).
 */
function LanguageLink({
  locale,
  target,
  active,
  label,
  children,
}: {
  locale: LocaleMeta;
  target: string;
  active: boolean;
  label: string | undefined;
  children: ReactNode;
}) {
  const href = useCarryHref(localizePath(locale.code, target));
  return (
    <a
      href={href}
      hrefLang={locale.htmlLang}
      lang={locale.htmlLang}
      aria-current={active ? 'true' : undefined}
      aria-label={label}
      onClick={() => {
        document.cookie = localeCookie(locale.code, window.location.protocol === 'https:');
      }}
      className={`inline-flex min-h-11 items-center underline-offset-2 hover:text-ink hover:underline ${
        active ? 'font-bold text-ink' : ''
      }`}
    >
      {children}
    </a>
  );
}

/**
 * The footer language switcher. Each language is a plain `<a>` to the equivalent URL: the English
 * and prefixed pages are different root layouts, so the browser makes a full load either way. A
 * page that exists only in English (legal, blog) has no twin, so the other language's link goes to
 * that language's home page. The click also stores the choice in `rb_locale`, a functional
 * preference cookie (no consent needed) that `/` reads to redirect a returning visitor.
 */
export function LanguageSwitcher() {
  const t = useT('layout.language');
  const current = useCurrentLocale();
  const basePath = useBasePath();
  const target = isTranslatedPath(basePath) ? basePath : '/';

  return (
    <nav aria-label={t('label')} className="flex items-center gap-2 font-mono text-xs text-ink-faint">
      <span aria-hidden="true">{t('label')}:</span>
      <ul className="flex items-center gap-2">
        {localeList.map((l) => {
          const active = l.code === current;
          return (
            <li key={l.code}>
              <LanguageLink
                locale={l}
                target={target}
                active={active}
                label={active ? undefined : t('switchTo', { language: l.label })}
              >
                {l.label}
              </LanguageLink>
            </li>
          );
        })}
      </ul>
    </nav>
  );
}
