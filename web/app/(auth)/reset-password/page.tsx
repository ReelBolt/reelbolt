import { Card, Stack, Text, Title } from '@mantine/core';
import type { Metadata } from 'next';
import { ResetPasswordForm } from '@/components/auth/ResetPasswordForm';
import { singleParam } from '@/lib/auth/config';
import { getT } from '@/i18n/server';

// The reset token travels in the query string, so it must never ride along in a Referer header
// sent to any other origin (docs/design/saas-accounts.md, "Token rules").
export const metadata: Metadata = { referrer: 'no-referrer' };

export default async function ResetPasswordPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const token = singleParam((await searchParams).token);
  const t = await getT('auth.pages.reset');

  return (
    <Card radius="md" p="xl" withBorder>
      <Stack gap="md" mb="md">
        <Title order={2} ta="center">
          {t('title')}
        </Title>
        <Text size="sm" ta="center" c="dimmed">
          {t('subtitle')}
        </Text>
      </Stack>
      <ResetPasswordForm token={token} />
    </Card>
  );
}
