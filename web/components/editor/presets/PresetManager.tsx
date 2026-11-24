'use client';

import React, { useState } from 'react';
import { Alert, Button, Group, Modal, Stack, Text, TextInput } from '@mantine/core';
import { createEditorPreset, deleteEditorPreset, type EditorPreset, type PresetKind } from '@/lib/editing/api/editor-p9';
import { useEditorPresets } from '@/lib/hooks/use-editor-p9';
import { describePreset, uniqueCopyName } from '@/lib/editing/preset-manage';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useT } from '@/i18n';
import { describePresetMsg } from '@/lib/editing/summary-msg';

export interface PresetManagerProps<P> {
  projectId: string;
  kind: PresetKind;
  opened: boolean;
  onClose: () => void;
  readOnly: boolean;
  onApply: (payload: P, name: string) => void;
  testId: string;
}

type Pending = { id: string; action: 'delete' | 'rename'; value: string };

/**
 * R12.8: manage the project's saved presets of one kind — apply (with a plain preview of what it does),
 * rename, duplicate, delete (asks first). The server re-validates every payload on create, so a copy
 * or a rename can never store a value the catalogues refuse.
 */
export function PresetManager<P>({ projectId, kind, opened, onClose, readOnly, onApply, testId }: PresetManagerProps<P>) {
  const t = useT('editor-misc.presets');
  const { data: saved, mutate } = useEditorPresets<P>(projectId, kind);
  const [pending, setPending] = useState<Pending | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  const list: EditorPreset<P>[] = saved ?? [];
  const names = list.map((p) => p.name);

  const run = async (job: () => Promise<void>, fallback: string) => {
    setBusy(true);
    setError(null);
    try {
      await job();
      await mutate();
      setPending(null);
    } catch (e) {
      setError(humanizeErrorT(t, e, fallback));
    } finally {
      setBusy(false);
    }
  };

  return (
    <Modal opened={opened} onClose={onClose} title={t('manageTitle')} centered data-testid={`${testId}-manager`}>
      <Stack gap="xs">
        {error && <Alert color="red" variant="light" data-testid={`${testId}-manager-error`}>{error}</Alert>}
        {list.length === 0 && <Text size="sm" c="dimmed">{t('none')}</Text>}
        {list.map((preset) => (
          <Stack key={preset.id} gap={4} p="xs" style={{ border: '1px solid var(--mantine-color-default-border)', borderRadius: 6 }} data-testid={`${testId}-row-${preset.name}`}>
            {pending?.id === preset.id && pending.action === 'rename' ? (
              <Group gap="xs" wrap="nowrap">
                <TextInput size="xs" value={pending.value} maxLength={80} onChange={(e) => setPending({ ...pending, value: e.currentTarget.value })} style={{ flex: 1 }} aria-label={t('newName')} data-testid={`${testId}-rename-input`} />
                <Button
                  size="xs" fz="xs" loading={busy}
                  disabled={pending.value.trim().length === 0 || names.includes(pending.value.trim())}
                  onClick={() => void run(async () => {
                    await createEditorPreset(projectId, kind, pending.value.trim(), preset.payload);
                    await deleteEditorPreset(projectId, preset.id);
                  }, t('renameFailed'))}
                  data-testid={`${testId}-rename-confirm`}
                >
                  {t('rename')}
                </Button>
                <Button size="xs" fz="xs" variant="subtle" onClick={() => setPending(null)}>{t('cancel')}</Button>
              </Group>
            ) : (
              <Text size="sm" fw={600}>{preset.name}</Text>
            )}
            <Text size="xs" c="dimmed" data-testid={`${testId}-preview-${preset.name}`}>{t.msg(describePresetMsg(kind, preset.payload))}</Text>
            {pending?.id === preset.id && pending.action === 'delete' ? (
              <Group gap="xs" wrap="nowrap">
                <Text size="xs" style={{ flex: 1 }}>{t('deletePrompt', { name: preset.name })}</Text>
                <Button size="xs" fz="xs" color="red" loading={busy} onClick={() => void run(() => deleteEditorPreset(projectId, preset.id), t('deleteFailed'))} data-testid={`${testId}-manage-delete-confirm`}>{t('delete')}</Button>
                <Button size="xs" fz="xs" variant="default" onClick={() => setPending(null)}>{t('keep')}</Button>
              </Group>
            ) : (
              <Group gap="xs">
                <Button size="xs" fz="xs" variant="light" mih={44} disabled={readOnly} onClick={() => { onApply(preset.payload, preset.name); onClose(); }} data-testid={`${testId}-manage-apply-${preset.name}`}>{t('apply')}</Button>
                <Button size="xs" fz="xs" variant="default" mih={44} disabled={readOnly} onClick={() => setPending({ id: preset.id, action: 'rename', value: preset.name })} data-testid={`${testId}-manage-rename-${preset.name}`}>{t('rename')}</Button>
                <Button size="xs" fz="xs" variant="default" mih={44} disabled={readOnly || busy} onClick={() => void run(async () => { await createEditorPreset(projectId, kind, uniqueCopyName(preset.name, names), preset.payload); }, t('duplicateFailed'))} data-testid={`${testId}-manage-duplicate-${preset.name}`}>{t('duplicate')}</Button>
                <Button size="xs" fz="xs" variant="subtle" color="red" mih={44} disabled={readOnly} onClick={() => setPending({ id: preset.id, action: 'delete', value: '' })} data-testid={`${testId}-manage-delete-${preset.name}`}>{t('delete')}</Button>
              </Group>
            )}
          </Stack>
        ))}
        <Group justify="flex-end"><Button size="xs" fz="xs" variant="default" onClick={onClose}>{t('close')}</Button></Group>
      </Stack>
    </Modal>
  );
}
