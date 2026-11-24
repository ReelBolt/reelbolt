'use client';

import { useState } from 'react';
import { Alert, Anchor, Button, Stack, Text, TextInput } from '@mantine/core';
import { useForm } from '@mantine/form';
import { IconAlertCircle, IconCircleCheck } from '@tabler/icons-react';
import Link from 'next/link';
import { forgotPassword } from '@/lib/api/auth';
import { TurnstileWidget } from './TurnstileWidget';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';

/**
 * Requests a password-reset link. The API answers the same 202 whether or not the address belongs
 * to an account, so this form shows that response verbatim and never suggests that the address was
 * recognised — no "check your inbox" only for known addresses, and no "no such account" ever.
 */
export function ForgotPasswordForm({ turnstileSiteKey }: { turnstileSiteKey: string }) {
  const t = useT('auth.forgot');
  const tAll = useT();
  const tLogin = useT('auth.login');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [loading, setLoading] = useState(false);
  const [turnstileToken, setTurnstileToken] = useState('');

  const form = useForm({
    initialValues: { email: '' },
    validate: {
      email: (v) => (!v.trim() ? tLogin('emailRequired') : null),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    setError('');
    if (turnstileSiteKey && !turnstileToken) {
      setError(t('challenge'));
      return;
    }
    setLoading(true);
    try {
      const res = await forgotPassword({
        email: values.email.trim(),
        ...(turnstileToken ? { turnstileToken } : {}),
      });
      setNotice(res?.message || t('uniformFallback'));
    } catch (err: unknown) {
      setError(describeError(tAll, err, t('sendFailed')));
    } finally {
      setLoading(false);
    }
  });

  if (notice) {
    return (
      <Stack gap="sm">
        <Alert icon={<IconCircleCheck size={16} />} color="green" variant="light" role="status">
          {notice}
        </Alert>
        <Text size="sm" c="dimmed">
          {t('linkInfo')}
        </Text>
        <Button component={Link} href="/login" variant="default" fullWidth>
          {t('backToSignIn')}
        </Button>
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
        <TextInput
           
          label={tLogin('email')}
           
          placeholder={tLogin('emailPlaceholder')}
          autoComplete="email"
          {...form.getInputProps('email')}
        />
        <TurnstileWidget siteKey={turnstileSiteKey} onToken={setTurnstileToken} />
        <Button type="submit" loading={loading} fullWidth>
          {t('submit')}
        </Button>
        <Text size="sm" ta="center" c="dimmed">
          {t('remembered')}{' '}
          <Anchor component={Link} href="/login">
            {t('signIn')}
          </Anchor>
        </Text>
      </Stack>
    </form>
  );
}
