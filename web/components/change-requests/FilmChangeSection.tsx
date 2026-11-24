'use client';

import type { Ref } from 'react';
import { Alert, Chip, Group, Radio, Select, Stack, Text, Textarea } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import { REVISION_INSTRUCTION_MAX_LENGTH, type RevisionCutPolicy, type RevisionHistory } from '@/lib/types/revisions';
import { useFormat, useT } from '@/i18n';
import { useCrT } from './change-request-copy';
import type { FilmDraft } from './change-set';
import { filmPlan, sceneTimeRange, versionOptionLabel } from './film-helpers';

/** The counter appears once the instruction gets this close to the limit. */
const COUNTER_THRESHOLD = REVISION_INSTRUCTION_MAX_LENGTH - 400;

export interface FilmChangeSectionProps {
  history: RevisionHistory;
  film: FilmDraft;
  onChange: (patch: Partial<FilmDraft>) => void;
  /** False while the run this page shows is still going (used to word the "not yet" note). */
  runFinished: boolean;
  /** The video is changed together with other steps: scenes, cut and start version do not apply. */
  combined: boolean;
  /** Errors the server tied to the video's own target. */
  errors?: string[];
  textareaRef: Ref<HTMLTextAreaElement>;
  /** Why a new note cannot be added (the request is full); the field is disabled while it is set and the note is empty. */
  lockedReason?: string | null;
}

/**
 * "The video itself": the owner says in plain words what should change in the finished video, optionally narrows it to some
 * scenes and picks how the cut is treated. The note counts as one change in the set; the options apply only when the video
 * is changed on its own.
 */
export function FilmChangeSection({ history, film, onChange, runFinished, combined, errors = [], textareaRef, lockedReason = null }: FilmChangeSectionProps) {
  const t = useT('revisions.panel');
  const tc = useCrT();
  const f = useFormat();
  const aloneOnly = tc('panel.aloneOnly');

  const unavailable = history.availability === 'Unavailable';
  const disabled = unavailable || !history.canRevise;
  const optionsOff = disabled || combined;
  const playableVersions = history.versions.filter((v) => v.hasRender);
  const startFromId = film.fromVersionId ?? history.headVersionId ?? history.currentVersionId;
  const startFrom = history.versions.find((v) => v.id === startFromId) ?? null;
  // Scene times are measured on the version the change applies to; fall back to the newest known frame rate.
  const currentVersion = startFrom
    ?? history.versions.find((v) => v.id === history.currentVersionId)
    ?? history.versions[history.versions.length - 1];
  const fps = currentVersion?.fps ?? null;
  const { replay, chooseCut } = filmPlan(history, film.cutPolicy);

  const disabledNote = unavailable
    ? (history.unavailableReason || t('cannotChange'))
    : !history.canRevise
      ? (!runFinished ? t('stillRunning') : (history.unavailableReason || t('stillRunning')))
      : null;

  return (
    <Stack gap="md" data-testid="request-changes">
      {history.availability === 'Recoverable' && (
        <Alert color="blue" variant="light" icon={<IconInfoCircle size={16} />}>
          {t('recoverable')}
        </Alert>
      )}

      {disabledNote && (
        <Alert color={unavailable ? 'gray' : 'blue'} variant="light" icon={<IconInfoCircle size={16} />} data-testid="request-changes-disabled">
          {disabledNote}
        </Alert>
      )}

      <Textarea
        ref={textareaRef}
        label={t('instruction')}
        placeholder={t('instructionPlaceholder')}
        autosize
        minRows={3}
        maxRows={10}
        maxLength={REVISION_INSTRUCTION_MAX_LENGTH}
        value={film.instruction}
        onChange={(e) => onChange({ instruction: e.currentTarget.value })}
        disabled={disabled || !!lockedReason}
        error={errors.length > 0 ? errors.join(' ') : undefined}
        description={film.instruction.length >= COUNTER_THRESHOLD
          ? t('counter', { count: f.number(film.instruction.length), max: f.number(REVISION_INSTRUCTION_MAX_LENGTH) })
          : undefined}
      />

      {lockedReason && <Text size="sm" c="dimmed" data-testid="request-changes-locked">{lockedReason}</Text>}
      {combined && (
        <Text size="sm" c="dimmed" data-testid="request-changes-alone-note">{aloneOnly}</Text>
      )}

      {history.scenes.length > 0 && (
        <div role="group" aria-labelledby="request-changes-scenes">
          <Text size="sm" fw={500} id="request-changes-scenes" mb={6}>{t('scenes')}</Text>
          <Chip.Group multiple value={film.scenes} onChange={(scenes) => onChange({ scenes })}>
            <Group gap="xs" wrap="wrap">
              {history.scenes.map((scene) => {
                const range = sceneTimeRange(scene, fps);
                return (
                  <Chip
                    key={scene.name}
                    value={scene.name}
                    disabled={optionsOff}
                    title={[range, scene.description].filter(Boolean).join(' · ') || undefined}
                    styles={{ label: { height: 'auto', minHeight: 44, whiteSpace: 'normal', maxWidth: '100%' } }}
                  >
                    {scene.name}
                    {range && <Text span size="xs" c="dimmed" ml={6}>{range}</Text>}
                  </Chip>
                );
              })}
            </Group>
          </Chip.Group>
        </div>
      )}

      {film.fromVersionId == null && history.headVersionId && history.headVersionId !== history.currentVersionId
        && history.headVersionNumber != null && (
        <Text size="sm" c="dimmed" data-testid="request-changes-based-on">
          {t('basedOnLatest', { number: history.headVersionNumber })}
        </Text>
      )}

      {playableVersions.length > 1 && (
        <Select
          label={t('startFrom')}
          data={playableVersions.map((v) => ({ value: v.id, label: versionOptionLabel(v, t) }))}
          value={startFrom?.hasRender ? startFrom.id : null}
          onChange={(value) => onChange({ fromVersionId: value })}
          allowDeselect={false}
          disabled={optionsOff}
          comboboxProps={{ withinPortal: true }}
        />
      )}

      {replay && chooseCut && (
        <Radio.Group
          value={film.cutPolicy}
          onChange={(value) => onChange({ cutPolicy: (value === 'ReEdit' ? 'ReEdit' : 'KeepCut') as RevisionCutPolicy })}
          label={t('edit')}
          description={t('editHint')}
        >
          <Stack gap="xs" mt="xs">
            <Radio value="KeepCut" disabled={optionsOff} label={t('keepCut')} description={t('keepCutHint')} />
            <Radio value="ReEdit" disabled={optionsOff} label={t('reEdit')} description={t('reEditHint')} />
          </Stack>
        </Radio.Group>
      )}
      {!replay && history.replay?.reason && (
        <Text size="sm" c="dimmed" data-testid="request-changes-replay-note">{history.replay.reason}</Text>
      )}
    </Stack>
  );
}
