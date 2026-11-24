'use client';

import { Stack, Group, Select, TextInput, NumberInput, Switch, Text, Fieldset, Checkbox } from '@mantine/core';
import { EgressRunNote } from './EgressRunNote';
import { EXTEND_SIDES, extendAmountOf, extendMargins, extendSidesOn } from '@/lib/editing/enhance/types';
import { useState } from 'react';
import { MediaPickerField } from '@/components/shared/media/MediaPickerField';
import { putEnhanceConsent } from '@/lib/editing/enhance/enhance-api';
import { EnhanceConsentDialog } from '@/components/editor/enhance/EnhanceParts';
import type {
  VideoEnhanceDenoiseStrength,
  VideoEnhanceEngine,
  VideoEnhanceOp,
  VideoEnhanceStepConfig as VideoEnhanceStepConfigValue,
  VideoSourceKind,
  VideoSourceRef,
} from '@/lib/types/workflow';
import { useT, msg } from '@/i18n';
import type { StepData } from './WorkflowStepList';
import {
  useFd, AskAssistantButton, assistantPrompt, stepOrderOptions, ProviderSelect, EgressWarning, ModeHint,
} from './config-kit';

/** The four ways a video can be named; `GeneratedClips` and `BriefFiles` (many files) are not offered. */
const SOURCE_KIND_VALUES: VideoSourceKind[] = ['ProjectFile', 'PreviousStepOutput', 'StepOutput', 'StepResult'];

const OP_VALUES: VideoEnhanceOp[] = ['Upscale', 'Interpolate', 'Denoise', 'MotionBlur', 'Extend'];

const DENOISE_STRENGTHS: VideoEnhanceDenoiseStrength[] = ['Light', 'Strong'];

/** The shapes Extend offers, in display order. */
const EXTEND_ASPECTS = ['16:9', '9:16', '1:1', '4:5'] as const;

/** The longest Extend description the server accepts. */
const EXTEND_PROMPT_MAX = 500;

/** Denoise and MotionBlur run only on this server; Upscale, Interpolate and Extend also run on a hosted AI model. */
export function isHostedVideoEnhanceOp(op: VideoEnhanceOp | string | null | undefined): boolean {
  return op === 'Upscale' || op === 'Interpolate' || op === 'Extend';
}

/** Extend has no free path: it is always a hosted AI operation. */
export function isHostedOnlyVideoEnhanceOp(op: VideoEnhanceOp | string | null | undefined): boolean {
  return op === 'Extend';
}

/** True when the step would upload the source to a hosted model. */
export function isHostedVideoEnhance(engine: VideoEnhanceEngine | string | null | undefined): boolean {
  return engine === 'Hosted';
}

/** The hosted request cap; a hosted step may only lower it. */
export const HOSTED_MAX_SOURCE_SECONDS = 120;
/** The local ceiling on `maxSourceSeconds`. */
export const LOCAL_MAX_SOURCE_SECONDS = 600;

/** An uploaded file is the only source that exists before any step has run. */
export const VIDEO_ENHANCE_FIRST_STEP_SOURCE: VideoSourceRef = { kind: 'ProjectFile', projectFileId: null, stepOrder: null };

/** Default config for a freshly-added VideoEnhance step: the free 2x upscale, no consent, listed in Files. */
export function createDefaultVideoEnhanceStepConfig(): VideoEnhanceStepConfigValue {
  return {
    op: 'Upscale',
    engine: 'Local',
    source: { kind: 'PreviousStepOutput', projectFileId: null, stepOrder: null },
    providerId: null,
    allowSourceMediaEgress: false,
    registerProjectFile: true,
    maxSourceSeconds: null,
    upscaleFactor: 2,
    targetHeight: null,
    frameMultiplier: null,
    denoiseStrength: null,
    keepGrain: false,
    shutterAngle: null,
    targetAspect: null,
    marginLeft: null,
    marginRight: null,
    marginTop: null,
    marginBottom: null,
    prompt: null,
  };
}

