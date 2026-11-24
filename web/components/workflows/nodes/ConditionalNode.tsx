'use client';

import { memo } from 'react';
import { IconGitBranch } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData } from './StepNodeShell';

export const ConditionalNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const { step } = data;
  return (
    <StepNodeShell
      data={data}
      accentHex="#f59e0b"
      color="orange"
      icon={<IconGitBranch size={13} />}
      kind={tn('conditional.kind')}
      role={stepRoleLabel('Conditional')}
      labelPlaceholder={tn('conditional.namePlaceholder')}
      summary={[
        { text: tn('conditional.ifYes', { step: step.trueBranchStepOrder || '?' }), color: 'green' },
        { text: tn('conditional.ifNo', { step: step.falseBranchStepOrder || '?' }), color: 'red' },
      ]}
      warning={!step.conditionExpression && !step.conditionalConfig ? tn('conditional.noCondition') : null}
    />
  );
});

ConditionalNode.displayName = 'ConditionalNode';
