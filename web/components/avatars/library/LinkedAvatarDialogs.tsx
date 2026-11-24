'use client';

import { useMemo, useState } from 'react';
import { Alert, Button, Group, Modal, Select, Stack, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { deleteAvatar, updateAvatar } from '@/lib/api/avatars';
import { useProjectVoices } from '@/lib/hooks/use-voices';
import type { ProjectAvatar } from '@/lib/types/avatars';
import { useT } from '@/i18n';
import { useLibraryErrorMessage } from './library-errors';

const NO_VOICE = '__none__';

function usePhone(): boolean {
  return Boolean(useMediaQuery('(max-width: 600px)'));
}

/**
 * The one thing a project may set on an import from the workspace library: which of the project's voices the
 * person speaks with. Its name, files and consent follow the library item.
 */
export function LinkedVoiceModal({ projectId, avatar, onClose, onSaved }: {
  projectId: string;
  avatar: ProjectAvatar | null;
  onClose: () => void;
  onSaved: () => void | Promise<void>;
}) {
  if (!avatar) return null;
  return <VoiceForm key={avatar.id} projectId={projectId} avatar={avatar} onClose={onClose} onSaved={onSaved} />;
}

function VoiceForm({ projectId, avatar, onClose, onSaved }: {
  projectId: string;
  avatar: ProjectAvatar;
  onClose: () => void;
  onSaved: () => void | Promise<void>;
}) {
  const t = useT('avatars.library.voice');
  const tForm = useT('avatars.form');
  const phone = usePhone();
  const { data: voices } = useProjectVoices(projectId);
  const [voiceId, setVoiceId] = useState<string>(avatar.projectVoiceId ?? NO_VOICE);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const describe = useLibraryErrorMessage();

  const options = useMemo(() => {
    const out = [{ value: NO_VOICE, label: tForm('voiceNone') }];
    for (const v of voices ?? []) {
      if (v.selectable || v.id === avatar.projectVoiceId) out.push({ value: v.id, label: v.name });
    }
    return out;
  }, [voices, avatar.projectVoiceId, tForm]);

  const save = async () => {
    const current = avatar.projectVoiceId ?? NO_VOICE;
    if (voiceId === current) { onClose(); return; }
    setSaving(true);
    setError(null);
    try {
      // Only the voice link is sent: the server refuses every other field of an import.
      await updateAvatar(projectId, avatar.id, voiceId === NO_VOICE ? { clearProjectVoice: true } : { projectVoiceId: voiceId });
      notifications.show({ color: 'green', message: t('saved', { name: avatar.name }) });
      await onSaved();
    } catch (e) {
      setError(describe(e, t('failed')));
    } finally {
      setSaving(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title', { name: avatar.name })} size="md" fullScreen={phone} radius="md">
      <Stack gap="sm">
        <Text size="sm" c="dimmed">{t('hint')}</Text>
        <Select label={tForm('voice')} description={tForm('voiceHint')} data={options} value={voiceId} onChange={(v) => setVoiceId(v ?? NO_VOICE)} allowDeselect={false} />
        {error && <Alert color="red" variant="light" data-testid="linked-voice-error">{error}</Alert>}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
          <Button onClick={() => void save()} loading={saving} style={{ minHeight: 44 }}>{t('save')}</Button>
        </Group>
      </Stack>
    </Modal>
  );
}

/**
 * Removing an import from this project: the copies of its files made for the project go, the library item and
 * every other project that uses it are left alone, and it can be imported again later.
 */
export function RemoveImportModal({ projectId, avatar, onClose, onDone }: {
  projectId: string;
  avatar: ProjectAvatar | null;
  onClose: () => void;
  onDone: () => void | Promise<void>;
}) {
  if (!avatar) return null;
  return <RemoveDialog key={avatar.id} projectId={projectId} avatar={avatar} onClose={onClose} onDone={onDone} />;
}

function RemoveDialog({ projectId, avatar, onClose, onDone }: {
  projectId: string;
  avatar: ProjectAvatar;
  onClose: () => void;
  onDone: () => void | Promise<void>;
}) {
  const t = useT('avatars.library.remove');
  const tForm = useT('avatars.form');
  const phone = usePhone();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string | null>(null);
  /** Copied files the server could not delete: said plainly, so the person can delete them in Files. */
  const [leftover, setLeftover] = useState<number | null>(null);
  const describe = useLibraryErrorMessage();

  const confirm = async () => {
    setBusy(true);
    setError(null);
    try {
      const report = await deleteAvatar(projectId, avatar.id);
      await onDone();
      const failed = report?.references?.failed ?? 0;
      if (failed > 0) {
        setLeftover(failed);
      } else {
        notifications.show({ color: 'green', message: t('done', { name: avatar.name }) });
        onClose();
      }
    } catch (e) {
      setError(describe(e, t('failed')));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened onClose={onClose} title={t('title', { name: avatar.name })} size="md" fullScreen={phone} radius="md">
      {leftover !== null ? (
        <Stack gap="sm" data-testid="linked-remove-report">
          <Text size="sm" fw={600}>{t('done', { name: avatar.name })}</Text>
          <Text size="sm" c="red">{t('filesFailed', { count: leftover })}</Text>
          <Group justify="flex-end">
            <Button onClick={onClose} style={{ minHeight: 44 }}>{t('close')}</Button>
          </Group>
        </Stack>
      ) : (
        <Stack gap="sm">
          <Text size="sm">{t('body', { name: avatar.name })}</Text>
          <Text size="sm" c="dimmed">{t('stays')}</Text>
          <Text size="sm" c="dimmed">{t('outputsStay')}</Text>
          {error && <Alert color="red" variant="light" data-testid="linked-remove-error">{error}</Alert>}
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={onClose} style={{ minHeight: 44 }}>{tForm('cancel')}</Button>
            <Button color="red" onClick={() => void confirm()} loading={busy} style={{ minHeight: 44 }}>{t('confirm')}</Button>
          </Group>
        </Stack>
      )}
    </Modal>
  );
}
