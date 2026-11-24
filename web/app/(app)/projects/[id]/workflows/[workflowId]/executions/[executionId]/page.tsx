'use client';
/* eslint-disable i18next/no-literal-string -- the remaining literals are ids, enum keys and t.rich tag handlers, not user-visible text */

import { use, useEffect, useRef, useState, useCallback, useMemo } from 'react';
import Link from 'next/link';
import useSWR from 'swr';
import { LoadState } from '@/components/shared/LoadState';
import { Stack, Card, Group, Text, Badge, Loader, Center, Progress, Timeline, Paper, Alert, Drawer, Button, SimpleGrid, Tooltip, Code, Modal, SegmentedControl, Collapse } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useDisclosure, useMediaQuery } from '@mantine/hooks';
import { IconPlayerPlay, IconAlertCircle, IconPlayerStop, IconBolt, IconActivity, IconRefresh, IconChevronDown, IconChevronUp, IconDownload, IconPencil } from '@tabler/icons-react';
import {
  ReactFlow,
  Background,
  BackgroundVariant,
  Controls,
  Node,
  Edge,
  ReactFlowProvider,
  useNodesState,
  useEdgesState,
  MarkerType,
  type NodeMouseHandler,
} from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import { PageHeader } from '@/components/shared/PageHeader';
import { apiFetch, ApiError } from '@/lib/api/client';
import { sourceRefusalMessage } from '@/lib/api/source-limits';
import { getExecution, getStepResult, stopExecution, retryExecution, resumeExecution } from '@/lib/api/executions';
import { notifyInsufficientCredits } from '@/components/billing/insufficientCreditsNotification';
import { useEstimateGate } from '@/components/design-system/EstimateGateModal';
import { useStepResultArtifact } from '@/lib/hooks/use-step-result-artifact';
import { useAgents } from '@/lib/hooks/use-agents';
import { useExecutions } from '@/lib/hooks/use-executions';
import { useProject } from '@/lib/hooks/use-projects';
import { useRunners } from '@/lib/hooks/use-runners';
import { useExecutionCredits } from '@/lib/hooks/use-billing';
import { isNeedsAttention } from '@/lib/utils/run-status';
import { RunWarningsAlert } from '@/components/outputs/RunWarningsAlert';
import { stepProgressPhrase, stepRoleLabel } from '@/lib/utils/agent-labels';
import { collectRunWarnings, estimateRemainingMs, formatRunDate, formatTimeLeft, runName, runOrdinal } from '@/lib/utils/run-summary';
import type { WorkflowExecution, WorkflowDefinition, WorkflowStepResult } from '@/lib/types/workflow';
import { formatDurationLong } from '@/lib/utils/format';
import { RunStepList, stepStateFor, type RunStepListItem } from '@/components/workflows/RunStepList';
import { runFailureText } from '@/lib/utils/run-failure';
import { failedRunPlanRefusal } from '@/lib/utils/plan-refusal';
import { UpgradeCallout } from '@/components/billing/UpgradeCallout';
import { useOutputVideos } from '@/lib/hooks/use-outputs';
import { PictureGallery, type GalleryPicture } from '@/components/outputs/PictureGallery';
import { outputMediaKind } from '@/lib/outputs/media-kind';
import { getOutputDownloadUrl, getOutputPreviewUrl, getOutputVideoUrl } from '@/lib/api/outputs';
import { stepChargeLabel } from '@/lib/utils/billing';
import { formatCredits } from '@/lib/utils/usage-labels';
import { STEP_TYPE_COLORS, STEP_TYPE_LABELS } from '@/lib/utils/constants';
import { StepResultPanel } from '@/components/workflows/StepResultPanel';
import { RUN_FORMAT_OPTIONS, resolutionLabel } from '@/components/styles/style-options';
import { RequestStepChangeModal } from '@/components/change-requests/RequestStepChangeModal';
import { ChangeVideoPanel } from '@/components/change-requests/ChangeVideoPanel';
import { DEFAULT_MAX_TARGETS, type ChangeEntry } from '@/components/change-requests/change-set';
import { useChangeSet } from '@/components/change-requests/use-change-set';
import { AppInputChangeModal } from '@/components/workflow-apps/AppInputChangeModal';
import { canChangeFields } from '@/components/workflow-apps/app-input-change';
import { useChangeCapabilities, useChangeRequests } from '@/lib/hooks/use-change-requests';
import type { StepChangeStepCapability } from '@/lib/types/change-requests';
import { RegenerateVideoClipsModal, getVideoGenerateInfo } from '@/components/workflows/RegenerateVideoClipsModal';
import {
  ExecutionEventCard,
  getEventIcon,
  getPayloadNumber,
  getPayloadString,
  isTechnicalEvent,
} from '@/components/workflows/ExecutionEventCard';
import { useExecutionStream } from '@/lib/hooks/use-execution-stream';
import { resyncDelay } from '@/lib/utils/resync-delay';
import { useResyncTick } from '@/lib/hooks/use-resync-tick';
import { hydrateHistoricalStepEvents, mergeStepEvents } from '@/lib/utils/execution-event-hydration';
import { executionPinLabel } from '@/components/design-system/copy';
import { JobProgress, JobStatusBadge } from '@/components/activity';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import type { ActivityJobState } from '@/lib/activity/types';
import { useFormat, useT } from '@/i18n';
import { describeError } from '@/lib/api/describe-error';
import { FailureCard } from '@/components/shared/FailureCard';
import { CheckedOutputMedia } from '@/components/outputs/CheckedVideo';
import { PostTextButton } from '@/components/outputs/PostTextDialog';
import { PublishButton } from '@/components/publishing/PublishDialog';
import { useDownloadGuard } from '@/lib/outputs/output-check';
import { useVideoEditorEnabled } from '@/components/shell/VideoEditorFlag';
import { RunAppInputsList } from '@/components/workflow-apps/RunAppInputsList';
import { FirstResultMoment } from '@/components/workflow-apps/FirstResultMoment';
import { FirstRunFailedLead, FirstRunFailedNext, FailedRunCredits } from '@/components/workflow-apps/FirstRunFailed';
import { InsertFailureNext } from '@/components/workflow-apps/InsertFailureNext';
import { useOptionalAssistant } from '@/components/assistant/AssistantShellProvider';
import { useFirstResult } from '@/components/workflow-apps/use-first-result';
import { useRouter } from 'next/navigation';
import { ReportProblem } from '@/components/feedback/ReportProblem';

function isTerminalExecutionStatus(status: WorkflowExecution['status'] | undefined): boolean {
  return status === 'Passed' || status === 'Failed' || status === 'Cancelled';
}

interface StepProgressInfo {
  stage: string;
  percent: number | null;
}

const RUN_STATUS_COLOR: Record<WorkflowExecution['status'], string> = {
  Queued: 'gray', Running: 'blue', Passed: 'green', Failed: 'red', Cancelled: 'orange', Stopped: 'orange',
};

/** The activity kit's state for a run status, so the run page badge reads like the tray. */
const RUN_JOB_STATE: Record<WorkflowExecution['status'], ActivityJobState> = {
  Queued: 'waiting', Running: 'running', Passed: 'done', Failed: 'failed', Cancelled: 'cancelled', Stopped: 'cancelled',
};

