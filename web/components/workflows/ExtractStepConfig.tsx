'use client';

import { useState } from 'react';
import {
  Stack, Select, TextInput, NumberInput, TagsInput, Switch, Collapse, Button, Text, Divider, Code, Group, Paper, SimpleGrid, Fieldset,
} from '@mantine/core';
import { IconChevronDown, IconChevronUp, IconSettings } from '@tabler/icons-react';
import { useT } from '@/i18n';
import { SchemaDrivenStepConfigForm } from './SchemaDrivenStepConfigForm';
import type {
  ExtractStepConfig as ExtractStepConfigValue,
  ExtractOperation,
  ExtractInputRef,
  ExtractInputSource,
  ExtractUnknownIdBehaviour,
} from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';
import { useFd, AdvancedSection } from './config-kit';

/** Builds a sensible default config for a freshly-added or operation-switched Extract step. */
export function createDefaultExtractStepConfig(operation: ExtractOperation = 'Project'): ExtractStepConfigValue {
  const base: ExtractStepConfigValue = {
    version: 1,
    operation,
    inputs: {},
    path: null,
    fields: null,
    idField: null,
    idPrefix: 'i',
    sortBy: null,
    take: null,
    skip: 0,
    idsPath: null,
    recordsPath: null,
    onUnknownId: 'Fail',
    categories: null,
    includeExtensions: null,
    excludePathContains: null,
    includeSummaries: true,
    includeContent: false,
    maxCharsPerFile: 2000,
    maxOutputChars: 24_000,
    expect: null,
  };

  if (operation === 'Project') {
    return {
      ...base,
      inputs: { source: { from: 'Previous' } },
      path: '$.components',
      take: 40,
    };
  }
  if (operation === 'Resolve') {
    return {
      ...base,
      inputs: { ids: { from: 'Previous' }, records: { from: 'Step', stepOrder: null } },
      idsPath: '$.selectedIds',
      recordsPath: '$.view.items',
    };
  }
  // Files
  return {
    ...base,
    inputs: { source: { from: 'ProjectFiles' } },
  };
}

const INPUT_SOURCE_VALUES: ExtractInputSource[] = ['Previous', 'Step', 'Accumulated', 'ProjectFiles'];

interface InputRefPickerProps {
  label: string;
  value: ExtractInputRef | undefined;
  onChange: (ref: ExtractInputRef) => void;
  priorStepOptions: { value: string; label: string }[];
  /** Restricts the offered sources, e.g. VideoCompileStepConfig's `decision` only accepts Previous|Step. */
  allowedSources?: ExtractInputSource[];
}

/** Exported so other step-config forms (e.g. VideoCompileStepConfig's `decision` field) can reuse it verbatim. */
export function InputRefPicker({ label, value, onChange, priorStepOptions, allowedSources }: InputRefPickerProps) {
  const t = useT('wfSteps.extract');
  const from = value?.from ?? 'Previous';
  const sourceOptions = (allowedSources ? INPUT_SOURCE_VALUES.filter((v) => allowedSources.includes(v)) : INPUT_SOURCE_VALUES)
    .map((v) => ({ value: v, label: t(`inputSource.${v}`) }));
  return (
    <Group gap="xs" align="flex-end" grow onClick={(e) => e.stopPropagation()}>
      <Select
        label={label}
        size="xs"
        value={from}
        data={sourceOptions}
        onChange={(v) => {
          if (!v) return;
          const nextFrom = v as ExtractInputSource;
          onChange({ from: nextFrom, stepOrder: nextFrom === 'Step' ? (value?.stepOrder ?? null) : null });
        }}
      />
      {from === 'Step' && (
        <Select
          label={t('step')}
          size="xs"
          data={priorStepOptions}
          value={value?.stepOrder != null ? String(value.stepOrder) : null}
          onChange={(v) => onChange({ from: 'Step', stepOrder: v ? Number(v) : null })}
          placeholder={priorStepOptions.length > 0 ? t('selectStep') : t('noEarlierSteps')}
          disabled={priorStepOptions.length === 0}
        />
      )}
    </Group>
  );
}

