'use client';

import { useState } from 'react';
import {
  Stack, Select, Textarea, Group, NumberInput, Switch, Collapse, Button, Divider, SimpleGrid, Text,
} from '@mantine/core';
import { EgressRunNote } from './EgressRunNote';
import { IconChevronDown, IconChevronUp, IconSettings } from '@tabler/icons-react';
import { useT, msg } from '@/i18n';
import { InputRefPicker } from './ExtractStepConfig';
import type { StepData } from './WorkflowStepList';
import { useFd, ProviderSelect, AskAssistantButton, CostWarning, assistantPrompt } from './config-kit';
import { VideoGenerateAvatarSection } from './VideoGenerateAvatarSection';
import type { ExtractInputRef, VideoGenerateStepConfig as VideoGenerateStepConfigValue } from '@/lib/types/workflow';
import {
  MAX_POLL_INTERVAL_SECONDS,
  MAX_SPEND_ERROR,
  MAX_TIMEOUT_SECONDS,
  MIN_POLL_INTERVAL_SECONDS,
  MIN_TIMEOUT_SECONDS,
  TAKES_EXCEED_MAX_CLIPS_ERROR,
  getVideoGeneratePollingErrors,
  getVideoGenerateRefErrors,
  isValidMaxSpendCredits,
} from '@/lib/utils/video-generate-validation';

/**
 * The ComfyUI provider accepts ONLY these two vetted first-party workflow template names as its
 * "model" (ComfyUiWorkflowTemplates.cs); anything else is refused before a request leaves the host.
 */
const COMFYUI_T2V = 'wan2.2-ti2v-5b-t2v';
const COMFYUI_I2V = 'wan2.2-ti2v-5b-i2v';

// The two ComfyUI entries are described in the catalogue (wfSteps.videoGenerate.model.*); vendor model names are not translated.
const MODEL_VALUES = ['MiniMax-H3', 'MiniMax-H3-Max', COMFYUI_T2V, COMFYUI_I2V];

const RESOLUTION_OPTIONS_COMFYUI = [
  { value: '480P', label: '480P' },
  { value: '720P', label: '720P' },
];

function isComfyUiModel(model: string) {
  return model === COMFYUI_T2V || model === COMFYUI_I2V;
}

const RESOLUTION_OPTIONS_H3 = [
  { value: '768P', label: '768P' },
  { value: '2K', label: '2K' },
];

const RESOLUTION_OPTIONS_H3_MAX = [
  { value: '480P', label: '480P' },
  { value: '768P', label: '768P' },
];

const RATIO_OPTIONS = [
  { value: '21:9', label: '21:9' },
  { value: '16:9', label: '16:9' },
  { value: '4:3', label: '4:3' },
  { value: '1:1', label: '1:1' },
  { value: '3:4', label: '3:4' },
  { value: '9:16', label: '9:16' },
];

const DEFAULT_POLL_INTERVAL_SECONDS = 10;
const DEFAULT_TIMEOUT_SECONDS = 1800;

/**
 * Plan-source choices. The values are the C# `VideoGeneratePlanSource` member names verbatim —
 * `Inline` (a prompt typed into this step) and `StepRef` (a planner step's GeneratedShotPlanOutput).
 */
const PLAN_SOURCE_VALUES = ['Inline', 'StepRef'];

const MIN_CLIPS = 1;
const MAX_CLIPS = 64;

/**
 * Coerces a NumberInput value to an integer in [MIN_CLIPS, upper]. Non-numeric/empty input
 * (Mantine reports '' or a string) falls back to MIN_CLIPS.
 */
function clampClips(value: number | string, upper: number = MAX_CLIPS): number {
  const n = typeof value === 'number' && Number.isFinite(value) ? Math.trunc(value) : MIN_CLIPS;
  return Math.min(Math.max(n, MIN_CLIPS), Math.max(MIN_CLIPS, Math.min(upper, MAX_CLIPS)));
}

function resolutionOptionsFor(model: string) {
  if (isComfyUiModel(model)) return RESOLUTION_OPTIONS_COMFYUI;
  return model === 'MiniMax-H3-Max' ? RESOLUTION_OPTIONS_H3_MAX : RESOLUTION_OPTIONS_H3;
}