/** A label/value tile. Labels are short and never wrap mid-word on a 375px screen. */
function StatTile({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <Paper withBorder p="sm" radius="md" style={{ minWidth: 0 }}>
      <Text size="xs" c="dimmed" style={{ whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{label}</Text>
      <Text size="md" fw={700} style={{ overflowWrap: 'anywhere' }}>{children}</Text>
    </Paper>
  );
}

function ExecutionDetailPageInner({ params }: { params: Promise<{ id: string; workflowId: string; executionId: string }> }) {
  const { id: projectId, workflowId, executionId } = use(params);
  const t = useT('projects.execution');
  const guardDownload = useDownloadGuard();
  const fmt = useFormat();
  const tr = useT();
  const editorOn = useVideoEditorEnabled();
  const [execution, setExecution] = useState<WorkflowExecution | null>(null);
  const [workflow, setWorkflow] = useState<WorkflowDefinition | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<unknown>(null);
  const [loadAttempt, setLoadAttempt] = useState(0);
  const [regenModalOpened, setRegenModalOpened] = useState(false);
  const estimateGate = useEstimateGate(projectId);
  const [nodes, setNodes, onNodesChange] = useNodesState<Node>([]);
  const [edges, setEdges, onEdgesChange] = useEdgesState<Edge>([]);
  // Only the id is state — the object itself is derived below from `execution`, so it always
  // reflects the latest data without a sync effect.
  const [selectedResultId, setSelectedResultId] = useState<string | null>(null);
  const [retryError, setRetryError] = useState<string | null>(null);
  const [retryConfirmOpened, setRetryConfirmOpened] = useState(false);
  const [resuming, setResuming] = useState(false);
  const [stepsView, setStepsView] = useState<'list' | 'diagram'>('list');
  const [failureDetailsOpen, setFailureDetailsOpen] = useState(false);
  const { data: changeCaps } = useChangeCapabilities(projectId, execution?.id ?? null);
  const { data: changeList, mutate: mutateChangeList } = useChangeRequests(projectId, execution?.id ?? null);
  const [changeTarget, setChangeTarget] = useState<StepChangeStepCapability | null>(null);
  const filmTextareaRef = useRef<HTMLTextAreaElement>(null);
  const changeSet = useChangeSet();
  // docs/design/app-inputs.md 29: an app run's fields and run settings join the same change set as its steps.
  const [fieldsOpen, setFieldsOpen] = useState(false);
  const maxTargets = changeCaps?.maxTargets ?? DEFAULT_MAX_TARGETS;
  // Below `sm` (768px) the flow canvas and step panel stack, so a selected step's detail would
  // scroll off-screen; render it as a bottom Drawer there instead of a grid cell.
  const isDesktop = useMediaQuery('(min-width: 48em)');
  // Telemetry (token counts, stream state, raw payloads, engine ids) lives behind this disclosure.
  const [detailsOpen, { toggle: toggleDetails }] = useDisclosure(false);
  const { data: agents } = useAgents();
  const { data: siblingExecutions, mutate: revalidateSiblings } = useExecutions(projectId, workflowId);
  const { data: project } = useProject(projectId);
  // E8-UI: the workspace's paired machines, so a step's "Ran on <machine>" badge can name the
  // machine the engine recorded on the step result. Member-readable and small; when it cannot be
  // read the badge still renders — just as "Ran on your machine", with the id in its tooltip.
  const { data: runnerDevices } = useRunners();
  const runnerNames = useMemo(() => {
    const map: Record<string, string> = {};
    for (const device of runnerDevices ?? []) {
      map[device.id] = device.name;
    }
    return map;
  }, [runnerDevices]);
  // The credits this run was charged, from the usage ledger. Undefined while loading and on a
  // deployment without billing, in which case the tile is simply absent — never a bare "0".
  const runCredits = useExecutionCredits(executionId);
  const assistant = useOptionalAssistant();
  // The activity model's live row for this run (stage and percent); the page keeps its own stream too.
  const runJob = useJobFor((j) => j.executionId === executionId);
  const agentById = useMemo(() => new Map((agents ?? []).map((a) => [a.id, a])), [agents]);

  const selectedStepResult = useMemo(() => {
    if (!selectedResultId || !execution) {
      return null;
    }
    return execution.stepResults.find((r) => r.id === selectedResultId) ?? null;
  }, [execution, selectedResultId]);

  // The run itself arrives as a summary (the heavy per-step fields are left out); the step the user
  // opened is read in full, and read again when that step finishes.
  const { data: fullStepData, error: fullStepError, mutate: retryFullStep } = useSWR<WorkflowStepResult>(
    selectedStepResult ? ['step-result', projectId, executionId, selectedStepResult.id, selectedStepResult.status ?? ''] : null,
    () => getStepResult(projectId, executionId, selectedResultId as string),
    { keepPreviousData: true },
  );
  const fullStepResult = fullStepData && fullStepData.id === selectedResultId ? fullStepData : null;

  // The steps this run executed: its own snapshot's when the server returned one, which the step
  // results reference; only an older run, without a snapshot, maps onto the workflow's current steps.
  const runSteps = useMemo(() => {
    const own = execution?.steps;
    return own && own.length > 0 ? own : workflow?.steps ?? [];
  }, [execution, workflow]);

  const streamEnabled = !!execution && !!workflow && !isTerminalExecutionStatus(execution.status);
  const {
    events,
    connectionState,
    lastError,
    metrics,
  } = useExecutionStream({
    projectId,
    workflowId,
    executionId,
    enabled: streamEnabled,
  });

  // Latest live stage/percent per step, from 'step.progress' events — these are ephemeral
  // (never persisted server-side, see WorkflowStepProgress's doc comment), so this map is the
  // ONLY source for them; a REST resync of `execution` can never carry this information.
  // `events` is newest-first (see useExecutionStream), so the first occurrence per stepId wins.
  const latestProgressByStepId = useMemo(() => {
    const map = new Map<string, StepProgressInfo>();
    for (const event of events) {
      if (event.type !== 'step.progress') continue;
      const stepId = getPayloadString(event.payload, 'stepId', 'StepId');
      if (!stepId || map.has(stepId)) continue;
      const stage = getPayloadString(event.payload, 'stage', 'Stage');
      const percent = getPayloadNumber(event.payload, 'percentComplete', 'PercentComplete');
      map.set(stepId, { stage, percent: percent > 0 ? percent : null });
    }
    return map;
  }, [events]);

  // Initialize flow visualization
  const initializeFlow = useCallback((
    wf: WorkflowDefinition,
    exec: WorkflowExecution,
    progressByStepId: Map<string, StepProgressInfo> = new Map(),
    showDetails = false,
  ) => {
    const newNodes: Node[] = [];
    const newEdges: Edge[] = [];

    wf.steps.forEach((step, index) => {
      const stepResult = [...exec.stepResults].reverse().find((r) => r.workflowStepId === step.id);
      const status = stepResult?.status || (exec.status === 'Passed' ? 'Skipped' : isTerminalExecutionStatus(exec.status) || exec.status === 'Stopped' ? 'Not run' : 'Pending');
      const statusWord = status === 'Completed' ? t('node.done') : status === 'Running' ? t('node.working') : status === 'Pending' ? t('node.waiting')
        : status === 'Failed' ? t('node.failed') : status === 'Skipped' ? t('node.skipped') : status === 'Not run' ? t('node.notRun') : status;
      const agent = agentById.get(step.agentDefinitionId);
      const role = agent && !agent.isBuiltIn && (step.stepType ?? 'Agent') === 'Agent' ? agent.name : stepRoleLabel(step.stepType, agent?.agentType);
      const stepName = step.label || role;
      const liveProgress = status === 'Running' ? progressByStepId.get(step.id) : undefined;

      const color =
        status === 'Completed' ? 'var(--rb-flow-done)'
          : status === 'Running' ? 'var(--rb-flow-running)'
            : status === 'Failed' ? 'var(--rb-flow-failed)'
              : status === 'Skipped' ? 'var(--rb-flow-skipped)'
                : 'var(--rb-flow-pending)';

      newNodes.push({
        id: step.id,
        type: 'default',
        position: { x: 250, y: index * 150 + 50 },
        data: {
          label: (
            <div style={{ padding: 10, width: '100%', cursor: 'var(--rb-cursor-pointer)' }}>
              <Group gap="xs" mb={6} justify="space-between">
                <Badge size="xs" variant="filled" style={{ background: color, color: 'var(--rb-flow-on-fill)' }}>
                  {statusWord}
                </Badge>
                <Badge size="xs" variant="light" color={STEP_TYPE_COLORS[step.stepType ?? 'Agent'] ?? 'gray'}>
                  {step.stepOrder}. {showDetails ? (STEP_TYPE_LABELS[step.stepType ?? 'Agent'] ?? step.stepType) : role}
                </Badge>
              </Group>
              <Tooltip label={stepName} multiline maw={260} openDelay={300}>
                <Text size="xs" fw={700} mb={6} lineClamp={2}>{stepName}</Text>
              </Tooltip>
              {status === 'Running' && (
                <Group gap={4} mb={6} wrap="nowrap">
                  <Loader size={10} />
                  <Text size="xs" lineClamp={1} style={{ wordBreak: 'break-word', color: 'var(--rb-flow-running)' }}>
                    {liveProgress?.stage || stepProgressPhrase(step.stepType, agent?.agentType, tr)}
                    {liveProgress?.percent != null ? ` (${Math.round(liveProgress.percent)}%)` : ''}
                  </Text>
                </Group>
              )}
              {stepResult && status !== 'Running' && (
                <Group gap={4} wrap="wrap">
                  <Badge size="xs" variant="dot" color="violet">
                    {stepResult.reusedFromStepResultId ? t('node.kept') : stepResult.fromCache ? t('node.reused') : formatDurationLong(Math.round(stepResult.durationMs))}
                  </Badge>
                  {showDetails && (
                    <Badge size="xs" variant="dot" color="violet">
                      {t('node.tokens', { count: stepResult.tokensUsed })}
                    </Badge>
                  )}
                  {(stepResult.creditsCharged ?? 0) > 0 && (
                    <Badge size="xs" variant="dot" color="yellow" data-testid="step-credits-charged">
                      {stepChargeLabel(stepResult.creditsCharged, tr, fmt.locale)}
                    </Badge>
                  )}
                  {showDetails && status === 'Failed' && stepResult.failureCode && (
                    <Badge size="xs" variant="dot" color="red" data-testid="step-failure-code">
                      {stepResult.failureCode}
                    </Badge>
                  )}
                  {showDetails && stepResult.iterationNumber != null && (
                    <Badge size="xs" variant="dot" color="gray">{t('node.iteration', { n: stepResult.iterationNumber })}</Badge>
                  )}
                </Group>
              )}
            </div>
          ),
          stepResult, // Store the step result in node data
        },
        style: {
          width: 280,
          border: `2px solid ${color}`,
          borderRadius: 'var(--mantine-radius-xs)',
          background:
            status === 'Running'
              ? 'light-dark(var(--mantine-color-violet-0), var(--mantine-color-dark-6))'
              : 'var(--rb-flow-surface)',
          color: 'var(--mantine-color-text)',
          boxShadow:
            stepResult && stepResult.id === selectedStepResult?.id
              ? `0 0 0 3px ${color}`
              : status === 'Running'
                ? `0 0 0 1px ${color}`
                : 'none',
          animation: status === 'Running' ? 'pulse 2s infinite' : 'none',
        },
      });

      if (index > 0) {
        newEdges.push({
          id: `e${wf.steps[index - 1].id}-${step.id}`,
          source: wf.steps[index - 1].id,
          target: step.id,
          type: 'smoothstep',
          animated: status === 'Running',
          markerEnd: {
            type: MarkerType.ArrowClosed,
            color,
          },
          style: {
            stroke: color,
            strokeWidth: 2,
          },
        });
      }
    });

    setNodes(newNodes);
    setEdges(newEdges);
  }, [setNodes, setEdges, selectedStepResult, agentById, t, tr, fmt.locale]);

  // Handle node click
  const onNodeClick: NodeMouseHandler = useCallback((_event, node) => {
    const stepResult = node.data.stepResult as WorkflowStepResult | undefined;
    if (stepResult) {
      setSelectedResultId(stepResult.id);
    }
  }, []);

  const selectedWorkflowStep = useMemo(() => {
    if (!workflow || !selectedStepResult) {
      return undefined;
    }
    return runSteps.find((step) => step.id === selectedStepResult.workflowStepId);
  }, [workflow, selectedStepResult, runSteps]);

  const selectedStepType = selectedWorkflowStep?.stepType;

  // Live SSE events for the selected step only (unfiltered `events` also carries other steps' and
  // execution-level events — see the full-feed Timeline below, which intentionally stays SSE-only).
  const liveSelectedStepEvents = useMemo(() => {
    if (!selectedStepResult) {
      return [];
    }

    return events.filter((event) => {
      const eventStepResultId = getPayloadString(event.payload, 'stepResultId', 'StepResultId');
      const eventStepId = getPayloadString(event.payload, 'stepId', 'StepId', 'workflowStepId', 'WorkflowStepId');

      return eventStepResultId === selectedStepResult.id || eventStepId === selectedStepResult.workflowStepId;
    });
  }, [events, selectedStepResult]);

  // Historical reasoning/tool-call/chat-turn events hydrated from the step result's persisted
  // `toolCallsJson`/`reasoningJson`/`chatTranscriptJson` fields (present once the step has
  // completed) — see `hydrateHistoricalStepEvents`'s own comment for why this only ever arrives as
  // one full batch, unlike the live trickle above.
  const historicalSelectedStepEvents = useMemo(() => {
    if (!selectedStepResult) {
      return [];
    }

    return hydrateHistoricalStepEvents(fullStepResult ?? selectedStepResult, {
      executionId,
      stepId: selectedStepResult.workflowStepId,
      stepResultId: selectedStepResult.id,
      stepOrder: selectedWorkflowStep?.stepOrder,
      stepLabel: selectedWorkflowStep?.label,
    });
  }, [fullStepResult, selectedStepResult, selectedWorkflowStep, executionId]);

  // Historical events seeded first (authoritative once persisted), live events merged in and
  // deduplicated against them — see `mergeStepEvents` for the exact per-event-type dedupe strategy.
  const selectedStepEvents = useMemo(
    () => mergeStepEvents(historicalSelectedStepEvents, liveSelectedStepEvents),
    [historicalSelectedStepEvents, liveSelectedStepEvents],
  );

  const selectedStepLiveTokenMetrics = useMemo(() => {
    const completedStepEvent = selectedStepEvents.find((event) => event.type === 'step.completed');
    if (!completedStepEvent) {
      return { inputTokens: 0, outputTokens: 0 };
    }

    return {
      inputTokens: getPayloadNumber(completedStepEvent.payload, 'inputTokens', 'InputTokens'),
      outputTokens: getPayloadNumber(completedStepEvent.payload, 'outputTokens', 'OutputTokens'),
    };
  }, [selectedStepEvents]);

  // Fetch initial data
  useEffect(() => {
    const fetchData = async () => {
      setLoadError(null);
      try {
        const [execData, workflowData] = await Promise.all([
          // note: getExecution no longer requires workflowId
          getExecution(projectId, executionId, { detail: 'summary' }),
          apiFetch<WorkflowDefinition>(`/api/v1/projects/${projectId}/workflows/${workflowId}`),
        ]);
        setExecution(execData);
        setWorkflow(workflowData);
      } catch (error) {
        console.error('Failed to fetch execution:', error);
        setLoadError(error);
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, [projectId, workflowId, executionId, loadAttempt]);

  // 'step.progress' is ephemeral (never persisted server-side — see WorkflowStepProgress's doc
  // comment) and fires far more often than a real DB change, so a REST resync only on OTHER event
  // types; progress ticks repaint the diagram locally via latestProgressByStepId below instead.
  const lastNonProgressEventAt = useMemo(
    () => events.find((event) => event.type !== 'step.progress')?.timestamp ?? null,
    [events],
  );

  // Refresh the execution snapshot after a burst of non-progress stream events: coalesced into one
  // read (1.5 s trailing), skipped while the tab is hidden (read once when it is visible again) and
  // skipped once the run is finished and loaded. Player events never come through here.
  const executionStatusRef = useRef<WorkflowExecution['status'] | null>(null);
  useEffect(() => {
    executionStatusRef.current = execution?.status ?? null;
  }, [execution]);
  // A steady stream of events must not postpone the refresh for ever: it runs at least every 5 s.
  const firstQueuedAtRef = useRef<number | null>(null);
  // Also resync once when the tab is visible again or the stream reconnected, in case a final event was missed.
  const resyncTick = useResyncTick(connectionState, !!execution && isTerminalExecutionStatus(execution.status));
  useEffect(() => {
    if (!workflow || (!lastNonProgressEventAt && resyncTick === 0)) {
      return;
    }

    const syncExecution = async () => {
      firstQueuedAtRef.current = null;
      try {
        const data = await getExecution(projectId, executionId, { detail: 'summary' });
        setExecution(data);
      } catch (error) {
        console.error('Failed to sync execution from event stream:', error);
      }
    };

    const now = Date.now();
    if (firstQueuedAtRef.current === null) firstQueuedAtRef.current = now;
    const wait = resyncDelay(now, firstQueuedAtRef.current);
    const timer = setTimeout(() => {
      const status = executionStatusRef.current;
      if (status && isTerminalExecutionStatus(status)) {
        return;
      }
      // Hidden: skip; returning to the tab bumps the resync counter, which comes back through here once.
      if (typeof document !== 'undefined' && document.hidden) {
        return;
      }
      void syncExecution();
    }, wait);
    return () => {
      clearTimeout(timer);
    };
  }, [lastNonProgressEventAt, resyncTick, projectId, executionId, workflow]);

  // Single reactive repaint of the flow diagram — runs on the initial load, every REST resync
  // above, AND every live progress tick (cheap: local state only, no network call).
  useEffect(() => {
    if (!workflow || !execution) {
      return;
    }
    initializeFlow({ ...workflow, steps: runSteps }, execution, latestProgressByStepId, detailsOpen);
  }, [workflow, runSteps, execution, latestProgressByStepId, initializeFlow, detailsOpen]);

  // Fetch the step's large artifact (e.g. a VideoAnalyze analysis JSON or VideoCompile EDL) whenever
  // the selected step result carries one. Separate from `output`/`outputJson` — this is the
  // "Edit decision list" style panel backed by `artifactStorageKey`.
  // Keyed on the stable id + artifact key rather than the whole `selectedStepResult` object: the
  // SSE sync effect above replaces `execution` (and thus this object's identity) on every event,
  // which would otherwise re-trigger this fetch of a potentially large artifact even when neither
  // the selected step nor its artifact had actually changed (found by Copilot review).
  const selectedStepResultId = selectedStepResult?.id ?? null;
  const selectedArtifactStorageKey = selectedStepResult?.artifactStorageKey;
  const {
    data: artifactData,
    error: artifactErrorObj,
    isLoading: artifactLoading,
  } = useStepResultArtifact(projectId, selectedArtifactStorageKey ? selectedStepResultId : null);
  const artifactJson = artifactData !== undefined ? JSON.stringify(artifactData) : null;
  const artifactError = artifactErrorObj
    ? describeError(tr, artifactErrorObj)
    : null;

  // Ticking clock for the live "elapsed" duration display below while the execution is still
  // running — Date.now() must not be read directly during render (impure), so this samples it on
  // an interval and render only reads the resulting state.
  const [now, setNow] = useState(() => Date.now());
  useEffect(() => {
    if (isTerminalExecutionStatus(execution?.status) || !execution?.startedAt) {
      return;
    }
    const id = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(id);
  }, [execution?.status, execution?.startedAt]);

  // Only a finished run asks for its video; the hook is called every render (rules of hooks).
  const { data: projectOutputs } = useOutputVideos(execution?.status === 'Passed' ? projectId : '');
  // A person's first finished video, opened here from the dashboard or a notification, gets the same
  // one-time moment as on the app page; a first run that failed gets the same kind words.
  const firstResult = useFirstResult({
    execution,
    executions: siblingExecutions,
    videoReady: execution?.status === 'Passed' && (projectOutputs ?? []).some((o) => o.workflowExecutionId === execution.id),
  });
  const router = useRouter();

  if (loading) {
    return (
      <LoadState
        loading
        skeleton={<Center h={400}><Loader size="lg" /></Center>}
        onRetry={() => setLoadAttempt((n) => n + 1)}
      />
    );
  }

  if (loadError || !execution || !workflow) {
    return (
      <FailureCard
        error={loadError ?? new ApiError('Not found', 404, [])}
        onRetry={() => {
          setLoading(true);
          setLoadAttempt((n) => n + 1);
        }}
        backHref={`/projects/${projectId}/workflows/${workflowId}`}
        backLabel={tr('projects.workflowPage.backToApp')}
      />
    );
  }

  const runFinished = isTerminalExecutionStatus(execution.status);
  const stepListItems: RunStepListItem[] = runSteps.map((step, index) => {
    const result = [...execution.stepResults].reverse().find((r) => r.workflowStepId === step.id);
    const agent = agentById.get(step.agentDefinitionId);
    const role = agent && !agent.isBuiltIn && (step.stepType ?? 'Agent') === 'Agent' ? agent.name : stepRoleLabel(step.stepType, agent?.agentType);
    const live = latestProgressByStepId.get(step.id);
    return {
      id: step.id,
      order: step.stepOrder ?? index + 1,
      name: step.label || role,
      state: stepStateFor(result?.status, execution.status),
      duration: result && result.status !== 'Running' && result.status !== 'Skipped' && result.durationMs > 0 ? formatDurationLong(Math.round(result.durationMs)) : null,
      live: result?.status === 'Running' ? `${live?.stage || stepProgressPhrase(step.stepType, agent?.agentType, tr)}${live?.percent != null ? ` (${Math.round(live.percent)}%)` : ''}` : null,
      resultId: result?.id ?? null,
    };
  });
  const failure = execution.status === 'Failed'
    ? runFailureText(execution.errorMessage, execution.stepResults.find((r) => r.status === 'Failed' && r.failureCode)?.failureCode, tr)
    : null;
  const myOutputs = execution.status === 'Passed' ? (projectOutputs ?? []).filter((o) => o.workflowExecutionId === execution.id) : [];
  // A run that made only pictures shows all of them as a gallery, with no final/draft distinction.
  const galleryPictures: GalleryPicture[] = myOutputs.length > 0 && myOutputs.every((o) => outputMediaKind(o.fileName) === 'image')
    ? myOutputs.filter((o) => !o.isIntermediate).map((o) => ({
        id: o.stepResultId,
        src: getOutputVideoUrl(projectId, o.stepResultId),
        thumbSrc: getOutputPreviewUrl(projectId, o.stepResultId, 480),
        largeSrc: getOutputPreviewUrl(projectId, o.stepResultId, 1920),
        downloadUrl: getOutputDownloadUrl(projectId, o.stepResultId, o.fileName),
        fileName: o.fileName,
      }))
    : [];
  const finishedOutput = galleryPictures.length > 0 ? null : myOutputs.find((o) => o.isFinal) ?? myOutputs[0] ?? null;

  const completedStepIds = new Set(execution.stepResults.filter((r) => r.status === 'Completed').map((r) => r.workflowStepId));
  const progress = runSteps.length > 0 ? (completedStepIds.size / runSteps.length) * 100 : 0;

  const duration = execution.startedAt && execution.completedAt
    ? new Date(execution.completedAt).getTime() - new Date(execution.startedAt).getTime()
    : execution.startedAt
      ? now - new Date(execution.startedAt).getTime()
      : 0;

  // Human name for the run ("Run 3 · 4 Oct, 14:02") instead of "Execution 9195db16".
  const runTitle = runName(runOrdinal(executionId, siblingExecutions), execution.startedAt, tr, fmt.locale);
  // Contract C1: warnings any step raised, shown before anything else — a "Finished" run can still
  // be missing most of its narration.
  const warnings = [
    ...(execution.designSystemWarnings ?? []).map((message) => ({ code: 'design_system', message })),
    ...collectRunWarnings(execution.stepResults),
  ];
  const attention = isNeedsAttention(execution);
  const designSystemPinLabel = executionPinLabel(
    execution.designSystemPin?.design?.versionNumber ?? null,
    execution.designSystemPin?.productMap?.versionNumber ?? null,
  );
  const timeLeftMs = estimateRemainingMs(runSteps, execution, now);

  // What is happening right now, per running step, in plain language.
  const latestByStep = new Map<string, WorkflowStepResult>();
  for (const r of execution.stepResults) latestByStep.set(r.workflowStepId, r);
  const runningNow = runSteps
    .filter((s) => latestByStep.get(s.id)?.status === 'Running')
    .map((s) => {
      const agent = agentById.get(s.agentDefinitionId);
      const live = latestProgressByStepId.get(s.id);
      return {
        id: s.id,
        name: s.label || (agent && !agent.isBuiltIn && (s.stepType ?? 'Agent') === 'Agent' ? agent.name : stepRoleLabel(s.stepType, agent?.agentType)),
        phrase: stepProgressPhrase(s.stepType, agent?.agentType, tr),
        detail: live?.stage ? `${live.stage}${live.percent != null ? ` (${Math.round(live.percent)}%)` : ''}` : null,
      };
    });

  // A refused Resume or Retry: no credits gets the shared "add credits" notification, a plan limit
  // the same red toast Execute shows, and anything else (the 409s included) a plain line on the card.
  const showRunRefusal = (err: unknown, fallback: string) => {
    if (notifyInsufficientCredits(err)) return;
    const footage = sourceRefusalMessage(err, tr);
    if (footage) {
      setRetryError(footage);
      return;
    }
    if (err instanceof ApiError && err.status === 402) {
      // eslint-disable-next-line no-restricted-syntax -- a 402 plan refusal carries the server's own plain-language sentence, shown on purpose
      notifications.show({ title: t('errorTitle'), message: err.message || fallback, color: 'red' });
      return;
    }
    // The server words a lost race accurately (queued again, workflow deleted, ...): show it as sent.
    const serverWorded: Record<string, string> = {
      RUN_NOT_RESUMABLE: t('refusal.notResumable'),
      RUN_CHANGED: t('refusal.changed'),
    };
    if (err instanceof ApiError && err.code && err.code in serverWorded) {
      setRetryError(err.message.trim() || serverWorded[err.code]);
      return;
    }
    const plain: Record<string, string> = {
      RESUME_UNAVAILABLE: t('refusal.notResumable'),
      RUN_STILL_STOPPING: t('refusal.stillStopping'),
      RETRY_UNAVAILABLE: t('refusal.retryUnavailable'),
    };
    setRetryError((err instanceof ApiError && err.code && plain[err.code]) || describeError(tr, err, fallback));
  };

  const doRetry = async (regenerateVideoClips: boolean, acceptEstimateCredits?: number) => {
    setRetryError(null);
    try {
      await retryExecution(projectId, workflowId, executionId, regenerateVideoClips, acceptEstimateCredits);
      // Re-read the run: the response of the action does not carry the run's own steps.
      setExecution(await getExecution(projectId, executionId, { detail: 'summary' }));
      void revalidateSiblings();
    } catch (err) {
      // The profile-build workflow asks for the estimate to be accepted first; Confirm resends the same retry.
      if (estimateGate.intercept(err, (accepted) => doRetry(regenerateVideoClips, accepted))) return;
      console.error('retry failed', err);
      showRunRefusal(err, t('retryFailed'));
    }
  };

  const doResume = async (acceptEstimateCredits?: number) => {
    setRetryError(null);
    setResuming(true);
    try {
      await resumeExecution(projectId, workflowId, executionId, acceptEstimateCredits);
      // Re-read the run (the action's response does not carry its own steps). It is live again, so its
      // status leaves the terminal state, which turns the stream back on.
      setExecution(await getExecution(projectId, executionId, { detail: 'summary' }));
      void revalidateSiblings();
    } catch (err) {
      // The profile-build workflow asks for the estimate to be accepted first; Confirm resends the same resume.
      if (estimateGate.intercept(err, (accepted) => doResume(accepted))) return;
      console.error('resume failed', err);
      showRunRefusal(err, t('resumeFailed'));
    } finally {
      setResuming(false);
    }
  };

  const connectionColor =
    connectionState === 'connected' ? 'green'
      : connectionState === 'reconnecting' ? 'yellow'
        : connectionState === 'connecting' ? 'blue'
          : 'gray';

  const feed = detailsOpen ? events : events.filter((event) => !isTechnicalEvent(event));

  const selectedChangeCapability = selectedStepResult
    ? changeCaps?.steps.find((c) => c.stepResultId === selectedStepResult.id)
    : undefined;
  const requestChanges = (cap: StepChangeStepCapability) => {
    if (cap.route === 'composition-revision') {
      // The video is changed from the "Change this video" panel further down: go to its note field.
      const el = filmTextareaRef.current;
      el?.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
      el?.focus({ preventScroll: true });
      setSelectedResultId(null);
      return;
    }
    setChangeTarget(cap);
  };

  /** "Change something" of the first-video moment: this page's own way to change the result, whichever this run has. */
  const changeSomething = () => {
    const film = filmTextareaRef.current;
    if (film) {
      film.scrollIntoView?.({ behavior: 'smooth', block: 'start' });
      film.focus({ preventScroll: true });
    } else if (canChangeFields(changeCaps?.appInputs)) {
      setFieldsOpen(true);
    } else {
      // Nothing to change in place: the app's own page is where its choices are changed and it is run again.
      router.push(`/projects/${projectId}/workflows/${workflowId}`);
    }
  };

  const editEntry = (entry: ChangeEntry) => {
    const cap = changeCaps?.steps.find((c) => c.stepOrder === entry.stepOrder);
    if (cap) setChangeTarget(cap);
  };
  const addEntry = (entry: ChangeEntry) => {
    changeSet.upsert(entry);
    setSelectedResultId(null);
  };
  const existingEntry = (stepOrder: number) => changeSet.entries.find((e) => e.stepOrder === stepOrder) ?? null;

  const stepPanel = selectedStepResult && (!fullStepResult && fullStepError ? (
    <Alert color="red" variant="light" data-testid="step-result-error" title={t('stepOpenFailedTitle')}>
      <Group justify="space-between" gap="sm">
        <Text size="sm">{t('stepOpenFailedBody')}</Text>
        <Button size="xs" variant="light" onClick={() => void retryFullStep()}>{t('tryAgain')}</Button>
      </Group>
    </Alert>
  ) : !fullStepResult ? (
    <Center py="xl" data-testid="step-result-loading">
      <Loader size="sm" />
    </Center>
  ) : (
    <StepResultPanel
      projectId={projectId}
      stepResult={fullStepResult ?? selectedStepResult}
      stepType={selectedStepType}
      stepEvents={selectedStepEvents}
      liveTokenMetrics={selectedStepLiveTokenMetrics}
      artifactJson={artifactJson}
      artifactError={artifactError}
      artifactLoading={artifactLoading}
      showTechnical={detailsOpen}
      runnerNames={runnerNames}
      changeCapability={selectedChangeCapability}
      onRequestChanges={requestChanges}
    />
  ));

  return (
    <>
      {estimateGate.modal}
      <RegenerateVideoClipsModal
        opened={regenModalOpened}
        onClose={() => setRegenModalOpened(false)}
        capCredits={getVideoGenerateInfo(runSteps).capCredits}
        actionLabel={t('retry')}
        onChoose={(regenerate) => {
          setRegenModalOpened(false);
          void doRetry(regenerate);
        }}
      />
      <Modal
        opened={retryConfirmOpened}
        onClose={() => setRetryConfirmOpened(false)}
        title={t('runAgainTitle')}
        centered
      >
        <Stack gap="md">
          <Text size="sm">
            {t('runAgainBody')}
          </Text>
          <Group justify="flex-end" gap="xs">
            <Button variant="default" onClick={() => setRetryConfirmOpened(false)}>{t('cancel')}</Button>
            <Button
              onClick={() => {
                setRetryConfirmOpened(false);
                if (getVideoGenerateInfo(runSteps).has) {
                  setRegenModalOpened(true);
                } else {
                  void doRetry(false);
                }
              }}
            >
              {t('runEveryStep')}
            </Button>
          </Group>
        </Stack>
      </Modal>
      <PageHeader
        title={runTitle}
        breadcrumbs={[
          { label: t('breadcrumbProjects'), href: '/projects' },
          { label: project?.name ?? t('projectFallback'), href: `/projects/${projectId}` },
          { label: workflow.name, href: `/projects/${projectId}/workflows/${workflowId}` },
          { label: runTitle },
        ]}
      />

      <Stack gap="lg">
        {attention && execution.needsAttention && execution.status !== 'Failed' && (
          <Alert color="yellow" variant="filled" autoContrast icon={<IconAlertCircle size={16} />} title={t('needsAttention')} data-testid="needs-attention-alert">
            {execution.needsAttention.message}
          </Alert>
        )}
        {warnings.length > 0 && execution.status !== 'Failed' && <RunWarningsAlert warnings={warnings} />}

        {galleryPictures.length > 0 && (
          <Card withBorder shadow="sm" radius="lg" padding="lg" data-testid="run-result-gallery">
            <PictureGallery pictures={galleryPictures} />
          </Card>
        )}
        {finishedOutput && firstResult.active && (
          <FirstResultMoment
            projectId={projectId}
            executionId={execution.id}
            output={finishedOutput}
            // The player is the card right below.
            showVideo={false}
            onChange={changeSomething}
            third="recipes"
            onDismiss={firstResult.dismiss}
          />
        )}
        {finishedOutput && (
          <Card withBorder shadow="sm" radius="lg" padding="lg" data-testid="run-result-player">
            <Stack gap="sm">
              <CheckedOutputMedia
                controls
                preload="metadata"
                playsInline
                src={getOutputVideoUrl(projectId, finishedOutput.stepResultId)}
                fileName={finishedOutput.fileName}
                alt={finishedOutput.fileName}
                aria-label={t('finishedVideo')}
                style={{ width: '100%', maxHeight: 480, background: '#000', borderRadius: 'var(--mantine-radius-md)' }}
              />
              <Group>
                <Button
                  component="a"
                  href={getOutputDownloadUrl(projectId, finishedOutput.stepResultId, finishedOutput.fileName)}
                  onClick={guardDownload(getOutputDownloadUrl(projectId, finishedOutput.stepResultId, finishedOutput.fileName), finishedOutput.fileName)}
                  download
                  leftSection={<IconDownload size={16} />}
                  style={{ minHeight: 44 }}
                >
                  {t('download')}
                </Button>
                {editorOn && (
                  <Button
                    component={Link}
                    href={`/projects/${projectId}/editor/${finishedOutput.stepResultId}`}
                    variant="default"
                    leftSection={<IconPencil size={16} />}
                    style={{ minHeight: 44 }}
                  >
                    {t('editInEditor')}
                  </Button>
                )}
                <PostTextButton projectId={projectId} outputId={finishedOutput.stepResultId} />
                <PublishButton projectId={projectId} outputId={finishedOutput.stepResultId} outputKind="step_result" />
              </Group>
            </Stack>
          </Card>
        )}

        {/* Status card. Deliberately no entrance animation: it used to fade in from opacity 0 via
            framer-motion, and wherever animation frames are throttled (a background tab, a hidden
            preview pane, reduced-motion setups) the card stayed stuck part-way, rendering dimmed. */}
        <Card withBorder shadow="sm" radius="lg" padding="lg" data-testid="run-summary">
          <Stack gap="md">
            <Group justify="space-between" gap="sm">
              <Group gap="sm">
                {attention ? (
                  <Badge size="lg" color="yellow" variant="filled" autoContrast leftSection={<IconAlertCircle size={14} />}>
                    {t('needsAttention')}
                  </Badge>
                ) : (
                  <JobStatusBadge state={RUN_JOB_STATE[execution.status] ?? 'waiting'} />
                )}
                {execution.status === 'Running' && <Loader size="sm" color="blue" />}
                {designSystemPinLabel && (
                  <Badge variant="light" data-testid="design-system-pin">
                    {t.msg(designSystemPinLabel)}
                  </Badge>
                )}
                {/* What the run was started with beyond its brief, when anything was chosen. */}
                {execution.styleName && (
                  <Badge size="md" variant="light" data-testid="run-style">
                    {t('style', { name: execution.styleName })}
                  </Badge>
                )}
                {execution.outputFormat && (
                  <Badge size="md" variant="outline" data-testid="run-format">
                    {t('shape', { name: RUN_FORMAT_OPTIONS.find((o) => o.value === execution.outputFormat)?.label ?? execution.outputFormat })}
                  </Badge>
                )}
                {execution.outputResolution && (
                  <Badge size="md" variant="outline" color="grape" data-testid="run-resolution">
                    {t('resolution', { name: resolutionLabel(execution.outputResolution) })}
                  </Badge>
                )}
              </Group>

              <Group gap="xs">
                {execution.status === 'Running' && (
                  <Button
                    size="xs"
                    variant="outline"
                    color="red"
                    leftSection={<IconPlayerStop size={14} />}
                    onClick={async () => {
                      try {
                        await stopExecution(projectId, workflowId, executionId);
                        // refresh execution data immediately using root endpoint; the reactive
                        // repaint effect picks this up automatically
                        const data = await getExecution(projectId, executionId, { detail: 'summary' });
                        setExecution(data);
                      } catch (err) {
                        console.error('stop failed', err);
                      }
                    }}
                  >
                    {t('stop')}
                  </Button>
                )}
                {execution.canResume && (
                  <Tooltip label={t('resumeTooltip')} multiline maw={260}>
                    <Button
                      size="xs"
                      leftSection={<IconPlayerPlay size={14} />}
                      loading={resuming}
                      onClick={() => void doResume()}
                    >
                      {t('resume')}
                    </Button>
                  </Tooltip>
                )}
                {(execution.status === 'Failed' || execution.status === 'Cancelled' || execution.status === 'Passed' || execution.status === 'Stopped') && (
                  <Button
                    size="xs"
                    variant={execution.canResume ? 'default' : 'outline'}
                    color={execution.canResume ? undefined : 'green'}
                    leftSection={<IconRefresh size={14} />}
                    onClick={() => setRetryConfirmOpened(true)}
                  >
                    {t('runAgain')}
                  </Button>
                )}
              </Group>
            </Group>

            {runFinished && execution.status !== 'Passed' && (
              <Stack gap={2} data-testid="run-recovery-help">
                {execution.canResume && (
                  <Text size="sm" c="dimmed">{t.rich('recoveryResume', { b: (c) => <b>{c}</b> })}</Text>
                )}
                <Text size="sm" c="dimmed">{t.rich('recoveryRunAgain', { b: (c) => <b>{c}</b> })}</Text>
              </Stack>
            )}

            {retryError && (
              <Alert color="yellow" withCloseButton onClose={() => setRetryError(null)}>
                {retryError}
              </Alert>
            )}

            {execution.status === 'Running' && (
              <Stack gap={4} aria-live="polite">
                {runningNow.length === 0 ? (
                  <Text size="sm">{t('gettingNextReady')}</Text>
                ) : (
                  runningNow.map((r) => (
                    <Text size="sm" key={r.id}>
                      <Text span fw={600}>{r.phrase}</Text>
                      <Text span c="dimmed"> — {r.name}{r.detail ? ` · ${r.detail}` : ''}</Text>
                    </Text>
                  ))
                )}
                {runJob && <JobProgress job={runJob} />}
              </Stack>
            )}
            {execution.status === 'Queued' && <Text size="sm">{t('queuedWaiting')}</Text>}
            {execution.status === 'Failed' && (execution.errorMessage || failure) && (
              <Alert icon={<IconAlertCircle size={14} />} color="red" variant="light" title={t('whatWentWrong')} data-testid="run-failure">
                {firstResult.failedFirst && <FirstRunFailedLead />}
                <Text size="sm" style={{ whiteSpace: 'pre-wrap' }}>{failure?.summary}</Text>
                {failedRunPlanRefusal(execution) && (
                  <div style={{ marginTop: 8 }}><UpgradeCallout feature={failedRunPlanRefusal(execution)?.feature} testId="run-failure-upgrade" /></div>
                )}
                {/* The credits tile's own rule: a figure means credits apply on this deployment. */}
                {firstResult.failedFirst && <FirstRunFailedNext execution={execution} billingEnabled={runCredits !== undefined} />}
                <FailedRunCredits execution={execution} billingEnabled={runCredits !== undefined} />
                <InsertFailureNext
                  code={execution.stepResults.find((r) => r.status === 'Failed' && r.failureCode)?.failureCode}
                  onRunAgain={() => setRetryConfirmOpened(true)}
                  onAskAssistant={assistant ? (prompt) => assistant.openWithPrompt(prompt, { projectId, workflowId, executionId }) : undefined}
                />
                {failure?.details && (
                  <>
                    <Button
                      variant="subtle"
                      size="compact-sm"
                      mt={4}
                      aria-expanded={failureDetailsOpen}
                      onClick={() => setFailureDetailsOpen((o) => !o)}
                      rightSection={failureDetailsOpen ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
                      style={{ minHeight: 44 }}
                    >
                      {t('details')}
                    </Button>
                    <Collapse expanded={failureDetailsOpen}>
                      <Text size="xs" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere' }} data-testid="run-failure-details">{failure.details}</Text>
                    </Collapse>
                  </>
                )}
                <ReportProblem run={execution} executionId={execution.id} showOnUs={!firstResult.failedFirst} />
              </Alert>
            )}

            <Progress
              value={progress}
              size="lg"
              radius="xl"
              animated={execution.status === 'Running'}
              color={RUN_STATUS_COLOR[execution.status] === 'gray' ? 'blue' : RUN_STATUS_COLOR[execution.status]}
              aria-label={t('runProgress')}
            />

            <SimpleGrid cols={{ base: 2, sm: 4 }} spacing="sm" data-testid="run-stats">
              <StatTile label={t('stepsDone')}>{completedStepIds.size} / {runSteps.length}</StatTile>
              <StatTile label={t('started')}>{formatRunDate(execution.startedAt, fmt.locale) ?? t('notYet')}</StatTile>
              <StatTile label={execution.completedAt ? t('took') : t('elapsed')}>{duration > 0 ? formatDurationLong(duration) : '—'}</StatTile>
              {runCredits !== undefined && (
                <StatTile label={t('credits')}>{formatCredits(runCredits, fmt.locale)}</StatTile>
              )}
              {!runFinished && (
                <StatTile label={t('timeLeft')}>
                  {execution.status === 'Running'
                    ? (timeLeftMs != null ? <Tooltip label={t('timeLeftTooltip')}><span>{t('timeLeftEstimate', { time: formatTimeLeft(timeLeftMs, tr) })}</span></Tooltip> : t('estimating'))
                    : '—'}
                </StatTile>
              )}
            </SimpleGrid>

            {/* An app run's own fields (docs/design/app-inputs.md 10.4), labelled by the run's page of the time. */}
            {execution.appInputs && execution.appInputs.length > 0 && <RunAppInputsList inputs={execution.appInputs} />}
            {execution.status === 'Passed' && canChangeFields(changeCaps?.appInputs) && (
              <div>
                <Button variant="light" size="compact-sm" onClick={() => setFieldsOpen(true)} data-testid="execution-change-fields">
                  {tr('apps.run.change.openFields')}
                </Button>
              </div>
            )}

            <div>
              <Button
                variant="subtle"
                size="compact-sm"
                onClick={toggleDetails}
                rightSection={detailsOpen ? <IconChevronUp size={14} /> : <IconChevronDown size={14} />}
                aria-expanded={detailsOpen}
              >
                {detailsOpen ? t('hideDetails') : t('details')}
              </Button>
              {detailsOpen && (
                <Stack gap="sm" mt="sm" data-testid="run-details">
                  <Group gap="xs" wrap="wrap">
                    <Badge size="sm" variant="light" color={connectionColor} leftSection={<IconActivity size={12} />}>
                      {t('stream', { state: connectionState })}
                    </Badge>
                    <Badge size="sm" variant="light" color="indigo">{t('avgStep', { ms: metrics.averageDurationMs })}</Badge>
                    <Badge size="sm" variant="light" color="cyan">{t('iterations', { n: execution.iterationCount })}</Badge>
                    <Badge size="sm" variant="light" color="gray">{t('events', { count: metrics.totalEvents })}</Badge>
                  </Group>
                  <Group gap={6} wrap="wrap">
                    <IconBolt size={14} />
                    <Text size="sm">
                      {t('tokens', { input: fmt.number(metrics.totalInputTokens), output: fmt.number(metrics.totalOutputTokens), total: fmt.number(metrics.totalTokens) })}
                    </Text>
                  </Group>
                  <Text size="xs" c="dimmed">{t.rich('executionId', { id: executionId, code: (c) => <Code>{c}</Code> })}</Text>
                  {lastError && (
                    <Alert icon={<IconAlertCircle size={14} />} color="yellow" variant="light">
                      {lastError}
                    </Alert>
                  )}
                </Stack>
              )}
            </div>
          </Stack>
        </Card>

        <SimpleGrid cols={{ base: 1, xl: 2 }} spacing="lg">
          <Card withBorder shadow="sm" radius="lg" padding="lg">
            <Group justify="space-between" mb="md" gap="sm">
              <Text fw={600}>{t('steps')}</Text>
              <SegmentedControl
                size="sm"
                value={stepsView}
                onChange={(v) => setStepsView(v === 'diagram' ? 'diagram' : 'list')}
                data={[{ label: t('viewList'), value: 'list' }, { label: t('viewDiagram'), value: 'diagram' }]}
                aria-label={t('stepsView')}
              />
            </Group>
            {stepsView === 'list' ? (
              <RunStepList items={stepListItems} selectedResultId={selectedStepResult?.id ?? null} onSelect={setSelectedResultId} />
            ) : (
            <div style={{ height: 500, border: '1px solid light-dark(var(--mantine-color-gray-4), var(--mantine-color-dark-4))', borderRadius: 'var(--mantine-radius-xs)', overflow: 'hidden' }}>
              <ReactFlow
                nodes={nodes}
                edges={edges}
                onNodesChange={onNodesChange}
                onEdgesChange={onEdgesChange}
                onNodeClick={onNodeClick}
                fitView
                nodesDraggable={false}
                nodesConnectable={false}
                elementsSelectable={true}
              >
                <Background variant={BackgroundVariant.Dots} gap={16} size={1} />
                <Controls showInteractive={false} />
              </ReactFlow>
            </div>
            )}
          </Card>

          {/* Below `sm` this panel renders as a bottom Drawer instead (see below), so a
              selected step's detail doesn't scroll off-screen once the grid stacks. */}
          {isDesktop && (
            <Card withBorder shadow="sm" radius="lg" padding="lg">
              <Text fw={600} mb="md">{t('stepDetails')}</Text>
              {!selectedStepResult ? (
                <Alert icon={<IconPlayerPlay size={14} />} color="blue" variant="light">
                  {t('selectStep')}
                </Alert>
              ) : stepPanel}
            </Card>
          )}
        </SimpleGrid>

        {execution && changeCaps?.appInputs && (
          <AppInputChangeModal
            opened={fieldsOpen}
            onClose={() => setFieldsOpen(false)}
            projectId={projectId}
            workflowId={workflowId}
            executionId={execution.id}
            appInputs={changeCaps.appInputs}
            phone={!isDesktop}
            onAddToSet={changeSet.setFields}
            initial={changeSet.fields}
          />
        )}

        {!isDesktop && (
          <Drawer
            opened={selectedStepResult !== null}
            onClose={() => setSelectedResultId(null)}
            position="bottom"
            size="85%"
            title={<Text fw={700} size="lg">{t('stepDetails')}</Text>}
          >
            {stepPanel}
          </Drawer>
        )}

        {/* "Change this video": the video itself, the other changes waiting, one estimate and the history.
            Renders nothing for a run with nothing to change. */}
        <ChangeVideoPanel
          projectId={projectId}
          executionId={execution.id}
          runStatus={execution.status}
          runFinished={isTerminalExecutionStatus(execution.status)}
          changeSet={changeSet}
          capabilities={changeCaps}
          requests={changeList?.requests ?? []}
          onEditEntry={editEntry}
          onEditFields={() => setFieldsOpen(true)}
          onCreated={() => { changeSet.clear(); void mutateChangeList(); }}
          textareaRef={filmTextareaRef}
        />
        {execution && changeTarget && (
          <RequestStepChangeModal
            projectId={projectId}
            executionId={execution.id}
            key={changeTarget.stepOrder}
            capability={changeTarget}
            opened
            onClose={() => setChangeTarget(null)}
            onCreated={() => { changeSet.clear(); void mutateChangeList(); }}
            onAdd={addEntry}
            existing={existingEntry(changeTarget.stepOrder)}
            setSize={changeSet.entries.length + (changeSet.film.instruction.trim() ? 1 : 0)}
            maxTargets={maxTargets}
          />
        )}
        <Card withBorder shadow="sm" radius="lg" padding="lg">
          <Text fw={600} mb="md">{t('activity')}</Text>
          {feed.length === 0 ? (
            <Text size="sm" c="dimmed">
              {isTerminalExecutionStatus(execution.status) ? t('activityOpenStep') : t('activityUpdates')}
            </Text>
          ) : (
            <Timeline active={feed.length - 1} bulletSize={24} lineWidth={2}>
              {feed.map((event, i) => (
                <Timeline.Item key={i} bullet={getEventIcon(event.type)}>
                  <ExecutionEventCard event={event} technical={detailsOpen} />
                </Timeline.Item>
              ))}
            </Timeline>
          )}
        </Card>
      </Stack>

      <style jsx global>{`
        @keyframes pulse {
          0%, 100% {
            box-shadow: 0 0 0 0 var(--mantine-color-blue-6);
          }
          50% {
            box-shadow: 0 0 0 10px transparent;
          }
        }
      `}</style>
    </>
  );
}

export default function ExecutionDetailPage(props: { params: Promise<{ id: string; workflowId: string; executionId: string }> }) {
  return (
    <ReactFlowProvider>
      <ExecutionDetailPageInner {...props} />
    </ReactFlowProvider>
  );
}
