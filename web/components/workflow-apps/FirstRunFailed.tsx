'use client';

import { Text } from '@mantine/core';
import type { WorkflowExecution } from '@/lib/types/workflow';
import { useFormat, useT } from '@/i18n';
import { failureSide } from './first-result';
import { runCreditsSummary } from './run-model';
import { formatCredits } from '@/lib/utils/usage-labels';

/** The line above the reason of a first run that failed: said once, plainly, with no blame. */
export function FirstRunFailedLead() {
  const t = useT('apps.run.first');
  return <Text size="sm" data-testid="first-run-failed">{t('failedLead')}</Text>;
}

/**
 * The line under the reason: what happens to the credits and what to do next. A refund is stated
 * only where it is known: when the engine put the failure on our side it says the credits held for
 * the step are refunded; when the engine did not say whose side it was it states the rule; when it
 * was the input (or a stop, the plan, the balance) it says nothing about a refund, because there is
 * none. Where credits do not apply at all it only says the run can be started again.
 */
export function FirstRunFailedNext({
  execution,
  billingEnabled,
}: {
  execution: Pick<WorkflowExecution, 'stepResults'>;
  billingEnabled: boolean;
}) {
  const t = useT('apps.run.first');
  const side = failureSide(execution);
  return (
    <Text size="sm" data-testid="first-run-failed-next">
      {side === 'yours' ? t('failedYours')
        : !billingEnabled ? t('failedRetry')
        : side === 'ours' ? t('failedOurs')
        : t('failedRefundRule')}
    </Text>
  );
}

/**
 * What this failed run cost, on every failed run (billing on only). Built from what each step recorded as
 * charged, never invented: a refund is claimed only when the failure was on our side and the failed step was charged.
 */
export function FailedRunCredits({
  execution,
  billingEnabled,
}: {
  execution: Pick<WorkflowExecution, 'stepResults'>;
  billingEnabled: boolean;
}) {
  const t = useT('apps.run.first');
  const f = useFormat();
  const summary = billingEnabled ? runCreditsSummary(execution) : null;
  if (!summary) return null;
  const fmt = (n: number) => formatCredits(n, f.locale);
  let text: string;
  if (summary.failed > 0) {
    text = failureSide(execution) === 'ours'
      ? t('creditsFailedRefunded', { credits: fmt(summary.finished), steps: summary.finishedSteps, failed: fmt(summary.failed) })
      : t('creditsFailedCharged', { credits: fmt(summary.finished + summary.failed), failed: fmt(summary.failed) });
  } else if (summary.finished > 0) {
    const args = { credits: fmt(summary.finished), steps: summary.finishedSteps };
    text = summary.failedAllZero ? t('creditsUsed', args) : t('creditsUsedOnly', args);
  } else if (!summary.failedAllZero) {
    return null;
  } else {
    text = t('creditsNone');
  }
  return <Text size="sm" data-testid="failed-run-credits">{text}</Text>;
}
