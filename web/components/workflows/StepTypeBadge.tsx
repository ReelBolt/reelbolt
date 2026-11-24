'use client';

import { Badge } from '@mantine/core';
import { STEP_TYPE_COLORS } from '@/lib/utils/constants';
import { useStepTypeText } from './step-type-i18n';
import type { StepType } from '@/lib/types/workflow';

export function StepTypeBadge({ stepType }: { stepType: StepType }) {
  const stepText = useStepTypeText();
  return (
    <Badge color={STEP_TYPE_COLORS[stepType] || 'gray'} variant="light" size="sm">
      {stepText.label(stepType)}
    </Badge>
  );
}
