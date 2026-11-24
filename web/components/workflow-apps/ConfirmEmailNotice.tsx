'use client';

import { createContext, useContext, useState, type ReactNode } from 'react';
import { Alert, Button, Text } from '@mantine/core';
import { IconMailExclamation } from '@tabler/icons-react';
import { TurnstileWidget } from '@/components/auth/TurnstileWidget';
import { ApiError } from '@/lib/api/client';
import { resendVerification } from '@/lib/api/auth';
import { useT } from '@/i18n';

/** The refusal a run start answers for an address that is not confirmed (`EmailVerificationGate`). */
export const EMAIL_NOT_VERIFIED = 'EMAIL_NOT_VERIFIED';

/** True for `403 {code: "EMAIL_NOT_VERIFIED"}`: the run was not started because the address is not confirmed. */
export function isEmailNotVerified(err: unknown): boolean {
  return err instanceof ApiError && err.status === 403 && err.code === EMAIL_NOT_VERIFIED;
}

/** The "Resend the email" button of the notice. */
export const CONFIRM_EMAIL_RESEND_ID = 'app-run-confirm-email-resend';
/** The notice itself: focusable from script, so focus has somewhere to land while the button waits. */
export const CONFIRM_EMAIL_NOTICE_ID = 'app-run-confirm-email';

/**
 * Brings the notice into view and moves focus to it: to "Resend the email" when the button can be
 * pressed, and to the notice itself while it cannot. With a challenge configured the button is
 * disabled until the challenge resolves, and a disabled button cannot take focus: focus would stay
 * on Run and the readiness strip's "Resend the email" fix would do nothing a keyboard user can see.
 */
export function focusConfirmEmailNotice(): void {
  const resend = document.getElementById(CONFIRM_EMAIL_RESEND_ID) as HTMLButtonElement | null;
  const target = resend && !resend.disabled ? resend : document.getElementById(CONFIRM_EMAIL_NOTICE_ID);
  target?.scrollIntoView?.({ block: 'center' });
  target?.focus();
}

/**
 * The Turnstile site key reaches the run page as a value (a browser bundle cannot read a server
 * variable): the route's layout reads it and provides it here, like the shell's own flags. Empty,
 * the default, means the challenge is not configured and the resend needs no token.
 */
const TurnstileSiteKeyContext = createContext('');

export function RunPageConfigProvider({ turnstileSiteKey, children }: { turnstileSiteKey: string; children: ReactNode }) {
  return <TurnstileSiteKeyContext.Provider value={turnstileSiteKey}>{children}</TurnstileSiteKeyContext.Provider>;
}

interface ConfirmEmailNoticeProps {
  /** The signed-in person's own address, from their own session. */
  email: string;
  /** A run was just refused for this reason: the notice says so and is announced at once. */
  refused?: boolean;
}

/**
 * "Confirm your email before you run", shown next to Run while the address is not confirmed, so the
 * person learns it before pressing Run rather than from a refusal after. It never blocks the button:
 * the session's copy of "confirmed" can be older than the truth (the link was opened on another
 * device), and the server is the one that decides. The resend is the same call the shell's banner
 * makes; it answers the same whether or not anything was sent, so it is no account signal.
 */
export function ConfirmEmailNotice({ email, refused = false }: ConfirmEmailNoticeProps) {
  const t = useT('apps.run.confirmEmail');
  const siteKey = useContext(TurnstileSiteKeyContext);
  const [state, setState] = useState<'idle' | 'sending' | 'sent' | 'failed'>('idle');
  const [token, setToken] = useState('');
  const [widgetKey, setWidgetKey] = useState(0);

  const resend = async () => {
    if (state === 'sending') return;
    setState('sending');
    try {
      await resendVerification({ email, ...(token ? { turnstileToken: token } : {}) });
      setState('sent');
    } catch {
      setState('failed');
    }
    if (siteKey) {
      // A token is single-use: a fresh challenge must be solved before the next send.
      setToken('');
      setWidgetKey((k) => k + 1);
    }
  };

  return (
    <Alert
      color="yellow"
      variant="light"
      icon={<IconMailExclamation size={18} aria-hidden="true" />}
      title={t('title')}
      role={refused ? 'alert' : 'status'}
      id={CONFIRM_EMAIL_NOTICE_ID}
      tabIndex={-1}
      data-testid="confirm-email-notice"
    >
      {refused && <Text size="sm" fw={600}>{t('refused')}</Text>}
      <Text size="sm">{t('body', { email })}</Text>
      <Button
        id={CONFIRM_EMAIL_RESEND_ID}
        size="compact-sm"
        variant="light"
        mt="xs"
        loading={state === 'sending'}
        disabled={Boolean(siteKey) && !token}
        onClick={() => void resend()}
        style={{ minHeight: 44 }}
      >
        {state === 'sent' ? t('resendAgain') : t('resend')}
      </Button>
      {siteKey && <TurnstileWidget key={widgetKey} siteKey={siteKey} onToken={setToken} />}
      {state === 'sent' && <Text size="sm" mt={4} role="status">{t('sent')}</Text>}
      {state === 'failed' && <Text size="sm" mt={4} role="status">{t('failed')}</Text>}
    </Alert>
  );
}
