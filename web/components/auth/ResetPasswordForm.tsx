'use client';

import { useState } from 'react';
import { Alert, Button, Stack, Text } from '@mantine/core';
import { useForm } from '@mantine/form';
import { PasswordInput } from '@/components/ui/PasswordInput';
import { IconAlertCircle, IconCircleCheck } from '@tabler/icons-react';
import Link from 'next/link';
import { resetPassword } from '@/lib/api/auth';
import { friendlyError } from '@/lib/api/error-messages';
import { validatePassword } from '@/lib/auth/password';
import { useT } from '@/i18n';

/**
 * Whether a token could possibly be one of ours: the API's reset tokens are 64 hex characters (32 random
 * bytes); the check is looser than that (any URL-safe token of plausible length) so it never
 * rejects a real one. Only the SHAPE is checked, so a typo, a truncated link or `?token=bogus` is
 * told up front that the link is no good; a well-formed token is never judged here, because only the
 * server can say whether it is live (no oracle).
 */
export function looksLikeResetToken(token: string): boolean {
  return token.length >= 20 && token.length <= 128 && /^[A-Za-z0-9_-]+$/.test(token);
}

interface ResetPasswordFormProps {
  /** The token from the emailed link (?token=…). */
  token: string;
}

/**
 * Redeems a password-reset link. The API deliberately does NOT sign the visitor in — it sets the
 * new password and kills every outstanding session — so a success sends them to the login page
 * with the new password rather than to the dashboard.
 */
export function ResetPasswordForm({ token }: ResetPasswordFormProps) {
  const t = useT('auth.reset');
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [loading, setLoading] = useState(false);
  const [linkExpired, setLinkExpired] = useState(false);

  const form = useForm({
    initialValues: { newPassword: '', confirmPassword: '' },
    validate: {
      newPassword: (v) => validatePassword(v),
      confirmPassword: (v, values) => (v !== values.newPassword ? t('mismatch') : null),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    setError('');
    setLoading(true);
    try {
      const res = await resetPassword({ token, newPassword: values.newPassword });
      setNotice(res?.message || t('updated'));
    } catch (err: unknown) {
      const friendly = friendlyError(err, t('failed'));
      if (friendly.kind === 'link-expired') setLinkExpired(true);
      else setError(friendly.message);
    } finally {
      setLoading(false);
    }
  });

  if (!looksLikeResetToken(token) || linkExpired) {
    return (
      <Stack gap="sm">
        <Alert icon={<IconAlertCircle size={16} />} color="red" variant="light" role="alert">
          {t('expired')}
        </Alert>
        <Text size="sm" c="dimmed">
          {t('expiredInfo')}
        </Text>
        <Button component={Link} href="/forgot-password" variant="default" fullWidth>
          {t('newLink')}
        </Button>
      </Stack>
    );
  }

  if (notice) {
    return (
      <Stack gap="sm">
        <Alert icon={<IconCircleCheck size={16} />} color="green" variant="light" role="status">
          {notice}
        </Alert>
        <Button component={Link} href="/login" fullWidth>
          {t('signIn')}
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
        <PasswordInput
          label={t('newPassword')}
          placeholder={t('newPlaceholder')}
          autoComplete="new-password"
          {...form.getInputProps('newPassword')}
        />
        <PasswordInput
          label={t('confirm')}
          placeholder={t('confirmPlaceholder')}
          autoComplete="new-password"
          {...form.getInputProps('confirmPassword')}
        />
        <Button type="submit" loading={loading} fullWidth>
          {t('submit')}
        </Button>
      </Stack>
    </form>
  );
}
