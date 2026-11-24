'use client';

import { useEffect, useState } from 'react';
import {
  Alert, Badge, Button, Center, Group, Loader, Stack, Text, Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconAlertTriangle, IconCircleCheck, IconClock, IconDeviceDesktop, IconFolder, IconPlayerPause, IconPlayerPlay, IconRefresh, IconTrash,
} from '@tabler/icons-react';
import { Panel, QuietText } from '@/components/dashboard/Panel';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { StorageQuotaBanner, lastErrorLooksLikeStorageFull } from '@/components/storage/StorageQuotaNotice';
import {
  deviceState,
  pauseProjectFolderLink,
  pausedExplanation,
  resumeProjectFolderLink,
  unlinkProjectFolderLink,
} from '@/lib/api/folderLinks';
import { timeAgo } from '@/lib/files/model';
import { useProjectFolderLinks } from '@/lib/hooks/use-storage';
import type { ProjectFolderLink } from '@/lib/types/folderLink';
import { useLocale, useT, useFormat } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { docsPageHref } from '@/lib/onboarding/guide-links';

/** How a computer reads, in one badge. */
function DeviceBadge({ link }: { link: ProjectFolderLink }) {
  const t = useT('integrations.folderSync.device');
  const state = deviceState(link);
  if (state === 'online') {
    return <Badge variant="light" color="green" leftSection={<IconDeviceDesktop size={12} />}>{t('online')}</Badge>;
  }
  if (state === 'disconnected') {
    return <Badge variant="light" color="gray" leftSection={<IconDeviceDesktop size={12} />}>{t('disconnected')}</Badge>;
  }
  return <Badge variant="light" color="gray" leftSection={<IconDeviceDesktop size={12} />}>{t('offline')}</Badge>;
}

/**
 * "Folders on your computers" (docs/design/local-folder-sync-and-storage.md Part 1, "Dashboard API"):
 * the folders the ReelBolt desktop app mirrors into this project. This side only lists and manages —
 * a link is created from the desktop app, because the folder picker is local.
 *
 * The list is re-read on mount, after every action and when the window regains focus; the hook owns
 * that rule. There is deliberately no interval.
 */
export function ProjectFolderLinksTab({ projectId }: { projectId: string }) {
  const t2 = useT('integrations.folderSync');
  const { data: links, error, isLoading, mutate } = useProjectFolderLinks(projectId);

  // The window-focus half of the re-read rule, stated here rather than left to SWR's own focus
  // detection: coming back to the tab is exactly when the desktop app has probably changed something.
  // There is deliberately no interval.
  useEffect(() => {
    const onFocus = () => { void mutate(); };
    window.addEventListener('focus', onFocus);
    return () => window.removeEventListener('focus', onFocus);
  }, [mutate]);

  if (error) {
    return (
      <Alert color="red" icon={<IconAlertTriangle size={16} />} title={t2('loadFailedTitle')} role="alert">
        <Stack gap="xs" align="flex-start">
          <Text size="sm">{t2('loadFailedBody')}</Text>
          <Button size="compact-sm" variant="light" color="red" leftSection={<IconRefresh size={14} />} onClick={() => void mutate()}>
            {t2('tryAgain')}
          </Button>
        </Stack>
      </Alert>
    );
  }

  if (!links && isLoading) {
    return (
      <Center h={160}>
        <Group gap="xs"><Loader size="sm" /><Text size="sm" c="dimmed">{t2('checking')}</Text></Group>
      </Center>
    );
  }

  const list = links ?? [];

  return (
    <Stack gap="md">
      <div>
        <Text fw={650}>{t2('heading')}</Text>
        <QuietText>
          {t2('intro')}
        </QuietText>
      </div>

      {list.length === 0 ? (
        <EmptyState />
      ) : (
        list.map((link) => (
          <FolderLinkCard key={link.id} projectId={projectId} link={link} onChanged={() => mutate()} />
        ))
      )}
    </Stack>
  );
}

