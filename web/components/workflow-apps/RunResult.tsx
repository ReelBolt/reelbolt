'use client';

import Link from 'next/link';
import { Anchor, Button, Loader, Text, Title } from '@mantine/core';
import { IconCheck, IconCircleDashed, IconDownload, IconMessageChatbot, IconPencil, IconRefresh, IconAlertCircle } from '@tabler/icons-react';
import { useEffect, useState } from 'react';
import { FailureCard } from '@/components/shared/FailureCard';
import { useOutputVideos } from '@/lib/hooks/use-outputs';
import { getOutputDownloadUrl, getOutputPreviewUrl, getOutputVideoUrl, type OutputVideo } from '@/lib/api/outputs';
import { useVideoEditorEnabled } from '@/components/shell/VideoEditorFlag';
import { stepProgressPhrase } from '@/lib/utils/agent-labels';
import type { AppResultKind, WorkflowExecution, WorkflowStep } from '@/lib/types/workflow';
import { failedStepCode, failureMessage, insertFailureAdvice, insertGuideHref, isActive, resolveText, runChargedNothing } from './run-model';
import type { ThirdStep } from './first-result';
import { FirstResultMoment } from './FirstResultMoment';
import { FirstRunFailedLead, FirstRunFailedNext } from './FirstRunFailed';
import { useFirstResult } from './use-first-result';
import { UpgradeCallout } from '@/components/billing/UpgradeCallout';
import { ReportProblem } from '@/components/feedback/ReportProblem';
import { failedRunPlanRefusal } from '@/lib/utils/plan-refusal';
import { useAuth } from '@/lib/hooks/use-auth';
import { emitFunnelEvent, userKeyFromEmail } from '@/lib/telemetry';
import { useFormat, useLocale, useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import styles from './AppRunPage.module.css';
import { CheckedOutputMedia } from '@/components/outputs/CheckedVideo';
import { PictureGallery } from '@/components/outputs/PictureGallery';
import { outputMediaKind } from '@/lib/outputs/media-kind';
import { useDownloadGuard } from '@/lib/outputs/output-check';

export interface ProgressLine {
  key: string;
  phrase: string;
  state: 'done' | 'working' | 'waiting';
}

/** One plain-language line per step of the run: done, working on it, or still waiting. */
export function progressLines(
  execution: WorkflowExecution,
  steps: readonly Pick<WorkflowStep, 'id' | 'stepOrder' | 'stepType' | 'agentDefinitionId'>[],
  agentTypeOf: (agentDefinitionId: string) => string | null,
  tr?: T,
): ProgressLine[] {
  const ordered = [...(execution.steps ?? steps)].sort((a, b) => a.stepOrder - b.stepOrder);
  const statusOf = (stepId: string) => {
    const results = (execution.stepResults ?? []).filter((r) => r.workflowStepId === stepId);
    if (results.some((r) => r.status === 'Running')) return 'working' as const;
    if (results.some((r) => r.status === 'Completed' || r.status === 'Skipped')) return 'done' as const;
    return 'waiting' as const;
  };
  const lines = ordered.map((s) => ({
    key: s.id,
    phrase: stepProgressPhrase(s.stepType ?? 'Agent', agentTypeOf(s.agentDefinitionId), tr),
    state: statusOf(s.id),
  }));
  // The first step with no result yet is the one being worked on while the run is active and no
  // result row says "Running" (results are written when a step starts or finishes).
  if (isActive(execution) && !lines.some((l) => l.state === 'working')) {
    const next = lines.find((l) => l.state === 'waiting');
    if (next) next.state = 'working';
  }
  return lines;
}

// The progress state words (done / in progress / waiting) are catalogue messages: apps.run.result.state*.

const OUTPUT_POLL_MS = 2000;
const OUTPUT_WAIT_MS = 60000;

function pickOutput(outputs: readonly OutputVideo[] | undefined, executionId: string): OutputVideo | null {
  const mine = (outputs ?? []).filter((o) => o.workflowExecutionId === executionId);
  if (mine.length === 0) return null;
  return mine.find((o) => o.isFinal) ?? [...mine].sort((a, b) => Date.parse(b.producedAt) - Date.parse(a.producedAt))[0] ?? null;
}

interface RunResultProps {
  projectId: string;
  workflowId: string;
  /** What the app leads with; only a video is played here, other kinds link to the run. */
  resultKind: AppResultKind;
  /** The newest run, or undefined before the first one. */
  execution: WorkflowExecution | undefined;
  /** Every run of this app the page knows: says whether the run on screen is the person's first. */
  executions?: readonly WorkflowExecution[] | undefined;
  /** Whether credits apply here at all; a refund is only mentioned where they do. */
  billingEnabled?: boolean;
  /** The third next step of the first-video moment, from what this app has (see `thirdStepFor`). */
  third?: ThirdStep;
  /** Brings this run's choices back with the Shape choice in focus. */
  onTryShape?: (() => void) | undefined;
  lines: readonly ProgressLine[];
  /** What the app needs, for the empty state ("Your clips"). */
  needs: readonly string[];
  busy: boolean;
  retryError: string | null;
  onRetry: () => void;
  onResume: () => void;
  onChangeOne: () => void;
  onAskAssistant: () => void;
  /** Scrolls to and opens the footage slot; the insert next-step block offers it only when given. */
  onChooseAnotherClip?: (slot: 'video' | 'insertContent') => void;
  phone?: boolean;
}

/**
 * The result area: the empty state before the first run, plain-language progress while it runs, a
 * banner with Retry or Resume when it failed, and the video with its actions when it finished.
 * Completion and failure are announced from the labelled status line.
 *
 * A person's first finished video is framed once as theirs (`FirstResultMoment`), and a first run
 * that failed says so kindly, with what happens to the credits; both only when the account's own
 * state says this really is their first (see `useFirstResult`).
 */
export function RunResult({
  projectId, workflowId, resultKind, execution, executions, lines, needs, busy, retryError, onRetry, onResume, onChangeOne, onAskAssistant, onChooseAnotherClip,
  billingEnabled = false, third = 'recipes', onTryShape, phone = false,
}: RunResultProps) {
  const t = useT('apps.run.result');
  const tr = useT();
  const tFirst = useT('apps.run.first');
  const { user } = useAuth();
  const locale = useLocale();
  const guardDownload = useDownloadGuard();
  const f = useFormat();
  const editorOn = useVideoEditorEnabled();
  const finished = execution?.status === 'Passed';
  const wantsOutputs = finished && (resultKind === 'video' || resultKind === 'image');
  const executionId = execution?.id;
  // A run that just Passed may be listed a moment late: re-read every 2 s for about a minute, then say plainly it is missing.
  const [gaveUpFor, setGaveUpFor] = useState<string | null>(null);
  useEffect(() => {
    if (!wantsOutputs || !executionId) return undefined;
    const timer = setTimeout(() => setGaveUpFor(executionId), OUTPUT_WAIT_MS);
    return () => clearTimeout(timer);
  }, [wantsOutputs, executionId]);
  const polling = gaveUpFor !== executionId;
  const { data: outputs, error: outputsError, mutate: mutateOutputs } = useOutputVideos(wantsOutputs ? projectId : '', {
    pollMs: polling ? OUTPUT_POLL_MS : 0,
    keepPolling: (list) => resultKind === 'video' && pickOutput(list, executionId ?? '') === null,
  });
  const output = finished && execution ? pickOutput(outputs, execution.id) : null;
  const insertAdvice = execution?.status === 'Failed' ? insertFailureAdvice(failedStepCode(execution)) : null;
  const runHref = execution ? `/projects/${projectId}/workflows/${workflowId}/executions/${execution.id}` : null;
  // The moment frames a video that is here to be played; until the file is listed the pane says "being prepared".
  // Pictures only: every picture of the run as one gallery, no final/draft distinction.
  const pictures = finished && execution
    ? (outputs ?? []).filter((o) => o.workflowExecutionId === execution.id && !o.isIntermediate && outputMediaKind(o.fileName) === 'image')
    : [];
  const picturesOnly = pictures.length > 0 && pictures.length === (outputs ?? []).filter((o) => o.workflowExecutionId === execution?.id && !o.isIntermediate).length;
  const videoReady = finished && resultKind === 'video' && output !== null;
  const first = useFirstResult({ execution, executions: executions ?? (execution ? [execution] : []), videoReady });
  const firstReady = first.active && videoReady;

  const notFoundSentence = t('videoNotFound');
  let announcement = '';
  if (!execution) announcement = t('announceEmpty');
  else if (isActive(execution)) announcement = t('announceActive');
  else if (execution.status === 'Passed') {
    // "Ready" only when the video really is: the heading must agree with the body below it.
    if (resultKind !== 'video' || videoReady) announcement = firstReady ? tFirst('title') : t('announceReady');
    else if (outputsError) announcement = tr('errors.state.title.other');
    else if (polling) announcement = t('preparing');
    else announcement = notFoundSentence;
  }
  else if (execution.status === 'Failed') announcement = t('announceFailed');
  else announcement = t('announceStopped');

  return (
    <section aria-label={t('label')} className={styles.resultPane} data-testid="run-result">
      <div role="status" aria-live="polite">
        <Title order={3} size="h4">
          {announcement}
        </Title>
      </div>

      {!execution && (
        <>
          <Text size="sm" className={styles.muted}>
            {t('fillIn', { where: phone ? 'above' : 'left' })}
          </Text>
          {needs.length > 0 && (
            <Text size="sm" className={styles.muted}>
              {t('needs', { list: f.list(needs.slice(), 'conjunction') })}
            </Text>
          )}
        </>
      )}

      {execution && isActive(execution) && (
        <>
          <ol className={styles.progress} aria-label={t('progress')}>
            {lines.map((l) => (
              <li key={l.key} className={styles.progressItem} data-state={l.state}>
                {l.state === 'done' ? <IconCheck size={16} aria-hidden="true" /> : l.state === 'working' ? <Loader size={14} aria-hidden="true" /> : <IconCircleDashed size={16} aria-hidden="true" />}
                <span>{l.phrase}</span>
                <span className={styles.muted}>({l.state === 'done' ? t('stateDone') : l.state === 'working' ? t('stateWorking') : t('stateWaiting')})</span>
              </li>
            ))}
          </ol>
          {runHref && (
            <Anchor component={Link} href={runHref} size="sm">
              {t('seeEveryStep')}
            </Anchor>
          )}
        </>
      )}

      {execution?.status === 'Failed' && (
        <div className={styles.banner} data-testid="failed-banner">
          <div style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
            <IconAlertCircle size={18} aria-hidden="true" />
            <strong>{t('failed')}</strong>
          </div>
          {first.failedFirst && <FirstRunFailedLead />}
          <Text size="sm">{resolveText(t, failureMessage(failedStepCode(execution), execution.errorMessage))}</Text>
          {failedRunPlanRefusal(execution) && (
            <UpgradeCallout feature={failedRunPlanRefusal(execution)?.feature} testId="plan-refusal-upgrade" />
          )}
          {first.failedFirst && <FirstRunFailedNext execution={execution} billingEnabled={billingEnabled} />}
          {retryError && <Text size="sm">{retryError}</Text>}
          <ReportProblem run={execution} executionId={execution.id} showOnUs={!first.failedFirst} />
          {insertAdvice && (
            <div data-testid="insert-next-step" role="group" aria-label={t('insertNext.heading')}>
              <Text size="sm" fw={600}>{t('insertNext.heading')}</Text>
              {runChargedNothing(execution) && <Text size="sm">{t('insertNext.notCharged')}</Text>}
              <Anchor href={insertGuideHref(locale)} target="_blank" rel="noopener noreferrer" size="sm">{t('insertNext.whatWorks')}</Anchor>
            </div>
          )}
          <div className={styles.buttonRow}>
            {execution.canResume && (
              <Button onClick={onResume} loading={busy} leftSection={<IconRefresh size={16} />} style={{ minHeight: 44 }}>
                {t('resume')}
              </Button>
            )}
            {insertAdvice?.chooseClip && insertAdvice.slot && onChooseAnotherClip && (
              <Button onClick={() => onChooseAnotherClip(insertAdvice.slot!)} style={{ minHeight: 44 }}>
                {t('insertNext.chooseClip')}
              </Button>
            )}
            <Button variant={execution.canResume || insertAdvice?.chooseClip ? 'default' : 'filled'} onClick={onRetry} loading={busy} style={{ minHeight: 44 }}>
              {insertAdvice ? t('insertNext.retry') : t('retry')}
            </Button>
            <Button variant="default" leftSection={<IconMessageChatbot size={16} />} onClick={onAskAssistant} style={{ minHeight: 44 }}>
              {t('askAssistant')}
            </Button>
          </div>
          {runHref && (
            <Anchor component={Link} href={runHref} size="sm">
              {t('seeWhatHappened')}
            </Anchor>
          )}
        </div>
      )}

      {execution && (execution.status === 'Cancelled' || execution.status === 'Stopped') && (
        <Text size="sm" className={styles.muted}>
          {t('stopped')}
        </Text>
      )}

      {finished && execution && (
        <>
          {picturesOnly ? (
            <PictureGallery
              pictures={pictures.map((o) => ({
                id: o.stepResultId,
                src: getOutputVideoUrl(projectId, o.stepResultId),
                thumbSrc: getOutputPreviewUrl(projectId, o.stepResultId, 480),
                largeSrc: getOutputPreviewUrl(projectId, o.stepResultId, 1920),
                downloadUrl: getOutputDownloadUrl(projectId, o.stepResultId, o.fileName),
                fileName: o.fileName,
              }))}
            />
          ) : firstReady && output ? (
            <>
              <FirstResultMoment
                projectId={projectId}
                executionId={execution.id}
                output={output}
                showTitle={false}
                onChange={onChangeOne}
                third={third}
                onTryShape={onTryShape}
                onDismiss={first.dismiss}
              />
              {editorOn && (
                <div className={styles.buttonRow}>
                  <Button component={Link} href={`/projects/${projectId}/editor/${output.stepResultId}`} variant="default" leftSection={<IconPencil size={16} />} style={{ minHeight: 44 }}>
                    {t('editInEditor')}
                  </Button>
                </div>
              )}
            </>
          ) : resultKind === 'video' && output ? (
            <>
              <CheckedOutputMedia className={styles.video} controls preload="metadata" playsInline src={getOutputVideoUrl(projectId, output.stepResultId)} fileName={output.fileName} alt={output.fileName} aria-label={t('finishedVideo')} />
              <div className={styles.buttonRow}>
                <Button
                  component="a"
                  href={getOutputDownloadUrl(projectId, output.stepResultId, output.fileName)}
                  // The same export transition the outputs player reports: once per person, with their consent.
                  onClick={(e) => {
                    emitFunnelEvent('alpha.first_export_initiated', userKeyFromEmail(user?.email));
                    void guardDownload(getOutputDownloadUrl(projectId, output.stepResultId, output.fileName), output.fileName)(e);
                  }}
                  download
                  leftSection={<IconDownload size={16} />}
                  style={{ minHeight: 44 }}
                >
                  {t('download')}
                </Button>
                {editorOn && (
                  <Button component={Link} href={`/projects/${projectId}/editor/${output.stepResultId}`} variant="default" leftSection={<IconPencil size={16} />} style={{ minHeight: 44 }}>
                    {t('editInEditor')}
                  </Button>
                )}
              </div>
            </>
          ) : outputsError && resultKind === 'video' ? (
            <FailureCard
              error={outputsError}
              onRetry={() => void mutateOutputs()}
              extra={runHref ? <Anchor component={Link} href={runHref} size="sm">{t('openRun')}</Anchor> : null}
            />
          ) : (
            <Text size="sm" className={styles.muted}>
              {resultKind !== 'video' ? t('runFinished') : polling ? t('preparing') : notFoundSentence}{' '}
              {runHref && (
                <Anchor component={Link} href={runHref} size="sm">
                  {t('openRun')}
                </Anchor>
              )}
            </Text>
          )}
          <div className={styles.buttonRow}>
            {/* The first-video moment carries its own "Change something": the same entry point, once. */}
            {!firstReady && (
              <Button variant="light" onClick={onChangeOne} style={{ minHeight: 44 }}>
                {t('changeOne')}
              </Button>
            )}
            <Button variant="default" leftSection={<IconMessageChatbot size={16} />} onClick={onAskAssistant} style={{ minHeight: 44 }}>
              {t('askToChange')}
            </Button>
          </div>
        </>
      )}
    </section>
  );
}
