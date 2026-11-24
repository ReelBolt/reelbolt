import { SimpleGrid, Stack, Text } from '@mantine/core';
import { PageHeader } from '@/components/shared/PageHeader';
import { DataExportCard } from '@/components/settings/DataExportCard';
import { DeleteAccountCard } from '@/components/settings/DeleteAccountCard';
import { useT } from '@/i18n';

/**
 * Your data: download a copy of everything ReelBolt holds about you, or close your account.
 *
 * This page exists so the legal re-acceptance gate can exempt exactly these two rights and nothing
 * else (web/proxy.ts, S-22). Neither may be made conditional on accepting a new Terms or Privacy
 * version, but the rest of /settings (password, integrations, billing) stays behind the gate. So
 * keep this page to these two cards: anything added here is reachable without re-accepting.
 *
 * The same cards also stay on /settings for a session that is not behind on anything.
 */
export default function DataSettingsPage() {
  const t = useT('settings.data');
  return (
    <>
      <PageHeader
        title={t('title')}
        breadcrumbs={[{ label: t('breadcrumbAccount'), href: '/settings' }, { label: t('title') }]}
      />
      <Stack gap="lg" maw={860}>
        <Text size="sm" c="dimmed">
          {t('intro')}
        </Text>
        <SimpleGrid cols={{ base: 1, lg: 2 }} spacing="lg">
          <DataExportCard />
          <DeleteAccountCard />
        </SimpleGrid>
      </Stack>
    </>
  );
}
