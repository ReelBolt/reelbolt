'use client';

import { useState } from 'react';
import { useSearchParams } from 'next/navigation';
import { Alert, Avatar, Badge, Button, Card, Group, Loader, SimpleGrid, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconPlugConnected } from '@tabler/icons-react';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { EmptyState } from '@/components/shared/EmptyState';
import { useT } from '@/i18n';
import { useAuth } from '@/lib/hooks/use-auth';
import { useSocialAccounts, useSocialProviders } from '@/lib/hooks/use-social';
import { disconnectSocialAccount, socialErrorMessage, startSocialConnect } from '@/lib/api/social';
import type { SocialAccount, SocialProvider } from '@/lib/types/social';

const RETURN_PATH = '/app/publishing';

/** Connect cards per configured platform, the connected accounts, reconnect and disconnect. */
export function AccountsTab() {
  const t = useT('publishing.accounts');
  const params = useSearchParams();
  const { isOrgAdmin } = useAuth();
  const providers = useSocialProviders();
  const accounts = useSocialAccounts();
  const [busy, setBusy] = useState<string | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);
  const [confirm, setConfirm] = useState<SocialAccount | null>(null);

  const outcome = params.get('social');
  const list = providers.data ?? [];
  const nameOf = (id: string) => list.find((p) => p.provider === id)?.displayName ?? id;

  const connect = async (provider: SocialProvider) => {
    if (busy) return;
    setBusy(`connect:${provider.provider}`);
    setRefusal(null);
    try {
      window.location.assign(await startSocialConnect(provider.provider, RETURN_PATH));
    } catch (e) {
      setRefusal(socialErrorMessage(e) ?? t('connectFailed', { provider: provider.displayName }));
      setBusy(null);
    }
  };

  const disconnect = async () => {
    if (!confirm) return;
    setBusy(`disconnect:${confirm.id}`);
    try {
      await disconnectSocialAccount(confirm.id);
      notifications.show({ color: 'green', message: t('disconnected', { name: confirm.displayName ?? nameOf(confirm.provider) }) });
    } catch (e) {
      notifications.show({ color: 'red', message: socialErrorMessage(e) ?? t('disconnectFailed') });
    } finally {
      setBusy(null);
      setConfirm(null);
      await accounts.mutate();
    }
  };

  if (providers.isLoading || accounts.isLoading) return <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t('loading')}</Text></Group>;
  if (providers.error || accounts.error) {
    return <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">{t('loadFailed')}</Alert>;
  }
  if (list.length === 0 && (accounts.data ?? []).length === 0) {
    return <EmptyState title={t('notAvailableTitle')} description={t('notAvailable')} />;
  }

  return (
    <Stack gap="md" data-testid="publishing-accounts">
      {outcome === 'connected' && <Alert color="green" role="status">{t('connectedOk', { provider: nameOf(params.get('provider') ?? '') })}</Alert>}
      {outcome === 'error' && <Alert color="red" role="alert">{t('connectError', { provider: nameOf(params.get('provider') ?? '') })}</Alert>}
      {refusal && <Alert color="red" role="alert">{refusal}</Alert>}
      {!isOrgAdmin && <Text size="sm" c="dimmed">{t('adminOnly')}</Text>}

      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        {list.map((p) => (
          <Card key={p.provider} withBorder padding="md" data-testid={`connect-card-${p.provider}`}>
            <Stack gap="xs">
              <Group justify="space-between" wrap="nowrap">
                <Text fw={600}>{p.displayName}</Text>
                <Button
                  size="sm" style={{ minHeight: 44 }} leftSection={<IconPlugConnected size={16} />}
                  disabled={!isOrgAdmin || busy !== null} loading={busy === `connect:${p.provider}`}
                  onClick={() => void connect(p)}
                >
                  {t('connect')}
                </Button>
              </Group>
              {p.provider === 'youtube' && <Text size="xs" c="dimmed" data-testid="youtube-private-note">{t('youtubePrivate')}</Text>}
            </Stack>
          </Card>
        ))}
      </SimpleGrid>

      {(accounts.data ?? []).length === 0
        ? <Text size="sm" c="dimmed">{t('none')}</Text>
        : (accounts.data ?? []).map((a) => (
          <Card key={a.id} withBorder padding="sm" data-testid={`account-${a.id}`}>
            <Group justify="space-between" wrap="wrap">
              <Group gap="sm" wrap="nowrap">
                <Avatar src={a.avatarUrl} radius="xl" size="md" alt="" />
                <div>
                  <Text fw={500}>{a.displayName ?? nameOf(a.provider)}</Text>
                  <Text size="xs" c="dimmed">{nameOf(a.provider)}</Text>
                </div>
                {a.status === 'active'
                  ? <Badge color="green" variant="light">{t('statusActive')}</Badge>
                  : <Badge color="orange" variant="light">{t('statusReconnect')}</Badge>}
              </Group>
              <Group gap="xs">
                {a.status !== 'active' && list.some((p) => p.provider === a.provider) && (
                  <Button size="sm" style={{ minHeight: 44 }} disabled={!isOrgAdmin || busy !== null} onClick={() => void connect({ provider: a.provider, displayName: nameOf(a.provider) })}>
                    {t('reconnect')}
                  </Button>
                )}
                <Button size="sm" variant="default" color="red" style={{ minHeight: 44 }} disabled={!isOrgAdmin || busy !== null} onClick={() => setConfirm(a)}>
                  {t('disconnect')}
                </Button>
              </Group>
            </Group>
            {a.status !== 'active' && <Text size="xs" c="dimmed" mt="xs">{t('reconnectHint')}</Text>}
          </Card>
        ))}

      <ConfirmModal
        opened={confirm !== null} onClose={() => setConfirm(null)} onConfirm={() => void disconnect()}
        title={t('disconnectTitle')} message={t('disconnectMessage')} confirmLabel={t('disconnect')} loading={busy !== null && confirm !== null}
      />
    </Stack>
  );
}
