'use client';

import { useEffect, useRef, useState } from 'react';
import { Alert, Anchor, Button, Divider, Loader, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertCircle, IconCircleCheck } from '@tabler/icons-react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { resendVerification, verifyEmail } from '@/lib/api/auth';
import { ApiError } from '@/lib/api/client';
import { describeError } from '@/lib/api/describe-error';
import { classifyFailure } from '@/lib/api/failure-kind';
import { describeWait } from '@/lib/auth/signup-copy';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { navigateToNext } from '@/lib/auth/redirect';
import { useT } from '@/i18n';
import { useLatest } from '@/components/assistant/useLatest';
import { TurnstileWidget } from './TurnstileWidget';

interface VerifyEmailStatusProps {
  /** The token from the emailed link (?token=…). Empty when the link arrived without one. */
  token: string;
  /**
   * A validated same-origin return path (the page runs the untrusted `?next=` through
   * `safeNextRedirect`). Honoured after a successful verification the way sign-in honours it.
   * Absent on today's emailed link, which carries only the token.
   */
  next?: string;
  /** Cloudflare Turnstile site key, or '' when the deployment has no challenge (the resend needs it). */
  turnstileSiteKey?: string;
}

type State = 'pending' | 'done' | 'failed';

/**
 * Redeems the verification link on mount. The API consumes the token in a single transaction and
 * answers with a session, exactly like /auth/token, so a success signs the visitor in and the
 * router can go straight to the dashboard. The dashboard is the target on purpose: the app layout
 * sends a new user on to the welcome questions from there, and everyone else stays.
 *
 * A used, expired or unknown token is the same generic failure (the API does not distinguish them),
 * and the page offers a fresh link right there rather than an explanation of which case it was:
 * the person types their address and the public resend endpoint is asked. That endpoint answers the
 * same whether or not anything was sent, so this page is no account-existence signal either.
 */
export function VerifyEmailStatus({ token, next, turnstileSiteKey = '' }: VerifyEmailStatusProps) {
  const router = useRouter();
  const t = useT('auth.verify');
  const tAll = useT();
  // Read inside the one-shot effect only: a new translator identity must not re-run the verification.
  const tRef = useLatest(t);
  const tAllRef = useLatest(tAll);
  const [state, setState] = useState<State>(token ? 'pending' : 'failed');
  const [error, setError] = useState('');
  // The call itself failed (offline, server down): the link may still be fine, so do not say it cannot be used.
  const [unreachable, setUnreachable] = useState(false);
  // React StrictMode mounts effects twice in development, and the second call would present an
  // already-consumed token and overwrite the success with "invalid or expired token".
  const started = useRef(false);

  useEffect(() => {
    if (!token || started.current) return;
    started.current = true;
    let cancelled = false;
    const t = tRef.current;
    verifyEmail({ token })
      .then(() => {
        if (cancelled) return;
        setState('done');
        notifications.show({ title: t('confirmedTitle'), message: t('confirmedBody'), color: 'green' });
        notifyAuthChange();
        if (next) navigateToNext(next, router);
        else router.push('/dashboard');
      })
      .catch((err: unknown) => {
        if (cancelled) return;
        setState('failed');
        const kind = classifyFailure(err);
        setUnreachable(kind === 'network' || kind === 'server' || kind === 'rateLimited');
        setError(describeError(tAllRef.current, err, t('invalidToken')));
      });
    return () => {
      cancelled = true;
    };
  }, [token, next, router, tRef, tAllRef]);

  if (state === 'pending') {
    return (
      <Stack align="center" gap="sm">
        <Loader size="sm" />
        <Text size="sm" c="dimmed">
          {t('verifying')}
        </Text>
      </Stack>
    );
  }

  if (state === 'done') {
    return (
      <Alert icon={<IconCircleCheck size={16} />} color="green" variant="light" role="status">
        {t('verified')}
      </Alert>
    );
  }

  return (
    <Stack gap="sm">
      <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
        {token ? (unreachable ? error : t('cannotUse', { error })) : t('missingToken')}
      </Alert>
      <Text size="sm" c="dimmed">
        {t('info')}
      </Text>
      <ResendLink turnstileSiteKey={turnstileSiteKey} />
      <Divider my={4} />
      <Button
        component={Link}
        href={next ? `/login?next=${encodeURIComponent(next)}` : '/login'}
        variant="default"
        fullWidth
      >
        {t('toSignIn')}
      </Button>
      <Text size="sm" ta="center" c="dimmed">
        {t('signedIn')}{' '}
        <Anchor component={Link} href="/dashboard">
          {t('toDashboard')}
        </Anchor>
      </Text>
    </Stack>
  );
}

/**
 * "Send me a new link", on the page a dead link leads to. Without it the only way forward was to
 * sign in with a password typed once and find the resend in the banner. The answer is uniform by
 * design: the same sentence for an address that is waiting, one that is confirmed and one nobody
 * registered.
 */
function ResendLink({ turnstileSiteKey }: { turnstileSiteKey: string }) {
  const t = useT('auth.verify');
  const tLogin = useT('auth.login');
  const tSignup = useT('auth.signup');
  const [email, setEmail] = useState('');
  const [fieldError, setFieldError] = useState('');
  const [state, setState] = useState<'idle' | 'sending' | 'sent' | 'failed'>('idle');
  const [wait, setWait] = useState<number | undefined>(undefined);
  const [challenge, setChallenge] = useState('');
  const [widgetKey, setWidgetKey] = useState(0);

  const send = async (event: React.FormEvent) => {
    event.preventDefault();
    const address = email.trim();
    if (!address) {
      setFieldError(tLogin('emailRequired'));
      return;
    }
    setFieldError('');
    setState('sending');
    try {
      await resendVerification({ email: address, ...(challenge ? { turnstileToken: challenge } : {}) });
      setState('sent');
    } catch (err) {
      setWait(err instanceof ApiError && err.status === 429 ? (err.retryAfterSeconds ?? 0) : undefined);
      setState('failed');
    }
    if (turnstileSiteKey) {
      // A token is single-use: a fresh widget (new key) must be solved before the next send.
      setChallenge('');
      setWidgetKey((k) => k + 1);
    }
  };

  return (
    <form onSubmit={(e) => void send(e)} noValidate data-testid="verify-resend">
      <Stack gap="xs">
        <Text fw={600} size="sm">
          {t('resendTitle')}
        </Text>
        <TextInput
          type="email"
          label={tLogin('email')}
          description={t('resendHint')}
          placeholder={tLogin('emailPlaceholder')}
          autoComplete="email"
          value={email}
          error={fieldError || undefined}
          onChange={(e) => {
            setEmail(e.currentTarget.value);
            if (state !== 'sending') setState('idle');
          }}
        />
        <TurnstileWidget key={widgetKey} siteKey={turnstileSiteKey} onToken={setChallenge} />
        <Button
          type="submit"
          fullWidth
          loading={state === 'sending'}
          disabled={state === 'sent' || (!!turnstileSiteKey && !challenge)}
          style={{ minHeight: 44 }}
        >
          {t('resendSubmit')}
        </Button>
        {state === 'sent' && (
          <Text size="sm" role="status">
            {t('resendSent')}
          </Text>
        )}
        {state === 'failed' && (
          <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
            {wait !== undefined && wait > 0 ? tSignup('resendRateLimited', { wait: describeWait(wait) }) : tSignup('resendFailed')}
          </Alert>
        )}
      </Stack>
    </form>
  );
}
