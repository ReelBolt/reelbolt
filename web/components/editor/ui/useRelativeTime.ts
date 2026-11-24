'use client';

import { useCallback } from 'react';
import { useLocale, useT } from '@/i18n';

/**
 * The locale-aware twin of `relativeTime` in `lib/editing/open-timeline`: same thresholds (just now, minutes,
 * hours, yesterday, days, then a short date), but the words come from the `editor-misc.relative` catalogue.
 */
export function useRelativeTime(): (iso: string, now?: number) => string {
  const t = useT('editor-misc.relative');
  const locale = useLocale();
  return useCallback(
    (iso: string, now: number = Date.now()): string => {
      const then = Date.parse(iso);
      if (Number.isNaN(then)) return iso;
      const seconds = Math.round((now - then) / 1000);
      if (seconds < 45) return t('justNow');
      const minutes = Math.round(seconds / 60);
      if (minutes < 60) return t('minutesAgo', { count: minutes });
      const hours = Math.round(minutes / 60);
      if (hours < 24) return t('hoursAgo', { count: hours });
      const days = Math.round(hours / 24);
      if (days === 1) return t('yesterday');
      if (days < 7) return t('daysAgo', { count: days });
      const date = new Date(then);
      const sameYear = date.getFullYear() === new Date(now).getFullYear();
      const options: Intl.DateTimeFormatOptions = sameYear ? { month: 'short', day: 'numeric' } : { month: 'short', day: 'numeric', year: 'numeric' };
      return date.toLocaleDateString(locale === 'en' ? 'en-US' : locale, options);
    },
    [t, locale],
  );
}
