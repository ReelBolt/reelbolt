'use client';

import { useState } from 'react';
import Link from 'next/link';
import { useRouter } from 'next/navigation';
import useSWR from 'swr';
import { Button, Group, Paper, Stack, Text } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconPlayerPlay } from '@tabler/icons-react';
import { notifyInsufficientCredits } from '@/components/billing/insufficientCreditsNotification';
import { useEstimateGate } from '@/components/design-system/EstimateGateModal';
import { useFormat, useT } from '@/i18n';
import { executeWorkflow, lintWorkflow } from '@/lib/api/workflows';
import { PHONE_QUERY, TOUCH_TARGET_PX } from '@/lib/breakpoints';
import { ApiError } from '@/lib/api/client';
import { useAuth } from '@/lib/hooks/use-auth';
import { useWorkflowCostEstimate } from '@/lib/hooks/use-billing';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useWorkflowApp, useWorkflowApps } from '@/lib/hooks/use-workflow-apps';
import { useWorkflow } from '@/lib/hooks/use-workflows';
import { emitFunnelEvent, userKeyFromEmail } from '@/lib/telemetry';
import { runBlockDetailOf } from '@/lib/telemetry/run-events';
import { useStylePresets } from '@/lib/hooks/use-styles';
import { formatCreditAmount } from '@/lib/utils/billing';
import type { RunBrief, WorkflowLintResponse } from '@/lib/types/workflow';
import { decideOneClick, oneClickBrief, reviewBlockDetail, type ReviewReason } from './one-click-model';
import { roundEstimate, roundUpEstimate, typicalCredits } from './run-model';

/**
 * The first run in one click: on the project, once the chosen starter app has its footage, one clear
 * next step. "Run it" starts the app with the footage filled in and nothing else decided for the
 * person; when anything is theirs to decide (a note, a permission, which clips, not enough credits)
 * the button reads "Review and run" and opens the run page instead. Never runs without the click.
 */
export function OneClickRun({ projectId, workflowId }: { projectId: string; workflowId: string }) {
  return <OneClickCard projectId={projectId} workflowId={workflowId} />;
}

