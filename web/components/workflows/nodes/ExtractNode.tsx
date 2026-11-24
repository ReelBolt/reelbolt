'use client';

import { memo } from 'react';
import { IconFilterCog } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultExtractStepConfig } from '../ExtractStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData } from './StepNodeShell';

export type ExtractNodeData = BaseStepNodeData;

export const ExtractNode = memo(({ data }: { data: ExtractNodeData }) => {
  const tn = useT('workflows.node');
  // Merge over defaults: a stored config may be partial (e.g. missing `operation`).
  const config = { ...createDefaultExtractStepConfig(), ...data.step.extractConfig };
  return (
    <StepNodeShell
      data={data}
      accentHex="#a855f7"
      color="grape"
      icon={<IconFilterCog size={13} />}
      kind={tn('extract.kind')}
      role={stepRoleLabel('Extract')}
      summary={[{ text: tn.has(`extract.operation.${config.operation}`) ? tn(`extract.operation.${config.operation}`) : tn('extract.prepares'), color: 'grape' }]}
    />
  );
});

ExtractNode.displayName = 'ExtractNode';
