'use client';

import { Alert, Text } from '@mantine/core';
import { IconInfoCircle } from '@tabler/icons-react';
import { useT } from '@/i18n';
import type { StepType } from '@/lib/types/workflow';
import { englishWorkflowsT } from './english-t';

/** English wording, pinned by tests; the dialog itself renders the active language. */
export const SILENT_RUN_TITLE = englishWorkflowsT('silentRun')('title');
export const SILENT_RUN_BODY = englishWorkflowsT('silentRun')('body');

/** The part of a workflow step the run confirmation needs: its type and, for an Agent step, the built-in agent type. */
export interface RunStepKind {
  stepType?: StepType;
  agentType?: string | null;
}

/** A missing step type is an Agent step, as everywhere else in the web app. */
const kindOf = (s: RunStepKind): StepType => s.stepType ?? 'Agent';

/**
 * True when a top-level step builds a film whose only possible sound is narration: the Author agent's
 * Remotion promo. Footage edits (VideoCompile, TimelineRender) keep the source clips' own audio, so a
 * missing Voiceover step does not make them silent.
 */
export function workflowProducesVideo(steps: RunStepKind[]): boolean {
  return steps.some((s) => kindOf(s) === 'Agent' && s.agentType === 'AuthorAgent');
}

/** True when a top-level step of the workflow narrates the film. */
export function workflowHasVoiceover(steps: RunStepKind[]): boolean {
  return steps.some((s) => kindOf(s) === 'Voiceover');
}

/** The run dialog's note that a video run has no narration. Informational: the run still starts. */
export function SilentRunNotice() {
  const t = useT('workflows.silentRun');
  return (
    <Alert color="blue" variant="light" p="xs" icon={<IconInfoCircle size={16} />} title={t('title')} role="status">
      <Text size="sm">{t('body')}</Text>
    </Alert>
  );
}
