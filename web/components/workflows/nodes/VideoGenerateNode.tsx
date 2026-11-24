'use client';

import { memo } from 'react';
import { IconWand } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultVideoGenerateStepConfig } from '../VideoGenerateStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const VideoGenerateNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultVideoGenerateStepConfig(), ...data.step.videoGenerateConfig };
  const summary: SummaryChip[] = [
    { text: config.planSource === 'Inline' ? tn('generate.fromPrompt') : tn('generate.fromPlan'), color: 'red' },
  ];
  if (config.maxSpendCredits > 0) summary.push({ text: tn('generate.spends', { credits: config.maxSpendCredits }) });
  return (
    <StepNodeShell
      data={data}
      accentHex="#dc2626"
      color="red"
      icon={<IconWand size={13} />}
      kind={tn('generate.kind')}
      role={stepRoleLabel('VideoGenerate')}
      labelPlaceholder={tn('generate.namePlaceholder')}
      summary={summary}
      warning={!(config.maxSpendCredits > 0) ? tn('generate.setLimit') : null}
    />
  );
});

VideoGenerateNode.displayName = 'VideoGenerateNode';