function OneClickCard({ projectId, workflowId }: { projectId: string; workflowId: string }) {
  const t = useT('apps.oneClick');
  const fm = useFormat();
  const router = useRouter();
  const { user } = useAuth();
  const phone = useMediaQuery(PHONE_QUERY, false, { getInitialValueInEffect: false }) ?? false;
  const { data: workflow } = useWorkflow(projectId, workflowId);
  const { data: app } = useWorkflowApp(projectId, workflowId);
  const { data: files } = useProjectFiles(projectId);
  const [busy, setBusy] = useState(false);
  const [started, setStarted] = useState(false);
  // Anything the project has already run: this is no longer a first run, so no second paid run is offered here.
  const { data: apps } = useWorkflowApps(projectId);
  const ranBefore = Boolean(apps?.apps.some((a) => a.lastRun));
  const estimateGate = useEstimateGate(projectId);
  const { data: presets, error: presetsError } = useStylePresets();
  const userKey = userKeyFromEmail(user?.email);

  const ready = Boolean(workflow && app && files);
  const prelim = ready && workflow && app && files
    ? oneClickBrief({ manifest: app.manifest, requiresUserInput: workflow.requiresUserInput ?? false, files, stylePresetKeys: presets?.map((p) => p.key) })
    : null;
  // The suggested style is checked against the presets that exist, so the brief waits for that list (or its failure).
  const presetsPending = presets === undefined && !presetsError;
  const brief: RunBrief | null = prelim && 'brief' in prelim && !presetsPending ? prelim.brief : null;
  const videoIds = brief?.sourceFiles?.find((s) => s.role === 'Video')?.fileIds ?? undefined;

  const { data: lint, error: lintError } = useSWR<WorkflowLintResponse>(
    brief ? ['one-click-lint', projectId, workflowId, JSON.stringify(brief)] : null,
    () => lintWorkflow(projectId, workflowId, { brief: brief as RunBrief }),
    { revalidateOnFocus: false, shouldRetryOnError: false },
  );
  const { data: estimate, error: estimateError } = useWorkflowCostEstimate(
    brief ? projectId : undefined,
    brief ? workflowId : undefined,
    undefined,
    videoIds,
  );

  if (!workflow || !app || !files || !prelim || started || ranBefore) return null;

  const decision = decideOneClick({
    manifest: app.manifest,
    requiresUserInput: workflow.requiresUserInput ?? false,
    mediaEgress: app.mediaEgress,
    files,
    findings: lint ? lint.findings : null,
    estimate: estimate ?? null,
    stylePresetKeys: presets?.map((p) => p.key),
    costClass: app.costClass,
  });
  // Still waiting for the check or the price: neither a run nor a review yet.
  const pending = presetsPending || brief !== null && ((!lint && !lintError) || (!estimate && !estimateError));
  const runPage = `/projects/${projectId}/workflows/${workflowId}`;
  const oneClick = !pending && decision.mode === 'oneClick';
  const reason = decision.mode === 'review' ? decision.reason : null;
  const count = (brief?.sourceFiles ?? []).reduce((n, s) => n + (s.fileIds?.length ?? 0), 0) || files.length;
  const title = app.manifest.title?.trim() || app.name;

  const priceLine = (() => {
    if (!oneClick || !estimate) return pending ? t('priceChecking') : null;
    if (!estimate.billingEnabled) return null;
    const typical = typicalCredits(estimate);
    if (app.costClass === 'No credits' || (typical <= 0 && estimate.highCredits <= 0 && (estimate.fixedMaxCredits ?? 0) <= 0)) return t('priceFree');
    const amount = formatCreditAmount(roundEstimate(typical), { locale: fm.locale });
    const have = estimate.balanceCredits === null || estimate.balanceCredits === undefined
      ? null
      : formatCreditAmount(estimate.balanceCredits, { locale: fm.locale });
    return have === null ? null : t('priceAbout', { amount, unit: amount === '1' ? 'one' : 'other', have });
  })();

  const reasonText = (r: ReviewReason): string => {
    switch (r) {
      case 'notes': return t('reason.notes');
      case 'consent': return t('reason.consent');
      case 'footage': return t('reason.footage');
      case 'inputs': return t('reason.inputs');
      case 'shortfall': return t('reason.shortfall');
      case 'check': return t('reason.check');
      case 'estimate': return t('reason.estimate');
      case 'cost': return t('reason.cost', { amount: formatCreditAmount(roundUpEstimate((estimate?.highCredits ?? 0) + (estimate?.fixedMaxCredits ?? 0)), { locale: fm.locale }) });
    }
  };

  const start = async (accept?: number): Promise<void> => {
    if (busy || decision.mode !== 'oneClick') return;
    // One click is one event: the estimate-gate resend carries the accepted figure and is not a new click.
    if (accept === undefined) emitFunnelEvent('alpha.run_clicked', userKey);
    setBusy(true);
    try {
      await executeWorkflow(projectId, workflowId, null, false, {}, accept, undefined, decision.brief);
      setStarted(true);
      emitFunnelEvent('alpha.first_run', userKeyFromEmail(user?.email));
      router.push(runPage);
    } catch (err: unknown) {
      setBusy(false);
      if (!(err instanceof ApiError && err.code === 'ESTIMATE_NOT_ACCEPTED')) emitFunnelEvent('alpha.run_blocked', userKey, runBlockDetailOf(err));
      // The same estimate gate as the run page: its modal asks, and Confirm sends the same request again.
      if (estimateGate.intercept(err, (accepted) => start(accepted))) return;
      if (notifyInsufficientCredits(err)) return;
      // Anything else (an unconfirmed address, a plan or source refusal, a field) has its place on the run page.
      notifications.show({ title: t('failedTitle'), message: t('failedMessage'), color: 'red' });
      router.push(runPage);
    }
  };

  return (
    <Paper withBorder p="md" radius="md" data-testid="one-click-run" role="region" aria-label={t('title')}>
      <Group justify="space-between" align={phone ? 'stretch' : 'center'} gap="md" wrap={phone ? 'wrap' : 'nowrap'}>
        <Stack gap={2} style={{ minWidth: 0, flex: 1 }}>
          <Text fw={650}>{t('title')}</Text>
          <Text size="sm" c="dimmed">{t('body', { app: title, count })}</Text>
          {reason && !pending && <Text size="sm" data-testid="one-click-reason">{reasonText(reason)}</Text>}
          {priceLine && <Text size="sm" fw={500} data-testid="one-click-price">{priceLine}</Text>}
        </Stack>
        {oneClick ? (
          <Button
            leftSection={<IconPlayerPlay size={18} aria-hidden />}
            onClick={() => void start()}
            loading={busy}
            fullWidth={phone}
            style={{ minHeight: TOUCH_TARGET_PX }}
          >
            {busy ? t('starting') : t('run')}
          </Button>
        ) : (
          <Button
            component={Link}
            href={runPage}
            disabled={pending}
            onClick={() => {
              if (reason) emitFunnelEvent('alpha.run_blocked', userKey, reviewBlockDetail(reason));
            }}
            fullWidth={phone}
            style={{ minHeight: TOUCH_TARGET_PX }}
          >
            {t('review')}
          </Button>
        )}
      </Group>
      {estimateGate.modal}
    </Paper>
  );
}
