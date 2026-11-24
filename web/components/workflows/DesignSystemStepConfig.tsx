'use client';

import {
  Stack, Group, Select, Text, Fieldset, Switch, NumberInput, TagsInput, MultiSelect,
} from '@mantine/core';
import { useT } from '@/i18n';
import { msg, type Msg } from '@/i18n/msg';
import type {
  DesignSystemStepConfig as DesignSystemStepConfigValue,
  DesignSystemOp,
  DesignSystemParseOptions,
  DesignSystemComposeOptions,
  DesignSystemRequireOptions,
} from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';
import {
  useFd, AskAssistantButton, assistantPrompt, AdvancedSection, StepOrderSelect, stepOrderOptions,
} from './config-kit';

/**
 * The three operations, in plain words. Mirrors `DesignSystemOp` in DesignSystemStepConfig.cs;
 * the C# member names are the persisted values, so only the labels are rewritten for customers.
 */
export const DESIGN_SYSTEM_OP_OPTIONS: { value: DesignSystemOp; label: Msg }[] = [
  { value: 'Parse', label: msg('wfSteps.designSystem.op.Parse') },
  { value: 'Compose', label: msg('wfSteps.designSystem.op.Compose') },
  { value: 'Require', label: msg('wfSteps.designSystem.op.Require') },
];

/** The operation's human label, for cards and chips. Falls back to the raw member name. */
export function designSystemOpLabel(op: DesignSystemOp): Msg | string {
  return DESIGN_SYSTEM_OP_OPTIONS.find((o) => o.value === op)?.label ?? op;
}

const DEFAULT_PARSE: DesignSystemParseOptions = {
  includeDesign: true,
  includeProductMap: true,
  maxFiles: 3000,
  maxBytesPerFile: 262144,
  maxTotalBytes: 25165824,
  extraGlobs: null,
};

const DEFAULT_COMPOSE: DesignSystemComposeOptions = {
  parseStepOrder: 1,
  designFallbackStepOrders: null,
  productFallbackStepOrders: null,
  ingest: true,
};

const DEFAULT_REQUIRE: DesignSystemRequireOptions = {
  requireDesign: true,
  requireProductMap: true,
};

/**
 * Builds the default config for a freshly-added DesignSystem step: a Parse, with the C# record's
 * own defaults. `compose`/`require` are deliberately absent (not null) so `JSON.stringify` emits
 * `{"op":"Parse","version":1,"parse":{...}}` — the shape SPEC 8.1 pins, and the one the C#
 * `Validate()` accepts (exactly the matching options object, the other two absent).
 */
export function createDefaultDesignSystemStepConfig(): DesignSystemStepConfigValue {
  return {
    op: 'Parse',
    version: 1,
    parse: { ...DEFAULT_PARSE },
  };
}

/**
 * The one config shape the server accepts for `config.op`: the matching options object (defaults
 * filled in when absent) and neither of the other two. Pure, so the op switch and every field edit
 * share it.
 */
export function normalizeDesignSystemStepConfig(
  config: DesignSystemStepConfigValue,
): DesignSystemStepConfigValue {
  switch (config.op) {
    case 'Compose':
      return { op: 'Compose', version: 1, compose: config.compose ?? { ...DEFAULT_COMPOSE } };
    case 'Require':
      return { op: 'Require', version: 1, require: config.require ?? { ...DEFAULT_REQUIRE } };
    default:
      return { op: 'Parse', version: 1, parse: config.parse ?? { ...DEFAULT_PARSE } };
  }
}

interface DesignSystemStepConfigProps {
  config: DesignSystemStepConfigValue;
  onChange: (config: DesignSystemStepConfigValue) => void;
  allSteps?: StepData[];
  currentStepIndex?: number;
}

/**
 * Configuration form for a DesignSystem step (deterministic, no model call): read the project's
 * design sources into a brand and product profile, combine them, or require an approved one.
 * Mirrors the backend `DesignSystemStepConfig` record field-for-field; the server's `Validate()`
 * is the only authority, and its bounds are reflected here so the form cannot offer a rejected value.
 */
