'use client';

import { useEffect, useMemo, useState } from 'react';
import {
  Alert, Badge, Button, Center, Group, Loader, Select, Stack, Text, Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconAlertTriangle, IconCheck, IconCloud, IconFiles, IconFolder, IconLink, IconPlugConnected, IconRefresh, IconTrash, IconUser,
} from '@tabler/icons-react';
import { Panel, QuietText } from '@/components/dashboard/Panel';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import {
  cloudErrorCode,
  createProjectCloudLink,
  deleteProjectCloudLink,
  describeCloudError,
  describeDriveKind,
  needsReconnect,
  projectCloudReturnPath,
  providerName,
  startCloudConnect,
  syncProjectCloudLink,
  updateProjectCloudLink,
} from '@/lib/api/cloudStorage';
import { timeAgo } from '@/lib/files/model';
import type { CloudConnection, CloudFolder, CloudProvider, ProjectCloudLink } from '@/lib/types/cloudStorage';
import { CloudFileSelector, initialChoice, type CloudFileChoice } from './CloudFileSelector';
import { CloudFolderBrowser } from './CloudFolderBrowser';
import { useCloudConnections, useCloudDrives, useCloudProviders, useProjectCloudLinks } from './hooks';
import { describeSelection } from './selection';
import { useCloudConnectOutcome } from './useCloudConnectOutcome';
import { useT, useFormat } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';
import { JobStatusBadge } from '@/components/activity';

/** How often the links are re-read while any of them is syncing. */
export const CLOUD_SYNC_POLL_MS = 3000;

/** The notification for a link whose background sync just ended. */
export function cloudSyncFinishedNotice(link: ProjectCloudLink, name: string): { color: string; title: Msg; message: Msg | string } {
  if (link.lastError) return { color: 'red', title: msg('integrations.cloud.tab.syncFailedTitle', { name }), message: link.lastError };
  return {
    color: 'green',
    title: msg('integrations.cloud.tab.syncFinishedTitle', { name }),
    message: msg('integrations.cloud.tab.syncFinishedMessage', { count: link.lastSyncedFileCount, path: link.folderDisplayPath }),
  };
}

/** True when the link's last failure is fixed by connecting the account again. */
function linkNeedsReconnect(link: ProjectCloudLink, connection: CloudConnection | undefined): boolean {
  if (!link.connectionId || !connection) return true;
  if (connection.lastError) return true;
  return Boolean(link.lastError && /reconnect|no longer accepts|access was removed|sign in again/i.test(link.lastError));
}

/** True when the link's last failure is fixed by choosing fewer files. */
function isSelectionProblem(lastError: string | null): boolean {
  return Boolean(lastError && /too many files|too large|choose fewer/i.test(lastError));
}

/** Sends the browser to the provider's sign-in; comes back to this tab. */
async function connect(provider: string, projectId: string): Promise<void> {
  window.location.assign(await startCloudConnect(provider, projectCloudReturnPath(projectId)));
}

/**
 * The project's "Cloud storage" tab (docs/design/cloud-drive-sync.md "Dashboard"): one card per
 * provider this server has set up — connect an account, choose a folder (and optionally which files in
 * it), then see and manage the linked folder. Syncs run in the background; while any link is syncing
 * the links are re-read every few seconds, and each finished sync is announced once.
 */
