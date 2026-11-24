'use client';

import {
  Stack, Group, TextInput, NumberInput, Switch, Text, SimpleGrid, Fieldset, Textarea, SegmentedControl,
} from '@mantine/core';
import { useT, msg } from '@/i18n';
import type {
  AudioGenerateStepConfig as AudioGenerateStepConfigValue,
  AudioGenerationKind,
} from '@/lib/types/workflow';
import {
  useFd, AskAssistantButton, assistantPrompt, AdvancedSection, ProviderSelect, CostWarning,
} from './config-kit';

/** The server's bounds (AudioGenerateStepConfig.Validate): music 3-300 s, sound effects 0.5-30 s, 1-4 takes. */
export const AUDIO_DURATION_RANGE: Record<AudioGenerationKind, { min: number; max: number; fallback: number }> = {
  Music: { min: 3, max: 300, fallback: 30 },
  SoundEffect: { min: 0.5, max: 30, fallback: 5 },
};
const MAX_PROMPT_LENGTH = 4000;
const MAX_TAKES = 4;

const KIND_VALUES: AudioGenerationKind[] = ['Music', 'SoundEffect'];

/** Builds the default config for a freshly-added AudioGenerate step. Mirrors the C# record defaults (1 take, saved to Files). */
export function createDefaultAudioGenerateStepConfig(): AudioGenerateStepConfigValue {
  return {
    kind: 'Music',
    prompt: '',
    durationSec: AUDIO_DURATION_RANGE.Music.fallback,
    instrumental: null,
    compositionPlanJson: null,
    loop: null,
    promptInfluence: null,
    providerId: null,
    model: null,
    takes: 1,
    maxSpendCredits: null,
    registerProjectFile: true,
    fileNamePrefix: null,
  };
}

interface AudioGenerateStepConfigProps {
  config: AudioGenerateStepConfigValue;
  onChange: (config: AudioGenerateStepConfigValue) => void;
  currentStepIndex?: number;
}

/**
 * Configuration form for an AudioGenerate step: generates music or a sound effect from a description (paid, billed in
 * credits). Mirrors the backend `AudioGenerateStepConfig` record field-for-field; the server's `Validate()` is the
 * authority and its messages are shown against the field they name.
 */
