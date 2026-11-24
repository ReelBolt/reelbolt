'use client';

import { memo } from 'react';
import { IconStarFilled } from '@tabler/icons-react';
import { useAgents } from '@/lib/hooks/use-agents';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData } from './StepNodeShell';

export const ReviewLoopNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const { step } = data;
  const { data: agents } = useAgents();
  const reviewer = agents?.find((a) => a.id === step.agentDefinitionId);
  const summary = [
    { text: tn('reviewLoop.needs', { score: step.minScore || 9 }), color: 'pink' },
    { text: tn('reviewLoop.tries', { count: step.maxIterations || 3 }) },
  ];
  if (step.loopTargetStepOrder) summary.push({ text: tn('reviewLoop.redo', { step: step.loopTargetStepOrder }) });
  return (
    <StepNodeShell
      data={data}
      accentHex="#ec4899"
      color="pink"
      icon={<IconStarFilled size={13} />}
      kind={tn('reviewLoop.kind')}
      role={stepRoleLabel('ReviewLoop', reviewer?.agentType)}
      labelPlaceholder={tn('reviewLoop.namePlaceholder')}
      summary={summary}
    />
  );
});

ReviewLoopNode.displayName = 'ReviewLoopNode';
