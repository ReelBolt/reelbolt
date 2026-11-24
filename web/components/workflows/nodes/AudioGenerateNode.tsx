'use client';

import { memo } from 'react';
import { IconMusic } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultAudioGenerateStepConfig } from '../AudioGenerateStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const AudioGenerateNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const config = { ...createDefaultAudioGenerateStepConfig(), ...data.step.audioGenerateConfig };
  const isMusic = config.kind === 'Music';
  const hasPlan = isMusic && Boolean(config.compositionPlanJson?.trim());
  const summary: SummaryChip[] = [{ text: isMusic ? tn('audio.music') : tn('audio.soundEffect') }];
  if (!hasPlan) summary.push({ text: tn('seconds', { seconds: config.durationSec }) });
  if ((config.takes ?? 1) > 1) summary.push({ text: tn('audio.takes', { count: config.takes ?? 1 }) });
  summary.push(
    (config.maxSpendCredits ?? 0) > 0
      ? { text: tn('audio.maxCredits', { credits: config.maxSpendCredits }) }
      : { text: tn('audio.noLimit') },
  );

  const warning = !hasPlan && !config.prompt.trim() ? tn('audio.describe') : null;

  return (
    <StepNodeShell
      data={data}
      accentHex="#e03131"
      color="red"
      icon={<IconMusic size={13} />}
      kind={tn('audio.kind')}
      role={stepRoleLabel('AudioGenerate')}
      labelPlaceholder={tn('audio.namePlaceholder')}
      summary={summary}
      warning={warning}
    />
  );
});

AudioGenerateNode.displayName = 'AudioGenerateNode';
