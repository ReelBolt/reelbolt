'use client';

import { memo } from 'react';
import { IconVolume2 } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { createDefaultAudioCleanupStepConfig, isAiAudioCleanupTarget } from '../AudioCleanupStepConfig';
import { useT } from '@/i18n';
import { StepNodeShell, type BaseStepNodeData, type SummaryChip } from './StepNodeShell';

export const AudioCleanupNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const ts = useT('wfSteps.audioCleanup');
  const config = { ...createDefaultAudioCleanupStepConfig(), ...data.step.audioCleanupConfig };
  const isAi = isAiAudioCleanupTarget(config.target);
  const summary: SummaryChip[] = [];
  summary.push({ text: ts(`target.${config.target}`) });
  summary.push({ text: isAi ? tn('audioCleanup.credits') : tn('audioCleanup.free') });
  if (config.registerProjectFile) summary.push({ text: tn('audioCleanup.saved') });

  // The step fails at save time without a named recording, and a hosted fix is refused without consent.
  const source = config.source;
  const missing =
    (source.kind === 'ProjectFile' && !source.projectFileId) ||
    (source.kind === 'StepOutput' && !source.stepOrder) ||
    (source.kind === 'StepResult' && !source.stepResultId);
  const hasEarlierStep = (data.currentStepIndex ?? 0) > 0;

  let warning: string | null = null;
  if (missing) warning = tn('audioCleanup.chooseSource');
  else if (source.kind === 'PreviousStepOutput' && !hasEarlierStep) warning = tn('audioCleanup.nothingBefore');
  else if (isAi && !config.allowSourceMediaEgress) warning = tn('audioCleanup.needsPermission');

  return (
    <StepNodeShell
      data={data}
      accentHex="#fa5252"
      color="red"
      icon={<IconVolume2 size={13} />}
      kind={tn('audioCleanup.kind')}
      role={stepRoleLabel('AudioCleanup')}
      labelPlaceholder={tn('audioCleanup.namePlaceholder')}
      summary={summary}
      warning={warning}
    />
  );
});

AudioCleanupNode.displayName = 'AudioCleanupNode';
