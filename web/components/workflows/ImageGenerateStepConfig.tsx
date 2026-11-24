'use client';

import {
  Stack, Group, Select, TextInput, NumberInput, Switch, Checkbox, Text, SimpleGrid, Fieldset, Textarea, SegmentedControl,
} from '@mantine/core';
import { MediaPickerField } from '@/components/shared/media/MediaPickerField';
import { useT, msg } from '@/i18n';
import type {
  ImageGenerateStepConfig as ImageGenerateStepConfigValue,
  ImageGenerateMode,
  ImageGenerateResolution,
  VideoSourceKind,
} from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';
import {
  useFd, AskAssistantButton, assistantPrompt, AdvancedSection, ProviderSelect, CostWarning, stepOrderOptions,
} from './config-kit';

const MAX_PROMPT_LENGTH = 4000;
const MAX_VARIATIONS = 4;

/** Long edge in pixels each generation tier makes; an exact size's larger side must fit inside the chosen tier. */
const TIER_EDGE: Record<ImageGenerateResolution, number> = { '1K': 1024, '2K': 2048, '4K': 4096 };
const TIER_VALUES = Object.keys(TIER_EDGE) as ImageGenerateResolution[];
const MIN_OUTPUT_SIDE = 64;
const MAX_OUTPUT_SIDE = 8192;

const MODE_VALUES: ImageGenerateMode[] = ['Generate', 'Edit'];

/** The four ways a picture can be named; `GeneratedClips` (many videos) is deliberately not offered. */
const SOURCE_KIND_VALUES: VideoSourceKind[] = ['ProjectFile', 'PreviousStepOutput', 'StepOutput', 'StepResult', 'BriefFiles'];

/** Builds the default config for a freshly-added ImageGenerate step. Mirrors the C# record defaults (Generate, 1 picture, saved to Files, no egress consent). */
export function createDefaultImageGenerateStepConfig(): ImageGenerateStepConfigValue {
  return {
    mode: 'Generate',
    prompt: '',
    instruction: null,
    source: null,
    variations: 1,
    maxSpendCredits: null,
    providerId: null,
    aspectRatio: null,
    width: null,
    height: null,
    resolution: null,
    outputWidth: null,
    outputHeight: null,
    registerProjectFile: true,
    allowSourceMediaEgress: false,
    fileNamePrefix: null,
    itemList: null,
  };
}

