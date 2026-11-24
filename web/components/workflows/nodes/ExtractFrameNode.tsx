'use client';

import { memo } from 'react';
import { IconCamera } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultExtractFrameStepConfig } from '../ExtractFrameStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const ExtractFrameNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultExtractFrameStepConfig(), ...data.step.extractFrameConfig };
  const summary: SummaryChip[] = [];
  summary.push({ text: config.at === 'Time' ? tn('extractFrame.atTime', { seconds: config.atSec ?? 0 }) : config.at === 'First' ? tn('extractFrame.first') : tn('extractFrame.last') });
  summary.push({ text: config.format?.toLowerCase() === 'jpeg' ? 'JPEG' : 'PNG' });
  if (config.registerProjectFile) summary.push({ text: tn('extractFrame.saved') });

  // The step fails at save time without a named video: flag it on the card rather than on the run.
  const source = config.source;
  const missing =
    (source.kind === 'ProjectFile' && !source.projectFileId) ||
    (source.kind === 'StepOutput' && !source.stepOrder) ||
    (source.kind === 'StepResult' && !source.stepResultId);
  const hasEarlierStep = (data.currentStepIndex ?? 0) > 0;

  let warning: string | null = null;
  if (missing) warning = tn('extractFrame.chooseVideo');
  else if (source.kind === 'PreviousStepOutput' && !hasEarlierStep) warning = tn('extractFrame.nothingBefore');

  return (
    <StepNodeShell
      data={data}
      accentHex="#6b7280"
      color="gray"
      icon={<IconCamera size={13} />}
      kind={tn('extractFrame.kind')}
      role={stepRoleLabel('ExtractFrame')}
      labelPlaceholder={tn('extractFrame.namePlaceholder')}
      summary={summary}
      warning={warning}
    />
  );
});

ExtractFrameNode.displayName = 'ExtractFrameNode';