export function ProjectCloudStorageTab({ projectId }: { projectId: string }) {
  const t = useT('integrations.cloud.tab');
  const { data: providers, error: providersError } = useCloudProviders();
  const { data: connections, mutate: mutateConnections } = useCloudConnections();
  const { data: links, error: linksError, isLoading: linksLoading, mutate: mutateLinks } = useProjectCloudLinks(projectId);
  const [requested, setRequested] = useState<string[]>([]);

  useCloudConnectOutcome(providers, () => void mutateConnections());

  const configured = useMemo(() => (providers ?? []).filter((p) => p.configured), [providers]);

  // Links this page asked to sync, plus any the server says are syncing (another tab, a teammate).
  const followKey = useMemo(() => {
    const ids = new Set(requested);
    for (const link of links ?? []) if (link.syncing) ids.add(link.id);
    return [...ids].sort().join(',');
  }, [requested, links]);

  useEffect(() => {
    if (!followKey) return undefined;
    const watching = followKey.split(',');
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      let fresh: ProjectCloudLink[] | undefined;
      try {
        fresh = await mutateLinks();
      } catch {
        fresh = undefined;
      }
      if (cancelled) return;
      const done = fresh ? watching.filter((id) => !fresh.find((l) => l.id === id)?.syncing) : [];
      if (done.length === 0) {
        timer = setTimeout(() => void tick(), CLOUD_SYNC_POLL_MS);
        return;
      }
      for (const id of done) {
        const link = fresh?.find((l) => l.id === id);
        if (link) {
          const notice = cloudSyncFinishedNotice(link, providerName(link.provider, providers));
          notifications.show({
            ...notice,
            title: t.msg(notice.title),
            message: typeof notice.message === 'string' ? notice.message : t.msg(notice.message),
            autoClose: link.lastError ? 10000 : 6000,
          });
        }
      }
      // Re-renders with the fresh list; whatever is still syncing is followed from there.
      setRequested((current) => current.filter((id) => !done.includes(id)));
    };
    timer = setTimeout(() => void tick(), CLOUD_SYNC_POLL_MS);
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [followKey, mutateLinks, providers, t]);

  const follow = (linkId: string) => setRequested((current) => (current.includes(linkId) ? current : [...current, linkId]));

  if (providersError || linksError) {
    return (
      <Alert color="red" icon={<IconAlertTriangle size={16} />} title={t('loadFailedTitle')} role="alert">
        <Text size="sm">{t('loadFailedBody')}</Text>
      </Alert>
    );
  }

  if (!providers || linksLoading) {
    return (
      <Center h={160}>
        <Group gap="xs"><Loader size="sm" /><Text size="sm" c="dimmed">{t('checkingLinks')}</Text></Group>
      </Center>
    );
  }

  if (configured.length === 0) {
    return <QuietText>{t('notSetUp')}</QuietText>;
  }

  return (
    <Stack gap="md">
      <div>
        <Text fw={650}>{t('heading')}</Text>
        <QuietText>
          {t('intro')}
        </QuietText>
      </div>

      {configured.map((provider) => {
        const link = links?.find((l) => l.provider === provider.provider);
        const own = (connections ?? []).filter((c) => c.provider === provider.provider);
        return link ? (
          <LinkedFolderCard
            key={provider.provider}
            projectId={projectId}
            provider={provider}
            link={link}
            connection={own.find((c) => c.id === link.connectionId)}
            syncing={link.syncing || requested.includes(link.id)}
            onFollow={follow}
            onChanged={() => mutateLinks()}
          />
        ) : (
          <LinkFolderCard
            key={provider.provider}
            projectId={projectId}
            provider={provider}
            connections={own}
            connectionsLoaded={connections !== undefined}
            onLinked={(created) => {
              void mutateLinks((current) => [...(current ?? []).filter((l) => l.id !== created.id), created], { revalidate: false });
              follow(created.id);
            }}
          />
        );
      })}
    </Stack>
  );
}

// ------------------------------------------------------------------ not linked

function ConnectButton({ projectId, provider, label }: { projectId: string; provider: CloudProvider; label?: string }) {
  const t = useT('integrations.cloud.tab');
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);
  const onClick = async () => {
    if (busy) return;
    setBusy(true);
    setRefusal(null);
    try {
      await connect(provider.provider, projectId);
    } catch (e) {
      setRefusal(describeCloudError(e, t('unreachable', { provider: provider.displayName })));
      setBusy(false);
    }
  };
  return (
    <Stack gap="xs" align="flex-start">
      <Button style={{ minHeight: 44 }} leftSection={<IconPlugConnected size={16} />} loading={busy} onClick={() => void onClick()}>
        {label ?? t('connect', { provider: provider.displayName })}
      </Button>
      {refusal && <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">{refusal}</Alert>}
    </Stack>
  );
}

