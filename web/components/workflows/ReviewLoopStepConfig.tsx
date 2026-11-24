'use client';

import { Stack, Divider, Text, Group, Switch, NumberInput, SimpleGrid } from '@mantine/core';
import { useT, msg } from '@/i18n';
import { ProviderSelect, StepOrderSelect, AskAssistantButton, ModeHint, assistantPrompt, AdvancedSection, useFd } from './config-kit';
import { SchemaDrivenStepConfigForm } from './SchemaDrivenStepConfigForm';
import { OutputSchemaViewer } from './OutputSchemaViewer';
import type { ReviewLoopStepConfig as ReviewLoopStepConfigValue, VisualCheckConfig } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

/**
 * Default config for a ReviewLoop step's calibrated early stop. Mirrors the C#
 * `ReviewLoopStepConfig` record defaults exactly
 * (`inference/src/ReelBolt.Shared/Workflows/ReviewLoopStepConfig.cs`): `Enabled = false`,
 * `AcceptAt = 0.85`, `BimodalMargin = 0.15`, `ProviderId = null`, `TimeoutSeconds = 5`. `Enabled` is
 * `false` deliberately — disabled, absent or unparseable config is byte-identically the
 * pre-decision-engine executor.
 *
 * This is the TypeScript source of truth the `default`s in `REVIEW_LOOP_SCHEMA` are cross-checked
 * against, the same relationship `createDefaultVideoGenerateStepConfig` has with
 * `VIDEO_GENERATE_SCHEMA`.
 */
export function createDefaultReviewLoopStepConfig(): ReviewLoopStepConfigValue {
  return {
    version: 1,
    enabled: false,
    acceptAt: 0.85,
    bimodalMargin: 0.15,
    providerId: null,
    timeoutSeconds: 5,
  };
}

/**
 * `true` when `candidate` is field-for-field the C# `ReviewLoopStepConfig` default — i.e. the author
 * touched nothing on the calibrated early stop. Keeps a `null` `reviewLoopConfigJson` null across an
 * edit that only touched the three `WorkflowStep` columns, so opening a step and pressing Apply does
 * not rewrite the column with an encoding of today's defaults.
 */
function isPristineDefault(candidate: Record<string, unknown>): boolean {
  const defaults = createDefaultReviewLoopStepConfig() as unknown as Record<string, unknown>;
  const keys = Object.keys(candidate);
  if (keys.length !== Object.keys(defaults).length) return false;
  return keys.every((key) => candidate[key] === defaults[key]);
}

/** Mirrors the C# `VisualCheckConfig` defaults (off, 8 frames, 5 % margin, 2.5 % text, cap 6, default Vision row). */
export function createDefaultVisualCheckConfig(): VisualCheckConfig {
  return {
    enabled: false,
    frames: 8,
    safeMarginPercent: 5,
    minTextHeightPercent: 2.5,
    failScoreCap: 6,
    visionProviderId: null,
  };
}

const clamp = (v: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, v));

/**
 * The "Visual check" block: before each review it samples frames of the target step's video and flags text outside
 * the safe margin or too small to read; a serious finding caps the iteration's score. Off by default and absent from
 * the saved config until the author switches it on, so an untouched review loop saves exactly as before.
 */