interface ImageGenerateStepConfigProps {
  config: ImageGenerateStepConfigValue;
  onChange: (config: ImageGenerateStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
  projectId?: string;
}

/**
 * Configuration form for an ImageGenerate step: makes pictures from a prompt, or edits one source picture by an
 * instruction (paid, billed in credits). Mirrors the backend `ImageGenerateStepConfig` record field-for-field; the
 * server's `Validate()` is the authority and its messages are shown against the field they name.
 */
export function ImageGenerateStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0, projectId,
}: ImageGenerateStepConfigProps) {
  const t = useT('wfSteps.imageGenerate');
  const fd = useFd();
  const patch = (updates: Partial<ImageGenerateStepConfigValue>) => onChange({ ...config, ...updates });
  const isEdit = config.mode === 'Edit';
  // An exact size excludes aspect ratio and width/height (and vice versa), so the invalid mix cannot be built in the form.
  const hasExact = config.outputWidth != null || config.outputHeight != null;
  const hasShape = !!config.aspectRatio || config.width != null || config.height != null;
  const exactLongSide = Math.max(config.outputWidth ?? 0, config.outputHeight ?? 0);
  const tierEdge = config.resolution ? TIER_EDGE[config.resolution] : null;
  const tierTooSmall = tierEdge !== null && exactLongSide > tierEdge;
  const exactSide = (v: string | number): number | null => (typeof v === 'number' && v > 0 ? Math.round(v) : null);
  const patchExact = (updates: Partial<ImageGenerateStepConfigValue>) => {
    const next = { ...config, ...updates };
    patch(next.outputWidth != null || next.outputHeight != null ? { ...updates, aspectRatio: null, width: null, height: null } : updates);
  };

  // Generate and Edit use different provider rows and different fields, so switching mode starts those afresh (the picture count carries over)
  // instead of carrying a value the server would refuse.
  const changeMode = (mode: ImageGenerateMode) => {
    if (mode === config.mode) return;
    onChange({
      ...config,
      mode,
      providerId: null,
      width: null,
      height: null,
      prompt: mode === 'Generate' ? (config.prompt ?? '') : null,
      instruction: mode === 'Edit' ? (config.instruction ?? '') : null,
      source: mode === 'Edit' ? (config.source ?? { kind: 'ProjectFile', projectFileId: null, stepOrder: null }) : null,
      allowSourceMediaEgress: false,
    });
  };

  const source = config.source ?? { kind: 'ProjectFile' as VideoSourceKind, projectFileId: null, stepOrder: null };
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
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'ImageGenerate', msg('wfSteps.prompts.imageGenerate'))} />
      </Group>

      <Fieldset legend={t('what.legend')}>
        <Stack gap="xs">
          <div
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.mode').wrapperProps}>
            <Text size="xs" fw={500} mb={4}>{t('what.mode')}</Text>
            <SegmentedControl
              aria-label={t('what.modeAria')}
              fullWidth
              size="xs"
              data={MODE_VALUES.map((v) => ({ value: v, label: t(`mode.${v}`) }))}
              value={config.mode}
              onChange={(v) => changeMode(v as ImageGenerateMode)}
            />
          </div>
          {!isEdit && (
            <Textarea
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.prompt')}
              label={t('what.prompt.label')}
              description={t('what.prompt.description')}
              size="xs"
              rows={3}
              maxLength={MAX_PROMPT_LENGTH}
              value={config.prompt ?? ''}
              onChange={(e) => patch({ prompt: e.target.value })}
            />
          )}
          <Textarea
            // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
            {...fd('config.itemList')}
            label={t('what.itemList.label')}
            description={t('what.itemList.description')}
            size="xs"
            rows={3}
            value={config.itemList ?? ''}
            onChange={(e) => patch({ itemList: e.target.value || null })}
          />
          {isEdit && (
            <Textarea
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.instruction')}
              label={t('what.instruction.label')}
              description={t('what.instruction.description')}
              size="xs"
              rows={3}
              maxLength={MAX_PROMPT_LENGTH}
              value={config.instruction ?? ''}
              onChange={(e) => patch({ instruction: e.target.value })}
            />
          )}
        </Stack>
      </Fieldset>

      {isEdit && (
        <Fieldset legend={t('source.legend')}>
          <Stack gap="xs">
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
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
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.source.projectFileId')}
                projectId={projectId}
                kind="image"
                label={t('source.file')}
                value={source.projectFileId ? [source.projectFileId] : []}
                onChange={(picked) => patch({ source: { kind: 'ProjectFile', projectFileId: picked[0]?.id ?? null, stepOrder: null, stepResultId: null } })}
              />
            )}
            {kind === 'StepOutput' && (
              <Select
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
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
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.source.stepResultId')}
                label={t('source.stepResult.label')}
                description={t('source.stepResult.description')}
                size="xs"
                value={source.stepResultId ?? ''}
                onChange={(e) => patch({ source: { kind: 'StepResult', projectFileId: null, stepOrder: null, stepResultId: e.target.value.trim() || null } })}
              />
            )}
            <Checkbox
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.allowSourceMediaEgress')}
              label={t('source.egress.label')}
              description={t('source.egress.description')}
              checked={config.allowSourceMediaEgress === true}
              onChange={(e) => patch({ allowSourceMediaEgress: e.currentTarget.checked })}
            />
          </Stack>
        </Fieldset>
      )}

      <Fieldset legend={t('cost.legend')}>
        <Stack gap="xs">
          <ProviderSelect
            capability={isEdit ? 'ImageEdit' : 'ImageGeneration'}
            label={t('cost.provider')}
            value={config.providerId}
            onChange={(providerId) => patch({ providerId })}
            nullMeans={isEdit ? t('cost.nullMeansEdit') : t('cost.nullMeansGenerate')}
            wrapperProps={fd('config.providerId').wrapperProps}
          />
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.variations')}
              label={t('cost.variations.label')}
              description={t('cost.variations.description', { max: MAX_VARIATIONS })}
              size="xs"
              min={1}
              max={MAX_VARIATIONS}
              value={config.variations ?? 1}
              onChange={(v) => patch({ variations: typeof v === 'number' ? Math.min(MAX_VARIATIONS, Math.max(1, Math.round(v))) : 1 })}
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
        <Fieldset legend={t('advanced.size.legend')}>
          <Stack gap="xs">
            <Select
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.resolution')}
              label={t('advanced.size.resolution.label')}
              size="xs"
              data={[
                { value: '', label: t('advanced.size.resolution.automatic') },
                ...TIER_VALUES.map((v) => ({ value: v, label: v })),
              ]}
              value={config.resolution ?? ''}
              onChange={(v) => patch({ resolution: v ? (v as ImageGenerateResolution) : null })}
              allowDeselect={false}
              error={tierTooSmall ? t('advanced.size.tierTooSmall', { tier: config.resolution ?? '', edge: tierEdge ?? 0, need: exactLongSide }) : undefined}
            />
            <SimpleGrid cols={{ base: 1, sm: 2 }}>
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.outputWidth')}
                label={t('advanced.size.outputWidth.label')}
                size="xs"
                min={MIN_OUTPUT_SIDE}
                max={MAX_OUTPUT_SIDE}
                disabled={hasShape}
                value={config.outputWidth ?? ''}
                onChange={(v) => patchExact({ outputWidth: exactSide(v) })}
              />
              <NumberInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.outputHeight')}
                label={t('advanced.size.outputHeight.label')}
                size="xs"
                min={MIN_OUTPUT_SIDE}
                max={MAX_OUTPUT_SIDE}
                disabled={hasShape}
                value={config.outputHeight ?? ''}
                onChange={(v) => patchExact({ outputHeight: exactSide(v) })}
              />
            </SimpleGrid>
            <Text size="xs" c="dimmed">{t('advanced.size.helper')}</Text>
            {hasShape && <Text size="xs" c="dimmed">{t('advanced.size.clearExactFirst')}</Text>}
          </Stack>
        </Fieldset>
        <TextInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.aspectRatio')}
          label={t('advanced.aspect.label')}
          description={t('advanced.aspect.description')}
          size="xs"
          disabled={hasExact}
          value={config.aspectRatio ?? ''}
          onChange={(e) => patch({ aspectRatio: e.target.value.trim() || null })}
        />
        {!isEdit && !hasExact && (
          <SimpleGrid cols={{ base: 1, sm: 2 }}>
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.width')}
              label={t('advanced.width.label')}
              description={t('advanced.width.description')}
              size="xs"
              min={1}
              max={8192}
              value={config.width ?? ''}
              onChange={(v) => patch({ width: typeof v === 'number' && v > 0 ? Math.round(v) : null })}
            />
            <NumberInput
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.height')}
              label={t('advanced.height.label')}
              description={t('advanced.height.description')}
              size="xs"
              min={1}
              max={8192}
              value={config.height ?? ''}
              onChange={(v) => patch({ height: typeof v === 'number' && v > 0 ? Math.round(v) : null })}
            />
          </SimpleGrid>
        )}
        <TextInput
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.fileNamePrefix')}
          label={t('advanced.prefix.label')}
          description={t('advanced.prefix.description')}
          size="xs"
          value={config.fileNamePrefix ?? ''}
          onChange={(e) => patch({ fileNamePrefix: e.target.value.trim() || null })}
        />
      </AdvancedSection>
    </Stack>
  );
}