function accountLabel(connection: CloudConnection, fallback: string): string {
  return connection.accountEmail ?? connection.accountDisplayName ?? fallback;
}

function LinkFolderCard({
  projectId,
  provider,
  connections,
  connectionsLoaded,
  onLinked,
}: {
  projectId: string;
  provider: CloudProvider;
  connections: CloudConnection[];
  connectionsLoaded: boolean;
  onLinked: (link: ProjectCloudLink) => void;
}) {
  const t = useT('integrations.cloud.tab');
  const [connectionId, setConnectionId] = useState<string | null>(null);
  const [driveKey, setDriveKey] = useState<string | null>(null);
  const [folder, setFolder] = useState<CloudFolder | null>(null);
  const [choice, setChoice] = useState<CloudFileChoice>(initialChoice(null));
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<{ message: string; reconnect: boolean } | null>(null);

  const connection = connections.find((c) => c.id === connectionId) ?? connections[0];
  const { data: drives, error: drivesError } = useCloudDrives(connection && !connection.lastError ? connection.id : null);
  const drive = drives?.find((d) => (d.driveId ?? '') === driveKey) ?? drives?.[0];

  const reset = () => {
    setFolder(null);
    setChoice(initialChoice(null));
    setRefusal(null);
  };

  const onLink = async () => {
    if (busy || !connection || !folder || choice.includePaths === null) return;
    setBusy(true);
    setRefusal(null);
    try {
      const created = await createProjectCloudLink(projectId, {
        connectionId: connection.id,
        driveId: drive?.driveId ?? null,
        folderId: folder.folderId,
        includePaths: choice.includePaths,
      });
      notifications.show({
        color: 'blue',
        title: t('linkedTitle', { folder: folder.displayPath || folder.name }),
        message: t('linkedMessage'),
      });
      onLinked(created);
    } catch (e) {
      setRefusal({ message: describeCloudError(e, t('linkFailed')), reconnect: needsReconnect(e) });
    } finally {
      setBusy(false);
    }
  };

  return (
    <Panel id={`cloud-${provider.provider}`} title={<Group gap="xs" component="span"><IconCloud size={16} aria-hidden /><span>{provider.displayName}</span></Group>}>
      <Stack gap="md">
        {!connectionsLoaded ? (
          <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t('checkingAccounts')}</Text></Group>
        ) : !connection ? (
          <Stack gap="xs">
            <QuietText>
              {t('connectIntro', { provider: provider.displayName })}
            </QuietText>
            <ConnectButton projectId={projectId} provider={provider} />
          </Stack>
        ) : (
          <>
            <Group gap="sm" align="flex-end" wrap="wrap">
              {connections.length > 1 ? (
                <Select
                  label={t('account')}
                  data={connections.map((c) => ({ value: c.id, label: accountLabel(c, t('connectedAccount')) }))}
                  value={connection.id}
                  onChange={(value) => { setConnectionId(value); setDriveKey(null); reset(); }}
                  allowDeselect={false}
                  style={{ minWidth: 240 }}
                />
              ) : (
                <Text size="sm"><IconUser size={14} aria-hidden style={{ verticalAlign: 'middle' }} /> {accountLabel(connection, t('connectedAccount'))}</Text>
              )}
              <ConnectButton projectId={projectId} provider={provider} label={t('connectAnother')} />
            </Group>

            {connection.lastError ? (
              <Alert color="yellow" icon={<IconAlertTriangle size={16} />} role="status" title={t('reconnectTitle')}>
                <Stack gap="xs" align="flex-start">
                  <Text size="sm">{connection.lastError}</Text>
                  <ConnectButton projectId={projectId} provider={provider} label={t('reconnect')} />
                </Stack>
              </Alert>
            ) : drivesError ? (
              <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">
                <Stack gap="xs" align="flex-start">
                  <Text size="sm">{describeCloudError(drivesError, t('drivesFailed'))}</Text>
                  {needsReconnect(drivesError) && <ConnectButton projectId={projectId} provider={provider} label={t('reconnect')} />}
                </Stack>
              </Alert>
            ) : !drives ? (
              <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t('listingDrives')}</Text></Group>
            ) : drives.length === 0 ? (
              <Text size="sm" c="dimmed">{t('noDrive')}</Text>
            ) : (
              <>
                {drives.length > 1 && (
                  <Select
                    label={t('drive')}
                    data={drives.map((d) => ({ value: d.driveId ?? '', label: t('driveOption', { name: d.name, kind: describeDriveKind(d.kind) }) }))}
                    value={drive?.driveId ?? ''}
                    onChange={(value) => { setDriveKey(value ?? ''); reset(); }}
                    allowDeselect={false}
                    style={{ maxWidth: 420 }}
                  />
                )}
                {drive && (
                  <Stack gap={4}>
                    <Text size="sm" fw={600}>{t('folder')}</Text>
                    <CloudFolderBrowser
                      key={`${connection.id}:${drive.driveId ?? ''}`}
                      connectionId={connection.id}
                      driveId={drive.driveId}
                      value={folder}
                      onChange={(next) => { setFolder(next); setChoice(initialChoice(null)); }}
                    />
                  </Stack>
                )}
                {drive && folder && (
                  <CloudFileSelector
                    key={`${connection.id}:${drive.driveId ?? ''}:${folder.folderId ?? ''}`}
                    connectionId={connection.id}
                    driveId={drive.driveId}
                    folderId={folder.folderId}
                    onChange={setChoice}
                  />
                )}
              </>
            )}

            {refusal && (
              <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert" title={t('notLinkedTitle')}>
                <Stack gap="xs" align="flex-start">
                  <Text size="sm">{refusal.message}</Text>
                  {refusal.reconnect && <ConnectButton projectId={projectId} provider={provider} label={t('reconnect')} />}
                </Stack>
              </Alert>
            )}

            {folder && (
              <Stack gap={4}>
                <Group>
                  <Button
                    style={{ minHeight: 44 }}
                    leftSection={<IconLink size={16} />}
                    loading={busy}
                    disabled={choice.includePaths === null}
                    onClick={() => void onLink()}
                  >
                    {t('linkAndSync', { folder: folder.displayPath || folder.name })}
                  </Button>
                </Group>
                {choice.problem && <Text size="sm" c="dimmed">{t.msg(choice.problem)}</Text>}
              </Stack>
            )}
          </>
        )}
      </Stack>
    </Panel>
  );
}

