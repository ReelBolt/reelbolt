'use client';

import { useState } from 'react';
import { Alert, Badge, Button, Group, Loader, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconCloud, IconPlugConnected } from '@tabler/icons-react';
import { SettingsCard } from '@/components/settings/SettingsCard';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { describeCloudError, disconnectCloudConnection, providerName, startCloudConnect } from '@/lib/api/cloudStorage';
import { timeAgo } from '@/lib/files/model';
import type { CloudConnection, CloudProvider } from '@/lib/types/cloudStorage';
import { useCloudConnections, useCloudProviders } from './hooks';
import { useCloudConnectOutcome } from './useCloudConnectOutcome';
import { useT, useFormat } from '@/i18n';

/** Where the browser comes back to after connecting from here. */
const SETTINGS_RETURN_PATH = '/app/settings';

/**
 * "Connected accounts" on the account settings page: the cloud storage accounts the person connected
 * in this workspace, which one needs reconnecting, and Disconnect. Hidden entirely when this server has
 * no cloud storage set up and nothing is connected.
 */
export function CloudAccountsCard() {
  const tr = useT();
  const f = useFormat();
  const t = useT('integrations.cloud.accounts');
  const { data: providers } = useCloudProviders();
  const { data: connections, error, mutate } = useCloudConnections();
  const [busy, setBusy] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<CloudConnection | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);

  useCloudConnectOutcome(providers, () => void mutate());

  const configured = (providers ?? []).filter((p) => p.configured);
  if (!providers || (configured.length === 0 && !(connections?.length))) return null;

  const onConnect = async (provider: CloudProvider) => {
    if (busy) return;
    setBusy(`connect:${provider.provider}`);
    setRefusal(null);
    try {
      window.location.assign(await startCloudConnect(provider.provider, SETTINGS_RETURN_PATH));
    } catch (e) {
      setRefusal(describeCloudError(e, t('unreachable', { provider: provider.displayName })));
      setBusy(null);
    }
  };

  const onDisconnect = async () => {
    if (!confirm) return;
    setBusy(`disconnect:${confirm.id}`);
    try {
      await disconnectCloudConnection(confirm.id);
      notifications.show({ color: 'green', message: t('disconnected', { provider: providerName(confirm.provider, providers) }) });
    } catch (e) {
      notifications.show({ color: 'red', message: describeCloudError(e, t('disconnectFailed')) });
    } finally {
      setBusy(null);
      setConfirm(null);
      await mutate();
    }
  };

  return (
    <SettingsCard
      title={t('title')}
      description={t('description')}
      testId="cloud-accounts-card"
    >
      <Stack gap="sm">
        {error && (
          <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">
            {t('loadFailed')}
          </Alert>
        )}
        {!connections && !error && <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t('loading')}</Text></Group>}
        {connections && connections.length === 0 && <Text size="sm" c="dimmed">{t('none')}</Text>}

        {connections?.map((connection) => {
          const name = providerName(connection.provider, providers);
          const provider = configured.find((p) => p.provider === connection.provider);
          return (
            <Stack key={connection.id} gap={4} data-testid="cloud-connection">
              <Group justify="space-between" wrap="wrap" gap="xs">
                <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
                  <IconCloud size={18} aria-hidden />
                  <div style={{ minWidth: 0 }}>
                    <Text size="sm" fw={600}>{name}</Text>
                    <Text size="xs" c="dimmed" style={{ overflowWrap: 'anywhere' }}>
                      {t('connectionLine', { account: connection.accountEmail ?? connection.accountDisplayName ?? t('connectedAccount'), when: timeAgo(connection.createdAt, undefined, tr, f.locale) })}
                    </Text>
                  </div>
                  {connection.lastError && <Badge color="yellow" variant="light">{t('needsReconnecting')}</Badge>}
                </Group>
                <Group gap="xs">
                  {connection.lastError && provider && (
                    <Button size="compact-sm" style={{ minHeight: 32 }} loading={busy === `connect:${provider.provider}`} onClick={() => void onConnect(provider)}>
                      {t('reconnect')}
                    </Button>
                  )}
                  <Button
                    size="compact-sm" variant="subtle" color="red" style={{ minHeight: 32 }}
                    onClick={() => setConfirm(connection)} disabled={busy !== null}
                  >
                    {t('disconnect')}
                  </Button>
                </Group>
              </Group>
              {connection.lastError && <Text size="xs" c="dimmed">{connection.lastError}</Text>}
            </Stack>
          );
        })}

        {refusal && <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">{refusal}</Alert>}

        {configured.length > 0 && (
          <Group gap="xs" wrap="wrap">
            {configured.map((provider) => (
              <Button
                key={provider.provider}
                variant="default" size="sm" style={{ minHeight: 40 }}
                leftSection={<IconPlugConnected size={14} />}
                loading={busy === `connect:${provider.provider}`}
                onClick={() => void onConnect(provider)}
              >
                {t('connect', { provider: provider.displayName })}
              </Button>
            ))}
          </Group>
        )}
      </Stack>

      <ConfirmModal
        opened={confirm !== null}
        onClose={() => setConfirm(null)}
        onConfirm={() => void onDisconnect()}
        title={t('confirmTitle')}
        confirmLabel={t('disconnect')}
        loading={confirm !== null && busy === `disconnect:${confirm.id}`}
        message={confirm
          ? t('confirmMessage', { account: confirm.accountEmail ?? confirm.accountDisplayName ?? t('thisAccount') })
          : ''}
      />
    </SettingsCard>
  );
}