function VisualCheckFields({ value, onChange }: { value: VisualCheckConfig | null | undefined; onChange: (next: VisualCheckConfig) => void }) {
  const t = useT('wfSteps.reviewLoop.visualCheck');
  const fd = useFd();
  const check = { ...createDefaultVisualCheckConfig(), ...value };
  const patch = (updates: Partial<VisualCheckConfig>) => onChange({ ...check, ...updates });
  return (
    <Stack gap="xs">
      <Switch
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.visualCheck.enabled')}
        label={t('enabled.label')}
        description={t('enabled.description')}
        checked={check.enabled}
        onChange={(e) => patch({ enabled: e.currentTarget.checked })}
      />
      {check.enabled && (
        <>
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.visualCheck.frames')}
              label={t('frames.label')}
              description={t('frames.description')}
              size="xs"
              min={2}
              max={16}
              value={check.frames}
              onChange={(v) => patch({ frames: typeof v === 'number' ? clamp(Math.round(v), 2, 16) : 8 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.visualCheck.failScoreCap')}
              label={t('failScoreCap')}
              size="xs"
              min={0}
              max={10}
              value={check.failScoreCap}
              onChange={(v) => patch({ failScoreCap: typeof v === 'number' ? clamp(Math.round(v), 0, 10) : 6 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.visualCheck.safeMarginPercent')}
              label={t('safeMargin.label')}
              description={t('safeMargin.description')}
              size="xs"
              min={0}
              max={20}
              step={0.5}
              decimalScale={1}
              value={check.safeMarginPercent}
              onChange={(v) => patch({ safeMarginPercent: typeof v === 'number' ? clamp(v, 0, 20) : 5 })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.visualCheck.minTextHeightPercent')}
              label={t('minText.label')}
              description={t('minText.description')}
              size="xs"
              min={0.5}
              max={20}
              step={0.5}
              decimalScale={1}
              value={check.minTextHeightPercent}
              onChange={(v) => patch({ minTextHeightPercent: typeof v === 'number' ? clamp(v, 0.5, 20) : 2.5 })}
            />
          </SimpleGrid>
          <ProviderSelect
            capability="Vision"
            label={t('vision.label')}
            description={t('vision.description')}
            value={check.visionProviderId ?? null}
            onChange={(visionProviderId) => patch({ visionProviderId })}
            wrapperProps={fd('config.visualCheck.visionProviderId').wrapperProps}
          />
        </>
      )}
    </Stack>
  );
}

interface ReviewLoopStepConfigProps {
  minScore: number | null;
  maxIterations: number;
  loopTargetStepOrder: number | null;
  /**
   * The step's parsed `reviewLoopConfigJson`. `null`/absent renders the C# defaults (`enabled:
   * false`, i.e. today's exact reviewer path), and the emitted update omits the config entirely
   * until the author actually changes one of its fields.
   */
  reviewLoopConfig?: ReviewLoopStepConfigValue | null;
  onChange: (updates: {
    minScore?: number | null;
    maxIterations?: number;
    loopTargetStepOrder?: number | null;
    reviewLoopConfig?: ReviewLoopStepConfigValue | null;
  }) => void;
  previousSteps?: StepData[];
  currentStepIndex?: number;
}

export function ReviewLoopStepConfig({
  minScore,
  maxIterations,
  loopTargetStepOrder,
  reviewLoopConfig,
  onChange,
  previousSteps = [],
  currentStepIndex = 0,
}: ReviewLoopStepConfigProps) {
  const t2 = useT('wfSteps.reviewLoop');
  const config = reviewLoopConfig ?? createDefaultReviewLoopStepConfig();

  const handleFormChange = (formValue: Record<string, unknown>) => {
    // The three `WorkflowStep` COLUMNS share this schema with the early-stop config members, so
    // split them back out rather than letting them leak into the config JSON. The rest-destructure
    // keeps every field the schema does not know about.
    const {
      minScore: nextMinScore,
      maxIterations: nextMaxIterations,
      loopTargetStepOrder: nextLoopTargetStepOrder,
      ...nextConfig
    } = formValue;
    const updates: {
      minScore: number | null;
      maxIterations: number;
      loopTargetStepOrder: number | null;
      reviewLoopConfig?: ReviewLoopStepConfigValue;
    } = {
      minScore: (nextMinScore as number | null) ?? null,
      maxIterations: (nextMaxIterations as number) ?? maxIterations,
      loopTargetStepOrder: (nextLoopTargetStepOrder as number | null) ?? null,
    };

    // A step that had NO config and is still on the pristine C# defaults has not changed: OMIT the
    // key so the caller's partial merge leaves `reviewLoopConfigJson` exactly as it was (`null`),
    // instead of rewriting the column with an encoding of today's defaults on every Open-Apply.
    if (reviewLoopConfig || !isPristineDefault(nextConfig)) {
      updates.reviewLoopConfig = nextConfig as unknown as ReviewLoopStepConfigValue;
    }

    onChange(updates);
  };

  const colKeys = ['minScore', 'maxIterations', 'loopTargetStepOrder'];

  return (
    <Stack gap="md">
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t2('intro')}
        </Text>
        <AskAssistantButton
          prompt={assistantPrompt(currentStepIndex, 'ReviewLoop', msg('wfSteps.prompts.reviewLoop'))}
        />
      </Group>
      <SchemaDrivenStepConfigForm
        stepType="ReviewLoop"
        value={{ ...config, minScore, maxIterations, loopTargetStepOrder }}
        onChange={handleFormChange}
        // The allowlist names the schema's real, now-wired fields — the three step COLUMNS plus the
        // calibrated early stop (`enabled`/`acceptAt`/`bimodalMargin`/`providerId`/`timeoutSeconds`)
        // — and `handleFormChange` carries every one of them. `version` is deliberately absent: it is
        // the config envelope's constant, never author-edited.
        fields={['loopTargetStepOrder', 'minScore', 'maxIterations', 'enabled', 'acceptAt', 'bimodalMargin', 'providerId', 'timeoutSeconds']}
        pathFor={(k) => (colKeys.includes(k) ? `col.${k}` : `config.${k}`)}
        labels={{
          loopTargetStepOrder: t2('labels.loopTargetStepOrder'),
          minScore: t2('labels.minScore'),
          maxIterations: t2('labels.maxIterations'),
          enabled: t2('labels.enabled'),
          acceptAt: t2('labels.acceptAt'),
          bimodalMargin: t2('labels.bimodalMargin'),
          providerId: t2('labels.providerId'),
          timeoutSeconds: t2('labels.timeoutSeconds'),
        }}
        hints={{
          minScore: t2('hints.minScore'),
          maxIterations: t2('hints.maxIterations'),
          enabled: t2('hints.enabled'),
          acceptAt: t2('hints.acceptAt'),
          bimodalMargin: t2('hints.bimodalMargin'),
          timeoutSeconds: t2('hints.timeoutSeconds'),
        }}
        renderers={{
          loopTargetStepOrder: ({ value, onChange, label, wrapperProps }) => (
            <StepOrderSelect
              label={label}
              description={t2('hints.loopTargetStepOrder')}
              steps={previousSteps}
              currentIndex={currentStepIndex}
              scope="before"
              value={value as number | null}
              onChange={onChange}
              wrapperProps={wrapperProps}
            />
          ),
          enabled: ({ value, onChange, label, description, wrapperProps }) => (
            <Stack gap={4}>
              <Switch
                label={label}
                description={description}
                checked={value === true}
                onChange={(e) => onChange(e.currentTarget.checked)}
                wrapperProps={wrapperProps}
              />
            </Stack>
          ),
          providerId: ({ value, onChange, label, wrapperProps }) => (
            <ProviderSelect
              capability="Decision"
              label={label}
              size="sm"
              description={t2('provider.description')}
              value={value as string | null}
              onChange={onChange}
              nullMeans={t2('provider.nullMeans')}
              wrapperProps={wrapperProps}
            />
          ),
        }}
      />
      <AdvancedSection label={t2('advanced.label')} description={t2('advanced.description')}>
        <VisualCheckFields
          value={config.visualCheck}
          onChange={(visualCheck) => onChange({ reviewLoopConfig: { ...config, visualCheck } })}
        />
      </AdvancedSection>
      <ModeHint>{t2('scoreHint')}</ModeHint>

      <Divider />

      <OutputSchemaViewer previousSteps={previousSteps} currentStepIndex={currentStepIndex} />
    </Stack>
  );
}
