'use client';

import { Anchor, Button, Group, Loader, Stack, Text } from '@mantine/core';
import Link from 'next/link';
import { SettingsCard } from './SettingsCard';
import { StorageUsageBar } from '@/components/storage/StorageUsageBar';
import { useStorageUsage } from '@/lib/hooks/use-storage';
import { useT } from '@/i18n';

/**
 * The workspace's storage, on the Account page (docs/design/local-folder-sync-and-storage.md Part 2,
 * "Shown to people"): the same figures the Billing page shows, because the person who hit the quota
 * usually arrives from a failed upload rather than from the billing page.
 *
 * Renders nothing at all when the deployment cannot answer — the storage endpoint is not a billing
 * document and works with billing off, but an unreachable API should not add a broken card to a page
 * that is otherwise about the account.
 */
export function StorageSettingsCard() {
  const { data, error, isLoading, mutate } = useStorageUsage();
  const t = useT('settings.storage');

  if (error) return null;

  return (
    <SettingsCard
      title={t('title')}
      description={t('description')}
      testId="storage-card"
    >
      {data ? (
        <Stack gap="sm">
          <StorageUsageBar usage={data} withProjects />
          <Group gap="xs">
            <Button component={Link} href="/settings/billing" size="compact-sm" variant="light">
              {t('seePlans')}
            </Button>
            <Text size="xs" c="dimmed">
              {t('hint')}
            </Text>
          </Group>
        </Stack>
      ) : isLoading ? (
        <Group gap="xs"><Loader size="sm" /><Text size="sm" c="dimmed">{t('loading')}</Text></Group>
      ) : (
        <Anchor component="button" type="button" size="sm" onClick={() => void mutate()}>
          {t('failed')}
        </Anchor>
      )}
    </SettingsCard>
  );
}
