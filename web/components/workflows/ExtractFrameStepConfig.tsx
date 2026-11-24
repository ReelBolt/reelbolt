'use client';

import { Stack, Group, Select, TextInput, NumberInput, Switch, Text, SimpleGrid, Fieldset } from '@mantine/core';
import { MediaPickerField } from '@/components/shared/media/MediaPickerField';
import type {
  ExtractFrameStepConfig as ExtractFrameStepConfigValue,
  FrameAt,
  VideoSourceKind,
  VideoSourceRef,
} from '@/lib/types/workflow';
import { useT, msg } from '@/i18n';
import type { StepData } from './WorkflowStepList';
import { useFd, AskAssistantButton, assistantPrompt, stepOrderOptions } from './config-kit';

/** The four ways a frame's video can be named; `GeneratedClips` (many videos) is deliberately not offered. */
const SOURCE_KIND_VALUES: VideoSourceKind[] = ['ProjectFile', 'PreviousStepOutput', 'StepOutput', 'StepResult'];

/** Mirrors `FrameAt` in ExtractFrameStepConfig.cs. */
const AT_VALUES: FrameAt[] = ['Last', 'First', 'Time'];

const FORMAT_VALUES = ['png', 'jpeg'];

/** The server's bounds for `MaxLongSide` (ExtractFrameStepConfig.MinLongSide / MaxLongSideLimit). */
const MIN_LONG_SIDE = 16;
const MAX_LONG_SIDE = 8192;

/** An uploaded file is the only source that exists before any step has run. */
export const EXTRACT_FRAME_FIRST_STEP_SOURCE: VideoSourceRef = { kind: 'ProjectFile', projectFileId: null, stepOrder: null };

/** Builds the default config for a freshly-added ExtractFrame step. Mirrors the C# record defaults (At=Last, png, register). */
export function createDefaultExtractFrameStepConfig(): ExtractFrameStepConfigValue {
  return {
    source: { kind: 'PreviousStepOutput', projectFileId: null, stepOrder: null },
    at: 'Last',
    atSec: null,
    format: 'png',
    maxLongSide: null,
    registerProjectFile: true,
  };
}

interface ExtractFrameStepConfigProps {
  config: ExtractFrameStepConfigValue;
  onChange: (config: ExtractFrameStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
  projectId?: string;
}

/**
 * Configuration form for an ExtractFrame step (deterministic, no model call, free): saves one frame of one video as
 * an image. Mirrors the backend `ExtractFrameStepConfig` record field-for-field; the server's `Validate()` is the
 * authority and its messages are shown against the field they name.
 */
export function ExtractFrameStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0, projectId,
}: ExtractFrameStepConfigProps) {
  const t = useT('wfSteps.extractFrame');
  const fd = useFd();
  const patch = (updates: Partial<ExtractFrameStepConfigValue>) => onChange({ ...config, ...updates });

  const source = config.source ?? EXTRACT_FRAME_FIRST_STEP_SOURCE;
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

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'ExtractFrame', msg('wfSteps.prompts.extractFrame'))} />
      </Group>

      <Fieldset legend={t('video.legend')}>
        <Stack gap="xs">
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.source.kind')}
            label={t('video.takeFrom')}
            size="xs"
            data={kindData}
            value={kind}
            onChange={(v) => v && patch({ source: { kind: v as VideoSourceKind, projectFileId: null, stepOrder: null, stepResultId: null } })}
            allowDeselect={false}
          />
          {kind === 'ProjectFile' && (
            <MediaPickerField
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.source.projectFileId')}
              projectId={projectId}
              kind="video"
              label={t('video.file')}
              value={source.projectFileId ? [source.projectFileId] : []}
              onChange={(picked) => patch({ source: { kind: 'ProjectFile', projectFileId: picked[0]?.id ?? null, stepOrder: null, stepResultId: null } })}
            />
          )}
          {kind === 'StepOutput' && (
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.source.stepOrder')}
              label={t('video.step')}
              size="xs"
              data={priorStepOptions}
              value={source.stepOrder != null ? String(source.stepOrder) : null}
              onChange={(v) => patch({ source: { kind: 'StepOutput', projectFileId: null, stepOrder: v ? Number(v) : null, stepResultId: null } })}
              placeholder={t('video.stepPlaceholder')}
            />
          )}
          {kind === 'StepResult' && (
            <TextInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.source.stepResultId')}
              label={t('video.stepResult.label')}
              description={t('video.stepResult.description')}
              size="xs"
              value={source.stepResultId ?? ''}
              onChange={(e) => patch({ source: { kind: 'StepResult', projectFileId: null, stepOrder: null, stepResultId: e.target.value.trim() || null } })}
            />
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('frame.legend')}>
        <Stack gap="xs">
          <Select
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.at')}
            label={t('frame.label')}
            size="xs"
            data={AT_VALUES.map((v) => ({ value: v, label: t(`at.${v}`) }))}
            value={config.at}
            // The moment only means something for "A moment I choose"; the server refuses it otherwise.
            onChange={(v) => v && patch({ at: v as FrameAt, atSec: v === 'Time' ? (config.atSec ?? 0) : null })}
            allowDeselect={false}
          />
          {config.at === 'Time' && (
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.atSec')}
              label={t('frame.moment.label')}
              description={t('frame.moment.description')}
              size="xs"
              min={0}
              step={0.1}
              decimalScale={2}
              value={config.atSec ?? 0}
              onChange={(v) => patch({ atSec: typeof v === 'number' ? Math.max(0, v) : 0 })}
            />
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('image.legend')}>
        <Stack gap="xs">
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.format')}
              label={t('image.format')}
              size="xs"
              data={FORMAT_VALUES.map((v) => ({ value: v, label: t(`format.${v}`) }))}
              value={config.format?.toLowerCase() === 'jpeg' ? 'jpeg' : 'png'}
              onChange={(v) => v && patch({ format: v })}
              allowDeselect={false}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.maxLongSide')}
              label={t('image.longestSide.label')}
              description={t('image.longestSide.description', { min: MIN_LONG_SIDE, max: MAX_LONG_SIDE })}
              size="xs"
              min={MIN_LONG_SIDE}
              max={MAX_LONG_SIDE}
              value={config.maxLongSide ?? ''}
              onChange={(v) => patch({ maxLongSide: typeof v === 'number' ? Math.min(MAX_LONG_SIDE, Math.max(MIN_LONG_SIDE, Math.round(v))) : null })}
            />
          </SimpleGrid>
          <Switch
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.registerProjectFile')}
            label={t('image.save.label')}
            description={t('image.save.description')}
            checked={config.registerProjectFile}
            onChange={(e) => patch({ registerProjectFile: e.currentTarget.checked })}
          />
        </Stack>
      </Fieldset>
    </Stack>
  );
}