export function DesignSystemStepConfig({
  config, onChange, allSteps = [], currentStepIndex = 0,
}: DesignSystemStepConfigProps) {
  const t = useT('wfSteps.designSystem');
  const fd = useFd();
  // The server's `Validate()` rejects an op whose mismatched options are non-null and an op whose own
  // options are missing, so every emission is normalised to exactly `{op, version, <matching object>}`.
  const patch = (updates: Partial<DesignSystemStepConfigValue>) =>
    onChange(normalizeDesignSystemStepConfig({ ...config, ...updates }));

  // A config saved before an option object existed (or by a hand-written API call) still renders a
  // usable form: the op is authoritative and its missing object falls back to the C# defaults.
  const parse = config.parse ?? DEFAULT_PARSE;
  const compose = config.compose ?? DEFAULT_COMPOSE;
  const require_ = config.require ?? DEFAULT_REQUIRE;

  const orderOptionValues = stepOrderOptions(allSteps, currentStepIndex, 'before', t.msg).map((o) => o.value);

  /** The picked orders, as numbers, in step order; an empty pick becomes null. */
  const ordersFrom = (ids: string[]): number[] | null => {
    const chosen = orderOptionValues.filter((v) => ids.includes(v)).map(Number).filter((n) => n >= 1);
    return chosen.length > 0 ? chosen : null;
  };

  return (
    <Stack gap="md" onClick={(e) => e.stopPropagation()}>
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <AskAssistantButton prompt={assistantPrompt(currentStepIndex, 'DesignSystem', msg('wfSteps.prompts.designSystem'))} />
      </Group>

      <Select
        // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
        {...fd('config.op')}
        label={t('whatItDoes')}
        size="xs"
        data={DESIGN_SYSTEM_OP_OPTIONS.map((o) => ({ value: o.value, label: t.msg(o.label) }))}
        value={config.op}
        onChange={(v) => v && patch({ op: v as DesignSystemOp, version: 1 })}
        allowDeselect={false}
      />

      {config.op === 'Parse' && (
        <Fieldset legend={t('parse.legend')}>
          <Stack gap="xs">
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.parse.includeDesign')}
              label={t('parse.includeDesign')}
              size="xs"
              checked={parse.includeDesign}
              onChange={(e) => patch({ parse: { ...parse, includeDesign: e.currentTarget.checked } })}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.parse.includeProductMap')}
              label={t('parse.includeProductMap')}
              size="xs"
              checked={parse.includeProductMap}
              onChange={(e) => patch({ parse: { ...parse, includeProductMap: e.currentTarget.checked } })}
            />
            {!parse.includeDesign && !parse.includeProductMap && (
              <Text size="xs" c="orange">{t('parse.chooseOne')}</Text>
            )}

            <AdvancedSection label={t('parse.limits.label')} description={t('parse.limits.description')}>
              <Stack gap="sm">
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.parse.maxFiles')}
                  label={t('parse.limits.maxFiles')} size="xs" min={1} max={5000} value={parse.maxFiles}
                  onChange={(v) => patch({ parse: { ...parse, maxFiles: typeof v === 'number' ? v : 3000 } })}
                />
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.parse.maxBytesPerFile')}
                  label={t('parse.limits.maxBytesPerFile')} size="xs" min={1024} max={1048576} value={parse.maxBytesPerFile}
                  onChange={(v) => patch({ parse: { ...parse, maxBytesPerFile: typeof v === 'number' ? v : 262144 } })}
                />
                <NumberInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.parse.maxTotalBytes')}
                  label={t('parse.limits.maxTotalBytes')} size="xs" min={1048576} max={67108864} value={parse.maxTotalBytes}
                  onChange={(v) => patch({ parse: { ...parse, maxTotalBytes: typeof v === 'number' ? v : 25165824 } })}
                />
                <TagsInput
                  // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
                  {...fd('config.parse.extraGlobs')}
                  label={t('parse.extraGlobs.label')}
                  description={t('parse.extraGlobs.description')}
                  placeholder={t('parse.extraGlobs.placeholder')}
                  size="xs"
                  maxTags={20}
                  value={parse.extraGlobs ?? []}
                  onChange={(globs) => patch({
                    parse: {
                      ...parse,
                      extraGlobs: globs
                        .map((g) => g.trim())
                        .filter((g) => g.length > 0 && g.length <= 100)
                        .slice(0, 20),
                    },
                  })}
                />
              </Stack>
            </AdvancedSection>
          </Stack>
        </Fieldset>
      )}

      {config.op === 'Compose' && (
        <Fieldset legend={t('compose.legend')}>
          <Stack gap="xs">
            <StepOrderSelect
              steps={allSteps}
              currentIndex={currentStepIndex}
              scope="before"
              value={compose.parseStepOrder ?? null}
              onChange={(v) => patch({ compose: { ...compose, parseStepOrder: v ?? 0 } })}
              label={t('compose.parseStep.label')}
              description={t('compose.parseStep.description')}
              size="xs"
              wrapperProps={fd('config.compose.parseStepOrder').wrapperProps}
            />
            {!compose.parseStepOrder || compose.parseStepOrder < 1 ? (
              <Text size="xs" c="orange">{t('compose.parseStep.missing')}</Text>
            ) : null}

            <MultiSelect
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.compose.designFallbackStepOrders')}
              label={t('compose.designFallback.label')}
              description={t('compose.designFallback.description')}
              size="xs"
              data={stepOrderOptions(allSteps, currentStepIndex, 'before', t.msg)}
              value={(compose.designFallbackStepOrders ?? []).map(String)}
              onChange={(ids) => patch({
                compose: { ...compose, designFallbackStepOrders: ordersFrom(ids) },
              })}
              placeholder={t('compose.noFallback')}
              clearable
            />
            <MultiSelect
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.compose.productFallbackStepOrders')}
              label={t('compose.productFallback.label')}
              description={t('compose.productFallback.description')}
              size="xs"
              data={stepOrderOptions(allSteps, currentStepIndex, 'before', t.msg)}
              value={(compose.productFallbackStepOrders ?? []).map(String)}
              onChange={(ids) => patch({
                compose: { ...compose, productFallbackStepOrders: ordersFrom(ids) },
              })}
              placeholder={t('compose.noFallback')}
              clearable
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.compose.ingest')}
              label={t('compose.ingest')}
              size="xs"
              checked={compose.ingest}
              onChange={(e) => patch({ compose: { ...compose, ingest: e.currentTarget.checked } })}
            />
          </Stack>
        </Fieldset>
      )}

      {config.op === 'Require' && (
        <Fieldset legend={t('require.legend')}>
          <Stack gap="xs">
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.require.requireDesign')}
              label={t('require.design')}
              size="xs"
              checked={require_.requireDesign}
              onChange={(e) => patch({ require: { ...require_, requireDesign: e.currentTarget.checked } })}
            />
            <Switch
              // eslint-disable-next-line i18next/no-literal-string -- fd() takes a form-data field path, not copy
              {...fd('config.require.requireProductMap')}
              label={t('require.product')}
              size="xs"
              checked={require_.requireProductMap}
              onChange={(e) => patch({ require: { ...require_, requireProductMap: e.currentTarget.checked } })}
            />
            {!require_.requireDesign && !require_.requireProductMap && (
              <Text size="xs" c="orange">{t('require.chooseOne')}</Text>
            )}
          </Stack>
        </Fieldset>
      )}
    </Stack>
  );
}