// ------------------------------------------------------------------ linked

function Detail({ label, icon, children }: { label: string; icon: React.ReactNode; children: React.ReactNode }) {
  return (
    <Group justify="space-between" wrap="wrap" gap="xs" align="flex-start" style={{ rowGap: 2 }}>
      <Group gap={6} wrap="nowrap">{icon}<Text size="sm" c="dimmed">{label}</Text></Group>
      <div style={{ minWidth: 0, maxWidth: '100%', textAlign: 'right', marginLeft: 'auto' }}>{children}</div>
    </Group>
  );
}

function LinkedFolderCard({
  projectId,
  provider,
  link,
  connection,
  syncing,
  onFollow,
  onChanged,
}: {
  projectId: string;
  provider: CloudProvider;
  link: ProjectCloudLink;
  connection: CloudConnection | undefined;
  syncing: boolean;
  onFollow: (linkId: string) => void;
  onChanged: () => Promise<unknown> | void;
}) {
  const tr = useT();
  const t = useT('integrations.cloud.tab');
  const f = useFormat();
  const [busy, setBusy] = useState<'sync' | 'save' | 'unlink' | null>(null);
  const [refusal, setRefusal] = useState<{ message: string; reconnect: boolean } | null>(null);
  const [editing, setEditing] = useState(false);
  const [choice, setChoice] = useState<CloudFileChoice>(initialChoice(link.includePaths));
  const [confirmUnlink, setConfirmUnlink] = useState(false);

  const reconnect = linkNeedsReconnect(link, connection);
  const failed = Boolean(link.lastError) && !syncing;

  const onSync = async () => {
    if (busy || syncing) return;
    setBusy('sync');
    setRefusal(null);
    try {
      await syncProjectCloudLink(projectId, link.id);
      onFollow(link.id);
    } catch (e) {
      setRefusal({ message: describeCloudError(e, t('syncStartFailed')), reconnect: needsReconnect(e) });
    } finally {
      setBusy(null);
    }
  };

  const startEditing = () => {
    setChoice(initialChoice(link.includePaths));
    setRefusal(null);
    setEditing(true);
  };

  const onSave = async () => {
    if (busy || choice.includePaths === null) return;
    setBusy('save');
    setRefusal(null);
    try {
      const updated = await updateProjectCloudLink(projectId, link.id, { includePaths: choice.includePaths });
      setEditing(false);
      if (updated.syncing) {
        onFollow(updated.id);
        notifications.show({ color: 'blue', title: t('savedTitle'), message: t('savedSyncing') });
      } else {
        notifications.show({ color: 'blue', message: t('nothingChanged') });
      }
      await onChanged();
    } catch (e) {
      setRefusal({ message: describeCloudError(e, t('saveFailed')), reconnect: needsReconnect(e) });
    } finally {
      setBusy(null);
    }
  };

  const onUnlink = async () => {
    setBusy('unlink');
    try {
      await deleteProjectCloudLink(projectId, link.id);
      notifications.show({ color: 'green', message: t('unlinked', { provider: provider.displayName }) });
    } catch (e) {
      notifications.show({
        color: 'red',
        message: cloudErrorCode(e) === 'link_not_found'
          ? t('alreadyUnlinked')
          : describeCloudError(e, t('unlinkFailed')),
      });
    } finally {
      setBusy(null);
      setConfirmUnlink(false);
      await onChanged();
    }
  };

  const status = syncing
    ? <JobStatusBadge state="running" />
    : failed
      ? <JobStatusBadge state="failed" />
      : link.lastSyncedAt
        ? <JobStatusBadge state="done" />
        : <Badge variant="light" color="gray">{t('status.never')}</Badge>;

  return (
    <Panel id={`cloud-${provider.provider}`} title={<Group gap="xs" component="span"><IconCloud size={16} aria-hidden /><span>{provider.displayName}</span></Group>}>
      <Stack gap="md">
        <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
          <Stack gap={4} style={{ minWidth: 0, flex: '1 1 240px' }}>
            <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
              <IconFolder size={18} aria-hidden style={{ flex: '0 0 auto' }} />
              <Text fw={650} style={{ overflowWrap: 'anywhere' }}>{link.folderDisplayPath}</Text>
            </Group>
            {/* Announced to screen readers as it changes: "Syncing…", then how it went. */}
            <Group gap="xs" role="status" aria-live="polite" aria-label={t('statusAria', { provider: provider.displayName })}>
              {status}
            </Group>
          </Stack>
          <Group gap="xs" wrap="wrap">
            <Button
              style={{ minHeight: 44 }} leftSection={<IconRefresh size={16} />}
              onClick={() => void onSync()} loading={busy === 'sync'} disabled={syncing || (busy !== null && busy !== 'sync')}
            >
              {syncing ? t('status.syncing') : t('syncNow')}
            </Button>
            <Button
              variant="default" style={{ minHeight: 44 }} leftSection={<IconFiles size={16} />}
              onClick={startEditing} disabled={editing || syncing || busy !== null || !link.connectionId}
            >
              {t('changeFiles')}
            </Button>
          </Group>
        </Group>

        <Stack gap={10}>
          <Detail label={t('details.account')} icon={<IconUser size={14} />}>
            <Text size="sm" style={{ overflowWrap: 'anywhere' }}>
              {link.accountEmail ?? (link.connectionId ? t('connectedAccount') : t('details.disconnected'))}
            </Text>
          </Detail>
          <Detail label={t('details.filesIncluded')} icon={<IconFiles size={14} />}>
            <Text size="sm" style={{ overflowWrap: 'anywhere' }}>{t.msg(describeSelection(link.includePaths))}</Text>
          </Detail>
          <Detail label={t('details.lastSynced')} icon={<IconRefresh size={14} />}>
            {link.lastSyncedAt ? (
              <Tooltip label={new Date(link.lastSyncedAt).toLocaleString(f.locale)}>
                <Text size="sm">{timeAgo(link.lastSyncedAt, undefined, tr, f.locale)}</Text>
              </Tooltip>
            ) : <Text size="sm" c="dimmed">{t('details.notYet')}</Text>}
          </Detail>
          <Detail label={t('details.filesInProject')} icon={<IconFiles size={14} />}>
            <Text size="sm">{link.lastSyncedAt ? t('details.fileCount', { count: link.lastSyncedFileCount }) : '—'}</Text>
          </Detail>
        </Stack>

        {syncing && (
          <Text size="sm" c="dimmed">
            {t('copying', { provider: provider.displayName })}
          </Text>
        )}

        {failed && (
          <Alert color="red" icon={<IconAlertTriangle size={16} />} title={t('lastSyncFailedTitle')}>
            <Stack gap="xs" align="flex-start">
              <Text size="sm">{link.lastError}</Text>
              <Text size="xs" c="dimmed">{t('nothingDeleted')}</Text>
              {reconnect ? (
                <ConnectButton projectId={projectId} provider={provider} label={t('reconnect')} />
              ) : isSelectionProblem(link.lastError) ? (
                <Button size="sm" style={{ minHeight: 40 }} leftSection={<IconFiles size={14} />} onClick={startEditing} disabled={editing}>
                  {t('changeFiles')}
                </Button>
              ) : (
                <Button size="sm" variant="default" style={{ minHeight: 40 }} leftSection={<IconRefresh size={14} />} onClick={() => void onSync()} loading={busy === 'sync'}>
                  {t('tryAgain')}
                </Button>
              )}
            </Stack>
          </Alert>
        )}

        {!failed && reconnect && !syncing && (
          <Alert color="yellow" icon={<IconAlertTriangle size={16} />} title={t('accountReconnectTitle')}>
            <Stack gap="xs" align="flex-start">
              <Text size="sm">
                {connection?.lastError ?? t('accountGone')}
              </Text>
              <ConnectButton projectId={projectId} provider={provider} label={t('reconnect')} />
            </Stack>
          </Alert>
        )}

        {refusal && (
          <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert" title={t('didNotWork')}>
            <Stack gap="xs" align="flex-start">
              <Text size="sm">{refusal.message}</Text>
              {refusal.reconnect && <ConnectButton projectId={projectId} provider={provider} label={t('reconnect')} />}
            </Stack>
          </Alert>
        )}

        {editing && link.connectionId && (
          <Stack gap="sm">
            <Text size="sm" fw={600}>{t('editHeading')}</Text>
            <CloudFileSelector
              connectionId={link.connectionId}
              driveId={link.driveId}
              folderId={link.folderId}
              initialIncludePaths={link.includePaths}
              onChange={setChoice}
            />
            <Group gap="xs">
              <Button style={{ minHeight: 44 }} loading={busy === 'save'} disabled={choice.includePaths === null} onClick={() => void onSave()}>
                {t('saveAndSync')}
              </Button>
              <Button variant="default" style={{ minHeight: 44 }} onClick={() => setEditing(false)} disabled={busy === 'save'}>{t('cancel')}</Button>
            </Group>
            {choice.problem && <Text size="sm" c="dimmed">{t.msg(choice.problem)}</Text>}
            <Text size="xs" c="dimmed">{t('unticked')}</Text>
          </Stack>
        )}

        <Group justify="flex-end">
          <Button
            variant="subtle" color="red" style={{ minHeight: 44 }} leftSection={<IconTrash size={16} />}
            onClick={() => setConfirmUnlink(true)} disabled={busy !== null}
          >
            {t('unlinkFolder')}
          </Button>
        </Group>
      </Stack>

      <ConfirmModal
        opened={confirmUnlink}
        onClose={() => setConfirmUnlink(false)}
        onConfirm={() => void onUnlink()}
        title={t('unlinkTitle')}
        confirmLabel={t('unlinkConfirm')}
        loading={busy === 'unlink'}
        message={t('unlinkMessage', { path: link.folderDisplayPath })}
      />
    </Panel>
  );
}
