'use client';

import React, { useState } from 'react';
import { Button, Group, Select, Text, TextInput, Tooltip } from '@mantine/core';
import { IconDeviceFloppy, IconTrash } from '@tabler/icons-react';
import { createEditorPreset, deleteEditorPreset, type PresetKind } from '@/lib/editing/api/editor-p9';
import { useEditorPresets } from '@/lib/hooks/use-editor-p9';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { PresetManager } from './PresetManager';
import { useT } from '@/i18n';

export interface BuiltInPreset<P> {
  id: string;
  name: string;
  payload: P;
}

export interface PresetPickerProps<P> {
  projectId: string | null | undefined;
  kind: PresetKind;
  /** Presets that ship with the editor (e.g. the mastering targets); listed first, never deleted. */
  builtIns?: readonly BuiltInPreset<P>[];
  /** The value "Save as preset" stores, or null when there is nothing to save. */
  current: P | null;
  readOnly: boolean;
  onApply: (payload: P, name: string) => void;
  testId: string;
}

/**
 * P9: a preset picker for one kind — the built-ins, then the project's saved presets; apply one, save
 * the current value under a name, delete a saved one. A preset is COPIED into the document or render
 * when applied; deleting it later changes nothing that used it. The server validates every payload
 * against the timeline's own catalogues, so a saved preset can never hold an out-of-range value.
 */
export function PresetPicker<P>({ projectId, kind, builtIns = [], current, readOnly, onApply, testId }: PresetPickerProps<P>) {
  const t = useT('editor-misc.presets');
  const { data: saved, mutate } = useEditorPresets<P>(projectId, kind);
  const [name, setName] = useState('');
  const [error, setError] = useState<string | null>(null);
  // W0.6 (UI2-14): choosing a preset to delete asks first; a failed delete says why.
  const [pendingDelete, setPendingDelete] = useState<{ id: string; name: string } | null>(null);
  const [deleteError, setDeleteError] = useState<string | null>(null);
  const [managing, setManaging] = useState(false);
  const options = [
    ...builtIns.map((p) => ({ value: `builtin:${p.id}`, label: p.name })),
    ...(saved ?? []).map((p) => ({ value: p.id, label: p.name })),
  ];
  const find = (value: string): { name: string; payload: P } | null => {
    if (value.startsWith('builtin:')) return builtIns.find((p) => `builtin:${p.id}` === value) ?? null;
    return (saved ?? []).find((p) => p.id === value) ?? null;
  };

  return (
    <Group gap={4} align="end" wrap="wrap" data-testid={testId}>
      <Select
        size="xs"
        label={t('label')}
        placeholder={options.length === 0 ? t('noneYet') : t('applyOne')}
        data={options}
        value={null}
        disabled={readOnly || options.length === 0}
        onChange={(value) => {
          if (!value) return;
          const preset = find(value);
          if (preset) onApply(preset.payload, preset.name);
        }}
        w={170}
        data-testid={`${testId}-select`}
      />
      {projectId && (
        <>
          <Button size="compact-xs" fz="xs" variant="default" onClick={() => setManaging(true)} disabled={(saved ?? []).length === 0} data-testid={`${testId}-manage`}>
            {t('manage')}
          </Button>
          <PresetManager<P> projectId={projectId} kind={kind} opened={managing} onClose={() => setManaging(false)} readOnly={readOnly} onApply={onApply} testId={testId} />
          <TextInput size="xs" label={t('saveAs')} placeholder={t('namePlaceholder')} value={name} onChange={(e) => setName(e.currentTarget.value)} disabled={readOnly || current === null} w={120} error={error ?? undefined} data-testid={`${testId}-name`} />
          <Tooltip label={t('saveTip')}>
            <Button
              size="compact-xs" fz="xs"
              variant="light"
              leftSection={<IconDeviceFloppy size={12} />}
              disabled={readOnly || current === null || name.trim().length === 0}
              onClick={async () => {
                setError(null);
                try {
                  await createEditorPreset(projectId, kind, name.trim(), current);
                  setName('');
                  void mutate();
                } catch (e) {
                  setError(humanizeErrorT(t, e, t('notSaved')));
                }
              }}
              data-testid={`${testId}-save`}
            >
              {t('save')}
            </Button>
          </Tooltip>
          {(saved ?? []).length > 0 && (
            <Select
              size="xs"
              label={t('delete')}
              placeholder={t('deleteOne')}
              data={(saved ?? []).map((p) => ({ value: p.id, label: p.name }))}
              value={null}
              disabled={readOnly || pendingDelete !== null}
              leftSection={<IconTrash size={12} />}
              onChange={(value) => {
                if (!value) return;
                const preset = (saved ?? []).find((p) => p.id === value);
                setDeleteError(null);
                if (preset) setPendingDelete({ id: preset.id, name: preset.name });
              }}
              error={deleteError ?? undefined}
              w={140}
              data-testid={`${testId}-delete`}
            />
          )}
          {pendingDelete && (
            <Group gap={4} align="center" wrap="nowrap" data-testid={`${testId}-delete-prompt`}>
              <Text size="xs">{t('deletePrompt', { name: pendingDelete.name })}</Text>
              <Button
                size="compact-xs" fz="xs"
                color="red"
                onClick={async () => {
                  const target = pendingDelete;
                  setPendingDelete(null);
                  try {
                    await deleteEditorPreset(projectId, target.id);
                    void mutate();
                  } catch (e) {
                    setDeleteError(humanizeErrorT(t, e, t('deleteFailed')));
                  }
                }}
                data-testid={`${testId}-delete-confirm`}
              >
                {t('delete')}
              </Button>
              <Button size="compact-xs" fz="xs" variant="subtle" onClick={() => setPendingDelete(null)} data-testid={`${testId}-delete-cancel`}>
                {t('keep')}
              </Button>
            </Group>
          )}
        </>
      )}
    </Group>
  );
}
