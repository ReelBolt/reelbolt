'use client';

import { Button, Checkbox, Menu, Select, Stack, Text, Fieldset, Group } from '@mantine/core';
import { useT, msg } from '@/i18n';
import { useAppBuilder } from '@/components/workflow-apps/AppBuilderContext';
import { END_BRANCH, conditionReadable, conditionSnippet } from '@/lib/workflow-apps/app-input-builder';
import { ProviderSelect, StepOrderSelect, AskAssistantButton, ModeHint, useFd, assistantPrompt, AdvancedSection } from './config-kit';
import { SchemaDrivenStepConfigForm } from './SchemaDrivenStepConfigForm';
import { OutputSchemaViewer } from './OutputSchemaViewer';
import type { ConditionalStepConfig as ConditionalStepConfigValue } from '@/lib/types/workflow';
import type { StepData } from './WorkflowStepList';

/**
 * Default config for a Conditional step's decision arm. Mirrors the C# `ConditionalStepConfig`
 * record defaults exactly (`inference/src/ReelBolt.Shared/Workflows/ConditionalStepConfig.cs`):
 * `Mode = ConditionMode.Expression`, `DecisionQuestion = null`, `Threshold = 0.5`,
 * `ProviderId = null`, `TimeoutSeconds = 5`. Nothing here turns the decision arm on — Expression is
 * the default everywhere and no shipped configuration sets Decision.
 *
 * This is the TypeScript source of truth the `default`s in `CONDITIONAL_SCHEMA` are cross-checked
 * against, the same relationship `createDefaultVideoGenerateStepConfig` has with
 * `VIDEO_GENERATE_SCHEMA`.
 */
export function createDefaultConditionalStepConfig(): ConditionalStepConfigValue {
  return {
    version: 1,
    mode: 'Expression',
    decisionQuestion: null,
    threshold: 0.5,
    providerId: null,
    timeoutSeconds: 5,
  };
}

/**
 * `true` when `candidate` is field-for-field the C# `ConditionalStepConfig` default — i.e. the
 * author touched nothing on the decision arm. Keeps a `null` `conditionalConfigJson` null across an
 * edit that only touched the condition expression, so opening a step and pressing Apply does not
 * rewrite the column with an encoding of today's defaults.
 */
function isPristineDefault(candidate: Record<string, unknown>): boolean {
  const defaults = createDefaultConditionalStepConfig() as unknown as Record<string, unknown>;
  const keys = Object.keys(candidate);
  if (keys.length !== Object.keys(defaults).length) return false;
  return keys.every((key) => candidate[key] === defaults[key]);
}

interface ConditionalStepConfigProps {
  conditionExpression: string | null;
  trueBranchStepOrder: string | null;
  falseBranchStepOrder: string | null;
  /**
   * The step's parsed `conditionalConfigJson`. `null`/absent renders the C# defaults so the
   * decision arm is visible on every Conditional step, and the emitted update omits the config
   * entirely until the author actually changes one of its fields.
   */
  conditionalConfig?: ConditionalStepConfigValue | null;
  onChange: (updates: {
    conditionExpression?: string | null;
    trueBranchStepOrder?: string | null;
    falseBranchStepOrder?: string | null;
    conditionalConfig?: ConditionalStepConfigValue | null;
  }) => void;
  previousSteps?: StepData[];
  currentStepIndex?: number;
}

