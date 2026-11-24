'use client';

import { useEffect, useState } from 'react';
import { Alert, Avatar, Badge, Button, Group, Loader, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconAlertTriangle, IconBrandGithub, IconExternalLink } from '@tabler/icons-react';
import {
  describeGitHubAccountError,
  describeGitHubConnectOutcome,
  disconnectGitHubAccount,
  startGitHubConnect,
} from '@/lib/api/github';
import { useGitHubAccount } from '@/lib/hooks/use-github-account';
import { SettingsCard } from './SettingsCard';
import { useT } from '@/i18n';

/**
 * Connected accounts -> GitHub.
 *
 * app/(app)/settings/page.tsx renders this with no props; keep the export name and signature. It uses
 * the one GitHub client, web/lib/api/github.ts, and its describeGitHub*Error helpers. Starting the
 * flow with no project id makes the server's OAuth callback return here as /app/settings?github=<code>
 * (GitHubOAuthController), which is read once on mount.
 *
 * The prose follows docs/user-guide/github-repository.md: read-only access, and the connection can
 * be removed from either side at any time. No setup vocabulary ("app", "installation", "webhook")
 * reaches the screen — that belongs to the administrator's docs.
 */
export function GitHubAccountCard() {
  const t = useT('integrations.github.account');
  const { data: account, error, mutate } = useGitHubAccount();
  const [busy, setBusy] = useState<'connect' | 'disconnect' | null>(null);
  const [refusal, setRefusal] = useState<string | null>(null);

  // Read the callback's outcome once, then drop it from the URL so a reload does not repeat it.
  useEffect(() => {
    const url = new URL(window.location.href);
    const outcome = url.searchParams.get('github');
    const message = describeGitHubConnectOutcome(outcome);
    if (!message) return;
    notifications.show({ color: outcome === 'connected' ? 'green' : 'yellow', message });
    url.searchParams.delete('github');
    window.history.replaceState(null, '', url.toString());
    void mutate();
  }, [mutate]);

  const onConnect = async () => {
    if (busy) return;
    setBusy('connect');
    setRefusal(null);
    try {
      window.location.assign(await startGitHubConnect(null));
    } catch (e) {
      setRefusal(describeGitHubAccountError(e));
      setBusy(null);
    }
  };

  const onDisconnect = async () => {
    if (busy) return;
    setBusy('disconnect');
    setRefusal(null);
    try {
      await disconnectGitHubAccount();
      await mutate();
      notifications.show({ color: 'green', message: t('disconnected') });
    } catch (e) {
      setRefusal(describeGitHubAccountError(e));
    } finally {
      setBusy(null);
    }
  };

  const connected = account?.connected ?? false;

  // A card whose only message is "ask your administrator" is a dead end for a hosted user (A20), so
  // when this server cannot connect GitHub and nothing is connected there is nothing to show.
  if (account && !connected && !account.canConnect && !account.revokedReason) return null;

  return (
    <SettingsCard
      title={t('title')}
      description={t('description')}
      testId="github-account-card"
    >
      <Stack gap="xs">
        <Group gap="xs">
          {connected && account?.avatarUrl ? (
            <Avatar src={account.avatarUrl} size={20} radius="xl" alt="" />
          ) : (
            <IconBrandGithub size={18} aria-hidden="true" />
          )}
          <Text fw={500}>{t('accountLabel')}</Text>
          {account === undefined && !error && <Loader size="xs" />}
          {account !== undefined && (
            <Badge variant="light" color={connected ? 'green' : 'gray'}>
              {connected ? t('connectedAs', { login: account.login }) : t('notConnected')}
            </Badge>
          )}
        </Group>

        <Text size="sm" c="dimmed">
          {t('intro')}
        </Text>

        {account && !connected && account.revokedReason && (
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />} role="status">
            {t('revoked', { reason: account.revokedReason })}
          </Alert>
        )}

        {(refusal || error) && (
          <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">
            {refusal ?? describeGitHubAccountError(error)}
          </Alert>
        )}

        {account && !connected && account.canConnect && (
          <Group>
            <Button
              style={{ minHeight: 44 }}
              leftSection={<IconBrandGithub size={16} />}
              loading={busy === 'connect'}
              onClick={() => void onConnect()}
            >
              {t('connect')}
            </Button>
          </Group>
        )}

        {connected && (
          <Group gap="xs" wrap="wrap">
            {account?.installUrl && (
              <Button
                component="a"
                href={account.installUrl}
                target="_blank"
                rel="noreferrer noopener"
                variant="light"
                style={{ minHeight: 44 }}
                leftSection={<IconExternalLink size={16} />}
              >
                {t('moreRepos')}
              </Button>
            )}
            <Button
              variant="default"
              style={{ minHeight: 44 }}
              loading={busy === 'disconnect'}
              onClick={() => void onDisconnect()}
            >
              {t('disconnect')}
            </Button>
          </Group>
        )}
      </Stack>
    </SettingsCard>
  );
}
