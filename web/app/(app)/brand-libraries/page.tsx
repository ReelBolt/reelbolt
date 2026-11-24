'use client';

import { describeError } from '@/lib/api/describe-error';
import { useState } from 'react';
import {
  Alert,
  Badge,
  Button,
  Card,
  Center,
  Group,
  Loader,
  Modal,
  Stack,
  Text,
  Textarea,
  TextInput,
  Title,
} from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { PageHeader } from '@/components/shared/PageHeader';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import {
  approveDesignLibraryVersion,
  createDesignLibrary,
  deleteDesignLibrary,
  renameDesignLibrary,
} from '@/lib/api/design-system';
import { useAuth } from '@/lib/hooks/use-auth';
import { useDesignLibraries, useDesignLibraryVersions } from '@/lib/hooks/use-design-libraries';
import type { DesignDocument, DesignLibrarySummary } from '@/lib/types/design-system';
import mobile from '@/components/design-system/mobile.module.css';
import { useT, type T } from '@/i18n';
import { useVersionStateLabel } from '@/components/design-system/useVersionStateLabel';

function errorMessage(tAll: T, err: unknown, fallback: string): string {
  return describeError(tAll, err, fallback);
}

/**
 * The organization's brand libraries (SPEC §16.1, P4-06). A library is the org-level analogue of a
 * project's design system, addressable by its own id and readable by every member. Billing cannot
 * read libraries at all, a Member is read-only, and only Owner/Admin may create, rename, delete or
 * approve — the server re-reads the role on every call, so this gate only avoids buttons that could
 * not work.
 */
