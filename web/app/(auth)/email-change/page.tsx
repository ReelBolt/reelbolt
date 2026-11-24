import { Card, Stack, Text, Title } from '@mantine/core';
import type { Metadata } from 'next';
import { EmailChangeAction } from './EmailChangeAction';
import { getT } from '@/i18n/server';

// The token travels in the URL fragment (never sent to a server); no-referrer is defence in depth,
// and a link-preview bot has nothing to index here.
export async function generateMetadata(): Promise<Metadata> {
  const t = await getT('auth.pages.emailChange');
  return {
    title: t('metaTitle'),
    referrer: 'no-referrer',
    robots: { index: false, follow: false },
  };
}

/**
 * Opened from the link mailed to the CURRENT address when an administrator requests an address
 * change. Public (web/proxy.ts): loading the page changes nothing; Confirm and Cancel each POST.
 */
export default async function EmailChangePage() {
  const t = await getT('auth.pages.emailChange');

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
      <EmailChangeAction />
    </Card>
  );
}
