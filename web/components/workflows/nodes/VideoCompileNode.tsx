'use client';

import { memo } from 'react';
import { IconScissors } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultVideoCompileStepConfig } from '../VideoCompileStepConfig';
import { outputFormatShortLabel } from '../output-format';
import { NoAudioFilesNotice } from '../config-kit';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const VideoCompileNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultVideoCompileStepConfig(), ...data.step.videoCompileConfig };
  const summary: SummaryChip[] = [{ text: outputFormatShortLabel(config.outputFormat), color: 'indigo' }];
  if ((config.targetDurationSec ?? 0) > 0) summary.push({ text: tn('compile.about', { seconds: config.targetDurationSec }) });
  if (config.enableVoiceover) summary.push({ text: tn('compile.narration') });
  if (config.enableVoiceover && config.enableCaptions) summary.push({ text: tn('compile.captions') });
  if (config.enableMusic) summary.push({ text: tn('compile.music') });
  if (config.enableSfx) summary.push({ text: tn('compile.sfx') });
  if (config.enableGraphics) summary.push({ text: tn('compile.titles') });
  if (config.enableColorGrade) summary.push({ text: tn('compile.colour') });
  if (config.enableGeneratedClips) summary.push({ text: tn('compile.aiClips') });

  return (
    <StepNodeShell
      data={data}
      accentHex="#6366f1"
      color="indigo"
      icon={<IconScissors size={13} />}
      kind={tn('compile.kind')}
      role={stepRoleLabel('VideoCompile')}
      labelPlaceholder={tn('compile.namePlaceholder')}
      summary={summary}
    >
      {(config.enableMusic || config.enableSfx) && (
        <NoAudioFilesNotice
          projectId={data.projectId}
          kind={config.enableMusic && config.enableSfx ? 'both' : config.enableMusic ? 'music' : 'sfx'}
        />
      )}
    </StepNodeShell>
  );
});

VideoCompileNode.displayName = 'VideoCompileNode';
