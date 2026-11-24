'use client';

import { useState } from 'react';
import {
  Alert,
  Anchor,
  Button,
  Checkbox,
  Stack,
  Text,
  TextInput,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { IconAlertCircle, IconCircleCheck } from '@tabler/icons-react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import { resendVerification, signup } from '@/lib/api/auth';
import { ApiError } from '@/lib/api/client';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { LEGAL_PRIVACY_URL, LEGAL_TERMS_URL, legalVersionsFromRefusal } from '@/lib/auth/legal';
import { validatePassword } from '@/lib/auth/password';
import { navigateToNext } from '@/lib/auth/redirect';
import {
  EMPTY_CARRY,
  browserLandingPath,
  signupMarketingFields,
  withCarry,
  type SignupCarry,
} from '@/lib/auth/signup-carry';
import { rememberSignupIntent } from '@/lib/auth/signup-intent';
import { describeWait, signupRateLimitMessage } from '@/lib/auth/signup-copy';
import { useMarketingWording } from '@/lib/hooks/use-marketing';
import { markWelcomeNotApplicable } from '@/lib/onboarding/state';
import { userKeyFromEmail } from '@/lib/telemetry/identity';
import { MarketingOptInCheckbox } from '@/components/marketing/MarketingOptInCheckbox';
import { restoreFirstTouch, sendSignupSubmitted } from '@/components/analytics/SignupTracker';
import { PasswordInput } from '@/components/ui/PasswordInput';
import { TurnstileWidget } from './TurnstileWidget';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

// Only used if the API somehow answers 202 without a body; the normal path shows the API's own
// message verbatim, because that message is the one promise the endpoint makes: it is identical
// for a new address and for one that already has an account.
interface SignupFormProps {
  /** Cloudflare Turnstile site key, or '' when the deployment has no challenge. */
  turnstileSiteKey: string;
  /**
   * The versions the deployment is serving, read from the API at request time (F-7). Empty when
   * that read failed: the API then refuses the submission and answers with the current ones, which
   * a 409 fills in below.
   */
  termsVersion: string;
  privacyVersion: string;
  /** Prefilled from a team invitation (C8). */
  initialEmail?: string;
  /** A team invite token: the account is created verified and the visitor is signed straight in. */
  inviteToken?: string;
  /**
   * What the visitor's own link carried, already sanitized by the page (lib/auth/signup-carry.ts):
   * the persona and the campaign parameters go into the signup request, and every link to the
   * sign-in page keeps `for`, `plan`, `next` and `utm_*`. Nothing here is stored on the device.
   */
  carry?: SignupCarry;
  /**
   * Whether the welcome questions follow sign-in on this deployment (`WELCOME_FLOW_ENABLED`, read
   * by the page). The check-email panel promises them only when they exist.
   */
  welcomeEnabled?: boolean;
}

export function SignupForm({
  turnstileSiteKey,
  termsVersion,
  privacyVersion,
  initialEmail = '',
  inviteToken = '',
  carry = EMPTY_CARRY,
  welcomeEnabled = true,
}: SignupFormProps) {
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  // The address a signup was just sent for: set only after a 202, it switches the form to the panel.
  const [sentTo, setSentTo] = useState('');
  const [resendToken, setResendToken] = useState('');
  const [resendWait, setResendWait] = useState<number | undefined>(undefined);
  const [resendWidgetKey, setResendWidgetKey] = useState(0);
  const [resendState, setResendState] = useState<'idle' | 'sending' | 'sent' | 'failed'>('idle');
  const [loading, setLoading] = useState(false);
  const [turnstileToken, setTurnstileToken] = useState('');
  // Server-reported at render time; replaced by whatever a 409 refusal carries.
  const [versions, setVersions] = useState({ termsVersion, privacyVersion });
  const router = useRouter();
  // The optional marketing box shows the registry's current text; without it the box is hidden and
  // signup works exactly as before (the opt-in never gates the account).
  const wording = useMarketingWording();
  const t = useT('auth.signup');
  const tAll = useT();
  const tf = useT('auth.forgot');
  const tLogin = useT('auth.login');
  const signInHref = withCarry('/login', carry);

  const form = useForm({
    initialValues: {
      displayName: '',
      email: initialEmail,
      password: '',
      acceptTerms: false,
      marketingOptIn: false,
    },
    validate: {
      email: (v) => (!v.trim() ? tLogin('emailRequired') : null),
      // The one password field: there is no "type it again" box. The show/hide eye on the field
      // is how a visitor checks what they typed, and a forgotten password has a reset link.
      password: (v) => validatePassword(v),
      acceptTerms: (v) => (v ? null : t('termsRequired')),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    setError('');
    setNotice('');
    // Fail closed, like the server: a configured challenge with no token is a refusal, not a
    // request the API is expected to wave through.
    if (turnstileSiteKey && !turnstileToken) {
      setError(t('challenge'));
      return;
    }
    // The form passed its own checks and is being sent: the consented `signup.submitted` beacon.
    // It carries no form value and does not wait for (or learn) the answer.
    sendSignupSubmitted();
    setLoading(true);
    const optIn = values.marketingOptIn && wording.data !== undefined;
    try {
      const res = await signup({
        email: values.email.trim(),
        password: values.password,
        displayName: values.displayName.trim(),
        acceptTerms: values.acceptTerms,
        termsVersion: versions.termsVersion,
        privacyVersion: versions.privacyVersion,
        marketingOptIn: optIn,
        ...(optIn && wording.data ? { marketingWordingVersion: wording.data.version } : {}),
        ...(turnstileToken ? { turnstileToken } : {}),
        ...(inviteToken ? { inviteToken } : {}),
        // The persona and the campaign parameters of the visitor's own link. They travel in the
        // request (no cookie), so they survive the email round trip; absent when the link had none.
        ...signupMarketingFields(carry, browserLandingPath()),
      });
      if (inviteToken) {
        // The invite proved the address, so the API answered with a session and there is no
        // verification step to wait for. A validated return path is honoured the way sign-in
        // honours it; otherwise the dashboard.
        //
        // An invited teammate is not a welcome-flow candidate. Their session opens in the Personal
        // workspace every account gets, which is empty, so the dashboard would otherwise send them
        // to "what are you here to make" and create a personal first project, when they came to
        // join a team. This browser is told before the navigation; the team is one switch away.
        markWelcomeNotApplicable(userKeyFromEmail(values.email));
        notifyAuthChange();
        if (carry.next) navigateToNext(carry.next, router);
        else router.push('/dashboard');
        return;
      }
      // Never says whether the account was created: the API answers the same 202 either way, and
      // claiming "account created" would turn this page into an account-existence oracle.
      setNotice(res?.message || tf('uniformFallback'));
      // The plan and the return path of the visitor's link wait in this browser for the emailed
      // link, which carries only its token (lib/auth/signup-intent.ts). Bound to this address.
      rememberSignupIntent(carry, { email: values.email });
      setSentTo(values.email.trim());
      setResendState('idle');
      form.setValues({
        displayName: '',
        email: '',
        password: '',
        acceptTerms: false,
        marketingOptIn: false,
      });
      setTurnstileToken('');
    } catch (err: unknown) {
      if (err instanceof ApiError && err.code === 'invalid_marketing_wording') {
        // The wording was republished while the page was open: the box was ticked against text
        // that is no longer current, so it is cleared and the new text shown for a fresh choice.
        form.setFieldValue('marketingOptIn', false);
        void wording.mutate();
        setError(t('wordingUpdated'));
        return;
      }
      const current = legalVersionsFromRefusal(err instanceof ApiError ? err.body : undefined);
      if (current) {
        // The documents were published under us: this is not a retry, it is a different document
        // to agree to, so the box is cleared and the current version is shown.
        setVersions(current);
        form.setFieldValue('acceptTerms', false);
        setError(t('termsUpdated', { version: current.termsVersion }));
        return;
      }
      if (err instanceof ApiError && err.status === 429) {
        setError(signupRateLimitMessage(err.retryAfterSeconds));
        return;
      }
      setError(describeError(tAll, err, t('failed')));
    } finally {
      setLoading(false);
    }
  });

  async function handleResend() {
    setResendState('sending');
    try {
      await resendVerification({ email: sentTo, ...(resendToken ? { turnstileToken: resendToken } : {}) });
      setResendState('sent');
    } catch (err) {
      setResendWait(err instanceof ApiError && err.status === 429 ? (err.retryAfterSeconds ?? 0) : undefined);
      setResendState('failed');
    }
    if (turnstileSiteKey) {
      // A token is single-use: a fresh widget (new key) must be solved before the next send.
      setResendToken('');
      setResendWidgetKey((k) => k + 1);
    }
  }

  if (sentTo) {
    return (
      <Stack data-testid="check-email-panel">
        <Alert icon={<IconCircleCheck size={16} />} color="green" variant="light" role="status">
          {notice}
        </Alert>
        <Text fw={600}>{t('checkTitle')}</Text>
        <Text size="sm">
          {t.rich('checkBody', { email: sentTo, strong: (chunks) => <strong>{chunks}</strong> })}
        </Text>
        {/* What happens after the link, said without claiming an account now exists: the sentence
            is about confirming, and it is the same for a new address and a known one. */}
        {welcomeEnabled && <Text size="sm">{t('checkNext')}</Text>}
        <Text size="sm" c="dimmed">
          {t('checkHint')}
        </Text>
        {resendState === 'failed' && (
          <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
            {resendWait !== undefined && resendWait > 0
              ? t('resendRateLimited', { wait: describeWait(resendWait) })
              : t('resendFailed')}
          </Alert>
        )}
        {resendState === 'sent' && (
          <Text size="sm" role="status">
            {t('resendSent')}
          </Text>
        )}
        <TurnstileWidget key={resendWidgetKey} siteKey={turnstileSiteKey} onToken={setResendToken} />
        <Button
          variant="default"
          fullWidth
          loading={resendState === 'sending'}
          disabled={resendState === 'sent' || (!!turnstileSiteKey && !resendToken)}
          onClick={() => void handleResend()}
        >
          {t('resend')}
        </Button>
        <Button
          variant="subtle"
          fullWidth
          onClick={() => {
            form.setFieldValue('email', sentTo);
            setSentTo('');
            setNotice('');
            // The first submit used up the first-touch cookie; the corrected address is the one
            // the person will really use, so it gets the same first touch (consent permitting).
            restoreFirstTouch();
          }}
        >
          {t('changeAddress')}
        </Button>
        <Text size="sm" ta="center" c="dimmed">
          {t('confirmed')}{' '}
          <Anchor component={Link} href={signInHref}>
            {t('signIn')}
          </Anchor>
        </Text>
      </Stack>
    );
  }

  return (
    <form onSubmit={handleSubmit}>
      <Stack>
        {error && (
          <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
            {error}
          </Alert>
        )}
        {notice && (
          <Alert icon={<IconCircleCheck size={16} />} color="green" variant="light" role="status">
            {notice}
          </Alert>
        )}
        <TextInput
          label={t('name')}
          placeholder={t('namePlaceholder')}
          autoComplete="name"
          {...form.getInputProps('displayName')}
        />
        <TextInput
           
          label={tLogin('email')}
           
          placeholder={tLogin('emailPlaceholder')}
          autoComplete="email"
          {...form.getInputProps('email')}
        />
        <PasswordInput
          label={tLogin('password')}
          placeholder={t('passwordPlaceholder')}
          autoComplete="new-password"
          visibilityToggleButtonProps={{ 'aria-label': t('showPassword') }}
          {...form.getInputProps('password')}
        />
        <Checkbox
          label={
            <Text size="sm">
              {t.rich('accept', {
                terms: (chunks) => (
                  <Anchor href={LEGAL_TERMS_URL} target="_blank" rel="noreferrer" inherit>
                    {chunks}
                  </Anchor>
                ),
                privacy: (chunks) => (
                  <Anchor href={LEGAL_PRIVACY_URL} target="_blank" rel="noreferrer" inherit>
                    {chunks}
                  </Anchor>
                ),
              })}
            </Text>
          }
          {...form.getInputProps('acceptTerms', { type: 'checkbox' })}
        />
        {wording.data && (
          <MarketingOptInCheckbox
            wording={wording.data}
            checked={form.values.marketingOptIn}
            onChange={(checked) => form.setFieldValue('marketingOptIn', checked)}
          />
        )}
        <TurnstileWidget siteKey={turnstileSiteKey} onToken={setTurnstileToken} />
        <Button type="submit" loading={loading} fullWidth>
          {t('submit')}
        </Button>
        <Text size="sm" ta="center" c="dimmed">
          {t('haveAccount')}{' '}
          <Anchor component={Link} href={signInHref}>
            {t('signIn')}
          </Anchor>
        </Text>
      </Stack>
    </form>
  );
}
