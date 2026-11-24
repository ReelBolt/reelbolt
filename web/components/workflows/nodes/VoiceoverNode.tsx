'use client';

import { memo } from 'react';
import { IconMicrophone } from '@tabler/icons-react';
import { stepRoleLabel } from '@/lib/utils/agent-roles';
import { getVoiceoverSourceError } from '@/lib/utils/voiceover-validation';
import { createDefaultVoiceoverStepConfig } from '../VoiceoverStepConfig';
import { useT } from '@/i18n';
import { englishWorkflowsT } from '../english-t';
import { StepNodeShell, type BaseStepNodeData } from './StepNodeShell';

/** Accessible name of the source chip while a narration-plan source has no step chosen. */
export const VOICEOVER_UNCONFIGURED_LABEL = englishWorkflowsT('node')('voiceover.unconfiguredLabel');
/** Visible text of that chip. */
export const VOICEOVER_UNCONFIGURED_SUMMARY = englishWorkflowsT('node')('voiceover.unconfiguredSummary');

export const VoiceoverNode = memo(({ data }: { data: BaseStepNodeData }) => {
  const tn = useT('workflows.node');
  const { step, allSteps = [] } = data;
  const config = step.voiceoverConfig ?? createDefaultVoiceoverStepConfig();

  // Configured-ness comes from the validator alone (getVoiceoverSourceError), never from whether a
  // display label could be produced: the label has a `Step N` fallback, which used to make an
  // invalid order (e.g. 0) read as configured while the editor reported it unconfigured.
  const sourceError = getVoiceoverSourceError(config.source);
  const narrationPlanConfigured = config.source.kind === 'NarrationPlan' && sourceError == null;
  const narrationWriterStepOrder = narrationPlanConfigured ? config.source.narrationWriterStepOrder : null;
  const narrationWriterStepLabel = narrationWriterStepOrder != null
    ? allSteps[narrationWriterStepOrder - 1]?.label?.trim() || tn('voiceover.stepFallback', { step: narrationWriterStepOrder })
    : null;

  let summaryText: string;
  let summaryLabel: string;
  if (config.source.kind === 'Inline') {
    const n = config.source.lines?.length ?? 0;
    summaryText = tn('voiceover.typedLines', { count: n });
    summaryLabel = tn('voiceover.sourceLabel', { text: summaryText });
  } else if (config.source.kind === 'NarrationPlan') {
    summaryText = narrationPlanConfigured ? tn('voiceover.fromStep', { step: narrationWriterStepOrder }) : tn('voiceover.unconfiguredSummary');
    summaryLabel = narrationPlanConfigured
      ? tn('voiceover.planLabel', { step: narrationWriterStepOrder, label: narrationWriterStepLabel })
      : tn('voiceover.unconfiguredLabel');
  } else if (config.source.kind === 'PickupPlan') {
    summaryText = tn('voiceover.pickup');
    summaryLabel = tn('voiceover.sourceLabel', { text: summaryText });
  } else {
    summaryText = tn('voiceover.script');
    summaryLabel = tn('voiceover.sourceLabel', { text: summaryText });
  }

  return (
    <StepNodeShell
      data={data}
      accentHex="#ef4444"
      color="red"
      icon={<IconMicrophone size={13} />}
      kind={tn('voiceover.kind')}
      role={stepRoleLabel('Voiceover')}
      labelPlaceholder={tn('voiceover.namePlaceholder')}
      summary={[{ text: summaryText, ariaLabel: summaryLabel, color: 'red' }]}
    />
  );
});

VoiceoverNode.displayName = 'VoiceoverNode';
