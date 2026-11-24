'use client';

import { memo } from 'react';
import { IconRepeat } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData } from './StepNodeShell';

export const ForEachNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const { step } = data;
  return (
    <StepNodeShell
      data={data}
      accentHex="#06b6d4"
      color="cyan"
      icon={<IconRepeat size={13} />}
      kind={tn('forEach.kind')}
      role={stepRoleLabel('ForEach')}
      summary={[{ text: tn('forEach.upTo', { count: step.maxIterations || 3 }) }]}
    />
  );
});

ForEachNode.displayName = 'ForEachNode';
