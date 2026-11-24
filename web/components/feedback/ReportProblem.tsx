'use client';

import { useState } from 'react';
import { Button, Stack, Text } from '@mantine/core';
import { IconFlag } from '@tabler/icons-react';
import { candidatesFromPath, type FeedbackCandidates } from '@/lib/feedback/context';
import { failureSide } from '@/components/workflow-apps/first-result';
import type { WorkflowExecution } from '@/lib/types/workflow';
import { FeedbackDialog } from './FeedbackDialog';
import { useT } from '@/i18n';

type RunFacts = Pick<WorkflowExecution, 'status' | 'stepResults'>;

/**
 * When a run offers "Report a problem": it Failed, and the person did not stop it. A run that only
 * shows a Cancelled / Stopped status, or Passed, never offers it. A Failed run whose failed steps
 * all carry the `UserCancelled` class was a stop, so it does not either.
 */
export function canReportRun(run: Partial<RunFacts> | null | undefined): boolean {
  if (!run || run.status !== 'Failed') return false;
  const classes = (run.stepResults ?? [])
    .filter((r) => r.status === 'Failed')
    .map((r) => r.failureClass ?? '')
    .filter((c) => c !== '' && c !== 'None');
  return !(classes.length > 0 && classes.every((c) => c === 'UserCancelled'));
}

/** "This one is on us": only when the run's own failure class says the platform's side, never assumed. */
export function isOnUs(run: Partial<RunFacts> | null | undefined): boolean {
  return canReportRun(run) && failureSide({ stepResults: run?.stepResults ?? [] }) === 'ours';
}

export interface ReportProblemProps {
  /** The run (or render) being reported: its status and step classes decide whether anything shows. */
  run: Partial<RunFacts>;
  executionId: string;
  projectId?: string;
  /** Say "on us" under the button. Off where the page already says it (the first-run failure text). */
  showOnUs?: boolean;
}

/**
 * "Report a problem" next to a failed run's error. It opens the shell's own feedback dialog as a bug
 * report with the run attached (still switched off by one tick, and nothing leaves until Send). No
 * new data: the dialog sends only the candidates it is given, the page and the run's ids.
 */
export function ReportProblem({ run, executionId, projectId, showOnUs = true }: ReportProblemProps) {
  const t = useT('feedback');
  const [opened, setOpened] = useState(false);
  const [candidates, setCandidates] = useState<FeedbackCandidates>({});
  if (!canReportRun(run)) return null;

  const open = () => {
    const base = typeof window === 'undefined' ? {} : candidatesFromPath(window.location.pathname);
    setCandidates({ ...base, ...(projectId ? { projectId } : {}), executionId });
    setOpened(true);
  };

  return (
    <Stack gap={4} align="flex-start" data-testid="report-problem">
      {showOnUs && isOnUs(run) && (
        <Text size="sm" data-testid="report-problem-on-us">{t('onUs')}</Text>
      )}
      <Button
        variant="light"
        leftSection={<IconFlag size={16} />}
        onClick={open}
        style={{ minHeight: 44 }}
        data-testid="report-problem-button"
      >
        {t('reportProblem')}
      </Button>
      {opened ? <FeedbackDialog candidates={candidates} onClose={() => setOpened(false)} /> : null}
    </Stack>
  );
}
