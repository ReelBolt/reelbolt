'use client';

import { useState } from 'react';
import { Alert, Button, Group, List, Modal, Stack, Text, TextInput, Textarea } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import {
  deleteAvatarLibraryItem, reconfirmAvatarLibraryItem, revokeAvatarLibraryConsent, updateAvatarLibraryItem,
} from '@/lib/api/avatar-library';
import type { LibraryLimits } from '@/lib/avatars/library';
import { useAvatarLibraryItem } from '@/lib/hooks/use-avatar-library';
import type { AvatarLibraryRequirements } from '@/lib/types/avatars';
import type { AvatarLibraryItem, DeleteLibraryItemResponse, UpdateLibraryItemRequest } from '@/lib/types/avatar-library';
import { useT } from '@/i18n';
import { useLibraryErrorMessage } from './library-errors';
import { useWorkspaceConfirmation, WorkspaceConfirmationFields } from './WorkspaceConfirmation';

function usePhone(): boolean {
  return Boolean(useMediaQuery('(max-width: 600px)'));
}

interface DialogProps {
  /** The item the dialog is about; null keeps it closed. */
  item: AvatarLibraryItem | null;
  onClose: () => void;
  /** Called after the library changed, so the list is read again. */
  onDone: () => void | Promise<void>;
}

/** Name and description of a library item. Every project that uses it follows. */
export function LibraryRenameModal({ item, limits, onClose, onDone }: DialogProps & { limits: LibraryLimits }) {
  if (!item) return null;
  return <RenameForm key={item.id} item={item} limits={limits} onClose={onClose} onDone={onDone} />;
}

