'use client';

import { useState } from 'react';
import { useRouter } from 'next/navigation';
import { NativeSelect } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { LOCALE_COOKIE, LOCALE_INFOS, isLocale } from '@/i18n/config';
import { useLocale, useT } from '@/i18n';
import { updateAuthSettings } from '@/lib/api/auth-settings';
import { readUserCookie } from '@/lib/auth/user-cookie';

const ONE_YEAR_SECONDS = 365 * 24 * 60 * 60;

/** Writes the visitor-chosen language. Readable by script (not HttpOnly) on purpose; no PII in it. */
function writeLocaleCookie(code: string): void {
  const secure = location.protocol === 'https:' ? '; Secure' : '';
  document.cookie = `${LOCALE_COOKIE}=${encodeURIComponent(code)}; Path=/; Max-Age=${ONE_YEAR_SECONDS}; SameSite=Lax${secure}`;
}

/**
 * The language picker: lists whatever i18n/locales.json registers (endonyms, so each is readable
 * whatever the current language is). Choosing saves the choice to the
 * account first when signed in (a failed save changes nothing and says so), then refreshes so server components re-render in the new language.
 */
export function LanguageSelect({ withLabel = true }: { withLabel?: boolean }) {
  const t = useT('common.language');
  const current = useLocale();
  const router = useRouter();
  const [busy, setBusy] = useState(false);

  const change = async (code: string) => {
    if (!isLocale(code) || code === current || busy) return;
    setBusy(true);
    // Signed in = the readable session cookie exists (UI state only; the server decides authority).
    // Save to the account FIRST: the saved language outranks the device cookie, so a failed save must not
    // leave the interface in a state that disagrees with what the user is told.
    if (readUserCookie()) {
      try {
        await updateAuthSettings({ locale: code });
      } catch {
        notifications.show({ color: 'yellow', message: t('saveFailed') });
        setBusy(false);
        return;
      }
    }
    writeLocaleCookie(code);
    router.refresh();
    setBusy(false);
  };

  return (
    <NativeSelect
      data={LOCALE_INFOS.map((l) => ({ value: l.code, label: l.label }))}
      value={LOCALE_INFOS.some((l) => l.code === current) ? current : undefined}
      onChange={(e) => void change(e.currentTarget.value)}
      label={withLabel ? t('label') : undefined}
      aria-label={withLabel ? undefined : t('label')}
      disabled={busy}
      styles={{ input: { minHeight: 44 } }}
      data-testid="language-select"
    />
  );
}
