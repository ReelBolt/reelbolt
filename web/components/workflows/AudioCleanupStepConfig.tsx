'use client';

import { Stack, Group, Select, TextInput, NumberInput, Switch, Text, Fieldset } from '@mantine/core';
import { EgressRunNote } from './EgressRunNote';
import { MediaPickerField } from '@/components/shared/media/MediaPickerField';
import type {
  AudioCleanupOutputKind,
  AudioCleanupStepConfig as AudioCleanupStepConfigValue,
  AudioCleanupTarget,
  VideoSourceKind,
  VideoSourceRef,
} from '@/lib/types/workflow';
import { useT, msg } from '@/i18n';
import type { StepData } from './WorkflowStepList';
import {
  useFd, AskAssistantButton, assistantPrompt, stepOrderOptions, ProviderSelect, EgressWarning, ModeHint,
} from './config-kit';

/** The four ways a recording can be named; `GeneratedClips` and `BriefFiles` (many files) are not offered. */
const SOURCE_KIND_VALUES: VideoSourceKind[] = ['ProjectFile', 'PreviousStepOutput', 'StepOutput', 'StepResult'];

/** Mirrors `AudioCleanupTarget` in AudioCleanupStepConfig.cs. */
const TARGET_VALUES: AudioCleanupTarget[] = ['VoiceBasic', 'Hum', 'Wind', 'Hiss', 'Echo', 'IsolateVoice', 'RemoveMusic'];

/** The last two run on a hosted AI service: they cost credits and send the audio away. */
const AI_TARGETS: ReadonlySet<AudioCleanupTarget> = new Set<AudioCleanupTarget>(['IsolateVoice', 'RemoveMusic']);

const OUTPUT_KIND_VALUES: AudioCleanupOutputKind[] = ['Auto', 'Audio', 'VideoWithCleanAudio'];

/** True when the target is one of the hosted AI fixes. */
export function isAiAudioCleanupTarget(target: AudioCleanupTarget | string | null | undefined): boolean {
  return AI_TARGETS.has(target as AudioCleanupTarget);
}

/** An uploaded file is the only source that exists before any step has run. */
export const AUDIO_CLEANUP_FIRST_STEP_SOURCE: VideoSourceRef = { kind: 'ProjectFile', projectFileId: null, stepOrder: null };

/** Default config for a freshly-added AudioCleanup step: the free voice fix, no consent, listed in Files. */
export function createDefaultAudioCleanupStepConfig(): AudioCleanupStepConfigValue {
  return {
    target: 'VoiceBasic',
    source: { kind: 'PreviousStepOutput', projectFileId: null, stepOrder: null },
    providerId: null,
    allowSourceMediaEgress: false,
    registerProjectFile: true,
    outputKind: 'Auto',
    maxSourceMinutes: null,
  };
}

