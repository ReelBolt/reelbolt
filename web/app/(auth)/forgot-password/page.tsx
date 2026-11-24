import { Card, Stack, Text, Title } from '@mantine/core';
import { ForgotPasswordForm } from '@/components/auth/ForgotPasswordForm';
import { authPageConfig } from '@/lib/auth/config';
import { useT } from '@/i18n';

// The Turnstile site key comes from the environment at request time (lib/auth/config.ts).
export const dynamic = 'force-dynamic';

export default function ForgotPasswordPage() {
  const config = authPageConfig();
  const t = useT('auth.pages.forgot');

  return (
    <Card radius="md" p="xl" withBorder>
      <Stack gap="md" mb="md">
        <Title order={1} size="h2" ta="center">
          {t('title')}
        </Title>
        <Text size="sm" ta="center" c="dimmed">
          {t('subtitle')}
        </Text>
      </Stack>
      <ForgotPasswordForm turnstileSiteKey={config.turnstileSiteKey} />
    </Card>
  );
}
