'use client';

import React, { useState } from 'react';
import { Button, Group, Stack, Text, TextInput } from '@mantine/core';
import { useT } from '@/i18n';
import {
  MAX_MY_PRESETS,
  MY_PRESET_NAME_MAX,
  addMyPreset,
  presetNameProblem,
  readMyPresets,
  removeMyPreset,
  renameMyPreset,
  sameFormSettings,
  writeMyPresets,
  type MyRenderPreset,
  type PresetStorage,
  type RenderFormSettings,
} from './render-presets';
import classes from './Deliver.module.css';

export interface MyPresetsProps {
  /** The settings on screen: what "Save" stores and what "in use" is compared with. */
  current: RenderFormSettings;
  onApply: (settings: RenderFormSettings) => void;
  /** The name of a format, for the one-line account of what a preset sets. */
  formatLabel: (preset: RenderFormSettings['preset']) => string;
  /** Tests pass their own storage; the page uses the browser's. */
  storage?: PresetStorage | null;
}

/**
 * "My presets": the settings a person saves under a name, kept on this device (never on the server). Each can be
 * applied, renamed and deleted; delete asks first. When the browser will not keep anything the section says so in
 * a sentence and the rest of the page carries on.
 */
export function MyPresets({ current, onApply, formatLabel, storage }: MyPresetsProps) {
  const t = useT('editor.deliver.myPresets');
  const ts = useT('editor.deliver.renderSettings');
  const [store, setStore] = useState(() => (storage === undefined ? readMyPresets() : readMyPresets(storage)));
  const [saving, setSaving] = useState(false);
  const [name, setName] = useState('');
  const [renaming, setRenaming] = useState<{ id: string; name: string } | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);
  const [writeFailed, setWriteFailed] = useState(false);
  const presets = store.presets;

  const commit = (next: MyRenderPreset[]) => {
    const ok = storage === undefined ? writeMyPresets(next) : writeMyPresets(next, storage);
    setWriteFailed(!ok);
    // A refused write still shows the change for this visit, with the sentence that says it will not last.
    setStore({ presets: next, available: store.available });
  };

  const problemText = (problem: ReturnType<typeof presetNameProblem>): string | undefined =>
    problem === 'empty' ? t('nameEmpty') : problem === 'taken' ? t('nameTaken') : problem === 'full' ? t('listFull', { max: MAX_MY_PRESETS }) : undefined;

  const saveProblem = presetNameProblem(name, presets);
  const renameProblem = renaming ? presetNameProblem(renaming.name, presets, renaming.id) : null;
  const full = presets.length >= MAX_MY_PRESETS;

  const save = () => {
    if (saveProblem !== null) return;
    commit(addMyPreset(presets, name, current));
    setName('');
    setSaving(false);
  };

  const rename = () => {
    if (!renaming || renameProblem !== null) return;
    commit(renameMyPreset(presets, renaming.id, renaming.name));
    setRenaming(null);
  };

  const summary = (settings: RenderFormSettings): string =>
    [
      formatLabel(settings.preset),
      settings.preset === 'AudioOnly' ? null : settings.custom ? t('ownEncoder') : ts(`qualitySteps.${settings.quality}`),
      settings.preset !== 'AudioOnly' && settings.rateControl !== 'Quality' ? t('bitrate', { mbps: Math.round(settings.videoKbps / 100) / 10 }) : null,
      settings.normalise ? t('loudness', { lufs: settings.targetLufs }) : null,
    ]
      .filter((part): part is string => part !== null)
      .join(' · ');

  return (
    <Stack gap={6} data-testid="my-presets">
      <Text size="xs" fw={600}>
        {t('title')}
      </Text>
      {!store.available && (
        <Text size="xs" c="dimmed" role="status" data-testid="my-presets-unavailable">
          {t('unavailable')}
        </Text>
      )}
      {store.available && presets.length === 0 && !saving && (
        <Text size="xs" c="dimmed" data-testid="my-presets-empty">
          {t('empty')}
        </Text>
      )}
      {presets.map((preset) => {
        const inUse = sameFormSettings(preset.settings, current);
        if (renaming?.id === preset.id) {
          return (
            <Stack key={preset.id} gap={4} data-testid={`my-preset-rename-${preset.id}`}>
              <TextInput
                size="sm"
                label={t('newName')}
                value={renaming.name}
                maxLength={MY_PRESET_NAME_MAX}
                onChange={(e) => setRenaming({ id: preset.id, name: e.currentTarget.value })}
                onKeyDown={(e) => {
                  if (e.key === 'Enter') rename();
                  if (e.key === 'Escape') setRenaming(null);
                }}
                error={renameProblem && renaming.name !== '' ? problemText(renameProblem) : undefined}
                autoFocus
                data-testid="my-preset-rename-input"
              />
              <Group gap="xs">
                <Button size="sm" fz="xs" className={classes.touchTarget} onClick={rename} disabled={renameProblem !== null} data-testid="my-preset-rename-save">
                  {t('saveName')}
                </Button>
                <Button size="sm" fz="xs" variant="default" className={classes.touchTarget} onClick={() => setRenaming(null)}>
                  {t('cancel')}
                </Button>
              </Group>
            </Stack>
          );
        }
        if (deleting === preset.id) {
          return (
            <Stack key={preset.id} gap={4} data-testid={`my-preset-delete-${preset.id}`}>
              <Text size="sm">{t('deleteQuestion', { name: preset.name })}</Text>
              <Group gap="xs">
                <Button
                  size="sm"
                  fz="xs"
                  color="red"
                  className={classes.touchTarget}
                  onClick={() => {
                    commit(removeMyPreset(presets, preset.id));
                    setDeleting(null);
                  }}
                  data-testid="my-preset-delete-confirm"
                >
                  {t('delete')}
                </Button>
                <Button size="sm" fz="xs" variant="default" className={classes.touchTarget} onClick={() => setDeleting(null)}>
                  {t('keep')}
                </Button>
              </Group>
            </Stack>
          );
        }
        return (
          <div key={preset.id} className={classes.myPresetRow} data-in-use={inUse} data-testid={`my-preset-${preset.id}`}>
            <Stack gap={0} style={{ minWidth: 0, flex: '1 1 120px' }}>
              <Text size="sm" fw={600} truncate title={preset.name}>
                {preset.name}
              </Text>
              <Text size="xs" c="dimmed" data-testid="my-preset-summary">
                {inUse ? t('inUse', { summary: summary(preset.settings) }) : summary(preset.settings)}
              </Text>
            </Stack>
            <Group gap={4} wrap="wrap">
              <Button size="sm" fz="xs" variant="light" className={classes.touchTarget} onClick={() => onApply(preset.settings)} aria-label={t('applyNamed', { name: preset.name })} data-testid="my-preset-apply">
                {t('apply')}
              </Button>
              <Button size="sm" fz="xs" variant="subtle" color="gray" className={classes.touchTarget} onClick={() => setRenaming({ id: preset.id, name: preset.name })} aria-label={t('renameNamed', { name: preset.name })} data-testid="my-preset-rename">
                {t('rename')}
              </Button>
              <Button size="sm" fz="xs" variant="subtle" color="red" className={classes.touchTarget} onClick={() => setDeleting(preset.id)} aria-label={t('deleteNamed', { name: preset.name })} data-testid="my-preset-delete">
                {t('delete')}
              </Button>
            </Group>
          </div>
        );
      })}
      {saving ? (
        <Stack gap={4} data-testid="my-preset-save-form">
          <TextInput
            size="sm"
            label={t('nameLabel')}
            description={t('whatIsSaved')}
            value={name}
            maxLength={MY_PRESET_NAME_MAX}
            onChange={(e) => setName(e.currentTarget.value)}
            onKeyDown={(e) => {
              if (e.key === 'Enter') save();
              if (e.key === 'Escape') setSaving(false);
            }}
            error={saveProblem && name !== '' ? problemText(saveProblem) : undefined}
            autoFocus
            data-testid="my-preset-name"
          />
          <Group gap="xs">
            <Button size="sm" fz="xs" className={classes.touchTarget} onClick={save} disabled={saveProblem !== null} data-testid="my-preset-save-confirm">
              {t('save')}
            </Button>
            <Button size="sm" fz="xs" variant="default" className={classes.touchTarget} onClick={() => setSaving(false)}>
              {t('cancel')}
            </Button>
          </Group>
        </Stack>
      ) : (
        store.available && (
          <Group gap="xs" align="center">
            <Button size="sm" fz="xs" variant="default" className={classes.touchTarget} onClick={() => setSaving(true)} disabled={full} data-testid="my-preset-save">
              {t('saveCurrent')}
            </Button>
            {full && (
              <Text size="xs" c="dimmed">
                {t('listFull', { max: MAX_MY_PRESETS })}
              </Text>
            )}
          </Group>
        )
      )}
      {writeFailed && (
        <Text size="xs" c="orange.9" role="alert" data-testid="my-presets-write-failed">
          {t('writeFailed')}
        </Text>
      )}
      {store.available && (
        <Text size="xs" c="dimmed">
          {t('deviceOnly')}
        </Text>
      )}
    </Stack>
  );
}
