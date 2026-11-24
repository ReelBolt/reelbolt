'use client';

import { memo } from 'react';
import { IconPhoto } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultThumbnailGenerateStepConfig } from '../ThumbnailGenerateStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const ThumbnailGenerateNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultThumbnailGenerateStepConfig(), ...data.step.thumbnailGenerateConfig };
  const summary: SummaryChip[] = [
    { text: `${config.width}×${config.height}`, ariaLabel: tn('thumbnail.sizeAria', { width: config.width, height: config.height }) },
  ];
  if (config.selection === 'AtTime') {
    summary.push({ text: tn('thumbnail.at', { seconds: config.atTimeSec }), ariaLabel: tn('thumbnail.atAria', { seconds: config.atTimeSec }) });
  } else {
    summary.push({ text: tn('thumbnail.best') });
  }
  if (config.fit === 'Contain') summary.push({ text: tn('thumbnail.whole') });
  if (config.overlayPosition !== 'None' && config.overlayText?.trim()) summary.push({ text: tn('thumbnail.text') });

  // The step has nothing to take a frame from without one, and the failure otherwise surfaces only
  // when the run reaches this step — so it is flagged on the card, exactly as the missing-audio
  // notices on VideoCompile's node are. A step pinned to an earlier run's analysis is runnable
  // without any Watch footage step above it, so it is not flagged.
  const hasAnalysisStep = Boolean(config.analysisStepResultId)
    || (data.allSteps ?? []).slice(0, data.currentStepIndex ?? 0).some((s) => s.stepType === 'VideoAnalyze');

  return (
    <StepNodeShell
      data={data}
      accentHex="#6b7280"
      color="gray"
      icon={<IconPhoto size={13} />}
      kind={tn('thumbnail.kind')}
      role={stepRoleLabel('ThumbnailGenerate')}
      labelPlaceholder={tn('thumbnail.namePlaceholder')}
      summary={summary}
      warning={hasAnalysisStep ? null : tn('thumbnail.addWatch')}
    />
  );
});

ThumbnailGenerateNode.displayName = 'ThumbnailGenerateNode';