/**
 * Default config for a freshly-added VideoGenerate step. Mirrors the C# `VideoGenerateStepConfig`
 * defaults exactly — including `maxSpendCredits: 0`, which the backend rejects, so the user must set a
 * spend cap before the step can run.
 */
export function createDefaultVideoGenerateStepConfig(): VideoGenerateStepConfigValue {
  return {
    planSource: 'Inline',
    inlinePrompt: null,
    plan: null,
    planProjectFileId: null,
    analysis: null,
    model: 'MiniMax-H3',
    resolution: '768P',
    durationSeconds: 6,
    ratio: '16:9',
    providerId: null,
    takes: 1,
    maxClips: 1,
    maxSpendCredits: 0,
    allowSourceMediaEgress: false,
    pollIntervalSeconds: DEFAULT_POLL_INTERVAL_SECONDS,
    timeoutSeconds: DEFAULT_TIMEOUT_SECONDS,
    loopBackBehavior: 'Reuse',
    lookMatching: false,
    cameraContinuity: false,
    maxGpuMinutes: 0,
    conformFps: 0,
    conformHeight: 0,
  };
}

interface VideoGenerateStepConfigProps {
  config: VideoGenerateStepConfigValue;
  onChange: (config: VideoGenerateStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
  /** The project, for the avatar section (hidden without it, and while the project's avatars answer 404). */
  projectId?: string;
}

/** `Previous` or one earlier step as an image reference; the select's values are 'none', 'prev' or a step order. */
function FrameStepSelect({
  label, value, onChange, priorStepOptions, disabled, description, wrapperProps,
}: {
  label: string;
  value: ExtractInputRef | null | undefined;
  onChange: (ref: ExtractInputRef | null) => void;
  priorStepOptions: { value: string; label: string }[];
  disabled?: boolean;
  description?: string;
  wrapperProps?: Record<string, string | number | boolean | undefined>;
}) {
  const t = useT('wfSteps.videoGenerate');
  const current = !value ? 'none' : value.from === 'Previous' ? 'prev' : value.stepOrder != null ? String(value.stepOrder) : 'none';
  return (
    <Select
      label={label}
      description={description}
      size="xs"
      disabled={disabled}
      wrapperProps={wrapperProps}
      data={[{ value: 'none', label: t('frames.none') }, { value: 'prev', label: t('frames.previousStep') }, ...priorStepOptions]}
      value={current}
      onChange={(v) => {
        if (!v || v === 'none') return onChange(null);
        onChange(v === 'prev' ? { from: 'Previous' } : { from: 'Step', stepOrder: Number(v) });
      }}
      allowDeselect={false}
    />
  );
}

/**
 * Configuration form for a VideoGenerate step (MiniMax video generation).
 * Mirrors the backend `VideoGenerateStepConfig` record field-for-field.
 */
export function VideoGenerateStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0, projectId,
}: VideoGenerateStepConfigProps) {
  const t = useT('wfSteps.videoGenerate');
  const fd = useFd();
  const pollingErrors = getVideoGeneratePollingErrors(config);
  const [advancedOpen, setAdvancedOpen] = useState(
    config.pollIntervalSeconds !== DEFAULT_POLL_INTERVAL_SECONDS
      || config.timeoutSeconds !== DEFAULT_TIMEOUT_SECONDS
      || pollingErrors.pollIntervalSeconds !== null
      || pollingErrors.timeoutSeconds !== null
      || Boolean(config.firstFrame || config.lastFrame),
  );

  const patch = (update: Partial<VideoGenerateStepConfigValue>) => {
    onChange({ ...config, ...update });
  };

  const resolutionOptions = resolutionOptionsFor(config.model);
  // A model id the list does not name (a template's fal.ai talking-head route, say) is still shown as chosen.
  const modelValues = MODEL_VALUES.includes(config.model) || !config.model ? MODEL_VALUES : [...MODEL_VALUES, config.model];
  const minDuration = config.model === 'MiniMax-H3-Max' ? 5 : 4;
  const maxDuration = 15;

  // Switching model resets the resolution when the current one isn't offered for the new model.
  // Done in the event handler (not during render) so the parent is never updated mid-render.
  const handleModelChange = (value: string | null) => {
    const model = value ?? 'MiniMax-H3';
    const options = resolutionOptionsFor(model);
    const resolution = options.some((opt) => opt.value === config.resolution)
      ? config.resolution
      : options[0].value;
    patch({ model, resolution });
  };

  // Takes may never exceed maxClips (backend Validate(): each take is one paid clip). Lowering
  // maxClips clamps takes in the same onChange; editing takes never raises maxClips.
  const maxClips = clampClips(config.maxClips);
  const handleMaxClipsChange = (value: number | string) => {
    const nextMaxClips = clampClips(value);
    patch({ maxClips: nextMaxClips, takes: Math.min(config.takes, nextMaxClips) });
  };
  const handleTakesChange = (value: number | string) => {
    patch({ takes: clampClips(value, maxClips) });
  };
  const takesError = config.takes > config.maxClips ? TAKES_EXCEED_MAX_CLIPS_ERROR : undefined;
  // A GPU-only budget (self-hosted ComfyUI bills $0) is the one case where maxSpendCredits may stay 0:
  // the C# Validate() accepts it when maxGpuMinutes > 0, and ValidateForKind() picks the right cap.
  const gpuBudgeted = (config.maxGpuMinutes ?? 0) > 0;
  const maxSpendError = isValidMaxSpendCredits(config.maxSpendCredits) || gpuBudgeted ? undefined : MAX_SPEND_ERROR;

  // Only earlier steps can source a plan (the backend resolves a ref against the step output
  // history), built exactly like the sibling VideoCompileStepConfig's pickers. `StepData` carries
  // no agent identity (only an agentDefinitionId), so every prior step is offered rather than
  // filtering to ShotDirector steps.
  const priorStepOptions = allSteps
    .slice(0, currentStepIndex)
    .map((previousStep, index) => {
      const stepOrder = index + 1;
      const label = previousStep.label?.trim() || t('stepFallback', { n: stepOrder });
      return { value: String(stepOrder), label: `${stepOrder}. ${label}` };
    });

  const planSourced = config.planSource === 'StepRef';
  const refErrors = getVideoGenerateRefErrors(config);

  return (
    <Stack gap="sm">
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">{t('intro')}</Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'VideoGenerate', msg('wfSteps.prompts.videoGenerate'))} />
      </Group>
      <CostWarning>
        {t('cost')}
      </CostWarning>
      <Select
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.planSource')}
        label={t('what.label')}
        description={t('what.description')}
        size="xs"
        data={PLAN_SOURCE_VALUES.map((v) => ({ value: v, label: t(`planSource.${v}`) }))}
        value={config.planSource}
        onChange={(v) => patch({ planSource: v === 'StepRef' ? 'StepRef' : 'Inline' })}
        allowDeselect={false}
        clearable={false}
      />

      <Textarea
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.inlinePrompt')}
        rows={3}
        label={t('prompt.label')}
        description={planSourced
          ? t('prompt.unused')
          : t('prompt.description')}
        placeholder={t('prompt.placeholder')}
        maxLength={7000}
        required={!planSourced}
        size="xs"
        value={config.inlinePrompt ?? ''}
        onChange={(e) => patch({ inlinePrompt: e.currentTarget.value })}
      />

      {planSourced && (
        <>
          <Stack gap={4}>
            <InputRefPicker
              label={t('plan.step')}
              value={config.plan ?? undefined}
              onChange={(ref) => patch({ plan: ref })}
              priorStepOptions={priorStepOptions}
              allowedSources={['Previous', 'Step']}
            />
            {refErrors.plan && <Text size="xs" c="red">{refErrors.plan}</Text>}
          </Stack>

          <Stack gap={4}>
            <InputRefPicker
              label={t('plan.analysis')}
              value={config.analysis ?? undefined}
              onChange={(ref) => patch({ analysis: ref })}
              priorStepOptions={priorStepOptions}
              allowedSources={['Previous', 'Step']}
            />
            {refErrors.analysis && <Text size="xs" c="red">{refErrors.analysis}</Text>}
          </Stack>

          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.lookMatching')}
              label={t('plan.lookMatching.label')}
              description={t('plan.lookMatching.description')}
              size="xs"
              checked={config.lookMatching ?? false}
              onChange={(e) => patch({ lookMatching: e.currentTarget.checked })}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.cameraContinuity')}
              label={t('plan.camera.label')}
              description={t('plan.camera.description')}
              size="xs"
              checked={config.cameraContinuity ?? false}
              onChange={(e) => patch({ cameraContinuity: e.currentTarget.checked })}
            />
          </SimpleGrid>
        </>
      )}

      <ProviderSelect
        // A talking head (a step with avatar speech) runs on a lip-sync model, never a video-generation one.
        capability={config.avatarAudio || config.avatarAudioProjectFileId ? 'LipSync' : 'VideoGeneration'}
        label={t('provider.label')}
        description={t('provider.description')}
        value={config.providerId}
        onChange={(providerId) => patch({ providerId })}
        nullMeans={t('provider.nullMeans')}
        wrapperProps={fd('config.providerId').wrapperProps}
      />

      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.model')}
          label={t('model.label')}
          size="xs"
          data={modelValues.map((v) => ({ value: v, label: v === COMFYUI_T2V ? t('model.comfyT2v') : v === COMFYUI_I2V ? t('model.comfyI2v') : v }))}
          value={config.model}
          onChange={handleModelChange}
          searchable
          clearable={false}
        />
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.resolution')}
          label={t('resolution')}
          size="xs"
          data={resolutionOptions}
          value={config.resolution}
          onChange={(v) => patch({ resolution: v ?? '768P' })}
          searchable
          clearable={false}
        />
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        <NumberInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.durationSeconds')}
          label={t('duration.label')}
          description={t('duration.description', { min: minDuration, max: maxDuration })}
          min={minDuration}
          max={maxDuration}
          size="xs"
          value={config.durationSeconds}
          onChange={(v) => patch({ durationSeconds: typeof v === 'number' ? v : 6 })}
        />
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.ratio')}
          label={t('ratio')}
          size="xs"
          data={RATIO_OPTIONS}
          value={config.ratio}
          onChange={(v) => patch({ ratio: v ?? '16:9' })}
          searchable
          clearable={false}
        />
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        <NumberInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.takes')}
          label={t('takes.label')}
          description={t('takes.description', { max: maxClips })}
          min={MIN_CLIPS}
          max={maxClips}
          allowDecimal={false}
          size="xs"
          value={config.takes}
          error={takesError}
          onChange={handleTakesChange}
        />
        <NumberInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.maxClips')}
          label={t('maxClips.label')}
          description={t('maxClips.description', { min: MIN_CLIPS, max: MAX_CLIPS })}
          min={MIN_CLIPS}
          max={MAX_CLIPS}
          allowDecimal={false}
          size="xs"
          value={config.maxClips}
          onChange={handleMaxClipsChange}
        />
      </SimpleGrid>

      <SimpleGrid cols={{ base: 1, sm: 2 }}>
        <NumberInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.maxSpendCredits')}
          label={t('maxSpend.label')}
          description={t('maxSpend.description')}
          min={0.01}
          step={0.01}
          decimalScale={2}
          size="xs"
          required
          value={config.maxSpendCredits}
          error={maxSpendError}
          onChange={(v) => patch({ maxSpendCredits: typeof v === 'number' ? v : 0 })}
        />
        <NumberInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.maxGpuMinutes')}
          label={t('maxGpu.label')}
          description={t('maxGpu.description')}
          min={0}
          step={1}
          decimalScale={2}
          size="xs"
          value={config.maxGpuMinutes ?? 0}
          onChange={(v) => patch({ maxGpuMinutes: typeof v === 'number' ? v : 0 })}
        />
      </SimpleGrid>

      <Select
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.loopBackBehavior')}
        label={t('loopBack.label')}
        description={t('loopBack.description')}
        size="xs"
        data={[
          { value: 'Reuse', label: t('loopBack.Reuse') },
          { value: 'Regenerate', label: t('loopBack.Regenerate') },
        ]}
        value={config.loopBackBehavior ?? 'Reuse'}
        onChange={(v) => patch({ loopBackBehavior: v === 'Regenerate' ? 'Regenerate' : 'Reuse' })}
        allowDeselect={false}
        clearable={false}
      />

      <VideoGenerateAvatarSection
        config={config}
        patch={patch}
        projectId={projectId}
        allSteps={allSteps}
        currentStepIndex={currentStepIndex}
      />

      <Switch
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.allowSourceMediaEgress')}
        label={t('egress.label')}
        description={config.avatarId || config.avatarAudio || config.avatarAudioProjectFileId
          ? `${t('egress.description')} ${t('egress.avatar')}`
          : t('egress.description')}
        size="xs"
        checked={config.allowSourceMediaEgress}
        onChange={(e) => patch({ allowSourceMediaEgress: e.currentTarget.checked })}
      />
      <EgressRunNote />

      <Divider />

      <Button
        variant="subtle"
        size="compact-xs"
        leftSection={<IconSettings size={14} />}
        rightSection={advancedOpen ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
        onClick={() => setAdvancedOpen(!advancedOpen)}
      >
        {t('advanced')}
      </Button>
      <Collapse expanded={advancedOpen}>
        {!planSourced && (
          <Stack gap="xs" mb="sm">
            <Text size="xs" c="dimmed">
              {t('frames.intro')}
            </Text>
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <FrameStepSelect
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.firstFrame')}
                label={t('frames.first')}
                value={config.firstFrame}
                onChange={(firstFrame) => patch(firstFrame ? { firstFrame } : { firstFrame: null, lastFrame: null })}
                priorStepOptions={priorStepOptions}
              />
              <FrameStepSelect
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.lastFrame')}
                label={t('frames.last')}
                value={config.lastFrame}
                onChange={(lastFrame) => patch({ lastFrame })}
                priorStepOptions={priorStepOptions}
                disabled={!config.firstFrame}
                description={config.firstFrame ? undefined : t('frames.pickFirst')}
              />
            </SimpleGrid>
            {(config.firstFrame || config.lastFrame) && !config.allowSourceMediaEgress && (
              <Text size="xs" c="orange">
                {t('frames.egressWarning')}
              </Text>
            )}
          </Stack>
        )}
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.conformFps')}
            label={t('conform.fps.label')}
            description={t('conform.fps.description')}
            min={0}
            max={120}
            allowDecimal={false}
            size="xs"
            value={config.conformFps ?? 0}
            onChange={(v) => patch({ conformFps: typeof v === 'number' ? v : 0 })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.conformHeight')}
            label={t('conform.height.label')}
            description={t('conform.height.description')}
            min={0}
            max={4320}
            allowDecimal={false}
            size="xs"
            value={config.conformHeight ?? 0}
            onChange={(v) => patch({ conformHeight: typeof v === 'number' ? v : 0 })}
          />
        </SimpleGrid>
        <SimpleGrid cols={{ base: 1, sm: 2 }}>
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.pollIntervalSeconds')}
            label={t('poll.label')}
            description={t('poll.description', { min: MIN_POLL_INTERVAL_SECONDS, max: MAX_POLL_INTERVAL_SECONDS })}
            min={MIN_POLL_INTERVAL_SECONDS}
            max={MAX_POLL_INTERVAL_SECONDS}
            allowDecimal={false}
            size="xs"
            error={pollingErrors.pollIntervalSeconds ?? undefined}
            value={config.pollIntervalSeconds}
            onChange={(v) => patch({ pollIntervalSeconds: typeof v === 'number' ? v : DEFAULT_POLL_INTERVAL_SECONDS })}
          />
          <NumberInput
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.timeoutSeconds')}
            label={t('timeout.label')}
            description={t('timeout.description', { min: MIN_TIMEOUT_SECONDS, max: MAX_TIMEOUT_SECONDS })}
            min={MIN_TIMEOUT_SECONDS}
            max={MAX_TIMEOUT_SECONDS}
            allowDecimal={false}
            size="xs"
            error={pollingErrors.timeoutSeconds ?? undefined}
            value={config.timeoutSeconds}
            onChange={(v) => patch({ timeoutSeconds: typeof v === 'number' ? v : DEFAULT_TIMEOUT_SECONDS })}
          />
        </SimpleGrid>
      </Collapse>
    </Stack>
  );
}
