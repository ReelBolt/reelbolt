'use client';

import { memo } from 'react';
import { IconWaveSine } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import type { VideoSourceRef } from '@/lib/types/workflow';
import { createDefaultVideoAnalyzeStepConfig } from '../VideoAnalyzeStepConfig';
import { NoAudioFilesNotice } from '../config-kit';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

function describeSource(tn: ReturnType<typeof useT>, source: VideoSourceRef | null | undefined, sources: VideoSourceRef[] | null | undefined): string {
  if (sources && sources.length > 0) {
    if (sources.length === 1) return describeSource(tn, sources[0], null);
    return tn('analyze.clips', { count: sources.length });
  }
  if (!source) return tn('analyze.noVideo');
  if (source.kind === 'ProjectFile') return source.projectFileId ? tn('analyze.oneUploaded') : tn('analyze.noVideo');
  if (source.kind === 'StepOutput') return source.stepOrder ? tn('analyze.fromStep', { step: source.stepOrder }) : tn('analyze.noVideo');
  return tn('analyze.fromPrevious');
}

export const VideoAnalyzeNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultVideoAnalyzeStepConfig(), ...data.step.videoAnalyzeConfig };
  // A first step has no earlier step to take a video from, whatever its stored source says.
  const firstStepWithoutFile = data.stepNumber === 1 && !config.sources?.length && config.source?.kind !== 'ProjectFile';
  const sourceText = firstStepWithoutFile ? tn('analyze.noVideo') : describeSource(tn, config.source, config.sources);
  const summary: SummaryChip[] = [{ text: sourceText, color: 'blue' }];
  if (config.transcription !== 'Off') summary.push({ text: tn('analyze.transcribes') });
  if ((config.vision ?? 'Off') !== 'Off') summary.push({ text: tn('analyze.describes') });
  if (config.offerMusicTracks) summary.push({ text: tn('analyze.music') });
  if (config.offerSfxClips) summary.push({ text: tn('analyze.sfx') });

  return (
    <StepNodeShell
      data={data}
      accentHex="#3b82f6"
      color="blue"
      icon={<IconWaveSine size={13} />}
      kind={tn('analyze.kind')}
      role={stepRoleLabel('VideoAnalyze')}
      labelPlaceholder={tn('analyze.namePlaceholder')}
      summary={summary}
      warning={sourceText === tn('analyze.noVideo') ? tn('analyze.chooseVideo') : null}
    >
      {(config.offerMusicTracks || config.offerSfxClips) && (
        <NoAudioFilesNotice
          projectId={data.projectId}
          kind={config.offerMusicTracks && config.offerSfxClips ? 'both' : config.offerMusicTracks ? 'music' : 'sfx'}
        />
      )}
    </StepNodeShell>
  );
});

VideoAnalyzeNode.displayName = 'VideoAnalyzeNode';
