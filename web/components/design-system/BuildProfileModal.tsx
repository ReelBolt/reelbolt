'use client';

import { describeError } from '@/lib/api/describe-error';
import { useCallback, useEffect, useRef, useState } from 'react';
import Link from 'next/link';
import { Alert, Button, Group, Modal, Stack, Text } from '@mantine/core';
import { InsufficientCreditsAlert } from '@/components/billing/InsufficientCreditsAlert';
import { QuietText } from '@/components/dashboard/Panel';
import { insufficientCredits } from '@/lib/api/billing';
import { ApiError, extractErrorMessage } from '@/lib/api/client';
import { buildProfile, getDesignSystemsSummary, ingestDesignSystem } from '@/lib/api/design-system';
import { getExecution } from '@/lib/api/executions';
import type { InsufficientCredits } from '@/lib/types/billing';
import {
  BUILD_BUTTON_LABEL,
  BUILD_CHECKING_DRAFTS,
  BUILD_COMPOSE_STEP_ORDER,
  BUILD_COST_NOTICE,
  BUILD_DONE,
  BUILD_DRAFT_READY,
  BUILD_ERROR,
  BUILD_FAILED,
  BUILD_FREE_PATH,
  BUILD_IMPORT_INSTEAD_LABEL,
  BUILD_MODAL_BODY,
  BUILD_MODAL_TITLE,
  BUILD_NOT_SAVED,
  BUILD_SAVE_DRAFTS_LABEL,
  BUILD_SAVE_ERROR,
  BUILD_SAVED,
  BUILD_STOPPED,
  buildEstimateRange,
  buildOverCapText,
  buildRunningStatus,
  CONFIRM_BUTTON_LABEL,
  upToCredits,
  VIEW_RUN_LABEL,
} from './copy';
import { InlineJobStatus } from '@/components/activity';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import { isActiveState } from '@/lib/activity/notifier';
import { useT } from '@/i18n';

interface BuildProfileModalProps {
  projectId: string;
  opened: boolean;
  onClose: () => void;
  /** Called on a terminal `Passed`/`Failed` so the tab can refresh the summaries. */
  onBuilt?: () => void;
  /** Opens the import modal; the free way forward when a build is over the limit. */
  onImport?: () => void;
}

type RunState = {
  executionId: string;
  workflowId: string;
  status: string;
};

const TERMINAL_STATUSES = ['Passed', 'Failed', 'Cancelled', 'Stopped'];

/** The estimate an `ESTIMATE_NOT_ACCEPTED` / `ESTIMATE_OVER_CAP` 409 carries; `typical` is optional. */
interface EstimateFacts {
  low: number | null;
  typical: number | null;
  high: number | null;
  cap: number | null;
}

function num(value: unknown): number | null {
  return typeof value === 'number' && Number.isFinite(value) ? value : null;
}

function estimateFacts(error: ApiError): EstimateFacts {
  const body = typeof error.body === 'object' && error.body !== null ? (error.body as Record<string, unknown>) : {};
  const estimate =
    typeof body.estimate === 'object' && body.estimate !== null
      ? (body.estimate as Record<string, unknown>)
      : {};
  return {
    low: num(estimate.low),
    typical: num(estimate.typical),
    high: num(estimate.high),
    cap: num(body.capCredits),
  };
}

/** After a Passed run: has its profile been saved as a draft, or must the user save it? */
type SaveState = 'checking' | 'ready' | 'offer' | 'saved';

/**
 * The Build flow of the brand-and-product tab (P2-22, SPEC §9/§9.4/§17): start the profile build,
 * explain the estimate when the server asks for it, surface a credits shortfall as the shared 402
 * state, and follow the resulting run to its run page.
 */
