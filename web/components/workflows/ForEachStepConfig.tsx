'use client';

import { Stack, Fieldset } from '@mantine/core';
import { useT } from '@/i18n';
import { SchemaDrivenStepConfigForm } from './SchemaDrivenStepConfigForm';
import { OutputSchemaViewer } from './OutputSchemaViewer';
import { AdvancedSection } from './config-kit';
import type { StepData } from './WorkflowStepList';

interface ForEachStepConfigProps {
  loopSourceExpression: string | null;
  maxIterations: number;
  onChange: (updates: {
    loopSourceExpression?: string | null;
    maxIterations?: number;
  }) => void;
  previousSteps?: StepData[];
  currentStepIndex?: number;
}

export function ForEachStepConfig({
  loopSourceExpression,
  maxIterations,
  onChange,
  previousSteps = [],
  currentStepIndex = 0,
}: ForEachStepConfigProps) {
  const t = useT('wfSteps.forEach');
  const handleFormChange = (formValue: Record<string, unknown>) => {
    onChange({
      loopSourceExpression: formValue.loopSourceExpression as string | null,
      maxIterations: formValue.maxIterations as number,
    });
  };

  return (
    <Stack gap="md">
      <Fieldset legend={t('loopSource')}>
        <SchemaDrivenStepConfigForm
          stepType="ForEach"
          value={{ loopSourceExpression, maxIterations }}
          onChange={handleFormChange}
        />
      </Fieldset>

      <AdvancedSection label={t('earlierFields.label')} description={t('earlierFields.description')}>
        <OutputSchemaViewer previousSteps={previousSteps} currentStepIndex={currentStepIndex} />
      </AdvancedSection>
    </Stack>
  );
}
