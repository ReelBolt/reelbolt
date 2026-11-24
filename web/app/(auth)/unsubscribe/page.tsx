import { Card, Stack, Text, Title } from '@mantine/core';
import type { Metadata } from 'next';
import { UnsubscribeAction } from '@/components/marketing/MarketingLinkAction';
import { singleParam } from '@/lib/auth/config';
import { getT } from '@/i18n/server';

// The token travels in the query string, so it must never ride along in a Referer header, and a
// link-preview bot has nothing to index here.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('auth.pages.unsubscribe');
  return {
    title: t('metaTitle'),
    referrer: 'no-referrer',
    robots: { index: false, follow: false },
  };
}

/**
 * Opened from the unsubscribe link in a marketing email. Public (web/proxy.ts) and needs no sign-in:
 * withdrawing must be as easy as agreeing. Loading the page changes nothing; the button POSTs.
 */
export default async function UnsubscribePage({
  searchParams,
}: {
  searchParams: Promise<Record<string, string | string[] | undefined>>;
}) {
  const token = singleParam((await searchParams).token);
  const t = await getT('auth.pages.unsubscribe');

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
      <UnsubscribeAction token={token} />
    </Card>
  );
}