export function BuildProfileModal({ projectId, opened, onClose, onBuilt, onImport }: BuildProfileModalProps) {
  const tr = useT();
  const [submitting, setSubmitting] = useState(false);
  const [estimate, setEstimate] = useState<EstimateFacts | null>(null);
  const [overCap, setOverCap] = useState<EstimateFacts | null>(null);
  const [saveState, setSaveState] = useState<SaveState>('checking');
  const [saving, setSaving] = useState(false);
  const [saveError, setSaveError] = useState<string | null>(null);
  // The parent passes an inline callback; keep it out of the polling effect's dependencies so a
  // refresh it triggers does not restart the poll.
  const onBuiltRef = useRef(onBuilt);
  useEffect(() => {
    onBuiltRef.current = onBuilt;
  }, [onBuilt]);
  const [error, setError] = useState<string | null>(null);
  const [credits, setCredits] = useState<InsufficientCredits | null>(null);
  const [run, setRun] = useState<RunState | null>(null);

  const reset = useCallback(() => {
    setSubmitting(false);
    setEstimate(null);
    setOverCap(null);
    setSaveState('checking');
    setSaving(false);
    setSaveError(null);
    setError(null);
    setCredits(null);
    setRun(null);
  }, []);

  /** Every close path resets, so reopening never shows the previous attempt's state. */
  const handleClose = useCallback(() => {
    reset();
    onClose();
  }, [reset, onClose]);

  const start = useCallback(
    async (acceptEstimateCredits?: number) => {
      setSubmitting(true);
      setError(null);
      setCredits(null);
      setOverCap(null);
      setEstimate(null);
      try {
        const response = await buildProfile(
          projectId,
          acceptEstimateCredits === undefined ? {} : { acceptEstimateCredits },
        );
        setRun({
          executionId: response.executionId,
          workflowId: response.workflowId,
          status: 'Queued',
        });
      } catch (e) {
        const shortfall = insufficientCredits(e);
        if (shortfall) {
          setCredits(shortfall);
        } else if (e instanceof ApiError && e.code === 'ESTIMATE_NOT_ACCEPTED') {
          const facts = estimateFacts(e);
          // No readable high figure means nothing honest to accept; show the server's sentence.
          if (facts.high === null && facts.cap === null) {
            setError(extractErrorMessage(e.body) ?? e.message);
          } else {
            setEstimate(facts);
          }
        } else if (e instanceof ApiError && e.code === 'ESTIMATE_OVER_CAP') {
          setOverCap(estimateFacts(e));
        } else {
          setError(describeError(tr, e, tr.msg(BUILD_ERROR)));
        }
      } finally {
        setSubmitting(false);
      }
    },
    [projectId],
  );

  // Follow the run in the background; the link is useful either way, so nothing blocks on it.
  const runExecutionId = run?.executionId ?? null;
  // The activity model's live row for the build run (stage, percent, stop).
  const buildJob = useJobFor((j) => runExecutionId !== null && j.executionId === runExecutionId);
  useEffect(() => {
    if (!runExecutionId) return;
    let cancelled = false;
    let terminal = false;
    const tick = async () => {
      if (terminal) return;
      try {
        const execution = await getExecution(projectId, runExecutionId);
        if (cancelled) return;
        setRun((current) => (current ? { ...current, status: execution.status } : current));
        if (TERMINAL_STATUSES.includes(execution.status)) {
          terminal = true;
          if (execution.status === 'Passed' || execution.status === 'Failed') onBuiltRef.current?.();
        }
      } catch {
        // A transient read failure leaves the last known status; keep polling.
      }
    };
    const timer = setInterval(() => void tick(), 5000);
    void tick();
    return () => {
      cancelled = true;
      clearInterval(timer);
    };
  }, [runExecutionId, projectId]);

  // A Passed run normally leaves a draft behind (the Compose step saves it). When the automatic save
  // was refused or never ran, there is no draft from this run and the user is offered "Save as drafts".
  const passed = run?.status === 'Passed';
  useEffect(() => {
    if (!passed || !runExecutionId) return;
    let cancelled = false;
    (async () => {
      try {
        const summary = await getDesignSystemsSummary(projectId);
        if (cancelled) return;
        const hasDraft = [summary.design, summary.productMap].some(
          (system) => system?.latestDraft?.workflowExecutionId === runExecutionId,
        );
        setSaveState(hasDraft ? 'ready' : 'offer');
      } catch {
        // Unknown: ingest is idempotent, so offering the save is safe.
        if (!cancelled) setSaveState('offer');
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [passed, runExecutionId, projectId]);

  const saveAsDrafts = useCallback(async () => {
    if (!runExecutionId) return;
    setSaving(true);
    setSaveError(null);
    try {
      await ingestDesignSystem(projectId, runExecutionId, BUILD_COMPOSE_STEP_ORDER);
      setSaveState('saved');
      onBuiltRef.current?.();
    } catch (e) {
      setSaveError(describeError(tr, e, tr.msg(BUILD_SAVE_ERROR)));
    } finally {
      setSaving(false);
    }
  }, [projectId, runExecutionId]);

  const runHref = run
    ? `/projects/${projectId}/workflows/${run.workflowId}/executions/${run.executionId}`
    : null;

  return (
    <Modal opened={opened} onClose={handleClose} title={tr.msg(BUILD_MODAL_TITLE)} data-testid="build-profile-modal">
      <Stack gap="md">
        {credits ? (
          <InsufficientCreditsAlert data={credits} />
        ) : run && runHref ? (
          <Stack gap="sm">
            {run.status === 'Passed' ? (
              <Stack gap="xs" data-testid="build-status">
                <Text size="sm">{tr.msg(BUILD_DONE)}</Text>
                {saveState === 'checking' && <QuietText>{tr.msg(BUILD_CHECKING_DRAFTS)}</QuietText>}
                {saveState === 'ready' && <Text size="sm">{tr.msg(BUILD_DRAFT_READY)}</Text>}
                {saveState === 'saved' && <Text size="sm">{tr.msg(BUILD_SAVED)}</Text>}
                {saveState === 'offer' && (
                  <>
                    <Text size="sm">{tr.msg(BUILD_NOT_SAVED)}</Text>
                    {saveError && (
                      <Alert color="red" data-testid="build-save-error">{saveError}</Alert>
                    )}
                    <Group>
                      <Button loading={saving} onClick={() => void saveAsDrafts()}>
                        {tr.msg(BUILD_SAVE_DRAFTS_LABEL)}
                      </Button>
                    </Group>
                  </>
                )}
              </Stack>
            ) : run.status === 'Failed' ? (
              <Text size="sm" data-testid="build-status">{tr.msg(BUILD_FAILED)}</Text>
            ) : run.status === 'Cancelled' || run.status === 'Stopped' ? (
              <Text size="sm" data-testid="build-status">{tr.msg(BUILD_STOPPED)}</Text>
            ) : (
              <Stack gap="xs">
                <Text size="sm" data-testid="build-status">{tr.msg(buildRunningStatus(run.status))}</Text>
                {buildJob && isActiveState(buildJob.state) && <InlineJobStatus job={buildJob} />}
              </Stack>
            )}
            <Group>
              <Button component={Link} href={runHref} variant="light">
                {tr.msg(VIEW_RUN_LABEL)}
              </Button>
            </Group>
          </Stack>
        ) : (
          <>
            <QuietText>{tr.msg(BUILD_MODAL_BODY)}</QuietText>
            <Text size="sm">{tr.msg(BUILD_COST_NOTICE)}</Text>
            {estimate !== null && (
              <Stack gap={2}>
                {estimate.low !== null && estimate.high !== null && (
                  <Text size="sm" fw={600} data-testid="build-estimate">
                    {tr.msg(buildEstimateRange(estimate.low, estimate.high, estimate.typical))}
                  </Text>
                )}
                {estimate.high !== null && (
                  <Text size="sm" data-testid={estimate.low === null ? 'build-estimate' : undefined}>
                    {tr.msg(upToCredits(estimate.high))}
                  </Text>
                )}
              </Stack>
            )}
            {overCap !== null && (
              <Alert color="yellow" data-testid="build-over-cap">
                <Stack gap="xs">
                  <Text size="sm">{tr.msg(buildOverCapText(overCap.low, overCap.high, overCap.cap))}</Text>
                  <Text size="sm">{tr.msg(BUILD_FREE_PATH)}</Text>
                  {onImport && (
                    <Group>
                      <Button size="xs" onClick={onImport}>{tr.msg(BUILD_IMPORT_INSTEAD_LABEL)}</Button>
                    </Group>
                  )}
                </Stack>
              </Alert>
            )}
            {error && (
              <Alert color="red" data-testid="build-error">
                {error}
              </Alert>
            )}
            {overCap === null && (
              <Group>
                {estimate !== null && estimate.high !== null ? (
                  <Button
                    onClick={() => void start(estimate.high ?? undefined)}
                    loading={submitting}
                    data-testid="build-confirm"
                  >
                    {tr.msg(CONFIRM_BUTTON_LABEL)}
                  </Button>
                ) : (
                  <Button
                    onClick={() => void start()}
                    loading={submitting}
                    data-testid="build-start"
                  >
                    {tr.msg(BUILD_BUTTON_LABEL)}
                  </Button>
                )}
              </Group>
            )}
          </>
        )}
      </Stack>
    </Modal>
  );
}
