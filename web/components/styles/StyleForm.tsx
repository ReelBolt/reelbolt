'use client';

import { useState } from 'react';
import { Button, Checkbox, Group, Select, SimpleGrid, Stack, Text, TextInput, Textarea } from '@mantine/core';
import type { SaveEditingStyleRequest, StyleConfig } from '@/lib/types/styles';
import { STYLE_AXES, STYLE_NOTES_MAX, isEmptyStyle } from './style-options';
import { useT } from '@/i18n';

interface StyleFormProps {
  /** Starting values: an existing style to edit, or a preset to start from. */
  initial?: { name?: string; description?: string | null; projectId?: string | null; config?: StyleConfig };
  /** When set, offers "Only use in this project". */
  projectId?: string | null;
  submitLabel?: string;
  busy?: boolean;
  onSubmit: (request: SaveEditingStyleRequest) => void;
  onCancel: () => void;
}

/** Create or edit a saved editing style: a name, one optional choice per axis, and short notes. */
export function StyleForm({ initial, projectId, submitLabel, busy, onSubmit, onCancel }: StyleFormProps) {
  const t = useT('styles.form');
  const tAll = useT();
  const [name, setName] = useState(initial?.name ?? '');
  const [description, setDescription] = useState(initial?.description ?? '');
  const [config, setConfig] = useState<StyleConfig>(initial?.config ?? {});
  const [projectOnly, setProjectOnly] = useState(Boolean(initial?.projectId));

  const empty = isEmptyStyle(config);
  const missingName = name.trim().length === 0;

  const submit = () => {
    if (empty || missingName) return;
    onSubmit({
      name: name.trim(),
      description: description.trim() || null,
      projectId: projectOnly ? (initial?.projectId ?? projectId ?? null) : null,
      config: { ...config, notes: config.notes?.trim() || null },
    });
  };

  return (
    <Stack gap="md">
      <TextInput label={t('name')} required maxLength={100} value={name} onChange={(e) => setName(e.currentTarget.value)} data-autofocus />
      <TextInput
        label={t('description')}
        description={t('descriptionHint')}
        maxLength={500}
        value={description}
        onChange={(e) => setDescription(e.currentTarget.value)}
      />
      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        {STYLE_AXES.map((axis) => (
          <Select
            key={axis.key}
            label={tAll.msg(axis.label)}
            description={tAll.msg(axis.description)}
            placeholder={t('noPreference')}
            clearable
            data={axis.options.map((o) => ({ value: o.value, label: tAll.msg(o.label) }))}
            value={(config[axis.key] as string | null | undefined) ?? null}
            onChange={(v) => setConfig((c) => ({ ...c, [axis.key]: v ?? null }))}
            comboboxProps={{ withinPortal: true }}
          />
        ))}
      </SimpleGrid>
      <Textarea
        label={t('notes')}
        description={t('notesHint')}
        maxLength={STYLE_NOTES_MAX}
        autosize
        minRows={2}
        maxRows={6}
        value={config.notes ?? ''}
        onChange={(e) => {
          const notes = e.currentTarget.value;
          setConfig((c) => ({ ...c, notes }));
        }}
      />
      {(projectId || initial?.projectId) && (
        <Checkbox
          label={t('projectOnly')}
          checked={projectOnly}
          onChange={(e) => setProjectOnly(e.currentTarget.checked)}
        />
      )}
      {empty && (
        <Text size="xs" c="dimmed">
          {t('pickOne')}
        </Text>
      )}
      <Group justify="flex-end">
        <Button variant="default" onClick={onCancel} disabled={busy}>
          {t('cancel')}
        </Button>
        <Button onClick={submit} loading={busy} disabled={empty || missingName}>
          {submitLabel ?? t('submit')}
        </Button>
      </Group>
    </Stack>
  );
}
