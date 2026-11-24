'use client';

import Link from 'next/link';
import { Accordion, Alert, Anchor, Group, List, Paper, Stack, Text, ThemeIcon } from '@mantine/core';
import { IconAlertTriangle, IconCoin } from '@tabler/icons-react';
import { useWorkflowCostEstimate } from '@/lib/hooks/use-billing';
import type { RunOutputResolution } from '@/lib/types/styles';
import type { WorkflowCostEstimate } from '@/lib/types/billing';
import { useT } from '@/i18n';
import {
  creditEstimateHeadlineMsg,
  creditFigure,
  creditFigureUp,
  creditRangeMsg,
  estimateBasisMsg,
  fixedPriceMsg,
  spreadReasonMsgs,
  stepEstimateMsg,
} from '@/lib/billing-msg';
import { mayRunOutPartway } from '@/lib/utils/billing';

interface RunCostEstimateProps {
  projectId?: string;
  workflowId?: string;
  /** The per-step breakdown below the range. Off for a compact placement. */
  showBreakdown?: boolean;
  /** The run's chosen resolution, priced by the server; omitted for Auto. */
  outputResolution?: RunOutputResolution;
  /**
   * An estimate the caller already holds (the app run page's `POST .../estimate` with the brief's app inputs); when
   * given, nothing is fetched and this one is shown.
   */
  estimate?: WorkflowCostEstimate | undefined;
}

/**
 * What the next run of this workflow is expected to cost, shown before Execute is pressed (B10c-UI).
 *
 * Everything here comes from B7's `GET .../estimate`: the typical cost and its usual range (or, from
 * an older backend, the range alone), any fixed-price ceiling, why the range is wide, what the balance must
 * cover, and the per-step breakdown with the BASIS of each figure. That last part is why this is not
 * a single sentence: an estimate built from B0's cold-start defaults is not a measurement, and the
 * only way to say so honestly is to repeat what the server said per step rather than to average it
 * away.
 *
 * Renders NOTHING when there is nothing true to show: a self-host install (`billingEnabled: false` —
 * nothing is metered, so there is no chrome at all), an estimate that failed, or one that has not
 * arrived yet. A deployment without billing must look exactly as it did before this existed.
 */
