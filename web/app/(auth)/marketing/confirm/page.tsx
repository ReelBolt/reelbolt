import { Card, Stack, Text, Title } from '@mantine/core';
import type { Metadata } from 'next';
import { ConfirmSubscriptionAction } from '@/components/marketing/MarketingLinkAction';
import { singleParam } from '@/lib/auth/config';
import { getT } from '@/i18n/server';

export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('auth.pages.confirmSub');
  return {
    title: t('metaTitle'),
    referrer: 'no-referrer',
    robots: { index: false, follow: false },
  };
}

/**
 * The double-opt-in step for an address given on the website. Public (web/proxy.ts). Opening the
 * link confirms nothing, so a mail scanner that follows it cannot subscribe anyone; the button POSTs.
 */
export default async function ConfirmSubscriptionPage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const token = singleParam((await searchParams).token);
  const t = await getT('auth.pages.confirmSub');

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
      <ConfirmSubscriptionAction token={token} />
    </Card>
  );
}
