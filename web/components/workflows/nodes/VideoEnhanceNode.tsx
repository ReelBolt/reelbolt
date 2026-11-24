'use client';

import { memo } from 'react';
import { IconZoomIn } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultVideoEnhanceStepConfig, isHostedVideoEnhance, isHostedVideoEnhanceOp } from '../VideoEnhanceStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const VideoEnhanceNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const ts = useT('wfSteps.videoEnhance');
  const config = { ...createDefaultVideoEnhanceStepConfig(), ...data.step.videoEnhanceConfig };
  const hosted = isHostedVideoEnhanceOp(config.op) && isHostedVideoEnhance(config.engine);
  const summary: SummaryChip[] = [];
  summary.push({ text: ts(`op.${config.op}`) });
  summary.push({ text: hosted ? tn('videoEnhance.credits') : tn('videoEnhance.free') });
  if (config.registerProjectFile) summary.push({ text: tn('videoEnhance.saved') });

  // The step fails at save time without a named video, and a hosted op is refused without consent.
  const source = config.source;
  const missing =
    (source.kind === 'ProjectFile' && !source.projectFileId) ||
    (source.kind === 'StepOutput' && !source.stepOrder) ||
    (source.kind === 'StepResult' && !source.stepResultId);
  const hasEarlierStep = (data.currentStepIndex ?? 0) > 0;

  let warning: string | null = null;
  if (missing) warning = tn('videoEnhance.chooseSource');
  else if (source.kind === 'PreviousStepOutput' && !hasEarlierStep) warning = tn('videoEnhance.nothingBefore');
  else if (hosted && !config.allowSourceMediaEgress) warning = tn('videoEnhance.needsPermission');

  return (
    <StepNodeShell
      data={data}
      accentHex="#1098ad"
      color="cyan"
      icon={<IconZoomIn size={13} />}
      kind={tn('videoEnhance.kind')}
      role={stepRoleLabel('VideoEnhance')}
      labelPlaceholder={tn('videoEnhance.namePlaceholder')}
      summary={summary}
      warning={warning}
    />
  );
});

VideoEnhanceNode.displayName = 'VideoEnhanceNode';