export function AudioGenerateStepConfig({ config, onChange, currentStepIndex = 0 }: AudioGenerateStepConfigProps) {
  const t = useT('wfSteps.audioGenerate');
  const fd = useFd();
  const patch = (updates: Partial<AudioGenerateStepConfigValue>) => onChange({ ...config, ...updates });
  const isMusic = config.kind === 'Music';
  const range = AUDIO_DURATION_RANGE[config.kind] ?? AUDIO_DURATION_RANGE.Music;
  const planMode = isMusic && Boolean(config.compositionPlanJson?.trim());

  // Music and sound effects use different provider rows and different options, so switching kind starts
  // those fields afresh instead of carrying a value the server would refuse ("Loop is for sound effects only").
  const changeKind = (kind: AudioGenerationKind) => {
    if (kind === config.kind) return;
    const next = AUDIO_DURATION_RANGE[kind];
    onChange({
      ...config,
      kind,
      providerId: null,
      model: null,
      instrumental: null,
      compositionPlanJson: null,
      loop: null,
      promptInfluence: null,
      durationSec: config.durationSec >= next.min && config.durationSec <= next.max ? config.durationSec : next.fallback,
    });
  };

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'AudioGenerate', msg('wfSteps.prompts.audioGenerate'))} />
      </Group>

      <Fieldset legend={t('what.legend')}>
        <Stack gap="xs">
          <div
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.kind').wrapperProps}>
            <Text size="xs" fw={500} mb={4}>{t('what.kind')}</Text>
            <SegmentedControl
              aria-label={t('what.kindAria')}
              fullWidth
              size="xs"
              data={KIND_VALUES.map((v) => ({ value: v, label: t(`kind.${v}`) }))}
              value={config.kind}
              onChange={(v) => changeKind(v as AudioGenerationKind)}
            />
          </div>
          {!planMode && (
            <Textarea
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.prompt')}
              label={t('what.description.label')}
              description={isMusic ? t('what.description.music') : t('what.description.soundEffect')}
              size="xs"
              rows={3}
              maxLength={MAX_PROMPT_LENGTH}
              value={config.prompt}
              onChange={(e) => patch({ prompt: e.target.value })}
            />
          )}
          {planMode ? (
            <Text size="xs" c="dimmed">{t('what.planNote')}</Text>
          ) : (
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.durationSec')}
              label={t('what.length.label')}
              description={t('what.length.description', { min: range.min, max: range.max })}
              size="xs"
              min={range.min}
              max={range.max}
              step={isMusic ? 1 : 0.5}
              decimalScale={2}
              value={config.durationSec}
              onChange={(v) => patch({ durationSec: typeof v === 'number' ? Math.min(range.max, Math.max(range.min, v)) : range.fallback })}
            />
          )}
          {isMusic && !planMode && (
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.instrumental')}
              label={t('what.instrumental')}
              checked={config.instrumental === true}
              onChange={(e) => patch({ instrumental: e.currentTarget.checked ? true : null })}
            />
          )}
          {!isMusic && (
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <Switch
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.loop')}
                label={t('what.loop.label')}
                description={t('what.loop.description')}
                checked={config.loop === true}
                onChange={(e) => patch({ loop: e.currentTarget.checked ? true : null })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.promptInfluence')}
                label={t('what.influence.label')}
                description={t('what.influence.description')}
                size="xs"
                min={0}
                max={1}
                step={0.1}
                decimalScale={2}
                value={config.promptInfluence ?? ''}
                onChange={(v) => patch({ promptInfluence: typeof v === 'number' ? Math.min(1, Math.max(0, v)) : null })}
              />
            </SimpleGrid>
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('cost.legend')}>
        <Stack gap="xs">
          <ProviderSelect
            capability={isMusic ? 'MusicGeneration' : 'SoundEffectGeneration'}
            label={t('cost.provider')}
            value={config.providerId}
            onChange={(providerId) => patch({ providerId })}
            nullMeans={isMusic ? t('cost.nullMeansMusic') : t('cost.nullMeansSoundEffect')}
            wrapperProps={fd('config.providerId').wrapperProps}
          />
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.takes')}
              label={t('cost.takes.label')}
              description={t('cost.takes.description', { max: MAX_TAKES })}
              size="xs"
              min={1}
              max={MAX_TAKES}
              value={config.takes ?? 1}
              onChange={(v) => patch({ takes: typeof v === 'number' ? Math.min(MAX_TAKES, Math.max(1, Math.round(v))) : 1 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxSpendCredits')}
              label={t('cost.limit.label')}
              description={t('cost.limit.description')}
              size="xs"
              min={0}
              decimalScale={2}
              value={config.maxSpendCredits ?? ''}
              onChange={(v) => patch({ maxSpendCredits: typeof v === 'number' && v > 0 ? v : null })}
            />
          </SimpleGrid>
          {(config.maxSpendCredits ?? 0) <= 0 && (
            <CostWarning>
              {t('cost.noLimitWarning')}
            </CostWarning>
          )}
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.registerProjectFile')}
            label={t('cost.save.label')}
            description={t('cost.save.description')}
            checked={config.registerProjectFile ?? true}
            onChange={(e) => patch({ registerProjectFile: e.currentTarget.checked })}
          />
        </Stack>
      </Fieldset>

      <AdvancedSection description={t('advanced.description')}>
        <TextInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.model')}
          label={t('advanced.model.label')}
          description={t('advanced.model.description')}
          size="xs"
          value={config.model ?? ''}
          onChange={(e) => patch({ model: e.target.value.trim() || null })}
        />
        {isMusic && (
          <Textarea
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.compositionPlanJson')}
            label={t('advanced.plan.label')}
            description={t('advanced.plan.description')}
            size="xs"
            rows={6}
            styles={{ input: { fontFamily: 'var(--mantine-font-family-monospace)' } }}
            value={config.compositionPlanJson ?? ''}
            onChange={(e) => {
              const text = e.target.value;
              // A plan replaces the description and the duration (the server refuses both with one).
              patch(text.trim()
                ? { compositionPlanJson: text, prompt: '', durationSec: 0, instrumental: null }
                : { compositionPlanJson: null, durationSec: AUDIO_DURATION_RANGE.Music.fallback });
            }}
          />
        )}
        <TextInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.fileNamePrefix')}
          label={t('advanced.prefix.label')}
          description={t('advanced.prefix.description')}
          size="xs"
          maxLength={40}
          value={config.fileNamePrefix ?? ''}
          onChange={(e) => patch({ fileNamePrefix: e.target.value.trim() || null })}
        />
      </AdvancedSection>
    </Stack>
  );
}