export default function BrandLibrariesPage() {
  const t = useT('brandLibraries.page');
  const tAll = useT();
  const versionStateLabel = useVersionStateLabel();
  const { user, isOrgAdmin } = useAuth();
  const { data: libraries, error, isLoading, mutate } = useDesignLibraries();

  const isBilling = user?.orgRole === 'Billing';
  const canWrite = isOrgAdmin;

  const [createOpened, setCreateOpened] = useState(false);
  const [newName, setNewName] = useState('');
  const [newDocument, setNewDocument] = useState('');
  const [documentError, setDocumentError] = useState<string | null>(null);
  const [creating, setCreating] = useState(false);

  const [renaming, setRenaming] = useState<DesignLibrarySummary | null>(null);
  const [renameName, setRenameName] = useState('');
  const [deleting, setDeleting] = useState<DesignLibrarySummary | null>(null);
  const [busy, setBusy] = useState(false);
  const [approving, setApproving] = useState<{ libraryId: string; versionId: string; versionNumber: number } | null>(null);

  const [historyId, setHistoryId] = useState<string | null>(null);
  const {
    data: versions,
    isLoading: versionsLoading,
    mutate: mutateVersions,
  } = useDesignLibraryVersions(historyId);

  async function handleCreate() {
    const name = newName.trim();
    if (!name) return;

    let document: DesignDocument | undefined;
    if (newDocument.trim()) {
      let parsed: unknown;
      try {
        parsed = JSON.parse(newDocument);
      } catch {
        setDocumentError(t('invalidJson'));
        return;
      }
      if (typeof parsed !== 'object' || parsed === null || Array.isArray(parsed)) {
        setDocumentError(t('notObject'));
        return;
      }
      document = parsed as DesignDocument;
    }

    setDocumentError(null);
    setCreating(true);
    try {
      await createDesignLibrary(name, document);
      await mutate();
      setCreateOpened(false);
      setNewName('');
      setNewDocument('');
      notifications.show({ title: t('createdTitle'), message: t('createdMessage', { name }), color: 'green' });
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: errorMessage(tAll, err, t('createFailed')),
        color: 'red',
      });
    } finally {
      setCreating(false);
    }
  }

  async function handleRename() {
    if (!renaming) return;
    const name = renameName.trim();
    if (!name || name === renaming.name) {
      setRenaming(null);
      return;
    }
    setBusy(true);
    try {
      await renameDesignLibrary(renaming.id, name);
      await mutate();
      setRenaming(null);
      notifications.show({ title: t('renamedTitle'), message: t('renamedMessage'), color: 'green' });
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: errorMessage(tAll, err, t('renameFailed')),
        color: 'red',
      });
    } finally {
      setBusy(false);
    }
  }

  async function handleDelete() {
    if (!deleting) return;
    setBusy(true);
    try {
      await deleteDesignLibrary(deleting.id);
      if (historyId === deleting.id) setHistoryId(null);
      await mutate();
      notifications.show({ title: t('deletedTitle'), message: t('deletedMessage', { name: deleting.name }), color: 'green' });
      setDeleting(null);
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: errorMessage(tAll, err, t('deleteFailed')),
        color: 'red',
      });
    } finally {
      setBusy(false);
    }
  }

  async function handleApprove(libraryId: string, versionId: string) {
    setBusy(true);
    try {
      await approveDesignLibraryVersion(libraryId, versionId);
      setApproving(null);
      await Promise.all([mutate(), mutateVersions()]);
      notifications.show({
        title: t('approvedTitle'),
        message: t('approvedMessage'),
        color: 'green',
      });
    } catch (err: unknown) {
      notifications.show({
        title: t('errorTitle'),
        message: errorMessage(tAll, err, t('approveFailed')),
        color: 'red',
      });
    } finally {
      setBusy(false);
    }
  }

  if (isBilling) {
    return (
      <>
        <PageHeader
          title={t('title')}
          breadcrumbs={[{ label: t('breadcrumbOrg') }, { label: t('title') }]}
        />
        <Alert color="gray" title={t('notAvailable')} data-testid="brand-libraries-billing-unavailable">
          {t('billingUnavailable')}
        </Alert>
      </>
    );
  }

  const rows = libraries ?? [];
  const currentVersions = historyId ? versions ?? [] : [];

  return (
    <>
      <PageHeader
        title={t('title')}
        breadcrumbs={[{ label: t('breadcrumbOrg') }, { label: t('title') }]}
      >
        {canWrite && <Button onClick={() => setCreateOpened(true)}>{t('newLibrary')}</Button>}
      </PageHeader>

      <Stack gap="lg">
        {isLoading && !libraries ? (
          <Center h={160}>
            <Loader size="sm" />
          </Center>
        ) : error ? (
          <Alert color="red" title={t('loadFailedTitle')} data-testid="brand-libraries-error">
            {errorMessage(tAll, error, t('loadFailedFallback'))}
          </Alert>
        ) : rows.length === 0 ? (
          <Card withBorder padding="lg" data-testid="brand-libraries-empty">
            <Text fw={650}>{t('emptyTitle')}</Text>
            <Text size="sm" c="dimmed" mt="xs">
              {canWrite
                ? t('emptyWrite')
                : t('emptyReadOnly')}
            </Text>
          </Card>
        ) : (
          rows.map((library) => (
            <Card key={library.id} withBorder padding="lg" data-testid={`brand-library-${library.id}`}>
              <Group justify="space-between" align="center" wrap="wrap" gap="sm">
                <Group gap="sm" align="center">
                  <Title order={4} className={mobile.breakText}>{library.name}</Title>
                  <Badge variant="light" color="violet">
                    {library.designSystem.currentVersion
                      ? t('versionBadge', { number: library.designSystem.currentVersion.versionNumber })
                      : t('noApproved')}
                  </Badge>
                </Group>
                <Group gap="xs">
                  <Button
                    size="xs"
                    variant="light"
                    onClick={() => setHistoryId(historyId === library.id ? null : library.id)}
                  >
                    {historyId === library.id ? t('hideVersions') : t('versions')}
                  </Button>
                  {canWrite && (
                    <>
                      <Button
                        size="xs"
                        variant="light"
                        onClick={() => {
                          setRenaming(library);
                          setRenameName(library.name);
                        }}
                      >
                        {t('rename')}
                      </Button>
                      <Button size="xs" variant="light" color="red" onClick={() => setDeleting(library)}>
                        {t('delete')}
                      </Button>
                    </>
                  )}
                </Group>
              </Group>

              {historyId === library.id && (
                <Stack gap="xs" mt="md" data-testid={`library-versions-${library.id}`}>
                  {versionsLoading ? (
                    <Loader size="xs" />
                  ) : currentVersions.length === 0 ? (
                    <Text size="sm" c="dimmed">
                      {t('noVersions')}
                    </Text>
                  ) : (
                    currentVersions.map((version) => (
                      <Group key={version.id} justify="space-between" align="center">
                        <Text size="sm">
                          {t('versionLine', { number: version.versionNumber, state: versionStateLabel(version.state) })}
                        </Text>
                        {canWrite && version.state === 'Draft' && (
                          <Button
                            size="xs"
                            variant="light"
                            onClick={() =>
                              setApproving({ libraryId: library.id, versionId: version.id, versionNumber: version.versionNumber })
                            }
                          >
                            {t('approve')}
                          </Button>
                        )}
                      </Group>
                    ))
                  )}
                  {!canWrite && (
                    <Text size="xs" c="dimmed">
                      {t('approveOnlyAdmin')}
                    </Text>
                  )}
                </Stack>
              )}
            </Card>
          ))
        )}

        {!canWrite && !isLoading && !error && rows.length > 0 && (
          <Text size="sm" c="dimmed">
            {t('readOnly')}
          </Text>
        )}
      </Stack>

      <Modal
        opened={createOpened}
        onClose={() => setCreateOpened(false)}
        title={t('createModal.title')}
        centered
      >
        <Stack gap="sm">
          <TextInput
            label={t('createModal.name')}
            value={newName}
            maxLength={200}
            onChange={(event) => setNewName(event.currentTarget.value)}
          />
          <Textarea
            label={t('createModal.draftLabel')}
            description={t('createModal.draftDescription')}
            minRows={4}
            value={newDocument}
            error={documentError}
            onChange={(event) => {
              setNewDocument(event.currentTarget.value);
              setDocumentError(null);
            }}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setCreateOpened(false)}>
              {t('createModal.cancel')}
            </Button>
            <Button onClick={handleCreate} loading={creating} disabled={!newName.trim()}>
              {t('createModal.submit')}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={renaming !== null}
        onClose={() => setRenaming(null)}
        title={t('renameModal.title')}
        centered
      >
        <Stack gap="sm">
          <TextInput
            label={t('renameModal.name')}
            value={renameName}
            maxLength={200}
            onChange={(event) => setRenameName(event.currentTarget.value)}
          />
          <Group justify="flex-end">
            <Button variant="default" onClick={() => setRenaming(null)}>
              {t('renameModal.cancel')}
            </Button>
            <Button
              onClick={() => void handleRename()}
              loading={busy}
              disabled={!renameName.trim() || renameName.trim() === renaming?.name}
            >
              {t('renameModal.save')}
            </Button>
          </Group>
        </Stack>
      </Modal>

      <Modal
        opened={approving !== null}
        onClose={() => setApproving(null)}
        title={t('approveModal.title', { number: approving?.versionNumber ?? '' })}
        centered
        size="sm"
      >
        <Text size="sm" mb="lg">
          {t('approveModal.body')}
        </Text>
        <Group justify="flex-end">
          <Button variant="default" onClick={() => setApproving(null)}>
            {t('approveModal.cancel')}
          </Button>
          <Button
            loading={busy}
            onClick={() => approving && void handleApprove(approving.libraryId, approving.versionId)}
          >
            {t('approveModal.confirm', { number: approving?.versionNumber ?? '' })}
          </Button>
        </Group>
      </Modal>

      <ConfirmModal
        opened={deleting !== null}
        onClose={() => setDeleting(null)}
        onConfirm={() => void handleDelete()}
        title={t('deleteDialog.title')}
        message={t('deleteDialog.message', { name: deleting?.name ?? t('deleteDialog.thisLibrary') })}
        confirmLabel={t('deleteDialog.confirm')}
        loading={busy}
      />
    </>
  );
}