interface AudioCleanupStepConfigProps {
  config: AudioCleanupStepConfigValue;
  onChange: (config: AudioCleanupStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
  projectId?: string;
}

/**
 * Configuration form for an AudioCleanup step. Mirrors the backend `AudioCleanupStepConfig` record field-for-field; the
 * server's `Validate()` is the authority. The first five fixes are free and local; the last two use a hosted service, so
 * only they show the provider and the consent switch (and switching back to a local fix clears both).
 */
export function AudioCleanupStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0, projectId,
}: AudioCleanupStepConfigProps) {
  const t = useT('wfSteps.audioCleanup');
  const fd = useFd();
  const patch = (updates: Partial<AudioCleanupStepConfigValue>) => onChange({ ...config, ...updates });

  const isAi = isAiAudioCleanupTarget(config.target);
  const egressAllowed = config.allowSourceMediaEgress ?? false;
  const source = config.source ?? AUDIO_CLEANUP_FIRST_STEP_SOURCE;
  const priorStepOptions = stepOrderOptions(allSteps, currentStepIndex, 'before', t.msg);
  const hasPriorSteps = priorStepOptions.length > 0;
  const kind: VideoSourceKind = source.kind;
  const sourceKindOptions = SOURCE_KIND_VALUES.filter(
    (v) => hasPriorSteps || v === 'ProjectFile' || v === 'StepResult',
  ).map((v) => ({ value: v, label: t(`sourceKind.${v}`) }));
  // A stored kind this build does not list (an integer-coded or newer value) stays visible.
  const kindData = sourceKindOptions.some((o) => o.value === kind)
    ? sourceKindOptions
    : [...sourceKindOptions, { value: kind, label: String(kind) }];

  // The provider, the consent and the length cap only mean something for a hosted fix; the server refuses them
  // otherwise, so moving to a free fix resets them rather than carrying a value that would fail validation.
  const changeTarget = (target: AudioCleanupTarget) => {
    if (target === config.target) return;
    onChange({
      ...config,
      target,
      ...(isAiAudioCleanupTarget(target) ? {} : { providerId: null, allowSourceMediaEgress: false, maxSourceMinutes: null }),
    });
  };

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'AudioCleanup', msg('wfSteps.prompts.audioCleanup'))} />
      </Group>

      <Fieldset legend={t('source.legend')}>
        <Stack gap="xs">
          <Select
            {...fd('config.source.kind')}
            label={t('source.takeFrom')}
            size="xs"
            data={kindData}
            value={kind}
            onChange={(v) => v && patch({ source: { kind: v as VideoSourceKind, projectFileId: null, stepOrder: null, stepResultId: null } })}
            allowDeselect={false}
          />
          {kind === 'ProjectFile' && (
            <MediaPickerField
              {...fd('config.source.projectFileId')}
              projectId={projectId}
              kind="videoAudio"
              label={t('source.file')}
              value={source.projectFileId ? [source.projectFileId] : []}
              onChange={(picked) => patch({ source: { kind: 'ProjectFile', projectFileId: picked[0]?.id ?? null, stepOrder: null, stepResultId: null } })}
            />
          )}
          {kind === 'StepOutput' && (
            <Select
              {...fd('config.source.stepOrder')}
              label={t('source.step')}
              size="xs"
              data={priorStepOptions}
              value={source.stepOrder != null ? String(source.stepOrder) : null}
              onChange={(v) => patch({ source: { kind: 'StepOutput', projectFileId: null, stepOrder: v ? Number(v) : null, stepResultId: null } })}
              placeholder={t('source.stepPlaceholder')}
            />
          )}
          {kind === 'StepResult' && (
            <TextInput
              {...fd('config.source.stepResultId')}
              label={t('source.stepResult.label')}
              description={t('source.stepResult.description')}
              size="xs"
              value={source.stepResultId ?? ''}
              onChange={(e) => patch({ source: { kind: 'StepResult', projectFileId: null, stepOrder: null, stepResultId: e.target.value.trim() || null } })}
            />
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('what.legend')}>
        <Stack gap="xs">
          <Select
            {...fd('config.target')}
            label={t('what.target')}
            size="xs"
            data={TARGET_VALUES.map((v) => ({ value: v, label: t(`target.${v}`) }))}
            value={config.target}
            onChange={(v) => v && changeTarget(v as AudioCleanupTarget)}
            allowDeselect={false}
          />
          <ModeHint>{isAi ? t('what.creditsHint') : t('what.freeHint')}</ModeHint>
          {isAi && (
            <>
              <ProviderSelect
                capability="AudioIsolation"
                label={t('provider.label')}
                value={config.providerId ?? null}
                onChange={(providerId) => patch({ providerId })}
                nullMeans={t('provider.nullMeans')}
                wrapperProps={fd('config.providerId').wrapperProps}
              />
              <EgressWarning>{t('consent.warning')}</EgressWarning>
              <Switch
                {...fd('config.allowSourceMediaEgress')}
                label={t('consent.label')}
                description={t('consent.description')}
                checked={egressAllowed}
                onChange={(e) => patch({ allowSourceMediaEgress: e.currentTarget.checked })}
              />
              <EgressRunNote />
              {!egressAllowed && (
                <Text size="xs" c="orange">
                  {t('consent.blocked')}
                </Text>
              )}
              <NumberInput
                {...fd('config.maxSourceMinutes')}
                label={t('maxMinutes.label')}
                description={t('maxMinutes.description')}
                size="xs"
                min={1}
                allowDecimal={false}
                value={config.maxSourceMinutes ?? ''}
                onChange={(v) => patch({ maxSourceMinutes: typeof v === 'number' ? Math.max(1, Math.round(v)) : null })}
              />
            </>
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('output.legend')}>
        <Stack gap="xs">
          <Select
            {...fd('config.outputKind')}
            label={t('output.kind')}
            size="xs"
            data={OUTPUT_KIND_VALUES.map((v) => ({ value: v, label: t(`outputKind.${v}`) }))}
            value={config.outputKind}
            onChange={(v) => v && patch({ outputKind: v as AudioCleanupOutputKind })}
            allowDeselect={false}
          />
          <Switch
            {...fd('config.registerProjectFile')}
            label={t('output.save.label')}
            description={t('output.save.description')}
            checked={config.registerProjectFile}
            onChange={(e) => patch({ registerProjectFile: e.currentTarget.checked })}
          />
        </Stack>
      </Fieldset>
    </Stack>
  );
}