function EmptyState() {
  const t2 = useT('integrations.folderSync');
  const locale = useLocale();
  return (
    <Panel id="folder-links-empty" title={<Group gap="xs" component="span"><IconDeviceDesktop size={16} aria-hidden /><span>{t2('empty.title')}</span></Group>}>
      <Stack gap="sm" align="flex-start">
        <Text size="sm">
          {t2.rich('empty.body', { b: (chunks) => <strong>{chunks}</strong> })}
        </Text>
        <Button component="a" href={docsPageHref('projects/local-folder-sync', locale)} variant="light" style={{ minHeight: 44 }}>
          {t2('empty.guide')}
        </Button>
      </Stack>
    </Panel>
  );
}

function Detail({ label, icon, children }: { label: string; icon: React.ReactNode; children: React.ReactNode }) {
  return (
    <Group justify="space-between" wrap="wrap" gap="xs" align="flex-start" style={{ rowGap: 2 }}>
      <Group gap={6} wrap="nowrap">{icon}<Text size="sm" c="dimmed">{label}</Text></Group>
      <div style={{ minWidth: 0, maxWidth: '100%', textAlign: 'right', marginLeft: 'auto' }}>{children}</div>
    </Group>
  );
}

function FolderLinkCard({
  projectId,
  link,
  onChanged,
}: {
  projectId: string;
  link: ProjectFolderLink;
  onChanged: () => Promise<unknown> | void;
}) {
  const tr = useT();
  const t2 = useT('integrations.folderSync');
  const f = useFormat();
  const [busy, setBusy] = useState<'pause' | 'resume' | 'unlink' | null>(null);
  const [confirmUnlink, setConfirmUnlink] = useState(false);

  const run = async (action: 'pause' | 'resume' | 'unlink', work: () => Promise<unknown>, done: string) => {
    setBusy(action);
    try {
      await work();
      notifications.show({ color: 'green', message: done });
    } catch (cause) {
      notifications.show({
        color: 'red',
        message: describeError(tr, cause, t2('actionFailed')),
      });
    } finally {
      setBusy(null);
      setConfirmUnlink(false);
      // Re-read after every action, whatever it did: the link's own state is the server's to state.
      await onChanged();
    }
  };

  const storageFull = lastErrorLooksLikeStorageFull(link.lastError);

  return (
    <div data-testid={`folder-link-${link.id}`}>
      <Panel
        id={`folder-link-${link.id}`}
        title={
          <Group gap="xs" component="span">
            <IconFolder size={16} aria-hidden />
            <span style={{ overflowWrap: 'anywhere' }}>{link.displayPath}</span>
          </Group>
        }
      >
      <Stack gap="md">
        <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
          <Stack gap={4} style={{ minWidth: 0, flex: '1 1 240px' }}>
            <Group gap="xs" wrap="wrap" role="status" aria-live="polite" aria-label={t2('statusAria')}>
              <DeviceBadge link={link} />
              {link.paused && (
                <Badge variant="light" color="yellow" leftSection={<IconPlayerPause size={12} />}>
                  {link.pausedReason === 'guard' ? t2('pausedGuard') : t2('paused')}
                </Badge>
              )}
            </Group>
            <Text size="sm" c="dimmed" style={{ overflowWrap: 'anywhere' }}>
              {link.deviceName || t2('unnamedComputer')}
            </Text>
          </Stack>

          <Group gap="xs" wrap="wrap">
            {link.paused ? (
              <Button
                style={{ minHeight: 44 }} leftSection={<IconPlayerPlay size={16} />} loading={busy === 'resume'}
                disabled={busy !== null && busy !== 'resume'} onClick={() => void run('resume', () => resumeProjectFolderLink(projectId, link.id), t2('resumed'))}
              >
                {t2('resume')}
              </Button>
            ) : (
              <Button
                variant="default" style={{ minHeight: 44 }} leftSection={<IconPlayerPause size={16} />} loading={busy === 'pause'}
                disabled={busy !== null && busy !== 'pause' || link.deviceRevoked}
                onClick={() => void run('pause', () => pauseProjectFolderLink(projectId, link.id), t2('pausedNotice'))}
              >
                {t2('pause')}
              </Button>
            )}
          </Group>
        </Group>

        <Stack gap={10}>
          <Detail label={t2('details.computer')} icon={<IconDeviceDesktop size={14} />}>
            <Text size="sm" style={{ overflowWrap: 'anywhere' }}>{link.deviceName || t2('unnamedComputer')}</Text>
          </Detail>
          <Detail label={t2('details.folderOnComputer')} icon={<IconFolder size={14} />}>
            <Text size="sm" style={{ overflowWrap: 'anywhere' }}>{link.displayPath}</Text>
          </Detail>
          <Detail label={t2('details.inProject')} icon={<IconFolder size={14} />}>
            <Text size="sm" style={{ overflowWrap: 'anywhere' }}>{link.targetFolder}</Text>
          </Detail>
          <Detail label={t2('details.files')} icon={<IconCircleCheck size={14} />}>
            <Text size="sm">{t2('details.fileCount', { count: link.fileCount })}</Text>
          </Detail>
          <Detail label={t2('details.lastChange')} icon={<IconClock size={14} />}>
            {link.lastChangeAt ? (
              <Tooltip label={new Date(link.lastChangeAt).toLocaleString(f.locale)}>
                <Text size="sm">{timeAgo(link.lastChangeAt, undefined, tr, f.locale)}</Text>
              </Tooltip>
            ) : <Text size="sm" c="dimmed">{t2('details.nothingYet')}</Text>}
          </Detail>
        </Stack>

        {link.deviceRevoked && (
          <Alert color="gray" icon={<IconAlertTriangle size={16} />} title={t2('revokedTitle')}>
            <Text size="sm">
              {t2('revokedBody')}
            </Text>
          </Alert>
        )}

        {link.paused && (
          <Alert color="yellow" icon={<IconPlayerPause size={16} />} title={link.pausedReason === 'guard' ? t2('pausedGuardTitle') : t2('pausedTitle')}
            data-testid={`folder-link-paused-${link.id}`}>
            <Text size="sm">{pausedExplanation(link)}</Text>
          </Alert>
        )}

        {link.lastError && !link.paused && (
          storageFull ? (
            <StorageQuotaBanner
              title={t2('quotaTitle')}
              message={link.lastError}
              testId={`folder-link-quota-${link.id}`}
            />
          ) : (
            <Alert color="red" icon={<IconAlertTriangle size={16} />} title={t2('lastSyncFailedTitle')} data-testid={`folder-link-error-${link.id}`}>
              <Stack gap="xs" align="flex-start">
                <Text size="sm">{link.lastError}</Text>
                <Text size="xs" c="dimmed">{t2('nothingDeleted')}</Text>
              </Stack>
            </Alert>
          )
        )}

        <Group justify="flex-end">
          <Button
            variant="subtle" color="red" style={{ minHeight: 44 }} leftSection={<IconTrash size={16} />}
            onClick={() => setConfirmUnlink(true)} disabled={busy !== null}
          >
            {t2('unlink')}
          </Button>
        </Group>
      </Stack>

      <ConfirmModal
        opened={confirmUnlink}
        onClose={() => setConfirmUnlink(false)}
        onConfirm={() => void run('unlink', () => unlinkProjectFolderLink(projectId, link.id), t2('unlinked'))}
        title={t2('unlinkTitle')}
        confirmLabel={t2('unlink')}
        loading={busy === 'unlink'}
        message={t2('unlinkMessage', { path: link.displayPath })}
      />
      </Panel>
    </div>
  );
}
