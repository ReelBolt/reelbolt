'use client';

import React, { useMemo, useState } from 'react';
import { Button, Checkbox, Group, Modal, SegmentedControl, Select, Stack, TagsInput, Text, TextInput } from '@mantine/core';
import { humanizeErrorT } from '@/components/editor/shell/humanize-error-t';
import { useLocale, useT } from '@/i18n';
import { ruleText } from './rule-text';
import type { T } from '@/i18n/translator';
import {
  cleanRule, countSmartMatches, isEmptySmartRule, type AnnotationIndex,
} from '@/lib/editing/media/pool-browse';
import { MEDIA_COLOR_LABELS, type MediaColorLabel, type MediaKind, type MediaPoolItem, type MediaSmartFilter } from '@/lib/editing/media/types';

export interface SmartBinEditorProps {
  opened: boolean;
  mode: 'create' | 'edit';
  /** Starting name and rule (edit) - ignored for create, which starts empty. */
  initialName: string;
  initialRule: MediaSmartFilter;
  items: readonly MediaPoolItem[];
  annotations: AnnotationIndex;
  onClose: () => void;
  /** Saves the cleaned rule. Rejecting keeps the dialog open and shows the message. */
  onSave: (name: string, rule: MediaSmartFilter) => Promise<void>;
}

type Tri = 'any' | 'yes' | 'no';
const toTri = (v: boolean | null | undefined): Tri => (v === true ? 'yes' : v === false ? 'no' : 'any');
const fromTri = (v: string): boolean | null => (v === 'yes' ? true : v === 'no' ? false : null);

const triData = (t: T) => [{ value: 'any', label: t('any') }, { value: 'yes', label: t('yes') }, { value: 'no', label: t('no') }];

const kindOptions = (t: T): ReadonlyArray<{ kind: MediaKind; label: string }> => [
  { kind: 'video', label: t('kindVideo') },
  { kind: 'audio', label: t('kindAudio') },
  { kind: 'image', label: t('kindImage') },
];

/**
 * R05.6: create or edit a smart bin's rule in plain words. A live line says what the rule means and how
 * many sources match right now; an empty rule is refused (it would be "All media"). Editing a bin shows the
 * same fields with its saved rule, so a rule is changed in place instead of being deleted and re-typed.
 */
export function SmartBinEditor(props: SmartBinEditorProps) {
  const t = useT('editor-media.smartBin');
  const { opened, mode, onClose } = props;
  // The form mounts when the dialog opens (Mantine unmounts a closed modal's body), so every open starts fresh.
  return (
    <Modal opened={opened} onClose={onClose} title={mode === 'create' ? t('newTitle') : t('ruleTitle')} radius="md" centered data-testid="smart-bin-editor">
      <SmartBinForm {...props} />
    </Modal>
  );
}

function SmartBinForm({ mode, initialName, initialRule, items, annotations, onClose, onSave }: SmartBinEditorProps) {
  const t = useT('editor-media.smartBin');
  const locale = useLocale();
  const tc = useT('editor-media.color');
  const [name, setName] = useState(mode === 'create' ? '' : initialName);
  const [rule, setRule] = useState<MediaSmartFilter>(mode === 'create' ? {} : initialRule);
  const [error, setError] = useState<string | null>(null);
  const [saving, setSaving] = useState(false);

  const matches = useMemo(() => countSmartMatches(rule, items, annotations), [rule, items, annotations]);
  const empty = isEmptySmartRule(rule);

  const save = async () => {
    if (name.trim().length === 0) return setError(t('needName'));
    if (empty) return setError(t('needRule'));
    setSaving(true);
    setError(null);
    try {
      await onSave(name.trim(), cleanRule(rule));
    } catch (e) {
      setError(humanizeErrorT(t, e, t('saveFailed')));
      setSaving(false);
    }
  };

  const kinds = rule.kinds ?? [];
  return (
      <Stack gap="sm">
        <TextInput label={t('name')} value={name} onChange={(e) => setName(e.currentTarget.value)} maxLength={120} data-testid="smart-bin-name-input" />
        <Text size="sm" c="dimmed">
          {t('intro')}
        </Text>
        <Checkbox.Group
          label={t('kindOfMedia')}
          value={kinds}
          onChange={(v) => setRule({ ...rule, kinds: v.length > 0 ? (v as MediaKind[]) : null })}
        >
          <Group gap="md" mt={4}>
            {kindOptions(t).map((o) => (
              <Checkbox key={o.kind} value={o.kind} label={o.label} data-testid={`smart-bin-kind-${o.kind}`} />
            ))}
          </Group>
        </Checkbox.Group>
        <Group grow align="flex-start">
          <Stack gap={2}>
            <Text size="sm" fw={500}>{t('starred')}</Text>
            <SegmentedControl size="xs" value={toTri(rule.favourite)} onChange={(v) => setRule({ ...rule, favourite: fromTri(v) })} data={triData(t)} data-testid="smart-bin-starred" />
          </Stack>
          <Stack gap={2}>
            <Text size="sm" fw={500}>{t('flagged')}</Text>
            <SegmentedControl size="xs" value={toTri(rule.flag)} onChange={(v) => setRule({ ...rule, flag: fromTri(v) })} data={triData(t)} data-testid="smart-bin-flagged" />
          </Stack>
        </Group>
        <Select
          label={t('colourLabel')}
          placeholder={t('anyColour')}
          clearable
          data={MEDIA_COLOR_LABELS.map((c) => ({ value: c, label: tc(c) }))}
          value={rule.colorLabel ?? null}
          onChange={(v) => setRule({ ...rule, colorLabel: (v as MediaColorLabel | null) ?? null })}
          comboboxProps={{ withinPortal: false }}
        />
        <TagsInput
          label={t('taggedAll')}
          description={t('taggedHint')}
          splitChars={[',']}
          value={rule.keywords ?? []}
          onChange={(v) => setRule({ ...rule, keywords: v.length > 0 ? v : null })}
          maxTags={32}
          data-testid="smart-bin-keywords"
        />
        <TextInput
          label={t('nameContains')}
          value={rule.nameContains ?? ''}
          onChange={(e) => setRule({ ...rule, nameContains: e.currentTarget.value.length > 0 ? e.currentTarget.value : null })}
          maxLength={120}
          data-testid="smart-bin-name-contains"
        />
        <Text size="sm" fw={600} data-testid="smart-bin-summary" data-matches={matches}>
          {ruleText(t, locale, rule)}
          {empty ? '' : t('matchesNow', { count: matches })}
        </Text>
        {error ? (
          <Text size="sm" c="red" role="alert" data-testid="smart-bin-error">
            {error}
          </Text>
        ) : null}
        <Group justify="flex-end" gap="xs">
          <Button variant="default" onClick={onClose}>
            {t('cancel')}
          </Button>
          <Button loading={saving} onClick={() => void save()} data-testid="smart-bin-save">
            {mode === 'create' ? t('create') : t('save')}
          </Button>
        </Group>
      </Stack>
  );
}
