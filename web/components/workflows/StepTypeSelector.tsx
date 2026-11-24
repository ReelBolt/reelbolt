'use client';

import { Select } from '@mantine/core';
import { useT } from '@/i18n';
import { STEP_TYPE_LABELS, isAuthorableStepType } from '@/lib/utils/constants';
import type { StepType } from '@/lib/types/workflow';
import { useStepTypeText } from './step-type-i18n';

// Every StepType the backend knows is selectable here, including all three room types — each has a
// real config editor (RoomStepConfigEditor) and a default config seeded on add/type-switch, so a
// step created here is runnable without hand-editing JSON. System step types (`Revise`) are left out.
const authorableStepTypes = (Object.keys(STEP_TYPE_LABELS) as StepType[]).filter(isAuthorableStepType);

interface StepTypeSelectorProps {
  value: StepType;
  onChange: (value: StepType) => void;
}

export function StepTypeSelector({ value, onChange }: StepTypeSelectorProps) {
  const t = useT('workflows.stepTypeSelector');
  const stepText = useStepTypeText();
  const stepTypeOptions = authorableStepTypes.map((key) => ({
    value: key,
    label: stepText.label(key),
    description: stepText.description(key),
  }));
  return (
    <Select
      label={t('label')}
      data={stepTypeOptions}
      value={value}
      onChange={(v) => v && onChange(v as StepType)}
      size="sm"
    />
  );
}
