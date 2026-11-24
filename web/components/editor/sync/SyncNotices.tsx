'use client';

import React, { useCallback, useEffect, useRef, useState } from 'react';
import { Alert, Button, Drawer, Group, Modal, PasswordInput, Stack, Text, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { IconEye, IconHistory, IconLogin, IconWindow } from '@tabler/icons-react';
import Link from 'next/link';
import { login } from '@/lib/api/auth';
import { ApiError } from '@/lib/api/client';
import { timelineEditorHref } from '@/lib/editing/open-timeline';
import { SIGN_IN_AGAIN, type SaveState, type SyncClient } from '@/lib/editing/sync';
import { downloadBlob } from '@/lib/editing/stills/stills';
import { ReadOnlyBanner } from './ReadOnlyBanner';
import { useT } from '@/i18n';

export interface SyncNoticesProps {
  sync: SyncClient;
  save: SaveState;
  isPhone: boolean;
  projectId: string;
  docName: string;
  /** Reloads the document from the server (a new store, a new sync client). */
  onReload: () => void;
}

/**
 * A dialog on a desktop, a bottom sheet on a phone (buttons are at least 44 px there). One component so every
 * sync decision — sign in, resolve a conflict — looks and behaves the same on both.
 */
function Sheet({ opened, onClose, title, isPhone, testId, children, closable = true }: { opened: boolean; onClose: () => void; title: string; isPhone: boolean; testId: string; children: React.ReactNode; closable?: boolean }) {
  if (isPhone) {
    return (
      <Drawer opened={opened} onClose={onClose} position="bottom" size="auto" title={title} closeOnClickOutside={closable} closeOnEscape={closable} withCloseButton={closable} zIndex={400}>
        <div data-testid={testId}>{children}</div>
      </Drawer>
    );
  }
  return (
    <Modal opened={opened} onClose={onClose} title={title} centered closeOnClickOutside={closable} closeOnEscape={closable} withCloseButton={closable} zIndex={400}>
      <div data-testid={testId}>{children}</div>
    </Modal>
  );
}

const fileSafe = (name: string): string => name.replace(/[^A-Za-z0-9 _-]+/g, '').trim() || 'timeline';

type CopyState = { kind: 'idle' } | { kind: 'working' } | { kind: 'done'; id: string; name: string } | { kind: 'failed'; message: string };

/**
 * Everything the sync client has to say to a person, outside the status chip: the read-only banners
 * (changed elsewhere, offline, view-only, open in another tab), the recovery offer, the sign-in-in-place
 * dialog and the conflict choices. Plain words; no revision numbers, ids or op names.
 */
export function SyncNotices({ sync, save, isPhone, projectId, docName, onReload }: SyncNoticesProps) {
  const t = useT('editor.sync.syncNotices');
  const [copy, setCopy] = useState<CopyState>({ kind: 'idle' });
  // A dialog the user closed stays closed for THAT episode only: the key changes with the next conflict / expiry.
  const [conflictDismissed, setConflictDismissed] = useState<number | null>(null);
  const [signInDismissed, setSignInDismissed] = useState<number | null>(null);
  const conflictOpen = save.status === 'conflict' && !save.offlineRefused;
  const conflictKey = save.conflictRevision ?? -1;
  const signInKey = save.signInRequestedAt ?? 0;

  // The merge toast: shown once per merge (the client stamps `mergedAt`).
  const lastMerged = useRef<number | null>(save.mergedAt ?? null);
  useEffect(() => {
    if (save.mergedAt && save.mergedAt !== lastMerged.current) {
      lastMerged.current = save.mergedAt;
      notifications.show({ color: 'teal', title: t('editsMerged'), message: t('yourEditsWereMerged'), autoClose: 6000 });
    }
  }, [save.mergedAt, t]);

  const keepCopy = useCallback(async () => {
    setCopy({ kind: 'working' });
    try {
      const made = await sync.saveAsCopy();
      setCopy({ kind: 'done', id: made.id, name: made.name });
    } catch (error) {
      setCopy({ kind: 'failed', message: error instanceof ApiError && error.status === 401 ? SIGN_IN_AGAIN : t('theCopyCouldNot') });
    }
  }, [sync, t]);

  const downloadRecovery = useCallback(() => {
    const entry = sync.getLocalEntry();
    downloadBlob(new Blob([JSON.stringify(entry, null, 2)], { type: 'application/json' }), `${fileSafe(docName)}-my-edits.json`);
  }, [sync, docName]);

  const useLatest = useCallback(() => {
    downloadRecovery();
    sync.discardLocalEdits();
    onReload();
  }, [downloadRecovery, sync, onReload]);

  // The copy holds this tab's edits now: leaving for it drops the local copy of them.
  const openCopy = useCallback(() => sync.discardLocalEdits(), [sync]);

  const takeOver = useCallback(async () => {
    await sync.takeOver();
    onReload();
  }, [sync, onReload]);

  const btnH = isPhone ? 44 : undefined;
  const btnSize = isPhone ? 'md' : 'xs';

  return (
    <>
      <ReadOnlyBanner
        save={save}
        onReload={onReload}
        variant={isPhone ? 'bar' : 'full'}
        busy={copy.kind === 'working'}
        onKeepCopy={() => void keepCopy().then(() => setConflictDismissed(null))}
        onUseLatest={useLatest}
        onDownloadQueued={() => {
          const entry = sync.getQueuedEntry();
          if (entry) downloadBlob(new Blob([JSON.stringify(entry, null, 2)], { type: 'application/json' }), `${fileSafe(docName)}-offline-edits.json`);
        }}
        onDiscardQueued={() => {
          sync.discardQueue();
          onReload();
        }}
      />

      {save.viewOnly && (
        <Alert color="gray" icon={<IconEye size={16} />} py={isPhone ? 6 : undefined} data-testid="view-only-banner">
          <Text size="sm">{t('youCanLookBut')}</Text>
        </Alert>
      )}

      {save.recovery && (
        <Alert color="blue" icon={<IconHistory size={16} />} title={t('weRecoveredEditsFrom')} data-testid="recovery-banner">
          <Stack gap={8}>
            <Text size="sm">{t('theseChangesWereNever')}</Text>
            <Group gap="xs">
              <Button size={btnSize} h={btnH} onClick={() => void sync.restoreRecovered()} data-testid="recovery-restore">
                {t('restore')}
              </Button>
              <Button size={btnSize} h={btnH} variant="default" onClick={() => sync.discardRecovered()} data-testid="recovery-discard">
                {t('discard')}
              </Button>
            </Group>
          </Stack>
        </Alert>
      )}

      {(save.status === 'otherTab' || save.status === 'takenOver') && (
        <Alert color="orange" icon={<IconWindow size={16} />} title={save.status === 'otherTab' ? t('openInAnotherTab') : t('editingMovedToAnother')} data-testid={save.status === 'otherTab' ? 'other-tab-banner' : 'taken-over-banner'}>
          <Group justify="space-between" align="center" wrap="wrap">
            <Text size="sm" style={{ flex: '1 1 280px' }}>
              {save.status === 'otherTab'
                ? t('thisTimelineIsAlready')
                : t('youTookThisTimeline')}
            </Text>
            <Button size={btnSize} h={btnH} onClick={() => void takeOver()} data-testid="take-over">
              {t('editHereInstead')}
            </Button>
          </Group>
        </Alert>
      )}

      <Sheet
        opened={!!save.accountChanged}
        onClose={() => undefined}
        closable={false}
        title={t('differentAccount')}
        isPhone={isPhone}
        testId="account-changed-dialog"
      >
        <Stack gap="sm">
          <Text size="sm" data-testid="account-changed-message">
            {t('youSignedInAs', { previous: save.accountChanged?.previous })}
          </Text>
          <Group gap="xs" grow={isPhone}>
            <Button
              size={isPhone ? 'md' : 'sm'}
              h={btnH}
              onClick={() => {
                downloadRecovery();
                sync.discardLocalEdits();
                onReload();
              }}
              data-testid="account-changed-download"
            >
              {t('downloadThemAsA')}
            </Button>
            <Button
              size={isPhone ? 'md' : 'sm'}
              h={btnH}
              variant="default"
              onClick={() => {
                sync.discardLocalEdits();
                onReload();
              }}
              data-testid="account-changed-discard"
            >
              {t('discardThem')}
            </Button>
          </Group>
        </Stack>
      </Sheet>

      <Sheet opened={save.status === 'authExpired' && !save.accountChanged && signInDismissed !== signInKey} onClose={() => setSignInDismissed(signInKey)} title={t('signInAgainTo')} isPhone={isPhone} testId="sign-in-dialog">
        <SignInForm sync={sync} isPhone={isPhone} />
      </Sheet>

      {isPhone && conflictOpen && (
        // RM.7: on a phone the conflict is a compact bar above the dock, not a modal: the strip stays on screen
        // (it is never squeezed below 160 px) and "Keep my copy" is one tap away.
        <div
          role="alert"
          style={{ display: 'flex', flexDirection: 'column', gap: 6, padding: '8px 12px', background: 'var(--rb-ed-header-bg)', borderTop: '1px solid var(--rb-ed-line)', flex: '0 0 auto' }}
          data-testid="conflict-dialog"
        >
          {copy.kind === 'done' ? (
            <>
              <Text size="sm" data-testid="conflict-copy-done">
                {t('yourEditsAreSaved', { name: copy.name })}
              </Text>
              <Group gap="xs" grow>
                <Button component={Link} href={timelineEditorHref(projectId, copy.id)} size="md" h={44} onClick={openCopy} data-testid="conflict-open-copy">
                  {t('openMyCopy')}
                </Button>
                <Button size="md" h={44} variant="default" onClick={useLatest} data-testid="conflict-use-latest-after-copy">
                  {t('useTheLatestHere')}
                </Button>
              </Group>
            </>
          ) : (
            <>
              <Text size="sm">{t('thisTimelineWasChanged')}</Text>
              {copy.kind === 'failed' && (
                <Text size="sm" c="red" role="alert" data-testid="conflict-copy-failed">
                  {copy.message}
                </Text>
              )}
              <Group gap="xs" grow wrap="nowrap">
                <Button size="md" h={44} loading={copy.kind === 'working'} onClick={() => void keepCopy()} data-testid="conflict-keep-copy">
                  {t('keepMyCopy')}
                </Button>
                <Button size="md" h={44} variant="default" onClick={useLatest} data-testid="conflict-use-latest">
                  {t('useTheLatest')}
                </Button>
              </Group>
            </>
          )}
        </div>
      )}

      <Sheet opened={!isPhone && conflictOpen && conflictDismissed !== conflictKey} onClose={() => setConflictDismissed(conflictKey)} title={t('thisTimelineWasChanged2')} isPhone={isPhone} testId="conflict-dialog">
        <Stack gap="sm">
          {copy.kind === 'done' ? (
            <>
              <Text size="sm" data-testid="conflict-copy-done">
                {t('yourEditsAreSaved2', { name: copy.name })}
              </Text>
              <Group gap="xs" grow={isPhone}>
                <Button component={Link} href={timelineEditorHref(projectId, copy.id)} size={btnSize} h={btnH} onClick={openCopy} data-testid="conflict-open-copy">
                  {t('openMyCopy2')}
                </Button>
                <Button size={btnSize} h={btnH} variant="default" onClick={useLatest} data-testid="conflict-use-latest-after-copy">
                  {t('useTheLatestHere2')}
                </Button>
              </Group>
            </>
          ) : (
            <>
              <Text size="sm">
                {t('someoneElseOrAnother')}
              </Text>
              {copy.kind === 'failed' && (
                <Text size="sm" c="red" role="alert" data-testid="conflict-copy-failed">
                  {copy.message}
                </Text>
              )}
              <Stack gap="xs">
                <Button size={btnSize} h={btnH} loading={copy.kind === 'working'} onClick={() => void keepCopy()} data-testid="conflict-keep-copy">
                  {t('keepMyCopy2')}
                </Button>
                <Text size="xs" c="dimmed" mt={-4}>
                  {t('savesYourEditsAs')}{docName.slice(0, 80)} {t('myEdits')}
                </Text>
                <Button size={btnSize} h={btnH} variant="default" onClick={useLatest} data-testid="conflict-use-latest">
                  {t('useTheLatest2')}
                </Button>
                <Text size="xs" c="dimmed" mt={-4}>
                  {t('loadsWhatIsSaved')}
                </Text>
              </Stack>
            </>
          )}
        </Stack>
      </Sheet>
    </>
  );
}

/** Sign in without leaving the editor: the app's own login endpoint, then the queue flushes. */
function SignInForm({ sync, isPhone }: { sync: SyncClient; isPhone: boolean }) {
  const t = useT('editor.sync.syncNotices');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const h = isPhone ? 44 : undefined;

  const submit = async (event: React.FormEvent) => {
    event.preventDefault();
    setBusy(true);
    setError(null);
    try {
      const result = await login({ email: email.trim(), password });
      if (result.mustChangePassword) {
        setError(t('thisAccountMustChange'));
        return;
      }
      await sync.resumeAfterSignIn();
    } catch (failure) {
      setError(failure instanceof ApiError && failure.status === 401 ? t('thatEmailOrPassword') : t('couldNotSignIn'));
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => void submit(e)} data-testid="sign-in-form">
      <Stack gap="sm">
        <Text size="sm">{t('yourSessionEndedYour')}</Text>
        <TextInput label={t('email')} type="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.currentTarget.value)} required data-testid="sign-in-email" styles={{ input: { height: h } }} />
        <PasswordInput label={t('password')} autoComplete="current-password" value={password} onChange={(e) => setPassword(e.currentTarget.value)} required data-testid="sign-in-password" styles={{ input: { height: h } }} />
        {error && (
          <Text size="sm" c="red" role="alert" data-testid="sign-in-error">
            {error}
          </Text>
        )}
        <Group gap="xs" grow={isPhone}>
          <Button type="submit" size={isPhone ? 'md' : 'sm'} h={h} loading={busy} leftSection={<IconLogin size={16} />} data-testid="sign-in-submit">
            {t('signIn')}
          </Button>
          <Button size={isPhone ? 'md' : 'sm'} h={h} variant="default" onClick={() => void sync.resumeAfterSignIn()} data-testid="sign-in-done">
            {t('iSignedIn')}
          </Button>
        </Group>
      </Stack>
    </form>
  );
}
