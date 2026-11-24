'use client';

import { memo } from 'react';
import { IconMovie } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultTimelineRenderStepConfig, timelineRenderPresetLabel } from '../TimelineRenderStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const TimelineRenderNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultTimelineRenderStepConfig(), ...data.step.timelineRenderConfig };
  const summary: SummaryChip[] = [
    { text: timelineRenderPresetLabel(config.preset) },
    { text: config.outputFileName, ariaLabel: tn('timeline.savedAs', { name: config.outputFileName }) },
  ];
  if (config.revision !== null) summary.push({ text: tn('timeline.revision', { revision: config.revision }) });
  if (config.range) summary.push({ text: tn('timeline.frames', { from: config.range.inFrame, to: config.range.outFrame }) });
  if (config.loudness) summary.push({ text: tn('timeline.loudness') });

  // The step renders one named timeline and nothing else, so a missing choice is flagged on the
  // card instead of surfacing as a failed step when the run reaches it.
  const hasTimeline = Boolean(config.editTimelineId);

  return (
    <StepNodeShell
      data={data}
      accentHex="#4c6ef5"
      color="indigo"
      icon={<IconMovie size={13} />}
      kind={tn('timeline.kind')}
      role={stepRoleLabel('TimelineRender')}
      labelPlaceholder={tn('timeline.namePlaceholder')}
      summary={summary}
      warning={hasTimeline ? null : tn('timeline.chooseTimeline')}
    />
  );
});

TimelineRenderNode.displayName = 'TimelineRenderNode';