export function ConditionalStepConfig({
  conditionExpression,
  trueBranchStepOrder,
  falseBranchStepOrder,
  conditionalConfig,
  onChange,
  previousSteps = [],
  currentStepIndex = 0,
}: ConditionalStepConfigProps) {
  const t = useT('wfSteps.conditional');
  const config = conditionalConfig ?? createDefaultConditionalStepConfig();

  const handleFormChange = (formValue: Record<string, unknown>) => {
    // `conditionExpression` is a WorkflowStep COLUMN sharing this schema with the decision-arm
    // config members, so split it back out rather than letting it leak into the config JSON. The
    // rest-destructure keeps every field the schema does not know about, matching the form's own
    // unknown-field-preserving contract.
    const { conditionExpression: nextConditionExpression, ...nextConfig } = formValue;
    const updates: {
      conditionExpression: string | null;
      conditionalConfig?: ConditionalStepConfigValue;
    } = { conditionExpression: (nextConditionExpression as string | null) ?? null };

    // A step that had NO config and is still on the pristine C# defaults has not changed: OMIT the
    // key so the caller's partial merge leaves `conditionalConfigJson` exactly as it was (`null`),
    // instead of rewriting the column with an encoding of today's defaults on every Open-Apply.
    if (conditionalConfig || !isPristineDefault(nextConfig)) {
      updates.conditionalConfig = nextConfig as unknown as ConditionalStepConfigValue;
    }

    onChange(updates);
  };

  const fd = useFd();
  const isDecision = config.mode === 'Decision';
  const toOrder = (n: number | null) => (n === null ? null : String(n));
  // App inputs on (docs/design/app-inputs.md 9, 24): the condition can read the app's fields, and a branch can finish the run.
  const builder = useAppBuilder();
  const tApp = useT('apps.builder.condition');
  const readable = builder ? conditionReadable(builder.manifest) : [];
  const insertField = (snippet: string) => {
    const current = (conditionExpression ?? '').trim();
    onChange({ conditionExpression: current ? `${current} and ${snippet}` : snippet });
  };
  const branch = (onTrue: boolean) => {
    const value = onTrue ? trueBranchStepOrder : falseBranchStepOrder;
    const key = onTrue ? 'trueBranchStepOrder' : 'falseBranchStepOrder';
    const ends = value === END_BRANCH;
    return (
      <Stack gap={4}>
        {ends ? (
          <Text size="sm" fw={500}>{onTrue ? t('branch.trueLabel') : t('branch.falseLabel')}</Text>
        ) : (
          <StepOrderSelect
            label={onTrue ? t('branch.trueLabel') : t('branch.falseLabel')}
            description={onTrue ? t('branch.trueDescription') : t('branch.falseDescription')}
            steps={previousSteps}
            currentIndex={currentStepIndex}
            scope="after"
            value={value}
            onChange={(n) => onChange({ [key]: toOrder(n) })}
            wrapperProps={fd(`col.${key}`).wrapperProps}
          />
        )}
        {builder && (
          <Checkbox
            label={tApp('finish')}
            checked={ends}
            onChange={(e) => onChange({ [key]: e.currentTarget.checked ? END_BRANCH : null })}
            styles={{ body: { minHeight: 44, alignItems: 'center' } }}
          />
        )}
      </Stack>
    );
  };

  return (
    <Stack gap="md">
      <Group justify="space-between" align="center">
        <Text size="xs" c="dimmed">
          {t('intro')}
        </Text>
        <AskAssistantButton
          prompt={assistantPrompt(currentStepIndex, 'Conditional', msg('wfSteps.prompts.conditional'))}
        />
      </Group>
      <Fieldset legend={t('condition')}>
        <Stack gap="md">
          {/*
            The allowlist names the schema's real, now-wired fields — every one of them is carried
            by `handleFormChange`, and nothing this form can render is dropped. `version` is
            deliberately absent: it is the config envelope's constant, never author-edited, and a
            control for it could only ever produce a schema-invalid value.
          */}
          <SchemaDrivenStepConfigForm
            stepType="Conditional"
            value={{ ...config, conditionExpression }}
            onChange={handleFormChange}
            fields={['mode', 'conditionExpression', 'decisionQuestion', 'threshold', 'providerId', 'timeoutSeconds']}
            pathFor={(k) => (k === 'conditionExpression' ? 'col.conditionExpression' : `config.${k}`)}
            labels={{
              mode: t('labels.mode'),
              conditionExpression: t('labels.conditionExpression'),
              decisionQuestion: t('labels.decisionQuestion'),
              threshold: t('labels.threshold'),
              providerId: t('labels.providerId'),
              timeoutSeconds: t('labels.timeoutSeconds'),
            }}
            hints={{
              conditionExpression: t('hints.conditionExpression'),
              decisionQuestion: t('hints.decisionQuestion'),
              threshold: isDecision
                ? t('hints.thresholdDecision')
                : t('hints.thresholdExpression'),
              timeoutSeconds: t('hints.timeoutSeconds'),
            }}
            renderers={{
              mode: ({ value, onChange, label, wrapperProps }) => (
                <Stack gap={6}>
                  <Select
                    label={label}
                    size="sm"
                    allowDeselect={false}
                    value={(value as string) ?? 'Expression'}
                    onChange={(v) => v && onChange(v)}
                    wrapperProps={wrapperProps}
                    data={[
                      { value: 'Expression', label: t('mode.expression') },
                      { value: 'Decision', label: t('mode.decision') },
                    ]}
                  />
                  <ModeHint>
                    {value === 'Decision'
                      ? t('mode.decisionHint')
                      : t('mode.expressionHint')}
                  </ModeHint>
                </Stack>
              ),
              providerId: ({ value, onChange, label, description, wrapperProps }) =>
                isDecision ? (
                  <ProviderSelect
                    capability="Decision"
                    label={label}
                    size="sm"
                    description={description ?? t('provider.description')}
                    value={value as string | null}
                    onChange={onChange}
                    nullMeans={t('provider.nullMeans')}
                    wrapperProps={wrapperProps}
                  />
                ) : (
                  <Text size="xs" c="dimmed">{t('provider.onlyDecision')}</Text>
                ),
            }}
          />
          {builder && !isDecision && readable.length > 0 && (
            <Menu position="bottom-start" withinPortal>
              <Menu.Target>
                <Button size="sm" variant="default" style={{ alignSelf: 'flex-start' }}>{tApp('insert')}</Button>
              </Menu.Target>
              <Menu.Dropdown>
                {readable.map((input) => (
                  <Menu.Item key={input.id} onClick={() => insertField(conditionSnippet(input))}>
                    {input.label?.trim() || input.id}
                  </Menu.Item>
                ))}
              </Menu.Dropdown>
            </Menu>
          )}
          <Stack gap="xs">
            <Group grow align="flex-start">
              {branch(true)}
              {branch(false)}
            </Group>
          </Stack>
        </Stack>
      </Fieldset>

      <AdvancedSection label={t('earlierFields.label')} description={t('earlierFields.description')}>
        <OutputSchemaViewer previousSteps={previousSteps} currentStepIndex={currentStepIndex} />
      </AdvancedSection>
    </Stack>
  );
}
