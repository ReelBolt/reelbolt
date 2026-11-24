'use client';

import { memo } from 'react';
import { IconDeviceTv } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultPinInsertStepConfig, isPinComplete } from '../PinInsertStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const PinInsertNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultPinInsertStepConfig(), ...data.step.pinInsertConfig };
  const pins = config.pins ?? [];
  const summary: SummaryChip[] = [];
  summary.push({ text: pins.length === 1 ? '1 pin' : `${pins.length} pins` });
  if (pins.some((p) => !p.regionId)) summary.push({ text: tn('pinInsert.everyScreen') });
  if (config.requireApplied === false) summary.push({ text: tn('pinInsert.skips') });

  // The step fails at save time without named content: flag it on the card rather than on the run.
  let warning: string | null = null;
  if (pins.some((p) => !isPinComplete(p))) warning = tn('pinInsert.chooseWhat');
  else if ((data.currentStepIndex ?? 0) === 0) warning = tn('pinInsert.addAnalyze');

  return (
    <StepNodeShell
      data={data}
      accentHex="#0d9488"
      color="teal"
      icon={<IconDeviceTv size={13} />}
      kind={tn('pinInsert.kind')}
      role={stepRoleLabel('PinInsert')}
      labelPlaceholder={tn('pinInsert.namePlaceholder')}
      summary={summary}
      warning={warning}
    />
  );
});

PinInsertNode.displayName = 'PinInsertNode';
