'use client';

import { Stack, Group, Select, TextInput, Switch, Text, Fieldset, Button, ActionIcon } from '@mantine/core';
import { IconPlus, IconTrash } from '@tabler/icons-react';
import { MediaPickerField } from '@/components/shared/media/MediaPickerField';
import type {
  InsertContentRef,
  PinInsertEntry,
  PinInsertStepConfig as PinInsertStepConfigValue,
} from '@/lib/types/workflow';
import { useT, msg } from '@/i18n';
import type { StepData } from './WorkflowStepList';
import { useFd, AskAssistantButton, assistantPrompt, stepOrderOptions } from './config-kit';

/** The server's bound (PinInsertStepConfig.MaxPins). */
export const MAX_PINS = 16;

/** Mirrors the `r{n}` shape PinInsertStepConfig.IsRegionId accepts. */
const REGION_ID = /^r[0-9]{1,4}$/;

// Labels live in the catalogue (wfSteps.pinInsert.contentKind.<value>); the value is what the API receives.
const CONTENT_KIND_VALUES: InsertContentRef['from'][] = ['Step', 'ProjectFile', 'StepResult'];

/** Builds the default config for a freshly-added PinInsert step: the previous step's analysis, one empty pin. */
export function createDefaultPinInsertStepConfig(): PinInsertStepConfigValue {
  return {
    analysis: { from: 'Previous', stepOrder: null },
    pins: [{ regionId: null, content: { from: 'Step', stepOrder: null } }],
    requireApplied: true,
  };
}

/** True when a pin names its content fully (the server refuses one that does not). */
export function isPinComplete(pin: PinInsertEntry): boolean {
  const c = pin.content;
  if (!c) return false;
  if (c.from === 'Step') return typeof c.stepOrder === 'number' && c.stepOrder > 0;
  if (c.from === 'ProjectFile') return !!c.projectFileId;
  return !!c.stepResultId;
}