function RenameForm({ item, limits, onClose, onDone }: DialogProps & { item: AvatarLibraryItem; limits: LibraryLimits }) {
  const t = useT('avatars.library.rename');
  const tForm = useT('avatars.form');
  const phone = usePhone();
  const [name, setName] = useState(item.name);
  const [description, setDescription] = useState(item.description ?? '');
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const describe = useLibraryErrorMessage();
  const nameOk = name.trim().length > 0 && name.trim().length <= limits.maxNameLength;

  const save = async () => {
    const patch: UpdateLibraryItemRequest = {};
    if (name.trim() !== item.name) patch.name = name.trim();
    if (description.trim() !== (item.description ?? '')) patch.description = description.trim();
    if (Object.keys(patch).length === 0) { onClose(); return; }
    setSaving(true);
    setError(null);
    try {
      await updateAvatarLibraryItem(item.id, patch);
      notifications.show({ color: 'green', message: t('saved', { name: patch.name ?? item.name }) });
      await onDone();
      onClose();
    } catch (e) {
      setError(describe(e, t('failed')));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title', { name: item.name })} size="md" fullScreen={phone} radius="md">
      <Stack gap="sm">
        <TextInput label={t('name')} required maxLength={limits.maxNameLength} value={name} onChange={(e) => setName(e.currentTarget.value)} />
        <Textarea label={tForm('description')} rows={3} maxLength={limits.maxDescriptionLength} value={description} onChange={(e) => setDescription(e.currentTarget.value)} />
        <Text size="sm" c="dimmed">{t('note')}</Text>
        {error && <Alert color="red" variant="light" data-testid="library-rename-error">{error}</Alert>}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
          <Button onClick={() => void save()} disabled={!nameOk} loading={saving} style={{ minHeight: 44 }}>{t('save')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * Confirming an item again: the workspace statement of its kind, and for a person the 18-or-older tick and the
 * keep-for days (the keep-until date starts again). The files never change here: other files are a new item.
 */
export function LibraryReconfirmModal({ item, library, limits, locales, onClose, onDone }: DialogProps & {
  /** Where the workspace statements come from; null when none could be loaded. */
  library: AvatarLibraryRequirements | null;
  limits: LibraryLimits;
  locales?: readonly string[];
}) {
  if (!item) return null;
  return <ReconfirmForm key={item.id} item={item} library={library} limits={limits} locales={locales} onClose={onClose} onDone={onDone} />;
}

function ReconfirmForm({ item, library, limits, locales, onClose, onDone }: DialogProps & {
  item: AvatarLibraryItem;
  library: AvatarLibraryRequirements | null;
  limits: LibraryLimits;
  locales?: readonly string[];
}) {
  const t = useT('avatars.library.reconfirm');
  const tForm = useT('avatars.form');
  const phone = usePhone();
  const isObject = item.subjectKind === 'Object';
  const confirmation = useWorkspaceConfirmation({ kind: isObject ? 'Object' : 'Person', library, limits, locales });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const describe = useLibraryErrorMessage();

  const submit = async () => {
    if (!confirmation.ready || saving) return;
    setSaving(true);
    setError(null);
    try {
      await reconfirmAvatarLibraryItem(item.id, confirmation.body);
      notifications.show({ color: 'green', message: t('done', { name: item.name }) });
      await onDone();
      onClose();
    } catch (e) {
      setError(confirmation.acceptRefusal(e) ? null : describe(e, t('failed')));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title', { name: item.name })} size="lg" fullScreen={phone} radius="md">
      <Stack gap="sm" data-testid="library-reconfirm-form">
        <Text size="sm">{isObject ? t('introObject') : t('introPerson')}</Text>
        <Text size="sm" c="dimmed">{t('filesNote')}</Text>
        <WorkspaceConfirmationFields confirmation={confirmation} limits={limits} />
        {error && <Alert color="red" variant="light" data-testid="library-reconfirm-error">{error}</Alert>}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
          <Button onClick={() => void submit()} disabled={!confirmation.ready} loading={saving} style={{ minHeight: 44 }} data-testid="library-reconfirm-submit">
            {t('confirm')}
          </Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/** Withdrawing a person's consent for the whole workspace: unusable in every project at once, deleted after the grace period. */
export function LibraryRevokeModal({ item, graceDays, onClose, onDone }: DialogProps & { graceDays: number }) {
  const t = useT('avatars.library.revoke');
  const tForm = useT('avatars.form');
  const phone = usePhone();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const describe = useLibraryErrorMessage();
  if (!item) return null;

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      await revokeAvatarLibraryConsent(item.id);
      notifications.show({ color: 'yellow', message: t('done', { name: item.name }) });
      await onDone();
      onClose();
    } catch (e) {
      setError(describe(e, t('failed')));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title', { name: item.name })} size="md" fullScreen={phone} radius="md">
      <Stack gap="sm">
        <Text size="sm">{t('body', { name: item.name, days: graceDays })}</Text>
        <Text size="sm" c="dimmed">{t('outputsStay')}</Text>
        <Text size="sm" c="dimmed">{t('cannotUndo')}</Text>
        {error && <Alert color="red" variant="light" data-testid="library-revoke-error">{error}</Alert>}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
          <Button color="orange" onClick={() => void confirm()} loading={busy} style={{ minHeight: 44 }}>{t('confirm')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * Deleting a library item: it goes from the library and from every project that uses it. Says how many projects
 * that is (read fresh), then shows the report of what was actually removed.
 */
export function LibraryDeleteModal({ item, onClose, onDone }: DialogProps) {
  if (!item) return null;
  return <DeleteDialog key={item.id} item={item} onClose={onClose} onDone={onDone} />;
}

function DeleteDialog({ item, onClose, onDone }: DialogProps & { item: AvatarLibraryItem }) {
  const t = useT('avatars.library.delete');
  const tForm = useT('avatars.form');
  const phone = usePhone();
  const [report, setReport] = useState<DeleteLibraryItemResponse | null>(null);
  // The list can be a little old: the number of projects is read again before a deletion that reaches them all.
  const { data: fresh } = useAvatarLibraryItem(report ? null : item.id);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const describe = useLibraryErrorMessage();
  const projects = fresh?.importCount ?? item.importCount ?? 0;

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      const result = await deleteAvatarLibraryItem(item.id);
      setReport({
        imports: { deleted: result?.imports?.deleted ?? 0, failed: result?.imports?.failed ?? 0 },
        objects: { deleted: result?.objects?.deleted ?? 0, failed: result?.objects?.failed ?? 0 },
      });
      await onDone();
    } catch (e) {
      setError(describe(e, t('failed')));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title', { name: item.name })} size="md" fullScreen={phone} radius="md">
      {report ? (
        <Stack gap="sm" data-testid="library-delete-report">
          <Text size="sm" fw={600}>{t('reportTitle', { name: item.name })}</Text>
          <List size="sm" spacing={4}>
            <List.Item>{report.imports.deleted > 0 ? t('reportImports', { count: report.imports.deleted }) : t('reportNoImports')}</List.Item>
            {report.imports.failed > 0 && <List.Item c="red">{t('reportImportsFailed', { count: report.imports.failed })}</List.Item>}
            <List.Item>{t('reportObjects', { count: report.objects.deleted })}</List.Item>
            {report.objects.failed > 0 && <List.Item c="red">{t('reportObjectsFailed', { count: report.objects.failed })}</List.Item>}
          </List>
          <Group justify="flex-end">
            <Button onClick={onClose} style={{ minHeight: 44 }}>{t('close')}</Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="sm">
          <Text size="sm">{t('intro', { name: item.name })}</Text>
          <Text size="sm" data-testid="library-delete-projects">{projects > 0 ? t('projects', { count: projects }) : t('noProjects')}</Text>
          <Text size="sm" c="dimmed">{t('outputsStay')}</Text>
          <Text size="sm" c="dimmed">{t('cannotUndo')}</Text>
          {error && <Alert color="red" variant="light" data-testid="library-delete-error">{error}</Alert>}
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
            <Button color="red" onClick={() => void confirm()} loading={busy} style={{ minHeight: 44 }}>{t('confirm')}</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