export function RunCostEstimate({ projectId, workflowId, showBreakdown = true, outputResolution, estimate }: RunCostEstimateProps) {
  const t = useT('billing.estimate');
  const fetched = useWorkflowCostEstimate(estimate ? undefined : projectId, estimate ? undefined : workflowId, outputResolution);
  const data = estimate ?? fetched.data;
  const isLoading = estimate ? false : fetched.isLoading;
  const error = estimate ? undefined : fetched.error;

  if (error || (!data && !isLoading)) return null;
  if (!data) {
    // One line rather than a skeleton: the page usually prefetches this, so it is rarely seen, and a
    // spinner that flashes for 40ms reads worse than the answer simply appearing.
    return (
      <Text size="xs" c="dimmed">
        {t('loading')}
      </Text>
    );
  }
  if (!data.billingEnabled) return null;

  const balance = data.balanceCredits;
  const shortfall = balance === null ? 0 : Math.max(0, data.requiredCredits - balance);
  const fixedMsg = fixedPriceMsg(data.fixedMaxCredits, data.spreadReasons);
  const fixedLine = fixedMsg ? t.msg(fixedMsg) : null;
  // The reasons are only worth reading when the range is wide enough to raise the question.
  const wideReasons =
    data.highCredits >= 2 * data.lowCredits ? spreadReasonMsgs(data.spreadReasons).map((m) => t.msg(m)) : [];
  // Enough to start, but below what such a run typically costs.
  const runOutRisk = mayRunOutPartway(data);

  return (
    <Paper withBorder radius="md" p="sm" data-testid="run-cost-estimate">
      <Stack gap="xs">
        <Group gap="xs" wrap="nowrap">
          <ThemeIcon size="sm" variant="light" color="violet" aria-hidden>
            <IconCoin size={14} />
          </ThemeIcon>
          <Text size="sm" fw={600}>
            {typeof data.typicalCredits === 'number' ? (
              <>{t('shouldCost', { figure: t.msg(creditEstimateHeadlineMsg(data.typicalCredits, data.lowCredits, data.highCredits)) })}</>
            ) : (
              // An older backend sends no typical figure: the range alone, as it always read.
              <>
                {data.hasHistory
                  ? t('shouldCost', { figure: t.msg(creditRangeMsg(data.lowCredits, data.highCredits)) })
                  : t('shouldCostAbout', { figure: t.msg(creditRangeMsg(data.lowCredits, data.highCredits)) })}
              </>
            )}
          </Text>
        </Group>

        {/* Generated video is priced per clip, outside the usual range: said as a ceiling. */}
        {fixedLine !== null ? (
          <Text size="xs" data-testid="run-cost-fixed">
            {fixedLine}
          </Text>
        ) : null}

        {/* The user's OWN daily video limit (a setting), for a workflow that generates video. */}
        {data.videoSpendLimit ? (
          <Text size="xs" data-testid="run-cost-video-limit" c={data.videoSpendLimit.limitReached ? 'red' : undefined}>
            {data.videoSpendLimit.dailyLimitCredits === null
              ? t('videoLimitNone', { used: creditFigure(data.videoSpendLimit.usedTodayCredits) })
              : t('videoLimit', {
                used: creditFigure(data.videoSpendLimit.usedTodayCredits),
                limit: creditFigure(data.videoSpendLimit.dailyLimitCredits),
                left: creditFigure(data.videoSpendLimit.remainingTodayCredits ?? 0),
              })}
            {data.videoSpendLimit.alertReached && !data.videoSpendLimit.limitReached
              ? ` ${t('videoLimitAlert')}`
              : null}
          </Text>
        ) : null}

        {/* What the numbers were built from, in the server's own terms. */}
        <Text size="xs" c="dimmed">
          {t.msg(estimateBasisMsg(data))}
        </Text>

        {wideReasons.length > 0 ? (
          <Stack gap={2} data-testid="run-cost-why">
            <Text size="xs" c="dimmed">
              {t('whyWide')}
            </Text>
            <List size="xs" c="dimmed" spacing={2}>
              {wideReasons.map((line) => (
                <List.Item key={line}>{line}</List.Item>
              ))}
            </List>
          </Stack>
        ) : null}

        {!data.affordable && (
          <Alert
            color="yellow"
            variant="light"
            p="xs"
            icon={<IconAlertTriangle size={16} />}
            title={t('notEnough.title')}
            role="status"
          >
            <Text size="xs">
              {shortfall > 0
                ? t('notEnough.bodyShort', { required: creditFigureUp(data.requiredCredits), balance: creditFigure(balance ?? 0), shortfall: creditFigureUp(shortfall) })
                : t('notEnough.body', { required: creditFigureUp(data.requiredCredits), balance: creditFigure(balance ?? 0) })}
            </Text>
            <Anchor component={Link} href="/settings/billing" size="xs" fw={500} mt={4} display="block">
              {t('topUp')}
            </Anchor>
          </Alert>
        )}

        {data.affordable && balance !== null && (
          <Text size="xs" c="dimmed">
            {t('covers', { balance: creditFigure(balance), required: creditFigureUp(data.requiredCredits) })}
          </Text>
        )}

        {runOutRisk ? (
          <Alert
            color="yellow"
            variant="light"
            p="xs"
            icon={<IconAlertTriangle size={16} />}
            title={t('mayRunOut.title')}
            role="status"
            data-testid="run-cost-may-run-out"
          >
            <Text size="xs">
              {t('mayRunOut.body')}
            </Text>
            <Anchor component={Link} href="/settings/billing" size="xs" fw={500} mt={4} display="block">
              {t('topUp')}
            </Anchor>
          </Alert>
        ) : null}

        {showBreakdown && data.steps.length > 0 && (
          <Accordion variant="contained" chevronPosition="left">
            <Accordion.Item value="steps">
              <Accordion.Control>
                <Text size="xs" fw={500}>
                  {t('stepsTitle')}
                </Text>
              </Accordion.Control>
              <Accordion.Panel>
                <List size="xs" spacing={4}>
                  {data.steps.map((step) => (
                    <List.Item key={step.stepOrder}>{t.msg(stepEstimateMsg(step))}</List.Item>
                  ))}
                </List>
              </Accordion.Panel>
            </Accordion.Item>
          </Accordion>
        )}
      </Stack>
    </Paper>
  );
}