/** Applies an engine change, dropping the fields a local operation may not carry and clamping hosted-only bounds. */
export function withVideoEnhanceEngine(
  config: VideoEnhanceStepConfigValue,
  engine: VideoEnhanceEngine,
): VideoEnhanceStepConfigValue {
  const target: VideoEnhanceEngine = isHostedOnlyVideoEnhanceOp(config.op) ? 'Hosted' : engine;
  if (target === 'Hosted') {
    return {
      ...config,
      engine: target,
      // Hosted interpolation offers 2 and 4 only.
      frameMultiplier: config.frameMultiplier === 8 ? 4 : config.frameMultiplier,
      maxSourceSeconds: config.maxSourceSeconds != null && config.maxSourceSeconds > HOSTED_MAX_SOURCE_SECONDS
        ? HOSTED_MAX_SOURCE_SECONDS
        : config.maxSourceSeconds,
    };
  }
  // A local operation never uploads or calls a provider.
  return { ...config, engine: target, providerId: null, allowSourceMediaEgress: false };
}

/**
 * Applies an op change and resets every op- or engine-specific field that the server would refuse: Denoise and
 * MotionBlur are local only (so they drop the provider and the consent), and each op keeps just its own options.
 */
export function withVideoEnhanceOp(
  config: VideoEnhanceStepConfigValue,
  op: VideoEnhanceOp,
): VideoEnhanceStepConfigValue {
  if (op === 'Upscale') {
    const useTargetHeight = config.upscaleFactor == null && config.targetHeight != null;
    return {
      ...config,
      op,
      // A provider is chosen for one capability (VideoUpscale or FrameInterpolation): never carry it across ops.
      providerId: config.op === op ? config.providerId : null,
      upscaleFactor: useTargetHeight ? null : (config.upscaleFactor ?? 2),
      targetHeight: useTargetHeight ? config.targetHeight : null,
      frameMultiplier: null,
      denoiseStrength: null,
      keepGrain: false,
      shutterAngle: null,
      targetAspect: null,
      marginLeft: null,
      marginRight: null,
      marginTop: null,
      marginBottom: null,
      prompt: null,
    };
  }
  if (op === 'Interpolate') {
    return {
      ...config,
      op,
      providerId: config.op === op ? config.providerId : null,
      upscaleFactor: null,
      targetHeight: null,
      frameMultiplier: config.frameMultiplier === 8 || config.frameMultiplier == null ? 2 : config.frameMultiplier,
      denoiseStrength: null,
      keepGrain: false,
      shutterAngle: null,
      targetAspect: null,
      marginLeft: null,
      marginRight: null,
      marginTop: null,
      marginBottom: null,
      prompt: null,
    };
  }
  if (op === 'Extend') {
    return {
      ...config,
      op,
      engine: 'Hosted',
      upscaleFactor: null,
      targetHeight: null,
      frameMultiplier: null,
      denoiseStrength: null,
      keepGrain: false,
      shutterAngle: null,
      targetAspect: config.targetAspect ?? '1:1',
      marginLeft: null,
      marginRight: null,
      marginTop: null,
      marginBottom: null,
      prompt: config.prompt ?? null,
    };
  }
  // Denoise and MotionBlur are free local operations.
  const local: VideoEnhanceStepConfigValue = {
    ...config,
    op,
    engine: 'Local',
    providerId: null,
    allowSourceMediaEgress: false,
    upscaleFactor: null,
    targetHeight: null,
    frameMultiplier: null,
    targetAspect: null,
    marginLeft: null,
    marginRight: null,
    marginTop: null,
    marginBottom: null,
    prompt: null,
  };
  if (op === 'Denoise') {
    return { ...local, denoiseStrength: config.denoiseStrength ?? 'Light', keepGrain: config.keepGrain ?? false, shutterAngle: null };
  }
  return { ...local, denoiseStrength: null, keepGrain: false, shutterAngle: config.shutterAngle ?? null };
}

