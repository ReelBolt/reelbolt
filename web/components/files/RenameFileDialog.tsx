'use client';

import { useState } from 'react';
import { Button, Group, Modal, Stack, TextInput } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { ApiError } from '@/lib/api/client';
import { useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { msg, type Msg } from '@/i18n/msg';
import { renameFile } from '@/lib/api/files';
import type { ProjectFile } from '@/lib/types/project';

/** Mirrors the server's rules (contract C2) so most mistakes are caught before a round trip. */
export function validateFileName(name: string): Msg | null {
  const trimmed = name.trim();
  if (!trimmed) return msg('files.rename.enterName');
  if (/[\\/]/.test(trimmed)) return msg('files.rename.noSlash');
  if (trimmed.length > 255) return msg('files.rename.tooLong', { length: trimmed.length });
  return null;
}

/** Plain-language message for a failed rename: 409 is a clash, 400 a rejected name. */
export function renameErrorMessage(e: unknown, name: string): Msg | null {
  if (e instanceof ApiError && e.status === 409) {
    return msg('files.rename.clash', { name });
  }
  if (e instanceof ApiError && e.status === 400) {
    const detail = e.errors[0] ?? e.message;
    return detail ? msg('files.rename.notAllowedDetail', { detail }) : msg('files.rename.notAllowed');
  }
  return null;
}

/**
 * Rename one file in place. Only its display name changes — workflows that reference the file keep
 * working. Validation errors (400) and name clashes (409) stay inline so the customer can fix the
 * name without losing what they typed; anything else is a notification.
 */
export function RenameFileDialog({ projectId, file, onClose, onRenamed }: {
  projectId: string;
  file: ProjectFile | null;
  onClose: () => void;
  onRenamed: (updated: ProjectFile) => void | Promise<unknown>;
}) {
  const t = useT('files.rename');
  return (
    <Modal opened={!!file} onClose={onClose} centered radius="lg" title={t('title')}>
      {/* Keyed so each opening starts from that file's current name. */}
      {file && <RenameForm key={file.id} projectId={projectId} file={file} onClose={onClose} onRenamed={onRenamed} />}
    </Modal>
  );
}

function RenameForm({ projectId, file, onClose, onRenamed }: {
  projectId: string;
  file: ProjectFile;
  onClose: () => void;
  onRenamed: (updated: ProjectFile) => void | Promise<unknown>;
}) {
  const t = useT('files.rename');
  const tAll = useT();
  const [name, setName] = useState(file.originalFileName);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);

  const submit = async () => {
    const trimmed = name.trim();
    const invalid = validateFileName(trimmed);
    if (invalid) { setError(t.msg(invalid)); return; }
    if (trimmed === file.originalFileName) { onClose(); return; }
    setBusy(true);
    try {
      const updated = await renameFile(projectId, file.id, trimmed);
      await onRenamed(updated);
      notifications.show({ message: t('renamed', { name: updated?.originalFileName ?? trimmed }), color: 'green' });
      onClose();
    } catch (e) {
      const inline = renameErrorMessage(e, trimmed);
      if (inline) setError(t.msg(inline));
      else notifications.show({ title: t('failedTitle'), message: describeError(tAll, e, t('failedFallback')), color: 'red' });
    } finally {
      setBusy(false);
    }
  };

  return (
    <form onSubmit={(e) => { e.preventDefault(); void submit(); }}>
      <Stack>
        <TextInput
          label={t('newName')}
          data-autofocus
          value={name}
          error={error}
          onChange={(e) => { setName(e.currentTarget.value); setError(null); }}
          description={t('hint')}
          onFocus={(e) => {
            // Select the name without its extension, so typing replaces just the name.
            const dot = e.currentTarget.value.lastIndexOf('.');
            if (dot > 0) e.currentTarget.setSelectionRange(0, dot);
          }}
        />
        <Group justify="flex-end">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{t('cancel')}</Button>
          <Button type="submit" loading={busy} disabled={!name.trim()} style={{ minHeight: 44 }}>{t('submit')}</Button>
        </Group>
      </Stack>
    </form>
  );
}
