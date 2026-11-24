'use client';

import { memo } from 'react';
import { IconColorSwatch } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultDesignSystemStepConfig } from '../DesignSystemStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const DesignSystemNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultDesignSystemStepConfig(), ...data.step.designSystemConfig };
  const summary: SummaryChip[] = [];
  let warning: string | null = null;

  if (config.op === 'Parse') {
    const parse = config.parse;
    if (parse?.includeDesign !== false) summary.push({ text: tn('designSystem.readsDesign') });
    if (parse?.includeProductMap) summary.push({ text: tn('designSystem.productStructure') });
    // A saved step can be edited into a shape the server rejects; the card flags it before the run does.
    if (parse && !parse.includeDesign && !parse.includeProductMap) {
      warning = tn('designSystem.chooseOne');
    }
  } else if (config.op === 'Compose') {
    summary.push({ text: tn('designSystem.combines') });
    if (config.compose?.parseStepOrder && config.compose.parseStepOrder >= 1) {
      summary.push({ text: tn('designSystem.readStep', { step: config.compose.parseStepOrder }) });
    } else {
      warning = tn('designSystem.chooseEarlier');
    }
  } else if (config.op === 'Require') {
    if (config.require?.requireDesign !== false) summary.push({ text: tn('designSystem.needsBrand') });
    if (config.require?.requireProductMap !== false) summary.push({ text: tn('designSystem.needsProfile') });
    if (config.require && !config.require.requireDesign && !config.require.requireProductMap) {
      warning = tn('designSystem.chooseOneRequired');
    }
  }

  return (
    <StepNodeShell
      data={data}
      accentHex="#7048e8"
      color="violet"
      icon={<IconColorSwatch size={13} />}
      kind={tn('designSystem.kind')}
      role={stepRoleLabel('DesignSystem')}
      labelPlaceholder={tn('designSystem.namePlaceholder')}
      summary={summary}
      warning={warning}
    />
  );
});

DesignSystemNode.displayName = 'DesignSystemNode';