interface PinInsertStepConfigProps {
  config: PinInsertStepConfigValue;
  onChange: (config: PinInsertStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
  projectId?: string;
}

/**
 * Configuration form for a PinInsert step (deterministic, no model call, free): puts a video or image on the tracked
 * screens a VideoAnalyze step found. Mirrors the backend `PinInsertStepConfig` record field-for-field; the server's
 * `Validate()` and the save validator are the authority.
 */
export function PinInsertStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0, projectId,
}: PinInsertStepConfigProps) {
  const t = useT('wfSteps.pinInsert');
  const fd = useFd();
  const contentKindOptions = CONTENT_KIND_VALUES.map((v) => ({ value: v, label: t(`contentKind.${v}`) }));
  const patch = (updates: Partial<PinInsertStepConfigValue>) => onChange({ ...config, ...updates });
  const pins = config.pins ?? [];
  const setPin = (index: number, pin: PinInsertEntry) => patch({ pins: pins.map((p, i) => (i === index ? pin : p)) });

  const priorSteps = stepOrderOptions(allSteps, currentStepIndex, 'before', t.msg);
  const analyzeSteps = priorSteps.filter((o) => allSteps[Number(o.value) - 1]?.stepType === 'VideoAnalyze');
  const analysisValue = config.analysis?.from === 'Step' && config.analysis.stepOrder ? String(config.analysis.stepOrder) : 'previous';
  const hasCatchAll = pins.some((p) => !p.regionId);

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'PinInsert', msg('wfSteps.prompts.pinInsert'))} />
      </Group>

      <Fieldset legend={t('trackedScreens.legend')}>
        <Select
          // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
          {...fd('config.analysis')}
          label={t('trackedScreens.foundBy')}
          size="xs"
          data={[{ value: 'previous', label: t('trackedScreens.previousStep') }, ...analyzeSteps]}
          value={analysisValue}
          onChange={(v) => patch({ analysis: !v || v === 'previous' ? { from: 'Previous', stepOrder: null } : { from: 'Step', stepOrder: Number(v) } })}
          allowDeselect={false}
        />
      </Fieldset>

      <Fieldset legend={t('contentLegend')}>
        <Stack gap="sm">
          {pins.map((pin, index) => {
            // eslint-disable-next-line i18next/no-literal-string -- enum value / fallback, not copy
            const kind = pin.content?.from ?? 'Step';
            const regionError = pin.regionId && !REGION_ID.test(pin.regionId) ? t('screenIdHint') : null;
            return (
              <Stack key={index} gap={6} data-testid={`pin-${index}`}>
                <Group gap="xs" align="flex-end" wrap="nowrap">
                  <TextInput {...fd(`config.pins.${index}.regionId`)}
                    label={t('screen.label')}
                    description={t('screen.description')}
                    size="xs"
                    style={{ flex: 1 }}
                    value={pin.regionId ?? ''}
                    error={regionError}
                    onChange={(e) => setPin(index, { ...pin, regionId: e.currentTarget.value.trim() || null })}
                  />
                  <ActionIcon
                    variant="subtle"
                    color="red"
                    aria-label={t('removePin', { n: index + 1 })}
                    disabled={pins.length <= 1}
                    onClick={() => patch({ pins: pins.filter((_, i) => i !== index) })}
                  >
                    <IconTrash size={14} />
                  </ActionIcon>
                </Group>
                <Select {...fd(`config.pins.${index}.content.from`)}
                  label={t('content')}
                  size="xs"
                  data={contentKindOptions}
                  value={kind}
                  onChange={(v) => v && setPin(index, { ...pin, content: { from: v as InsertContentRef['from'] } })}
                  allowDeselect={false}
                />
                {kind === 'Step' && (
                  <Select {...fd(`config.pins.${index}.content.stepOrder`)}
                    label={t('step.label')}
                    size="xs"
                    data={priorSteps}
                    value={pin.content?.stepOrder != null ? String(pin.content.stepOrder) : null}
                    onChange={(v) => setPin(index, { ...pin, content: { from: 'Step', stepOrder: v ? Number(v) : null } })}
                    placeholder={t('step.placeholder')}
                  />
                )}
                {kind === 'ProjectFile' && (
                  <MediaPickerField {...fd(`config.pins.${index}.content.projectFileId`)}
                    projectId={projectId}
                    kind="both"
                    label={t('file.label')}
                    value={pin.content?.projectFileId ? [pin.content.projectFileId] : []}
                    onChange={(picked) => setPin(index, { ...pin, content: { from: 'ProjectFile', projectFileId: picked[0]?.id ?? null } })}
                  />
                )}
                {kind === 'StepResult' && (
                  <TextInput {...fd(`config.pins.${index}.content.stepResultId`)}
                    label={t('stepResult.label')}
                    description={t('stepResult.description')}
                    size="xs"
                    value={pin.content?.stepResultId ?? ''}
                    onChange={(e) => setPin(index, { ...pin, content: { from: 'StepResult', stepResultId: e.currentTarget.value.trim() || null } })}
                  />
                )}
              </Stack>
            );
          })}
          <Button
            size="compact-xs"
            variant="light"
            leftSection={<IconPlus size={12} />}
            disabled={pins.length >= MAX_PINS}
            // A second pin needs its own screen: only one pin may cover "every screen".
            onClick={() => patch({ pins: [...pins, { regionId: hasCatchAll ? `r${pins.length}` : null, content: { from: 'Step', stepOrder: null } }] })}
          >
            {t('addPin')}
          </Button>
        </Stack>
      </Fieldset>

      <Switch
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.requireApplied')}
        label={t('requireApplied.label')}
        description={t('requireApplied.description')}
        checked={config.requireApplied !== false}
        onChange={(e) => patch({ requireApplied: e.currentTarget.checked })}
      />
    </Stack>
  );
}
