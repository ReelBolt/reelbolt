'use client';

import { useState } from 'react';
import { Link } from '@/i18n/link';
import { useT } from '@/i18n';
import { useCookieConsent } from './CookieConsentProvider';
import { buttonClass } from '@/components/ui/button-styles';

export function CookieBanner() {
  const { consent, accept, reject } = useCookieConsent();
  const t = useT('consent');
  const [open, setOpen] = useState(false);

  // No choice recorded yet (or the mount effect hasn't run): show the banner. Once a choice has
  // been made (`'granted'` or `'denied'`), render nothing.
  if (consent !== null) {
    return null;
  }

  return (
    <div
      role="dialog"
      aria-modal="false"
      aria-labelledby="cookie-banner-heading"
      className="fixed inset-x-0 bottom-0 z-50 border-t border-line bg-paper pb-[env(safe-area-inset-bottom)] shadow-[0_-4px_16px_rgba(0,0,0,0.08)]"
    >
      <div className="mx-auto flex w-full max-w-6xl flex-col gap-1 px-3 py-1 sm:px-6 lg:px-8">
        <div className="flex items-center justify-between gap-2 sm:gap-4">
          <p className="min-w-0 font-sans text-xs leading-tight text-ink-muted">
            <span id="cookie-banner-heading">{t('summary')}</span>{' '}
            <Link
              href="/legal/privacy"
              className="inline-flex min-h-11 items-center font-medium text-accent-ink underline underline-offset-2 hover:text-accent"
            >
              {t('privacyLink')}
            </Link>{' '}
            <button
              type="button"
              aria-expanded={open}
              aria-controls="cookie-banner-details"
              onClick={() => setOpen((v) => !v)}
              className="inline-flex min-h-11 items-center font-medium text-accent-ink underline underline-offset-2 hover:text-accent"
            >
              {open ? t('hideDetails') : t('details')}
            </button>
          </p>
        {/* Reject/Accept share identical size classes from buttonClass — only color differs,
            so neither choice is visually weighted over the other. */}
        <div className="flex shrink-0 gap-2">
          <button
            type="button"
            onClick={reject}
            className={`!px-3 sm:!px-5 ${buttonClass({ variant: 'outline' })}`}
          >
            {t('reject')}
          </button>
          <button
            type="button"
            onClick={accept}
            className={`!px-3 sm:!px-5 ${buttonClass({ variant: 'accent' })}`}
          >
            {t('accept')}
          </button>
        </div>
        </div>
        <p
          id="cookie-banner-details"
          hidden={!open}
          className="pb-1 font-sans text-xs leading-tight text-ink-muted"
        >
          {t.rich('banner', {
            privacy: (chunks) => (
              <Link href="/legal/privacy" className="font-medium text-accent-ink hover:text-accent">
                {chunks}
              </Link>
            ),
          })}
        </p>
      </div>
    </div>
  );
}