interface ExtractStepConfigProps {
  config: ExtractStepConfigValue;
  onChange: (config: ExtractStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
}

/**
 * Configuration form for a Extract step (deterministic, non-LLM projection).
 * Operations mirror the backend `ExtractStepConfig` record exactly: `project`, `resolve`, `files`.
 */
export function ExtractStepConfig({ config, onChange, allSteps = [], currentStepIndex = 0 }: ExtractStepConfigProps) {
  const t = useT('wfSteps.extract');
  const fd = useFd();
  const [expectOpen, setExpectOpen] = useState(Boolean(config.expect));

  const priorStepOptions = allSteps
    .slice(0, currentStepIndex)
    .map((previousStep, index) => {
      const stepOrder = index + 1;
      const label = previousStep.label?.trim() || t('stepFallback', { n: stepOrder });
      return { value: String(stepOrder), label: `${stepOrder}. ${label}` };
    });

  const setInput = (name: string, ref: ExtractInputRef) => {
    onChange({ ...config, inputs: { ...config.inputs, [name]: ref } });
  };

  const patch = (updates: Partial<ExtractStepConfigValue>) => onChange({ ...config, ...updates });

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Select
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.operation')}
        label={t('operation.label')}
        description={t('operation.description')}
        size="sm"
        value={config.operation}
        data={[
          { value: 'Project', label: t('operation.Project') },
          { value: 'Resolve', label: t('operation.Resolve') },
          { value: 'Files', label: t('operation.Files') },
        ]}
        onChange={(v) => {
          if (!v) return;
          onChange(createDefaultExtractStepConfig(v as ExtractOperation));
        }}
      />

      <Fieldset legend={t('inputs.legend')}>
        <Stack gap="xs">
          {config.operation === 'Project' && (
            <>
              <InputRefPicker
                label={t('inputs.source')}
                value={config.inputs.source}
                onChange={(ref) => setInput('source', ref)}
                priorStepOptions={priorStepOptions}
              />
              <TextInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.path')}
                label={t('inputs.path.label')}
                description={t('inputs.path.description')}
                // eslint-disable-next-line i18next/no-literal-string -- syntax example, not copy
                placeholder="$.components"
                size="xs"
                value={config.path ?? ''}
                onChange={(e) => patch({ path: e.target.value || null })}
              />
            </>
          )}
          {config.operation === 'Resolve' && (
            <>
              <InputRefPicker
                label={t('inputs.ids')}
                value={config.inputs.ids}
                onChange={(ref) => setInput('ids', ref)}
                priorStepOptions={priorStepOptions}
              />
              <TextInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.idsPath')}
                label={t('inputs.idsPath.label')}
                description={t('inputs.idsPath.description')}
                // eslint-disable-next-line i18next/no-literal-string -- syntax example, not copy
                placeholder="$.selectedIds"
                size="xs"
                value={config.idsPath ?? ''}
                onChange={(e) => patch({ idsPath: e.target.value || null })}
              />
              <Divider variant="dashed" />
              <InputRefPicker
                label={t('inputs.records')}
                value={config.inputs.records}
                onChange={(ref) => setInput('records', ref)}
                priorStepOptions={priorStepOptions}
              />
              <TextInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.recordsPath')}
                label={t('inputs.recordsPath.label')}
                description={t('inputs.recordsPath.description')}
                // eslint-disable-next-line i18next/no-literal-string -- syntax example, not copy
                placeholder="$.view.items"
                size="xs"
                value={config.recordsPath ?? ''}
                onChange={(e) => patch({ recordsPath: e.target.value || null })}
              />
            </>
          )}
          {config.operation === 'Files' && (
            <>
              <TagsInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.categories')}
                label={t('inputs.categories')}
                // eslint-disable-next-line i18next/no-literal-string -- syntax example, not copy
                placeholder="component, route, style"
                size="xs"
                value={config.categories ?? []}
                onChange={(v) => patch({ categories: v.length > 0 ? v : null })}
              />
              <TagsInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.includeExtensions')}
                label={t('inputs.includeExtensions')}
                // eslint-disable-next-line i18next/no-literal-string -- syntax example, not copy
                placeholder=".tsx, .ts"
                size="xs"
                value={config.includeExtensions ?? []}
                onChange={(v) => patch({ includeExtensions: v.length > 0 ? v : null })}
              />
              <TagsInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.excludePathContains')}
                label={t('inputs.excludePathContains')}
                // eslint-disable-next-line i18next/no-literal-string -- syntax example, not copy
                placeholder="node_modules, .test."
                size="xs"
                value={config.excludePathContains ?? []}
                onChange={(v) => patch({ excludePathContains: v.length > 0 ? v : null })}
              />
            </>
          )}
          {(config.operation === 'Project' || config.operation === 'Files') && (
            <SchemaDrivenStepConfigForm
              stepType="Extract"
              value={config as unknown as Record<string, unknown>}
              onChange={(next) => onChange({ ...config, ...next } as ExtractStepConfigValue)}
              fields={config.operation === 'Project' ? ['fields', 'sortBy', 'take'] : []}
            />
          )}
        </Stack>
      </Fieldset>

      <Fieldset legend={t('idSkip.legend')}>
        <SchemaDrivenStepConfigForm
          stepType="Extract"
          value={config as unknown as Record<string, unknown>}
          onChange={(next) => onChange({ ...config, ...next } as ExtractStepConfigValue)}
          fields={config.operation === 'Resolve' ? ['idField', 'idPrefix', 'skip', 'onUnknownId'] : ['idField', 'idPrefix', 'skip']}
        />
      </Fieldset>

      <Fieldset legend={t('output.legend')}>
        <Stack gap="xs">
          <SchemaDrivenStepConfigForm
            stepType="Extract"
            value={config as unknown as Record<string, unknown>}
            onChange={(next) => onChange({ ...config, ...next } as ExtractStepConfigValue)}
            fields={config.operation === 'Files' ? ['includeSummaries', 'includeContent', 'maxCharsPerFile', 'maxOutputChars'] : ['includeSummaries', 'includeContent', 'maxCharsPerFile', 'maxOutputChars']}
          />
          <Button
            variant="subtle"
            size="compact-xs"
            leftSection={<IconSettings size={14} />}
            rightSection={expectOpen ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
            onClick={() => setExpectOpen(!expectOpen)}
          >
            {t('output.expectations')}
          </Button>
          <Collapse expanded={expectOpen}>
            <Stack gap="xs">
              <TagsInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.expect')}
                label={t('output.requiredPaths.label')}
                description={t('output.requiredPaths.description')}
                // eslint-disable-next-line i18next/no-literal-string -- syntax example, not copy
                placeholder="$.view.items"
                size="xs"
                value={config.expect?.requiredPaths ?? []}
                onChange={(v) => patch({ expect: { ...config.expect, requiredPaths: v.length > 0 ? v : null } })}
              />
              <SimpleGrid cols={{ base: 1, sm: 2 }}>
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.expect')}
                  label={t('output.minItems')}
                  min={0}
                  size="xs"
                  value={config.expect?.minItems ?? undefined}
                  onChange={(v) => patch({ expect: { ...config.expect, minItems: typeof v === 'number' ? v : null } })}
                />
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.expect')}
                  label={t('output.maxItems')}
                  min={0}
                  size="xs"
                  value={config.expect?.maxItems ?? undefined}
                  onChange={(v) => patch({ expect: { ...config.expect, maxItems: typeof v === 'number' ? v : null } })}
                />
              </SimpleGrid>
              <TagsInput
                // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                {...fd('config.expect')}
                label={t('output.nonEmpty.label')}
                description={t('output.nonEmpty.description')}
                size="xs"
                value={config.expect?.nonEmptyStringPaths ?? []}
                onChange={(v) => patch({ expect: { ...config.expect, nonEmptyStringPaths: v.length > 0 ? v : null } })}
              />
            </Stack>
          </Collapse>
        </Stack>
      </Fieldset>

      <AdvancedSection label={t('technical.label')} description={t('technical.description')}>
      <Paper p="xs" withBorder>
        <Text size="xs" fw={600} c="dimmed" mb={4}>{t('technical.handsOver')}</Text>
        <Code block style={{ fontSize: 12 }}>
{`{
  "view": { "items": [ { "id": "c1", "name": "..." } ] },
  "meta": {
    "operation": "${config.operation.toLowerCase()}",
    "shape": "items",
    "itemCount": 40,
    "totalItemCount": 137,
    "truncated": true,
    "droppedItems": 97
  }
}`}
        </Code>
      </Paper>
      </AdvancedSection>
    </Stack>
  );
}
