'use client';

import { useCallback, useState } from 'react';
import { Alert, Group, List, Loader, Stack, Switch, Text } from '@mantine/core';
import { IconAlertCircle, IconCircleCheck } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { SettingsCard } from '@/components/settings/SettingsCard';
import { updateNotificationPreferences } from '@/lib/api/notifications';
import type { NotificationPreferences, NotificationPreferencesPatch } from '@/lib/api/notifications';
import { useNotificationPreferences, useRecentNotifications } from '@/lib/hooks/use-notifications';
import { useT, useFormat } from '@/i18n';

// The switches, in display order, with the plain-language label and the one-line
// explanation of what turning the switch on means for the person reading it.
export default function NotificationsPage() {
  const t = useT('settings.notifications');
  const f = useFormat();
  const SWITCHES = [
    { key: 'jobSucceeded', label: t('switches.jobSucceeded.label'), description: t('switches.jobSucceeded.description') },
    { key: 'jobFailed', label: t('switches.jobFailed.label'), description: t('switches.jobFailed.description') },
    { key: 'fileCreated', label: t('switches.fileCreated.label'), description: t('switches.fileCreated.description') },
    { key: 'graphicsEdited', label: t('switches.graphicsEdited.label'), description: t('switches.graphicsEdited.description') },
    { key: 'reviewActivity', label: t('switches.reviewActivity.label'), description: t('switches.reviewActivity.description') },
    { key: 'avatarRetention', label: t('switches.avatarRetention.label'), description: t('switches.avatarRetention.description') },
    { key: 'socialPublishing', label: t('switches.socialPublishing.label'), description: t('switches.socialPublishing.description') },
  ] as const satisfies readonly { key: keyof NotificationPreferences; label: string; description: string }[];
  const { data: prefs, isLoading, error, mutate } = useNotificationPreferences();
  const [saveError, setSaveError] = useState<string | null>(null);
  const { data: recent, isLoading: recentLoading } = useRecentNotifications();

  const setSwitch = useCallback(
    async (key: keyof NotificationPreferences, value: boolean) => {
      if (!prefs) return;
      // Flip the switch in the UI immediately so the toggle feels instant; the server answer
      // (or an error) reconciles it.
      setSaveError(null);
      const optimistic: NotificationPreferences = { ...prefs, [key]: value };
      await mutate(optimistic, { revalidate: false });
      try {
        const saved = await updateNotificationPreferences({ [key]: value } satisfies NotificationPreferencesPatch);
        await mutate(saved, { revalidate: false });
      } catch {
        setSaveError(t('saveFailed'));
        // The write failed; roll back to the last known good value and revalidate from the server.
        await mutate(prefs, { revalidate: true });
      }
    },
    [prefs, mutate, t],
  );

  const header = (
    <PageHeader
      title={t('title')}
      breadcrumbs={[{ label: t('breadcrumbSettings'), href: '/settings' }, { label: t('title') }]}
    />
  );

  return (
    <Stack gap="lg" w="100%">
      {header}

      {isLoading ? (
        <Group justify="center" py="xl">
          <Loader />
        </Group>
      ) : error ? (
        <Alert color="red">{t('loadFailed')}</Alert>
      ) : prefs ? (
        <Stack gap="md">
          {saveError ? <Alert color="red" data-testid="save-error">{saveError}</Alert> : null}
          {SWITCHES.map(({ key, label, description }) => (
            <SettingsCard key={key} title={label} description={description} testId={`switch-${key}`}>
              <Switch
                label={t('emailMe')}
                aria-label={t('emailMeAria', { label })}
                checked={prefs[key] ?? true}
                onChange={(e) => {
                  void setSwitch(key, e.currentTarget.checked);
                }}
              />
            </SettingsCard>
          ))}
        </Stack>
      ) : null}

      <SettingsCard
        title={t('recent.title')}
        description={t('recent.description')}
        testId="recent-activity"
      >
        {recentLoading ? (
          <Group justify="center" py="md">
            <Loader size="sm" />
          </Group>
        ) : recent && recent.length > 0 ? (
          <List spacing="sm">
            {recent.map((record) => (
              <List.Item key={record.id} fw={record.delivered ? 400 : 600}>
                <Group gap="sm" wrap="nowrap">
                  {record.delivered ? (
                    <IconCircleCheck size={16} color="var(--mantine-color-green-filled)" />
                  ) : (
                    <IconAlertCircle size={16} color="var(--mantine-color-red-filled)" />
                  )}
                  <Text size="sm" c={record.delivered ? undefined : 'red'}>
                    {record.title}
                  </Text>
                  <Text size="xs" c="dimmed" style={{ marginLeft: 'auto' }}>
                    {f.date(record.createdAt, 'dateTime')}
                  </Text>
                </Group>
              </List.Item>
            ))}
          </List>
        ) : (
          <Text size="sm" c="dimmed">
            {t('recent.empty')}
          </Text>
        )}
      </SettingsCard>
    </Stack>
  );
}
