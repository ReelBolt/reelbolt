'use client';

import { useEffect, useState } from 'react';
import {
  Alert, Anchor, Avatar, Badge, Button, Center, Group, Loader, Stack, Text, Tooltip,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import {
  IconAlertTriangle, IconBrandGithub, IconCheck, IconExternalLink, IconFiles, IconGitBranch, IconGitCommit,
  IconLink, IconRefresh, IconTrash,
} from '@tabler/icons-react';
import { Panel, QuietText } from '@/components/dashboard/Panel';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import {
  branchUrl,
  commitUrl,
  describeGitHubAccountError,
  describeGitHubConnectOutcome,
  describeGitHubLinkError,
  describeGitHubPullError,
  describeGitHubUnlinkError,
  describeLastSyncError,
  disconnectGitHubAccount,
  githubErrorCode,
  linkProjectRepository,
  pullProjectRepository,
  repositoryUrl,
  startGitHubConnect,
  unlinkProjectRepository,
  updateProjectRepository,
} from '@/lib/api/github';
import { timeAgo } from '@/lib/files/model';
import { describeIncludePaths } from '@/lib/github/selection';
import { useGitHubAccount } from '@/lib/hooks/use-github-account';
import { useProjectGitHub } from '@/lib/hooks/use-project-github';
import type { GitHubAccount, GitHubRepositoryOption, ProjectGitHubLink } from '@/lib/types/github';
import { GitHubBranchSelect } from './GitHubBranchSelect';
import { GitHubFileSelector, describeSelectionProblem, type FileSelection } from './GitHubFileSelector';
import { GitHubRepositoryPicker } from './GitHubRepositoryPicker';
import { useT, useFormat } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';

/** The first 7 characters of a commit SHA, which is how GitHub itself abbreviates them. */
export function shortCommit(sha: string | null): string | null {
  if (!sha) return null;
  return sha.slice(0, 7);
}

/**
 * What the dashboard can say about the last sync, derived from the link.
 *
 * - `never`  nothing has been synced yet;
 * - `failed` the last ATTEMPT failed, and `reason` says why in plain words. The commit fields still
 *            describe the last SUCCESS, which is why they are reported alongside it;
 * - `ok`     the project mirrors `commit`, synced `at`.
 */
export type PullState =
  | { kind: 'never' }
  | { kind: 'failed'; reason: string; commit: string | null; at: string | null }
  | { kind: 'ok'; commit: string; at: string | null; files: number };

export function pullState(link: ProjectGitHubLink): PullState {
  if (link.lastError) {
    return {
      kind: 'failed',
      reason: describeLastSyncError(link.lastError) ?? link.lastError,
      commit: shortCommit(link.lastPulledCommit),
      at: link.lastPulledAt,
    };
  }
  if (!link.lastPulledCommit) return { kind: 'never' };
  return { kind: 'ok', commit: shortCommit(link.lastPulledCommit) as string, at: link.lastPulledAt, files: link.lastPulledFileCount };
}

/** How often the link is re-read while a sync runs in the background. */
export const SYNC_POLL_MS = 3000;

/**
 * Why the page is following a background sync: it asked for one ("Sync now", a new link, a changed
 * choice of files), or it found one already running when it loaded.
 */
export type SyncWatch = 'pull' | 'link' | 'change' | 'observed';

/** The notification for a background sync that has just ended, from the link as it now reads. */
export function syncFinishedNotice(link: ProjectGitHubLink, watch: SyncWatch): { color: string; title: Msg; message: Msg | string } {
  if (link.lastError) {
    return {
      color: 'red',
      title: watch === 'link'
        ? msg('integrations.github.tab.syncFailedLinkTitle', { repo: link.repository })
        : msg('integrations.github.tab.syncFailedTitle'),
      message: describeLastSyncError(link.lastError) ?? link.lastError,
    };
  }
  return {
    color: 'green',
    title: watch === 'link'
      ? msg('integrations.github.tab.syncDoneLinkTitle', { repo: link.repository })
      : msg('integrations.github.tab.syncDoneTitle'),
    message: msg('integrations.github.tab.syncDoneMessage', {
      count: link.lastPulledFileCount,
      repo: link.repository,
      branch: link.branch,
    }),
  };
}

/** True for a sync failure that choosing fewer files fixes. */
function isSelectionProblem(lastError: string | null): boolean {
  return Boolean(lastError && /too many files|too large to sync|configured limit|choose fewer/i.test(lastError));
}

function Detail({ label, icon, children }: { label: string; icon: React.ReactNode; children: React.ReactNode }) {
  return (
    <Group justify="space-between" wrap="wrap" gap="xs" align="flex-start" style={{ rowGap: 2 }}>
      <Group gap={6} wrap="nowrap">
        {icon}
        <Text size="sm" c="dimmed">{label}</Text>
      </Group>
      <div style={{ minWidth: 0, maxWidth: '100%', textAlign: 'right', marginLeft: 'auto' }}>{children}</div>
    </Group>
  );
}

/**
 * The project's GitHub repository (docs/github-app.md, docs/user-guide/github-repository.md): link
 * one, choose which of its files to sync, and — once linked — see at a glance what is linked, what
 * was last synced and whether it worked.
 *
 * Linking is a sequence of choices, never typing: connect your GitHub account, pick the repository
 * from the list GitHub returns for it, pick the branch, tick the files. Every sentence on screen is
 * written for the person using it; how the GitHub side is set up is the administrator's business and
 * lives in the operator docs only.
 */
export function ProjectGitHubTab({
  projectId,
  connectOutcome = null,
}: {
  projectId: string;
  /** The `?github=` token the sign-in with GitHub redirects back with. Read by the page, passed in here. */
  connectOutcome?: string | null;
}) {
  const t = useT('integrations.github.tab');
  const { data: link, isLoading, error, mutate } = useProjectGitHub(projectId);
  const { data: account, error: accountError, mutate: mutateAccount } = useGitHubAccount();
  const [requested, setWatch] = useState<SyncWatch | null>(null);
  // A sync that was already running when the page loaded (another tab, a teammate) is followed too.
  const watch: SyncWatch | null = requested ?? (link?.syncing ? 'observed' : null);

  // While a sync runs in the background, re-read the link every few seconds; when it says the sync is
  // over, say how it went. A read that fails is simply tried again on the next tick.
  useEffect(() => {
    if (watch === null) return undefined;
    let cancelled = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const tick = async () => {
      let fresh: ProjectGitHubLink | null | undefined;
      try {
        fresh = await mutate();
      } catch {
        fresh = undefined;
      }
      if (cancelled) return;
      if (fresh === undefined || fresh?.syncing) {
        timer = setTimeout(() => void tick(), SYNC_POLL_MS);
        return;
      }
      setWatch(null);
      if (fresh) {
        const notice = syncFinishedNotice(fresh, watch);
        notifications.show({
          ...notice,
          title: t.msg(notice.title),
          message: typeof notice.message === 'string' ? notice.message : t.msg(notice.message),
          autoClose: fresh.lastError ? 10000 : 6000,
        });
      }
    };
    timer = setTimeout(() => void tick(), SYNC_POLL_MS);
    return () => {
      cancelled = true;
      if (timer) clearTimeout(timer);
    };
  }, [watch, mutate, t]);

  // The sign-in lands the browser back on this page with a short token in the URL. It is reported
  // once and then the connection is re-read, because the page was rendered before it existed.
  useEffect(() => {
    const message = describeGitHubConnectOutcome(connectOutcome);
    if (!message) return;
    notifications.show({ color: connectOutcome === 'connected' ? 'green' : 'yellow', message });
    void mutateAccount();
  }, [connectOutcome, mutateAccount]);

  if (isLoading) {
    return (
      <Center h={160}>
        <Group gap="xs"><Loader size="sm" /><Text size="sm" c="dimmed">{t('checkingLink')}</Text></Group>
      </Center>
    );
  }

  if (error) {
    return (
      <Alert color="red" icon={<IconAlertTriangle size={16} />} title={t('linkLoadFailedTitle')}>
        <Stack gap="sm" align="flex-start">
          <Text size="sm">{t('linkLoadFailedBody')}</Text>
          <Button variant="default" size="sm" style={{ minHeight: 44 }} onClick={() => void mutate()}>{t('tryAgain')}</Button>
        </Stack>
      </Alert>
    );
  }

  return link
    ? (
      <LinkedRepository
        projectId={projectId}
        link={link}
        account={account}
        syncing={watch !== null || Boolean(link.syncing)}
        onSyncStarted={setWatch}
        onChanged={() => mutate()}
      />
    )
    : (
      <LinkRepository
        projectId={projectId}
        account={account}
        accountError={accountError}
        onAccountChanged={() => mutateAccount()}
        onLinked={(created) => {
          void mutate(created, { revalidate: false });
          if (created.syncing) setWatch('link');
        }}
      />
    );
}

// ------------------------------------------------------------------ connecting an account

function ConnectAccount({
  projectId,
  account,
  accountError,
  compact = false,
  onRetry,
}: {
  projectId: string;
  account: GitHubAccount | undefined;
  accountError: unknown;
  compact?: boolean;
  onRetry: () => void;
}) {
  const t2 = useT('integrations.github.tab.connect');
  const [busy, setBusy] = useState(false);
  const [refusal, setRefusal] = useState<string | null>(null);

  const onConnect = async () => {
    if (busy) return;
    setBusy(true);
    setRefusal(null);
    try {
      // A full navigation: the sign-in leaves for github.com and comes back to this tab.
      window.location.assign(await startGitHubConnect(projectId));
    } catch (e) {
      setRefusal(describeGitHubAccountError(e));
      setBusy(false);
    }
  };

  if (accountError) {
    return (
      <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert" title={t2('checkFailedTitle')}>
        <Stack gap="sm" align="flex-start">
          <Text size="sm">{describeGitHubAccountError(accountError)}</Text>
          <Button variant="default" size="sm" style={{ minHeight: 44 }} onClick={onRetry}>{t2('tryAgain')}</Button>
        </Stack>
      </Alert>
    );
  }

  if (account === undefined) {
    return <Group gap="xs"><Loader size="xs" /><Text size="sm" c="dimmed">{t2('checking')}</Text></Group>;
  }

  if (!account.canConnect) {
    return (
      <Alert color="gray" icon={<IconBrandGithub size={16} />} role="status" title={t2('unavailableTitle')}>
        <Text size="sm">
          {t2('unavailableBody')}
        </Text>
      </Alert>
    );
  }

  return (
    <Stack gap="sm">
      {!compact && (
        <QuietText>
          {t2('intro')}
        </QuietText>
      )}

      {account.revokedReason && (
        <Alert color="yellow" icon={<IconAlertTriangle size={16} />} role="status">
          <Text size="sm">
            {t2('revoked', { reason: account.revokedReason })}
          </Text>
        </Alert>
      )}

      {refusal && <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert">{refusal}</Alert>}

      <Group>
        <Button style={{ minHeight: 44 }} leftSection={<IconBrandGithub size={16} />} loading={busy} onClick={() => void onConnect()}>
          {t2('button')}
        </Button>
      </Group>
    </Stack>
  );
}

// ------------------------------------------------------------------ not linked yet

function Step({ number, title, done, children }: { number: number; title: string; done?: boolean; children: React.ReactNode }) {
  return (
    <Panel
      id={`github-step-${number}`}
      title={(
        <Group gap="xs" wrap="nowrap" component="span">
          <Badge
            component="span"
            circle
            size="lg"
            variant={done ? 'filled' : 'light'}
            color={done ? 'green' : 'gray'}
            aria-hidden
          >
            {done ? <IconCheck size={12} /> : number}
          </Badge>
          <span>{title}</span>
        </Group>
      )}
    >
      {children}
    </Panel>
  );
}

function LinkRepository({
  projectId,
  account,
  accountError,
  onAccountChanged,
  onLinked,
}: {
  projectId: string;
  account: GitHubAccount | undefined;
  accountError: unknown;
  onAccountChanged: () => void;
  onLinked: (link: ProjectGitHubLink) => void;
}) {
  const t3 = useT('integrations.github.tab.link');
  const [picked, setPicked] = useState<GitHubRepositoryOption | null>(null);
  const [branch, setBranch] = useState<string | null>(null);
  const [selection, setSelection] = useState<FileSelection | null>(null);
  const [busy, setBusy] = useState<'link' | 'disconnect' | null>(null);
  const [refusal, setRefusal] = useState<{ message: string; connect: boolean } | null>(null);

  const connected = account?.connected ?? false;
  const effectiveBranch = picked ? branch ?? picked.defaultBranch : null;

  const choose = (next: GitHubRepositoryOption) => {
    setPicked(next);
    setBranch(null);
    setSelection(null);
    setRefusal(null);
  };

  const onDisconnect = async () => {
    if (busy) return;
    setBusy('disconnect');
    try {
      await disconnectGitHubAccount();
      setPicked(null);
      setBranch(null);
      setSelection(null);
      onAccountChanged();
      notifications.show({ color: 'green', message: t3('disconnected') });
    } catch (e) {
      notifications.show({ color: 'red', message: describeGitHubAccountError(e) });
    } finally {
      setBusy(null);
    }
  };

  const canLink = Boolean(picked && selection && selection.includePaths !== null && !selection.problem);

  const onLink = async () => {
    if (busy || !picked || !selection || selection.includePaths === null || selection.problem) return;
    setBusy('link');
    setRefusal(null);
    try {
      const created = await linkProjectRepository(projectId, {
        repository: picked.fullName,
        branch: effectiveBranch && effectiveBranch !== picked.defaultBranch ? effectiveBranch : null,
        includePaths: selection.includePaths,
      });
      if (created.syncing) {
        notifications.show({
          color: 'blue',
          title: t3('linkedTitle', { repo: created.repository }),
          message: t3('copying', { count: selection.totals.files }),
        });
      } else if (created.lastError) {
        notifications.show({
          color: 'yellow',
          title: t3('linkedFirstSyncFailed', { repo: created.repository }),
          message: describeLastSyncError(created.lastError),
          autoClose: 10000,
        });
      } else {
        notifications.show({
          color: 'green',
          title: t3('linkedTitle', { repo: created.repository }),
          message: t3('everyPush', { branch: created.branch }),
        });
      }
      onLinked(created);
    } catch (e) {
      setRefusal({ message: describeGitHubLinkError(e), connect: githubErrorCode(e) === 'github_account_not_connected' });
    } finally {
      setBusy(null);
    }
  };

  return (
    <Stack gap="md">
      <div>
        <Text fw={650}>{t3('heading')}</Text>
        <QuietText>
          {t3('intro')}
        </QuietText>
      </div>

      <Step number={1} title={t3('step1')} done={connected}>
        {connected ? (
          <Group justify="space-between" wrap="wrap" gap="xs">
            <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
              {account?.avatarUrl
                ? <Avatar src={account.avatarUrl} size={24} radius="xl" alt="" />
                : <IconBrandGithub size={18} aria-hidden />}
              <Text size="sm" truncate>{t3.rich('connectedAs', { login: account?.login ?? '', b: (chunks) => <Text span fw={650}>{chunks}</Text> })}</Text>
            </Group>
            <Button
              variant="subtle" color="gray" size="compact-sm" style={{ minHeight: 32 }}
              loading={busy === 'disconnect'} onClick={() => void onDisconnect()}
            >
              {t3('disconnect')}
            </Button>
          </Group>
        ) : (
          <ConnectAccount projectId={projectId} account={account} accountError={accountError} onRetry={onAccountChanged} />
        )}
      </Step>

      {connected && (
        <Step number={2} title={t3('step2')} done={Boolean(picked)}>
          <GitHubRepositoryPicker projectId={projectId} value={picked} onChange={choose} />
        </Step>
      )}

      {connected && picked && effectiveBranch && (
        <Step number={3} title={t3('step3')} done={canLink}>
          <Stack gap="md">
            <GitHubBranchSelect
              projectId={projectId}
              repositoryId={picked.id}
              defaultBranch={picked.defaultBranch}
              value={effectiveBranch}
              onChange={(next) => { setBranch(next); setSelection(null); }}
            />
            {/* Keyed on repository and branch: a new tree starts a new choice. */}
            <GitHubFileSelector
              key={`${picked.id}:${effectiveBranch}`}
              projectId={projectId}
              repositoryId={picked.id}
              branch={effectiveBranch}
              onChange={setSelection}
            />
          </Stack>
        </Step>
      )}

      {refusal && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert" title={t3('notLinkedTitle')}>
          <Stack gap="sm" align="flex-start">
            <Text size="sm">{refusal.message}</Text>
            {refusal.connect && (
              <ConnectAccount projectId={projectId} account={account} accountError={null} compact onRetry={onAccountChanged} />
            )}
          </Stack>
        </Alert>
      )}

      {connected && picked && (
        <Stack gap={6}>
          <Group>
            <Button
              style={{ minHeight: 44 }}
              leftSection={<IconLink size={16} />}
              loading={busy === 'link'}
              disabled={!canLink}
              onClick={() => void onLink()}
            >
              {selection && selection.includePaths !== null && !selection.problem
                ? t3('linkAndSync', { count: selection.totals.files })
                : t3('linkRepository')}
            </Button>
          </Group>
          {selection?.problem && (
            <Text size="sm" c={selection.problem.kind === 'empty' ? 'dimmed' : 'red'}>
              {t3.msg(describeSelectionProblem(selection.problem))}
            </Text>
          )}
        </Stack>
      )}
    </Stack>
  );
}

// ------------------------------------------------------------------ linked

function LinkedRepository({
  projectId,
  link,
  account,
  syncing,
  onSyncStarted,
  onChanged,
}: {
  projectId: string;
  link: ProjectGitHubLink;
  account: GitHubAccount | undefined;
  /** A sync of this project is queued or running in the background. */
  syncing: boolean;
  /** Tells the page to follow the sync a request just queued. */
  onSyncStarted: (watch: SyncWatch) => void;
  onChanged: () => Promise<unknown> | void;
}) {
  const tr = useT();
  const t4 = useT('integrations.github.tab.linked');
  const f = useFormat();
  const [busy, setBusy] = useState<'pull' | 'save' | 'unlink' | null>(null);
  const [refusal, setRefusal] = useState<{ message: string; connect: boolean } | null>(null);
  const [confirmUnlink, setConfirmUnlink] = useState(false);
  const [editing, setEditing] = useState(false);
  const [editBranch, setEditBranch] = useState(link.branch);
  const [selection, setSelection] = useState<FileSelection | null>(null);

  const state = pullState(link);
  const defaultBranch = link.defaultBranch ?? link.branch;
  const connected = account?.connected ?? false;
  const failure = (e: unknown) => ({ message: describeGitHubPullError(e), connect: githubErrorCode(e) === 'github_account_not_connected' });

  const onPull = async () => {
    if (busy || syncing) return;
    setBusy('pull');
    setRefusal(null);
    try {
      // Answered at once (queued, or already syncing): the sync itself runs in the background and the
      // page follows it until the link says it is over.
      await pullProjectRepository(projectId);
      onSyncStarted('pull');
    } catch (e) {
      setRefusal(failure(e));
      await onChanged();
    } finally {
      setBusy(null);
    }
  };

  const startEditing = () => {
    setEditBranch(link.branch);
    setSelection(null);
    setRefusal(null);
    setEditing(true);
  };

  const onSave = async () => {
    if (busy || !selection || selection.includePaths === null || selection.problem) return;
    setBusy('save');
    setRefusal(null);
    try {
      const updated = await updateProjectRepository(projectId, {
        branch: editBranch === link.branch ? null : editBranch === defaultBranch ? '' : editBranch,
        includePaths: selection.includePaths,
      });
      setEditing(false);
      if (updated.syncing) {
        onSyncStarted('change');
        notifications.show({ color: 'blue', title: t4('savedTitle'), message: t4('savedSyncing') });
      } else {
        notifications.show({ color: 'blue', message: t4('nothingChanged') });
      }
    } catch (e) {
      setRefusal({ message: describeGitHubLinkError(e), connect: githubErrorCode(e) === 'github_account_not_connected' });
    } finally {
      await onChanged();
      setBusy(null);
    }
  };

  const onUnlink = async () => {
    setBusy('unlink');
    try {
      await unlinkProjectRepository(projectId);
      notifications.show({ color: 'green', message: t4('unlinked') });
    } catch (e) {
      notifications.show({ color: 'red', message: describeGitHubUnlinkError(e) });
    } finally {
      setBusy(null);
      setConfirmUnlink(false);
      await onChanged();
    }
  };

  const status = syncing
    ? <Badge variant="light" color="blue" leftSection={<Loader size={10} color="blue" />}>{t4('status.syncing')}</Badge>
    : state.kind === 'ok'
    ? <Badge variant="light" color="green" leftSection={<IconCheck size={12} />}>{t4('status.synced')}</Badge>
    : state.kind === 'failed'
      ? <Badge variant="light" color="red" leftSection={<IconAlertTriangle size={12} />}>{t4('status.failed')}</Badge>
      : <Badge variant="light" color="gray">{t4('status.never')}</Badge>;

  const commitSha = link.lastPulledCommit;

  return (
    <Stack gap="md">
      <Panel id="github-linked" title={t4('panelTitle')}>
        <Stack gap="md">
          <Group justify="space-between" align="flex-start" wrap="wrap" gap="sm">
            <Stack gap={4} style={{ minWidth: 0, flex: '1 1 240px' }}>
              <Group gap="xs" wrap="nowrap" style={{ minWidth: 0 }}>
                <IconBrandGithub size={20} aria-hidden style={{ flex: '0 0 auto' }} />
                <Anchor
                  href={repositoryUrl(link.repository)}
                  target="_blank"
                  rel="noreferrer noopener"
                  fw={650}
                  ff="monospace"
                  style={{ minWidth: 0, overflowWrap: 'anywhere' }}
                >
                  {link.repository}
                </Anchor>
                <IconExternalLink size={14} aria-hidden style={{ flex: '0 0 auto', opacity: 0.6 }} />
              </Group>
              <Group gap="xs">{status}</Group>
            </Stack>
            <Group gap="xs" wrap="wrap">
              <Button
                style={{ minHeight: 44 }} leftSection={<IconRefresh size={16} />}
                onClick={() => void onPull()} loading={busy === 'pull'} disabled={syncing || (busy !== null && busy !== 'pull')}
              >
                {syncing ? t4('status.syncing') : t4('syncNow')}
              </Button>
              <Button
                variant="default" style={{ minHeight: 44 }} leftSection={<IconFiles size={16} />}
                onClick={startEditing} disabled={editing || syncing || busy !== null}
              >
                {t4('changeFiles')}
              </Button>
            </Group>
          </Group>

          <QuietText>
            {t4('everyPush', { branch: link.branch })}
          </QuietText>

          <Stack gap={10}>
            <Detail label={t4('branch')} icon={<IconGitBranch size={14} />}>
              <Anchor href={branchUrl(link.repository, link.branch)} target="_blank" rel="noreferrer noopener" size="sm" ff="monospace">
                {link.branch}
              </Anchor>
              {link.branch === defaultBranch && <Text span size="xs" c="dimmed"> {t4('defaultSuffix')}</Text>}
            </Detail>
            <Detail label={t4('filesIncluded')} icon={<IconFiles size={14} />}>
              <Text size="sm" style={{ overflowWrap: 'anywhere' }}>{describeIncludePaths(link.includePaths, link.pathPrefix)}</Text>
            </Detail>
            <Detail label={t4('lastSynced')} icon={<IconGitCommit size={14} />}>
              {link.lastPulledAt && commitSha ? (
                <Text size="sm">
                  {t4.rich('lastSyncedCommit', {
                    ago: timeAgo(link.lastPulledAt, undefined, tr, f.locale),
                    sha: shortCommit(commitSha) ?? '',
                    when: (chunks) => (
                      <Tooltip label={new Date(link.lastPulledAt as string).toLocaleString(f.locale)}>
                        <span>{chunks}</span>
                      </Tooltip>
                    ),
                    commit: (chunks) => (
                      <Anchor href={commitUrl(link.repository, commitSha)} target="_blank" rel="noreferrer noopener" ff="monospace" size="sm">
                        {chunks}
                      </Anchor>
                    ),
                  })}
                </Text>
              ) : (
                <Text size="sm" c="dimmed">{t4('notYet')}</Text>
              )}
            </Detail>
            <Detail label={t4('filesInProject')} icon={<IconFiles size={14} />}>
              <Text size="sm">{link.lastPulledCommit ? t4('fileCount', { count: link.lastPulledFileCount }) : '—'}</Text>
            </Detail>
            {link.linkedAt && (
              <Detail label={t4('linked')} icon={<IconLink size={14} />}>
                <Text size="sm">{timeAgo(link.linkedAt, undefined, tr, f.locale)}</Text>
              </Detail>
            )}
          </Stack>
        </Stack>
      </Panel>

      {refusal && (
        <Alert color="red" icon={<IconAlertTriangle size={16} />} role="alert" title={t4('didNotWork')}>
          <Stack gap="sm" align="flex-start">
            <Text size="sm">{refusal.message}</Text>
            {refusal.connect && (
              <ConnectAccount projectId={projectId} account={account} accountError={null} compact onRetry={() => void onChanged()} />
            )}
          </Stack>
        </Alert>
      )}

      {syncing && (
        <Alert color="blue" icon={<Loader size={16} color="blue" />} role="status" title={t4('syncingTitle')}>
          <Text size="sm">
            {t4('syncingBody')}
          </Text>
        </Alert>
      )}

      {state.kind === 'failed' && !syncing && (
        <Panel tone="attention" id="github-sync-failed" title={t4('failedTitle')}>
          <Stack gap="sm">
            <Text size="sm">{state.reason}</Text>
            <Text size="xs" c="dimmed">
              {state.commit
                ? t4('keptFiles', { commit: state.commit })
                : t4('noFilesYet')}
            </Text>
            <Group gap="xs">
              {isSelectionProblem(link.lastError) ? (
                <Button size="sm" style={{ minHeight: 44 }} leftSection={<IconFiles size={14} />} onClick={startEditing} disabled={editing}>
                  {t4('chooseFewer')}
                </Button>
              ) : (
                <Button
                  variant="default" size="sm" style={{ minHeight: 44 }}
                  leftSection={<IconRefresh size={14} />} onClick={() => void onPull()} loading={busy === 'pull'}
                >
                  {t4('tryAgain')}
                </Button>
              )}
            </Group>
          </Stack>
        </Panel>
      )}

      {state.kind === 'never' && !editing && !syncing && (
        <Alert color="gray" icon={<IconRefresh size={16} />} role="status" title={t4('neverTitle')}>
          <Text size="sm">
            {t4('neverBody', { branch: link.branch })}
          </Text>
        </Alert>
      )}

      {editing && (
        <Panel id="github-edit" title={t4('editTitle')}>
          {connected ? (
            <Stack gap="md">
              <GitHubBranchSelect
                projectId={projectId}
                repositoryId={null}
                defaultBranch={defaultBranch}
                value={editBranch}
                onChange={(next) => { setEditBranch(next); setSelection(null); }}
              />
              {link.pathPrefix && (
                <Text size="xs" c="dimmed">
                  {t4('prefixNote', { prefix: link.pathPrefix })}
                </Text>
              )}
              <GitHubFileSelector
                key={editBranch}
                projectId={projectId}
                repositoryId={null}
                branch={editBranch}
                root={link.pathPrefix ?? ''}
                initialIncludePaths={link.includePaths ?? null}
                onChange={setSelection}
              />
              <Group gap="xs">
                <Button
                  style={{ minHeight: 44 }}
                  loading={busy === 'save'}
                  disabled={syncing || !selection || selection.includePaths === null || Boolean(selection.problem)}
                  onClick={() => void onSave()}
                >
                  {t4('saveAndSync')}
                </Button>
                <Button variant="default" style={{ minHeight: 44 }} onClick={() => setEditing(false)} disabled={busy === 'save'}>
                  {t4('cancel')}
                </Button>
              </Group>
              <Text size="xs" c="dimmed">
                {t4('unticked')}
              </Text>
            </Stack>
          ) : (
            <Stack gap="sm">
              <Text size="sm">{t4('connectToChange')}</Text>
              <ConnectAccount projectId={projectId} account={account} accountError={null} compact onRetry={() => void onChanged()} />
            </Stack>
          )}
        </Panel>
      )}

      <Group justify="flex-end">
        <Button
          variant="subtle" color="red" style={{ minHeight: 44 }} leftSection={<IconTrash size={16} />}
          onClick={() => setConfirmUnlink(true)} disabled={busy !== null}
        >
          {t4('unlinkRepo')}
        </Button>
      </Group>

      <ConfirmModal
        opened={confirmUnlink}
        onClose={() => setConfirmUnlink(false)}
        onConfirm={() => void onUnlink()}
        title={t4('unlinkTitle')}
        confirmLabel={t4('unlinkConfirm')}
        loading={busy === 'unlink'}
        message={t4('unlinkMessage', { repo: link.repository })}
      />
    </Stack>
  );
}