/** One option per upscale size: a multiplier or an exact target height, never both. */
type UpscaleSize = { value: string; factor: number | null; height: number | null };

const UPSCALE_SIZES: UpscaleSize[] = [
  { value: 'x2', factor: 2, height: null },
  { value: 'x4', factor: 4, height: null },
  { value: 'h720', factor: null, height: 720 },
  { value: 'h1080', factor: null, height: 1080 },
  { value: 'h1440', factor: null, height: 1440 },
  { value: 'h2160', factor: null, height: 2160 },
];

interface VideoEnhanceStepConfigProps {
  config: VideoEnhanceStepConfigValue;
  onChange: (config: VideoEnhanceStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
  projectId?: string;
}

/**
 * Configuration form for a VideoEnhance step. Mirrors the backend `VideoEnhanceStepConfig` record field-for-field; the
 * server's `Validate()` is the authority. Upscale and Interpolate run free on this server or on a hosted AI model (with
 * the customer's consent); Denoise and MotionBlur are local only. The hosted-only provider and consent fields disappear
 * for a local step, and every op shows only its own options.
 */
export function VideoEnhanceStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0, projectId,
}: VideoEnhanceStepConfigProps) {
  const t = useT('wfSteps.videoEnhance');
  const fd = useFd();
  const patch = (updates: Partial<VideoEnhanceStepConfigValue>) => onChange({ ...config, ...updates });

  const hostedCapable = isHostedVideoEnhanceOp(config.op);
  const hostedOnly = isHostedOnlyVideoEnhanceOp(config.op);
  // A stored config may predate the op gating (e.g. Denoise + Hosted); show what the server would accept.
  const engine: VideoEnhanceEngine = hostedOnly ? 'Hosted' : hostedCapable ? config.engine : 'Local';
  const hosted = isHostedVideoEnhance(engine);
  const egressAllowed = config.allowSourceMediaEgress ?? false;
  // Ticking the box asks the same plain question the editor asks, then records the project's consent; unticking only
  // turns this step's flag off (the project consent stays, a person withdraws it where they gave it).
  const [consent, setConsent] = useState<'closed' | 'open' | 'busy' | 'failed'>('closed');
  const confirmConsent = async () => {
    if (!projectId) return;
    setConsent('busy');
    try {
      await putEnhanceConsent(projectId);
      setConsent('closed');
      patch({ allowSourceMediaEgress: true });
    } catch {
      setConsent('failed');
    }
  };
  const source = config.source ?? VIDEO_ENHANCE_FIRST_STEP_SOURCE;
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

  const engineData = hostedOnly
    ? [{ value: 'Hosted', label: t('engine.Hosted') }]
    : hostedCapable
      ? [{ value: 'Local', label: t('engine.Local') }, { value: 'Hosted', label: t('engine.Hosted') }]
      : [{ value: 'Local', label: t('engine.Local') }];

  const selectedSize = UPSCALE_SIZES.find((s) => (
    s.height != null ? config.targetHeight === s.height : config.upscaleFactor === s.factor
  )) ?? UPSCALE_SIZES[0];
  const frameMultipliers = hosted ? [2, 4] : [2, 4, 8];
  const frameMultiplier = config.frameMultiplier != null && frameMultipliers.includes(config.frameMultiplier)
    ? config.frameMultiplier
    : frameMultipliers[0];
  const maxSourceSeconds = hosted ? HOSTED_MAX_SOURCE_SECONDS : LOCAL_MAX_SOURCE_SECONDS;
  const extendMode: 'aspect' | 'sides' = config.targetAspect != null ? 'aspect' : 'sides';

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'VideoEnhance', msg('wfSteps.prompts.videoEnhance'))} />
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
              kind="video"
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
          {kind === 'PreviousStepOutput' && !hasPriorSteps && (
            <Text size="xs" c="orange">
              {t('source.nothingBefore')}
            </Text>
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('op.legend')}>
        <Stack gap="xs">
          <Select
            {...fd('config.op')}
            label={t('op.label')}
            size="xs"
            data={OP_VALUES.map((v) => ({ value: v, label: t(`op.${v}`) }))}
            value={config.op}
            onChange={(v) => v && onChange(withVideoEnhanceOp(config, v as VideoEnhanceOp))}
            allowDeselect={false}
          />
          <ModeHint>{hostedOnly ? t('op.extendHostedHint') : hostedCapable ? t('op.hostedCapableHint') : t('op.localOnlyHint')}</ModeHint>
          <Select
            {...fd('config.engine')}
            label={t('engine.label')}
            size="xs"
            data={engineData}
            value={engine}
            onChange={(v) => v && onChange(withVideoEnhanceEngine(config, v as VideoEnhanceEngine))}
            allowDeselect={false}
          />
          <ModeHint>{hosted ? t('engine.creditsHint') : t('engine.freeHint')}</ModeHint>
        </Stack>
      </Fieldset>

      <Fieldset legend={t('options.legend')}>
        <Stack gap="xs">
          {config.op === 'Upscale' && (
            <Select
              {...fd('config.upscaleFactor')}
              label={t('upscale.label')}
              size="xs"
              data={UPSCALE_SIZES.map((s) => ({ value: s.value, label: t(`upscale.size.${s.value}`) }))}
              value={selectedSize.value}
              onChange={(v) => {
                const size = UPSCALE_SIZES.find((s) => s.value === v) ?? UPSCALE_SIZES[0];
                patch({ upscaleFactor: size.factor, targetHeight: size.height });
              }}
              allowDeselect={false}
            />
          )}
          {config.op === 'Interpolate' && (
            <Select
              {...fd('config.frameMultiplier')}
              label={t('interpolate.label')}
              size="xs"
              data={frameMultipliers.map((m) => ({ value: String(m), label: t(`interpolate.multiplier.${m}`) }))}
              value={String(frameMultiplier)}
              onChange={(v) => v && patch({ frameMultiplier: Number(v) })}
              allowDeselect={false}
            />
          )}
          {config.op === 'Denoise' && (
            <>
              <Select
                {...fd('config.denoiseStrength')}
                label={t('denoise.strength')}
                size="xs"
                data={DENOISE_STRENGTHS.map((v) => ({ value: v, label: t(`denoise.strengthValue.${v}`) }))}
                value={config.denoiseStrength ?? 'Light'}
                onChange={(v) => v && patch({ denoiseStrength: v as VideoEnhanceDenoiseStrength })}
                allowDeselect={false}
              />
              <Switch
                {...fd('config.keepGrain')}
                label={t('denoise.keepGrain.label')}
                description={t('denoise.keepGrain.description')}
                checked={config.keepGrain ?? false}
                onChange={(e) => patch({ keepGrain: e.currentTarget.checked })}
              />
            </>
          )}
          {config.op === 'MotionBlur' && (
            <NumberInput
              {...fd('config.shutterAngle')}
              label={t('motionBlur.label')}
              description={t('motionBlur.description')}
              size="xs"
              min={90}
              max={360}
              allowDecimal={false}
              value={config.shutterAngle ?? ''}
              onChange={(v) => patch({ shutterAngle: typeof v === 'number' ? Math.min(360, Math.max(90, Math.round(v))) : null })}
            />
          )}
          {config.op === 'Extend' && (
            <>
              <Select
                label={t('extend.mode.label')}
                size="xs"
                data={[
                  { value: 'aspect', label: t('extend.mode.aspect') },
                  { value: 'sides', label: t('extend.mode.sides') },
                ]}
                value={extendMode}
                onChange={(v) => {
                  if (!v) return;
                  if (v === 'aspect') {
                    patch({ targetAspect: config.targetAspect ?? '1:1', marginLeft: null, marginRight: null, marginTop: null, marginBottom: null });
                  } else {
                    patch({
                      targetAspect: null,
                      marginLeft: config.marginLeft ?? 0.2,
                      marginRight: config.marginRight ?? 0.2,
                      marginTop: config.marginTop ?? 0.2,
                      marginBottom: config.marginBottom ?? 0.2,
                    });
                  }
                }}
                allowDeselect={false}
              />
              {extendMode === 'aspect' ? (
                <Select
                  {...fd('config.targetAspect')}
                  label={t('extend.aspect.label')}
                  size="xs"
                  data={EXTEND_ASPECTS.map((a) => ({ value: a, label: t(`extend.aspect.${a}`) }))}
                  value={config.targetAspect ?? '1:1'}
                  onChange={(v) => v && patch({ targetAspect: v })}
                  allowDeselect={false}
                />
              ) : (
                <>
                  <Group gap="sm" wrap="wrap">
                    {EXTEND_SIDES.map((side) => (
                      <Checkbox
                        key={side}
                        size="sm"
                        label={t(`extend.side.${side}`)}
                        checked={extendSidesOn(config).includes(side)}
                        onChange={(e) => {
                          const current = extendSidesOn(config);
                          patch(extendMargins(e.currentTarget.checked ? [...current, side] : current.filter((x) => x !== side), extendAmountOf(config)));
                        }}
                      />
                    ))}
                  </Group>
                  <NumberInput
                    {...fd('config.marginLeft')}
                    label={t('extend.side.amount')}
                    description={t('extend.side.description')}
                    size="sm"
                    min={1}
                    max={50}
                    suffix="%"
                    allowDecimal={false}
                    value={Math.round(extendAmountOf(config) * 100)}
                    onChange={(v) => {
                      // Empty or sub-percent keeps the last valid amount: nothing near zero is ever saved.
                      if (typeof v !== 'number' || v < 1) return;
                      patch(extendMargins(extendSidesOn(config), Math.min(0.5, v / 100)));
                    }}
                  />
                </>
              )}
              <TextInput
                {...fd('config.prompt')}
                label={t('extend.prompt.label')}
                description={`${t('extend.prompt.description', { max: EXTEND_PROMPT_MAX })} (${(config.prompt ?? '').length}/${EXTEND_PROMPT_MAX})`}
                size="sm"
                maxLength={EXTEND_PROMPT_MAX}
                value={config.prompt ?? ''}
                onChange={(e) => patch({ prompt: e.currentTarget.value.trim() === '' ? null : e.currentTarget.value })}
              />
            </>
          )}
          <NumberInput
            {...fd('config.maxSourceSeconds')}
            label={t('maxSeconds.label')}
            description={t('maxSeconds.description')}
            size="xs"
            min={1}
            max={maxSourceSeconds}
            allowDecimal={false}
            value={config.maxSourceSeconds ?? ''}
            onChange={(v) => patch({ maxSourceSeconds: typeof v === 'number' ? Math.min(maxSourceSeconds, Math.max(1, Math.round(v))) : null })}
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

      {hosted && (
        <Fieldset legend={t('hosted.legend')}>
          <Stack gap="xs">
            <ProviderSelect
              capability={config.op === 'Interpolate' ? 'FrameInterpolation' : config.op === 'Extend' ? 'VideoOutpaint' : 'VideoUpscale'}
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
              onChange={(e) => {
                if (!e.currentTarget.checked) patch({ allowSourceMediaEgress: false });
                else if (projectId) setConsent('open');
                else patch({ allowSourceMediaEgress: true });
              }}
            />
            <EgressRunNote />
            <EnhanceConsentDialog
              opened={consent !== 'closed'}
              busy={consent === 'busy'}
              failed={consent === 'failed'}
              onCancel={() => setConsent('closed')}
              onConfirm={() => void confirmConsent()}
            />
            {!egressAllowed && (
              <Text size="xs" c="orange">
                {t('consent.blocked')}
              </Text>
            )}
          </Stack>
        </Fieldset>
      )}
    </Stack>
  );
}
