'use client';

import { useState } from 'react';
import { Alert, Button, Group, Stack, Text } from '@mantine/core';
import { useAuthSettings } from '@/lib/hooks/use-auth-settings';
import { cancelOwnEmailChange } from '@/lib/api/auth-settings';
import { useFormat, useT } from '@/i18n';

/**
 * Shown in the profile card while an administrator-requested address change waits for confirmation
 * from the current mailbox. Cancel withdraws it (DELETE); the server stays the authority.
 */
export function PendingEmailChangeNotice() {
  const { data, mutate } = useAuthSettings();
  const t = useT('settings.emailChange');
  const format = useFormat();
  const [busy, setBusy] = useState(false);
  const [failed, setFailed] = useState(false);

  const pending = data?.pendingEmailChange;
  if (!pending) return null;

  const cancel = async () => {
    setBusy(true);
    setFailed(false);
    try {
      await cancelOwnEmailChange();
      await mutate(data ? { ...data, pendingEmailChange: null } : data);
    } catch {
      setFailed(true);
    } finally {
      setBusy(false);
    }
  };

  return (
    <Alert color="yellow" variant="light" title={t('title')} data-testid="pending-email-change">
      <Stack gap="xs">
        <Text size="sm" style={{ wordBreak: 'break-word' }}>
          {t('body', {
            email: pending.newEmail,
            date: format.date(pending.expiresAt, 'dateTime'),
          })}
        </Text>
        {failed && (
          <Text size="sm" c="red">
            {t('cancelFailed')}
          </Text>
        )}
        <Group>
          <Button
            size="xs"
            variant="default"
            loading={busy}
            onClick={() => void cancel()}
            data-testid="pending-email-change-cancel"
          >
            {t('cancel')}
          </Button>
        </Group>
      </Stack>
    </Alert>
  );
}
