'use client';

import { useT } from '@/i18n';
import { useCookieConsent } from './CookieConsentProvider';

export function CookiePreferencesLink({ className }: { className?: string }) {
  const { reopen } = useCookieConsent();
  const t = useT('layout.footer');

  return (
    <button
      type="button"
      onClick={reopen}
      className={`font-mono text-sm text-ink-muted underline-offset-2 hover:text-ink hover:underline ${className ?? ''}`.trim()}
    >
      {t('cookiePreferences')}
    </button>
  );
}
