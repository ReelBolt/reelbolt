'use client';

import { useState } from 'react';
import { Button, Stack, Alert } from '@mantine/core';
import { useForm } from '@mantine/form';
import { PasswordInput } from '@/components/ui/PasswordInput';
import { IconAlertCircle } from '@tabler/icons-react';
import { changePassword } from '@/lib/api/auth';
import { friendlyError } from '@/lib/api/error-messages';
import { MIN_PASSWORD_LENGTH } from '@/lib/auth/password';
import { navigateToNext } from '@/lib/auth/redirect';
import { validatePassword } from '@/lib/auth/password';
import { readUserCookie, writeUserCookie } from '@/lib/auth/user-cookie';
import { notifyAuthChange } from '@/lib/hooks/use-auth';
import { useRouter } from 'next/navigation';
import { useT } from '@/i18n';

// Clears `mustChangePassword` on the client-readable `reelbolt_user` cookie after a successful
// change, so middleware.ts stops redirecting to /change-password without a full reload. Kept as a
// standalone function (not inline in the component) since it mutates `document.cookie`, a value
// outside React's render tree.
function clearMustChangePasswordCookie() {
  const user = readUserCookie();
  if (!user) return;
  writeUserCookie({ ...user, mustChangePassword: false }, 86400);
}

/**
 * @param redirectTo where to go after a successful change. Defaults to the dashboard, which is where
 * the forced first-sign-in flow wants to land; the account settings page passes its own route so a
 * voluntary change does not throw the user out of the page they were reading.
 * @param next the validated domain-root return path the forced gate carries (the OAuth consent URL).
 * When present it wins over redirectTo, so a user who must change their password still reaches the
 * screen they were heading for; it is already validated by the page (safeNextRedirect).
 */
export function ChangePasswordForm({
  redirectTo = '/dashboard',
  next,
}: { redirectTo?: string; next?: string } = {}) {
  const t = useT('auth.change');
  const tr = useT('auth.reset');
  const [error, setError] = useState('');
  const [loading, setLoading] = useState(false);
  const router = useRouter();

  const form = useForm({
    initialValues: { currentPassword: '', newPassword: '', confirmPassword: '' },
    validate: {
      currentPassword: (v) => (!v ? t('currentRequired') : null),
      // Was a bare 8-character check; the API's floor moved to 10 (and 72 bytes) with the
      // account work (C4), so this mirrors api/services/token_service.go instead of guessing.
      newPassword: (v) => validatePassword(v),
      confirmPassword: (v, values) => (v !== values.newPassword ? tr('mismatch') : null),
    },
  });

  const handleSubmit = form.onSubmit(async (values) => {
    setError('');
    setLoading(true);
    try {
      await changePassword({
        currentPassword: values.currentPassword,
        newPassword: values.newPassword,
      });
      clearMustChangePasswordCookie();
      notifyAuthChange();
      // A forced change carries the return path the sign-in (or the API's own redirect) was
      // heading for; it wins over the caller's redirectTo. Both are already validated.
      if (next) navigateToNext(next, router);
      else router.push(redirectTo);
    } catch (err: unknown) {
      const friendly = friendlyError(err, t('failed'));
      if (friendly.kind === 'wrong-current-password') form.setFieldError('currentPassword', friendly.message);
      else setError(friendly.message);
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
        <PasswordInput
          label={t('current')}
          description={t('currentHint')}
          placeholder={t('currentPlaceholder')}
          autoComplete="current-password"
          {...form.getInputProps('currentPassword')}
        />
        <PasswordInput
          label={t('new')}
          description={t('newHint', { min: MIN_PASSWORD_LENGTH })}
          placeholder={t('newPlaceholder', { min: MIN_PASSWORD_LENGTH })}
          autoComplete="new-password"
          {...form.getInputProps('newPassword')}
        />
        <PasswordInput
          label={t('confirm')}
          autoComplete="new-password"
          placeholder={t('confirmPlaceholder')}
          {...form.getInputProps('confirmPassword')}
        />
        <Button type="submit" loading={loading} fullWidth>
          {t('submit')}
        </Button>
      </Stack>
    </form>
  );
}
