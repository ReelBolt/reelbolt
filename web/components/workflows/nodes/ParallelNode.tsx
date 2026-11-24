'use client';

import { memo } from 'react';
import { IconGitFork } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData } from './StepNodeShell';

export const ParallelNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const count = data.step.parallelAgentIds?.length ?? 0;
  return (
    <StepNodeShell
      data={data}
      accentHex="#0ea5e9"
      color="cyan"
      icon={<IconGitFork size={13} />}
      kind={tn('parallel.kind')}
      role={stepRoleLabel('Parallel')}
      summary={[{ text: tn('parallel.agents', { count }) }]}
      warning={count === 0 ? tn('parallel.noAgents') : null}
    />
  );
});

ParallelNode.displayName = 'ParallelNode';
