'use client';

import { useState } from 'react';
import { Alert, Anchor, Button } from '@mantine/core';
import { IconAlertTriangle } from '@tabler/icons-react';
import { resendVerification } from '@/lib/api/auth';
import { useAuth } from '@/lib/hooks/use-auth';
import { useT } from '@/i18n';
import { TurnstileWidget } from './TurnstileWidget';

/**
 * The "confirm your email" bar for a signed-in but unverified account.
 *
 * An unverified account can log in and look around, but the workflows that cost money are refused
 * with 403 EMAIL_NOT_VERIFIED (C5), so the state has to be visible rather than surfacing as a
 * confusing failure at the moment of running something.
 *
 * Renders nothing for a verified user (`emailVerified` defaults to true in useAuth, so a cookie
 * without the field — a self-host session issued before C3 — is not nagged), and nothing while
 * signed out. The resend endpoint answers 202 whether or not the address is an unverified account,
 * so this never becomes an account-existence signal.
 */
export function UnverifiedEmailBanner({ turnstileSiteKey = '' }: { turnstileSiteKey?: string }) {
  const { user, emailVerified } = useAuth();
  const t = useT('auth.unverified');
  const [state, setState] = useState<'idle' | 'sending' | 'sent' | 'failed'>('idle');
  const [token, setToken] = useState('');
  const [widgetKey, setWidgetKey] = useState(0);

  if (!user || emailVerified) return null;

  const resend = async () => {
    setState('sending');
    try {
      await resendVerification({ email: user.email, ...(token ? { turnstileToken: token } : {}) });
      setState('sent');
    } catch {
      setState('failed');
    }
    if (turnstileSiteKey) {
      // A token is single-use: a fresh widget must be solved before the next send.
      setToken('');
      setWidgetKey((k) => k + 1);
    }
  };

  return (
    <Alert
      icon={<IconAlertTriangle size={16} />}
      color="yellow"
      variant="light"
      mb="md"
      title={t('title')}
      role="status"
    >
      {/* Before the first attempt and after a failure the address is still shown: it is the
          signed-in user's own, read from their own cookie. */}
      {t('body', { email: user.email })}
      {state === 'sent' ? (
        <Anchor component="button" type="button" onClick={resend} ml={6} size="sm" disabled={!!turnstileSiteKey && !token}>
          {t('sendAgain')}
        </Anchor>
      ) : (
        <Button size="compact-sm" variant="light" ml="sm" loading={state === 'sending'}
          disabled={!!turnstileSiteKey && !token}
          onClick={resend}
        >
          {t('resend')}
        </Button>
      )}
      {turnstileSiteKey && <TurnstileWidget key={widgetKey} siteKey={turnstileSiteKey} onToken={setToken} />}
      {state === 'sent' && <span> {t('sent')}</span>}
      {state === 'failed' && <span> {t('failed')}</span>}
    </Alert>
  );
}
