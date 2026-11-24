'use client';

import { Anchor, Group, Text, UnstyledButton } from '@mantine/core';
import { useAuth } from '@/lib/hooks/use-auth';
import { useT } from '@/i18n';

/**
 * The ways out of /accept-terms that never require accepting anything: signing out, and
 * /settings/data, where a person downloads their data or deletes their account. The proxy gate
 * exempts exactly that page (web/proxy.ts, S-22) -- not the rest of /settings -- and this page sits
 * outside the app shell, so without these links the only way to reach them would be typing a URL.
 */
export function LegalGateExits() {
  const { logout } = useAuth();
  const t = useT('auth.exits');
  return (
    <Group justify="center" gap="xs" mt="md">
      <Text size="xs" c="dimmed" component="span">
        <Anchor href="/app/settings/data" size="xs" c="dimmed" underline="always">
          {t('dataOrDelete')}
        </Anchor>
      </Text>
      <Text size="xs" c="dimmed" component="span">
        ·
      </Text>
      <UnstyledButton onClick={() => void logout()} fz="xs" c="dimmed" td="underline">
        {t('signOut')}
      </UnstyledButton>
    </Group>
  );
}
