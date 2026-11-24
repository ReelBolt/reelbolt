'use client';

import { useState } from 'react';
import {
  TextInput,
  Button,
  Divider,
  Stack,
  Alert,
  Anchor,
  Text,
} from '@mantine/core';
import { useForm } from '@mantine/form';
import { PasswordInput } from '@/components/ui/PasswordInput';
import { IconAlertCircle } from '@tabler/icons-react';
import Link from 'next/link';
import { login } from '@/lib/api/auth';
import { friendlyError } from '@/lib/api/error-messages';
import { navigateToNext } from '@/lib/auth/redirect';
import { EMPTY_CARRY, withCarry, type SignupCarry } from '@/lib/auth/signup-carry';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { notifyDeletionCancelled } from './DeletionCancelledNotice';
import { OAuthButtons } from './OAuthButtons';
import { useRouter } from 'next/navigation';
import { useT } from '@/i18n';

interface LoginFormProps {
  /**
   * Hidden when the deployment does not take self-serve sign-ups (SIGNUP_ENABLED=false, the
   * self-host default). Forgot-password stays: it is not gated on the API side, and an
   * admin-created user still has to be able to reset a temporary password.
   */
  signupEnabled?: boolean;
  /**
   * Configured OAuth providers (C10). Empty on a deployment with no credentials, in which case
   * nothing but the password form is rendered.
   */
  oauthProviders?: string[];
  /**
   * A validated, same-origin, absolute path to return to after a normal sign-in (see
   * safeNextRedirect). Set by the login page from an untrusted ?next=, so it is only ever a safe
   * path here; when absent, the normal destination applies. On the mustChangePassword branch it is
   * carried over to the change-password page (which re-validates it), never honoured directly, so
   * "next" can never bypass the forced password change.
   */
  next?: string;
  /**
   * What the visitor's own link carried: the persona, the campaign parameters and the plan,
   * already sanitized by the page (lib/auth/signup-carry.ts). The "Create account" link keeps all
   * of it together with `next` (the `next` prop above is the one used), and the provider buttons,
   * which can create an account, carry the persona and the campaign parameters to the API.
   * Sign-in itself sends none of it.
   */
  carry?: SignupCarry;
  /** The page is open inside another app's built-in browser, where Google refuses sign-in. */
  inAppBrowser?: boolean;
}

export function LoginForm({ signupEnabled = false, oauthProviders = [], next, carry = EMPTY_CARRY, inAppBrowser = false }: LoginFormProps) {
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const router = useRouter();
  const t = useT('auth.login');

  const form = useForm({
    initialValues: { email: '', password: '' },
    validate: {
      email: (v) => (!v ? t('emailRequired') : null),
      password: (v) => (!v ? t('passwordRequired') : null),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    setError('');
    setLoading(true);
    try {
      const res = await login(values);
      // Cookies are set by the Go API via Set-Cookie headers
      notifyAuthChange();
      // Signing in during the grace period cancelled a pending deletion: say so, once. The
      // notification outlives the client-side navigation below.
      if (res.deletionCancelled) notifyDeletionCancelled();
      if (res.mustChangePassword) {
        // The forced password gate is never bypassed by "next": a hostile return path must not let a
        // user skip the change. The change-password page owns its own post-change redirect, so the
        // validated path is carried over to it rather than dropped — a user who must change their
        // password still has to reach the OAuth consent screen afterwards.
        router.push(next ? `/change-password?next=${encodeURIComponent(next)}` : '/change-password');
      } else if (next) {
        // `next` is a validated same-origin path, and a domain-root one: the OAuth consent URL.
        // navigateToNext leaves for it the way the change-password page does — the dashboard's
        // router would prepend its '/app' basePath and 404, so a destination the dashboard does not
        // serve is requested verbatim by the browser.
        navigateToNext(next, router);
      } else {
        router.push('/dashboard');
      }
    } catch (err: unknown) {
      const friendly = friendlyError(err, t('failed'));
      if (friendly.kind === 'invalid-credentials') {
        // Tied to the field it is about, with the way out; Mantine clears it when the field is edited.
        form.setFieldError(
          'password',
          <>
            {friendly.message}{' '}
            <Anchor component={Link} href="/forgot-password" size="xs">
              {t('forgotPassword')}
            </Anchor>
          </>,
        );
      } else {
        setError(friendly.message);
      }
    } finally {
      setLoading(false);
    }
  });

  return (
    <form onSubmit={handleSubmit}>
      <Stack>
        {error && (
          <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light">
            {error}
          </Alert>
        )}
        <TextInput
          label={t('email')}
          placeholder={t('emailPlaceholder')}
          autoComplete="email"
          {...form.getInputProps('email')}
        />
        <PasswordInput
          label={t('password')}
          placeholder={t('passwordPlaceholder')}
          autoComplete="current-password"
          {...form.getInputProps('password')}
        />
        <Button type="submit" loading={loading} fullWidth>
          {t('submit')}
        </Button>
        {oauthProviders.length > 0 && (
          <>
            <Divider label={t('or')} labelPosition="center" />
            {/* The return path is this page's own validated `next`: kept for the way back from the provider. */}
            <OAuthButtons providers={oauthProviders} carry={{ ...carry, next: next ?? carry.next }} inAppBrowser={inAppBrowser} />
          </>
        )}
        <Text size="sm" ta="center" c="dimmed">
          {/* While the password field's own error offers the reset link, this one steps aside. */}
          {!form.errors.password && (
            <Anchor component={Link} href="/forgot-password">
              {t('forgotPassword')}
            </Anchor>
          )}
          {signupEnabled && (
            <>
              {!form.errors.password && ' · '}
              <Anchor component={Link} href={withCarry('/signup', { ...carry, next })}>
                {t('createAccount')}
              </Anchor>
            </>
          )}
        </Text>
      </Stack>
    </form>
  );
}
