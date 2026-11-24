'use client';
/* eslint-disable i18next/no-literal-string -- the remaining literals are ids, enum keys and t.rich tag handlers, not user-visible text */

import { ShareAsRecipeButton } from '@/components/marketplace/ShareAsRecipeButton';
import { plainErrorMessage } from '@/lib/api/network-error';
import { sourceRefusalMessage } from '@/lib/api/source-limits';
import { use, useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { TextInput, Button, Stack, Loader, Center, Text, Switch, Indicator, VisuallyHidden, Group, Menu, ActionIcon } from '@mantine/core';
import { IconDotsVertical, IconPlayerPlay, IconTrash, IconMessageChatbot } from '@tabler/icons-react';
import { useForm } from '@mantine/form';
import { notifications } from '@mantine/notifications';
import { useWorkflow } from '@/lib/hooks/use-workflows';
import { useProject } from '@/lib/hooks/use-projects';
import { useExecutions } from '@/lib/hooks/use-executions';
import { useAgents } from '@/lib/hooks/use-agents';
import { useAuth } from '@/lib/hooks/use-auth';
import { emitFunnelEvent, userKeyFromEmail } from '@/lib/telemetry';
import { useDocumentTitle, useMediaQuery } from '@mantine/hooks';
import { RunWithInputsModal } from '@/components/workflow-apps/RunWithInputsModal';
import { withoutPermission } from '@/components/workflow-apps/app-input-model';
import { estimateRunDuration } from '@/lib/utils/run-estimate';
import { updateWorkflow, deleteWorkflow, executeWorkflow, workflowAppPath } from '@/lib/api/workflows';
import { PageHeader } from '@/components/shared/PageHeader';
import { FlowchartBuilderWrapper } from '@/components/workflows/FlowchartBuilder';
import { ExecutionHistory } from '@/components/workflows/ExecutionHistory';
import { ConfirmModal } from '@/components/shared/ConfirmModal';
import { RegenerateVideoClipsModal, getVideoGenerateInfo } from '@/components/workflows/RegenerateVideoClipsModal';
import { InsufficientCreditsAlert } from '@/components/billing/InsufficientCreditsAlert';
import { useWorkflowCostEstimate } from '@/lib/hooks/use-billing';
import { insufficientCredits } from '@/lib/api/billing';
import { useEstimateGate } from '@/components/design-system/EstimateGateModal';
import { creditEstimateHeadline, creditRange, formatCreditAmount } from '@/lib/utils/billing';
import type { InsufficientCredits } from '@/lib/types/billing';
import type { StepData } from '@/components/workflows/WorkflowStepList';
import type { WorkflowDefinition, CreateWorkflowStepRequest } from '@/lib/types/workflow';
import { findInvalidVideoGenerateStep } from '@/lib/utils/video-generate-validation';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import { LoadState } from '@/components/shared/LoadState';
import { useSWRConfig, type KeyedMutator } from 'swr';
import { useAssistant } from '@/components/assistant/AssistantShellProvider';
import { AssistantPageContext, useAssistantPageContext } from '@/components/assistant/AssistantPageContext';
import { WorkflowDiffPreview } from '@/components/assistant/WorkflowDiffPreview';
import { applyWorkflow } from '@/lib/api/workflow-assistant';
import { onAssistantActionDone } from '@/lib/assistant/events';
import { ApiError } from '@/lib/api/client';
import type { WorkflowAssistantApplyResponse, WorkflowEditDiff } from '@/lib/types/workflow-assistant';
import { parseStepErrors } from '@/lib/utils/step-errors';
import { AppViewToggle, useWorkflowView } from '@/components/workflow-apps/AppViewToggle';
import { AppInputsPanel } from '@/components/workflow-apps/AppInputsPanel';
import { AppBuilderContext, type AppBuilderValue } from '@/components/workflow-apps/AppBuilderContext';
import { manifestForSave } from '@/lib/workflow-apps/app-input-builder';
import { useWorkflowApp } from '@/lib/hooks/use-workflow-apps';
import { manifestErrorsOf } from '@/lib/workflow-apps/app-inputs';
import type { AppManifest, RunBrief } from '@/lib/types/workflow';
import restyle from '@/components/workflows/graph-restyle.module.css';
import { AppRunPage } from '@/components/workflow-apps/AppRunPage';
import { useWorkflowAppsEnabled } from '@/components/shell/WorkflowAppsFlag';
import { mapWorkflowStepsToStepData, describeStepsForRun, prependExecution, resolveApplyTarget, stepDataToCreateRequest, sameId } from '@/components/workflows/workflowPageHelpers';
import { useFormat, useT } from '@/i18n';
import { FailureCard } from '@/components/shared/FailureCard';
import styles from './page.module.css';

function buildApplyStepsPayload(pendingDiff: WorkflowEditDiff): CreateWorkflowStepRequest[] {
  return pendingDiff.proposedSteps ?? [];
}

/** The builder's draft as one comparable value: what "unsaved changes" is decided from. */
function draftSignature(name: string, requiresUserInput: boolean, steps: StepData[]): string {
  return JSON.stringify({ name, requiresUserInput, steps });
}

/** The edit form's id, so the header Save button can submit a form it no longer wraps. */
const WORKFLOW_FORM_ID = 'workflow-edit-form';

/** SWR keys of a project's workflows: its list and every one of them, whichever the page is on. */
function workflowKeysOf(projectId: string): (key: unknown) => boolean {
  const prefix = `/api/v1/projects/${projectId}/workflows`;
  return (key) => typeof key === 'string' && (key === prefix || key.startsWith(`${prefix}/`) || key.startsWith(`${prefix}?`));
}

/** A workflow's name, brief setting and steps as one comparable value. */
function workflowSignature(workflow: WorkflowDefinition): string {
  return JSON.stringify([
    workflow.name,
    workflow.requiresUserInput ?? false,
    [...workflow.steps].sort((a, b) => a.stepOrder - b.stepOrder),
  ]);
}

/**
 * Remembers the server's workflow as the page last put it on screen, so a refetch is only taken in
 * when something OTHER than this page changed it. Without that, every refetch would replace the
 * canvas — and with it whatever was being typed that the server has never seen.
 */
function useAdoptedWorkflow(initial: WorkflowDefinition) {
  const last = useRef({
    signature: workflowSignature(initial),
    name: initial.name,
    requiresUserInput: initial.requiresUserInput ?? false,
  });
  return useMemo(
    () => ({
      /** Records `workflow` as what the page now shows. */
      remember(workflow: WorkflowDefinition) {
        last.current = {
          signature: workflowSignature(workflow),
          name: workflow.name,
          requiresUserInput: workflow.requiresUserInput ?? false,
        };
      },
      /** How `workflow` differs from what the page last showed. */
      compare(workflow: WorkflowDefinition) {
        return {
          differs: workflowSignature(workflow) !== last.current.signature,
          nameMoved: workflow.name !== last.current.name,
          switchMoved: (workflow.requiresUserInput ?? false) !== last.current.requiresUserInput,
        };
      },
    }),
    [],
  );
}

function WorkflowEditPage({ params }: { params: Promise<{ id: string; workflowId: string }> }) {
  const { id: projectId, workflowId } = use(params);
  const t = useT('projects.workflowPage');
  const { data: workflow, isLoading, error: workflowError, mutate } = useWorkflow(projectId, workflowId);
  const { data: project } = useProject(projectId);
  // The route's fallback title is metadata (this segment's layout.tsx, "Workflow"); the names
  // come from a client-side fetch, so they are added here once it resolves.
  useDocumentTitle(workflow ? t('documentTitle', { name: workflow.name, project: project?.name ?? t('projectFallback') }) : t('documentTitleFallback'));

  if (isLoading) {
    return <LoadState loading skeleton={<Center h={300}><Loader /></Center>} onRetry={() => void mutate()} />;
  }
  if (workflowError || !workflow) {
    return (
      <FailureCard
        error={workflowError ?? new ApiError('Not found', 404, [])}
        onRetry={() => void mutate()}
        backHref="/dashboard"
      />
    );
  }

  // Keyed on the workflow id so switching to a different workflow remounts the form fresh — its
  // `steps`/form state below is seeded once from `workflow` at mount, no sync effect needed.
  return (
    <WorkflowEditForm
      key={workflow.id}
      projectId={projectId}
      workflowId={workflowId}
      workflow={workflow}
      mutate={mutate}
      projectName={project?.name}
    />
  );
}

/**
 * The workflow route. With the app view on, the page is the app run page and the graph builder sits
 * behind `?view=advanced` (so every existing link keeps working); with it off, it is the builder
 * exactly as it was.
 */
export default function WorkflowRoute({ params }: { params: Promise<{ id: string; workflowId: string }> }) {
  const { id: projectId, workflowId } = use(params);
  const t = useT('projects.workflowPage');
  const appsEnabled = useWorkflowAppsEnabled();
  const { user } = useAuth();
  const isDesktop = useMediaQuery('(min-width: 48em)', true);
  // URL first (`?view=`), then this user's last choice, then the app (spec 4.3, 4.6).
  const [view, setView] = useWorkflowView(userKeyFromEmail(user?.email), useSearchParams()?.toString() ?? '');
  if (!appsEnabled) return <WorkflowEditPage params={params} />;
  return (
    <>
      {isDesktop ? (
        <Group justify="space-between" mb="sm" data-testid="workflow-view-bar">
          <AppViewToggle view={view} onChange={setView} />
          {view === 'advanced' && (
            <Link href={`/projects/${projectId}/workflows/${workflowId}`} onClick={() => setView('app')}>{t('backToApp')}</Link>
          )}
        </Group>
      ) : (
        // Phone: the control is hidden; the app page's overflow holds "Edit steps (advanced)".
        <Text size="sm" mb="xs" className={restyle.muted} role="status" data-testid="phone-view-state">
          {view === 'advanced' ? t('editingAdvanced') : t('showingApp')}
        </Text>
      )}
      {view === 'app' ? (
        <AppRunPage projectId={projectId} workflowId={workflowId} />
      ) : (
        <>
          {!isDesktop && (
            <Text size="sm" mb="xs">
              <Link href={`/projects/${projectId}/workflows/${workflowId}`} onClick={() => setView('app')}>{t('backToApp')}</Link>
            </Text>
          )}
          <WorkflowEditPage params={params} />
        </>
      )}
    </>
  );
}

function WorkflowEditForm({
  projectId,
  workflowId,
  workflow,
  mutate,
  projectName,
}: {
  projectId: string;
  workflowId: string;
  workflow: WorkflowDefinition;
  mutate: KeyedMutator<WorkflowDefinition>;
  projectName?: string;
}) {
  const t = useT('projects.workflowPage');
  const tr = useT();
  const fm = useFormat();
  const router = useRouter();
  const { user } = useAuth();
  const appsEnabled = useWorkflowAppsEnabled();
  const [inputsOpen, setInputsOpen] = useState(false);
  // The API's APP_MANIFEST_INVALID messages from the last refused save (they name the orphaned input).
  const [manifestErrors, setManifestErrors] = useState<string[] | null>(null);
  // App inputs (docs/design/app-inputs.md 24): the app page is drafted here, beside the steps,
  // so a field made from a step's setting and the steps that use it are saved together.
  const { data: app } = useWorkflowApp(projectId, workflowId);
  // The Run dialog uses the app's typed inputs whenever the app can be read; the builder additionally needs the apps switch.
  const builderOn = appsEnabled && app != null;
  const [draftManifest, setDraftManifest] = useState<AppManifest | null>(null);
  // Read through a ref by the apply handler below: that handler feeds the assistant's review tray,
  // and the tray is re-registered with the shell whenever the handler changes, so nothing it merely
  // READS may be a dependency of it.
  const routerRef = useRef(router);
  useEffect(() => {
    routerRef.current = router;
  }, [router]);
  const { mutate: mutateGlobal } = useSWRConfig();
  // The assistant outlives this page: the shell owns whether it is open, so this page no longer
  // mounts a drawer of its own. It opens THAT assistant, and contributes its context and its review
  // tray through the bridge below — nowhere does it register anything by hand.
  const { opened: assistantOpened, open: openAssistant } = useAssistant();
  const [saving, setSaving] = useState(false);
  const [executing, setExecuting] = useState(false);
  const [executeModalOpened, setExecuteModalOpened] = useState(false);
  // B7's estimate for the next run, fetched once per workflow and shared with the confirm dialog —
  // the dialog renders the same figures from the same SWR key rather than asking again.
  const { data: costEstimate, error: costEstimateError, mutate: mutateCostEstimate } =
    useWorkflowCostEstimate(projectId, workflowId);
  // The last refusal the server answered with (402 INSUFFICIENT_CREDITS), kept until the next run
  // attempt: a run that was never queued is a state to explain, not a toast to lose.
  const [insufficient, setInsufficient] = useState<InsufficientCredits | null>(null);
  const [regenModalOpened, setRegenModalOpened] = useState(false);
  const [pendingBrief, setPendingBrief] = useState<RunBrief>({});
  const videoInfo = getVideoGenerateInfo(workflow.steps);
  const { data: executions, mutate: mutateExecutions } = useExecutions(projectId, workflowId);
  const { data: agents } = useAgents();
  const [deleteOpened, setDeleteOpened] = useState(false);
  const [deleteLoading, setDeleteLoading] = useState(false);
  const [pendingDiff, setPendingDiff] = useState<WorkflowEditDiff | null>(null);
  // Synchronous re-entrancy guard for apply: survives the preview remounting mid-request.
  const applyingRef = useRef(false);
  const [steps, setSteps] = useState<StepData[]>(() => mapWorkflowStepsToStepData(workflow));
  // Per-step validation messages from a rejected save; cleared on the next save attempt.
  const [stepErrors, setStepErrors] = useState<Map<number, string[]>>(new Map());
  // The server's workflow as last put on screen. The page only takes a refetched workflow in when it
  // differs from this, so a refetch that found nothing new never throws away what is being typed.
  const adopted = useAdoptedWorkflow(workflow);
  // The draft as it was last saved or adopted from the server. Seeded on mount, so the untouched
  // page starts clean without a first-render effect.
  const savedSignatureRef = useRef<string | null>(null);
  if (savedSignatureRef.current === null) {
    savedSignatureRef.current = draftSignature(
      workflow.name,
      workflow.requiresUserInput ?? false,
      mapWorkflowStepsToStepData(workflow),
    );
  }

  const form = useForm({
    initialValues: { name: workflow.name, requiresUserInput: workflow.requiresUserInput ?? false },
    validate: {
      name: (v) => (!v.trim() ? t('nameRequired') : null),
    },
  });

  // Computed on every render, not memoized: a successful save only changes a ref, and the
  // `setSaving(false)` in its `finally` is the render that must flip the button back to disabled.
  // The ref read is intentional and is what makes that render the one that recomputes the baseline.
  // eslint-disable-next-line react-hooks/refs
  const dirty = draftSignature(form.values.name, form.values.requiresUserInput, steps) !== savedSignatureRef.current
    || (builderOn && draftManifest !== null);

  // `appManifest` is sent only by the "App inputs" panel; a plain save sends none, and the API then
  // re-checks the stored manifest against the new steps (APP_MANIFEST_INVALID names an orphaned input).
  const saveWorkflow = async (
    values: { name: string; requiresUserInput: boolean },
    appManifest?: AppManifest | 'reset',
  ): Promise<boolean> => {
    if (!steps || steps.length === 0) {
      notifications.show({ title: t('errorTitle'), message: t('addStep'), color: 'red' });
      return false;
    }
    const videoGenerateError = findInvalidVideoGenerateStep(steps);
    if (videoGenerateError) {
      notifications.show({ title: t('errorTitle'), message: videoGenerateError, color: 'red' });
      return false;
    }
    setSaving(true);
    setStepErrors(new Map());
    setManifestErrors(null);
    // With app inputs on, a drafted app page always rides the save: the steps may use its fields.
    const manifestToSave = appManifest ?? (builderOn && draftManifest ? manifestForSave(draftManifest, app?.hasExplicitManifest === true) : undefined);
    try {
      await updateWorkflow(projectId, workflowId, {
        name: values.name,
        requiresUserInput: values.requiresUserInput,
        steps: steps.map((s, i) => stepDataToCreateRequest(s, i + 1)),
        ...(manifestToSave === 'reset' ? { clearAppManifest: true } : manifestToSave ? { appManifest: manifestToSave } : {}),
      });
      setDraftManifest(null);
      // Steps may have changed what the app can ask for, so the cached manifest is re-read after every save.
      void mutateGlobal(workflowAppPath(projectId, workflowId));
      // Exactly what was sent is the draft's new baseline, so an edit made while the request was in
      // flight still reads as unsaved rather than being swallowed by the save.
      savedSignatureRef.current = draftSignature(values.name, values.requiresUserInput, steps);
      // What was just saved IS the server's workflow now; remember it so a later refetch is only
      // taken in when something other than this save changed it.
      void mutate().then((saved) => {
        if (saved) adopted.remember(saved);
      }, () => {});
      notifications.show({ title: t('savedTitle'), message: t('savedMessage'), color: 'green' });
      return true;
    } catch (err: unknown) {
      const orphaned = manifestErrorsOf(err);
      if (orphaned) {
        // Not a per-step problem: it belongs to the app's inputs, so it is shown by name next to them.
        setManifestErrors(orphaned);
        setInputsOpen(true);
        notifications.show({ title: t('notSavedTitle'), message: orphaned.join(' '), color: 'red' });
        return false;
      }
      const { byStep } = parseStepErrors(err instanceof ApiError ? err.errors : []);
      setStepErrors(byStep);
      notifications.show({
        title: byStep.size > 0 ? t('stepsNeedAttention', { count: byStep.size }) : t('errorTitle'),
        message: plainErrorMessage(err, t('saveFailed')),
        color: 'red',
      });
      return false;
    } finally {
      setSaving(false);
    }
  };
  const builder = useMemo<AppBuilderValue | null>(
    () => (builderOn && app
      ? {
          // The permission to send media is the app's own field: never part of the draft an author edits or saves.
          manifest: draftManifest ?? withoutPermission(app.manifest),
          setManifest: setDraftManifest,
          mediaEgress: app.mediaEgress ?? null,
          steps,
          setSteps,
          bindableFields: app.bindableFields ?? {},
          hasExplicitManifest: app.hasExplicitManifest,
        }
      : null),
    [builderOn, app, draftManifest, steps],
  );
  // The handler only reads the ref after an await — never during render; the lint rule cannot see
  // through `form.onSubmit`.
  // eslint-disable-next-line react-hooks/refs
  const handleSave = form.onSubmit(async (values) => {
    await saveWorkflow(values);
  });

  // A run never starts on one click: it can take an hour and spend money. The confirmation always
  // offers a brief (the execute API accepts one whether or not "Require a brief" is on) and, when
  // billing is on, what the run is expected to cost (B10c-UI).
  const handleExecuteClick = () => setExecuteModalOpened(true);

  // The profile-build workflow asks for the estimate to be accepted (409); Confirm resends the SAME
  // run with the figure. Every other workflow never gets that answer and runs exactly as before.
  const estimateGate = useEstimateGate(projectId);

  const doExecute = async (
    brief: RunBrief,
    regenerateVideoClips = false,
    acceptEstimateCredits?: number,
  ) => {
    setExecuting(true);
    setInsufficient(null);
    try {
      // An ordinary run calls it exactly as before; the figure is passed only on the confirmed resend.
      const execution =
        acceptEstimateCredits === undefined
          ? await executeWorkflow(projectId, workflowId, null, regenerateVideoClips, {}, undefined, undefined, brief)
          : await executeWorkflow(projectId, workflowId, null, regenerateVideoClips, {}, acceptEstimateCredits, undefined, brief);
      // alpha-launch T-018: the real transition — a run was accepted and queued. Once per user, and
      // only with consent; it fires after the API answered, never on a page load, and it changes
      // nothing about the run.
      emitFunnelEvent('alpha.first_run', userKeyFromEmail(user?.email));
      // Show the new run in "Run history" immediately (the page used to read "No executions" while
      // the run was already going), then revalidate in the background.
      void mutateExecutions((list) => prependExecution(list, execution), { revalidate: true });
      notifications.show({ title: t('runStartedTitle'), message: t('runStartedMessage'), color: 'blue' });
      router.push(`/projects/${projectId}/workflows/${workflowId}/executions/${execution.id}`);
    } catch (err: unknown) {
      // 402 INSUFFICIENT_CREDITS (B7): the run was refused before anything was queued, so this is a
      // state with a remedy rather than a failure — the alert below carries the shortfall and the
      // way to close it. Anything else (a 403, a plan limit, the network) stays a toast.
      const refused = insufficientCredits(err);
      if (estimateGate.intercept(err, (accepted) => doExecute(brief, regenerateVideoClips, accepted))) {
        setExecuting(false);
        return;
      }
      if (refused) {
        setInsufficient(refused);
        // The refusal read a balance that has just moved — or has not — so the estimate shown next
        // time is re-read rather than left as it was before the attempt.
        void mutateCostEstimate();
      } else {
        notifications.show({
          title: t('errorTitle'),
          message: sourceRefusalMessage(err, tr) ?? plainErrorMessage(err, t('executeFailed')),
          color: 'red',
        });
      }
      setExecuting(false);
    }
  };

  const handleDelete = async () => {
    setDeleteLoading(true);
    try {
      await deleteWorkflow(projectId, workflowId);
      notifications.show({ title: t('deletedTitle'), message: t('deletedMessage'), color: 'green' });
      router.push(`/projects/${projectId}`);
    } catch {
      notifications.show({ title: t('errorTitle'), message: t('deleteFailed'), color: 'red' });
    } finally {
      setDeleteLoading(false);
    }
  };

  /**
   * Brings the server's version of this workflow onto the canvas.
   *
   * `force` is for when this page just changed the server itself (a proposal it applied), so the
   * answer is taken in whatever it is. Without it the refetched workflow is only adopted when it
   * differs from the one last adopted — i.e. when someone ELSE (the assistant, in this case) changed
   * it — so a refetch that found nothing new never discards what is being typed on the canvas.
   * Answers the refetched workflow, or undefined when it could not be fetched.
   */
  const syncWithServer = useCallback(
    async (options: { force?: boolean } = {}): Promise<WorkflowDefinition | undefined> => {
      let fresh: WorkflowDefinition | undefined;
      try {
        fresh = await mutate();
      } catch {
        return undefined;
      }
      if (!fresh) return undefined;
      const change = adopted.compare(fresh);
      if (options.force || change.differs) {
        // A name or switch is only taken from the server when the server's value is what moved;
        // adopting one value while showing the other would make the baseline lie about the page.
        if (change.nameMoved) form.setFieldValue('name', fresh.name);
        if (change.switchMoved) form.setFieldValue('requiresUserInput', fresh.requiresUserInput ?? false);
        adopted.remember(fresh);
        const freshSteps = mapWorkflowStepsToStepData(fresh);
        // The server's workflow is now what the page shows, so it is the clean baseline too.
        savedSignatureRef.current = draftSignature(fresh.name, fresh.requiresUserInput ?? false, freshSteps);
        setSteps(freshSteps);
      }
      return fresh;
    },
    // `form` is a fresh object each render; only its stable setter is used.
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [mutate, adopted],
  );

  const handleApplyDiff = useCallback(async () => {
    if (!pendingDiff || applyingRef.current) return;

    // Guard against missing proposedSteps — if undefined, the backend response is incomplete or stale
    if (pendingDiff.proposedSteps === undefined) {
      notifications.show({
        title: t('errorTitle'),
        message: t('proposalIncomplete'),
        color: 'red',
      });
      return;
    }

    applyingRef.current = true;
    try {
      // The proposal says which workflow it was computed for, and only that one can accept it.
      const target = resolveApplyTarget(pendingDiff, { workflowId, name: form.values.name });
      let result: WorkflowAssistantApplyResponse;
      try {
        const applyStepsPayload = buildApplyStepsPayload(pendingDiff);
        // The response is only a summary ({ workflowId, name, stepCount }); steps come from a refresh.
        result = await applyWorkflow(projectId, target.workflowId, target.name, applyStepsPayload, pendingDiff.confirmationToken);
      } catch (err: unknown) {
        if (err instanceof ApiError && err.status === 409) {
          // The token no longer matches the workflow: this proposal can never apply, so drop it.
          setPendingDiff(null);
          // And show what the workflow really holds now — it moved without this page knowing.
          if (target.kind === 'page') void syncWithServer();
          else void mutateGlobal(workflowKeysOf(projectId));
          notifications.show({
            title: t('outOfDateTitle'),
            message: t('outOfDateMessage'),
            color: 'orange',
          });
          return;
        }
        notifications.show({ title: t('errorTitle'), message: plainErrorMessage(err, t('applyFailed')), color: 'red' });
        return;
      }

      // The server has answered: the proposal is consumed whatever happens to the refresh below,
      // and a failed refresh must never surface as an apply error.
      setPendingDiff(null);

      if (target.kind === 'create') {
        void mutateGlobal(workflowKeysOf(projectId));
        notifications.show({ title: t('createdTitle'), message: t('createdMessage', { name: result.name }), color: 'green' });
        routerRef.current.push(`/projects/${projectId}/workflows/${result.workflowId}`);
        return;
      }

      if (target.kind === 'other') {
        void mutateGlobal(workflowKeysOf(projectId));
        notifications.show(
          result.alreadyApplied
            ? { title: t('alreadyAppliedTitle'), message: t('alreadyAppliedOther'), color: 'blue' }
            : { title: t('appliedTitle'), message: t('appliedOther', { name: result.name }), color: 'green' },
        );
        return;
      }

      // The steps were already in the workflow — the assistant applied this same proposal first. That
      // is a success, and the canvas is brought up to what the server holds.
      const fresh = await syncWithServer({ force: true });
      if (result.alreadyApplied) {
        notifications.show({ title: t('alreadyAppliedTitle'), message: t('alreadyAppliedHere'), color: 'blue' });
      } else if (fresh) {
        notifications.show({ title: t('appliedTitle'), message: t('appliedHere'), color: 'green' });
      } else {
        notifications.show({
          title: t('appliedTitle'),
          message: t('appliedNoReload'),
          color: 'orange',
        });
      }
    } finally {
      applyingRef.current = false;
    }
    // `form.values.name` is a dependency because a successful apply writes it to the server; the
    // token, the workflow and the ref guard are read fresh, so nothing here goes stale.
  }, [pendingDiff, projectId, workflowId, form.values.name, mutateGlobal, syncWithServer, t]);

  const handleDiscardDiff = useCallback(() => setPendingDiff(null), []);

  // The shell sends every proposal the conversation emits; the page holds it, exactly as before.
  const handleWorkflowDiff = useCallback((diff: WorkflowEditDiff) => setPendingDiff(diff), []);

  // The assistant changed something while the person was looking at this page. Two consequences
  // for the page, both only about the workflow a finished action names:
  //   * a proposal waiting in the tray for that workflow can no longer apply — its confirmation
  //     token covers the steps as they were — so it goes, rather than staying to answer "out of
  //     date" for changes that are already in;
  //   * if it is THIS workflow, the canvas is brought up to what the server now holds.
  useEffect(
    () =>
      onAssistantActionDone((done) => {
        const changedId = done.refresh?.workflowId;
        if (!changedId) return;
        if (done.kind === 'workflow') {
          // `undefined` is a proposal that did not say (read as this workflow); `null` is a create,
          // which no existing workflow's change can have consumed.
          setPendingDiff((pending) =>
            pending && sameId(pending.workflowId === undefined ? workflowId : pending.workflowId, changedId) ? null : pending,
          );
        }
        if (sameId(changedId, workflowId)) void syncWithServer();
      }),
    [workflowId, syncWithServer],
  );

  // A proposal for ANOTHER workflow is not drawn over this one's canvas, nor named from its steps.
  const proposalIsForThisPage = !pendingDiff || resolveApplyTarget(pendingDiff, { workflowId, name: workflow.name }).kind === 'page';
  const proposalTarget = useMemo(() => {
    if (!pendingDiff) return undefined;
    const target = resolveApplyTarget(pendingDiff, { workflowId, name: workflow.name });
    if (target.kind === 'create') return pendingDiff.name?.trim() ? t('targetCreatesNamed', { name: pendingDiff.name.trim() }) : t('targetCreates');
    if (target.kind === 'other') return pendingDiff.name?.trim() ? t('targetChangesNamed', { name: pendingDiff.name.trim() }) : t('targetChangesOther');
    return undefined;
  }, [pendingDiff, workflowId, workflow.name, t]);

  // Memoized so the registration below sees a STABLE element: a freshly-built `<WorkflowDiffPreview>`
  // on every render would make `useAssistantPageContext` re-register on every render, and each
  // registration re-renders the shell — a register → setState → re-render loop.
  const review = useMemo(
    () =>
      pendingDiff ? (
        <WorkflowDiffPreview
          key={pendingDiff.confirmationToken}
          diff={pendingDiff}
          currentSteps={proposalIsForThisPage ? steps : undefined}
          targetNote={proposalTarget}
          onApply={handleApplyDiff}
          onDiscard={handleDiscardDiff}
        />
      ) : undefined,
    [pendingDiff, steps, proposalIsForThisPage, proposalTarget, handleApplyDiff, handleDiscardDiff],
  );

  useAssistantPageContext({ onWorkflowDiff: handleWorkflowDiff, currentSteps: steps, review });

  // The desktop rail has to start below the sticky dock, and the dock's height is not a constant:
  // the title, the cost chip and the action row's wrapping all move it. Measure it into a CSS
  // variable rather than guessing, so the rail never slides underneath it.
  const dockRef = useRef<HTMLDivElement>(null);
  const layoutRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const dock = dockRef.current;
    const layout = layoutRef.current;
    if (!dock || !layout) return;
    const apply = () => layout.style.setProperty('--workflow-dock-height', `${dock.offsetHeight}px`);
    apply();
    if (typeof ResizeObserver === 'undefined') return;
    const observer = new ResizeObserver(apply);
    observer.observe(dock);
    return () => observer.disconnect();
  }, []);

  return (
    <>
      {/* Which project and workflow the conversation is about. Renders nothing; cleared on unmount. */}
      <AssistantPageContext projectId={projectId} workflowId={workflowId} />
      <div ref={dockRef} className={styles.headerDock} data-testid="workflow-page-header">
        <PageHeader
          title={workflow.name}
          breadcrumbs={[
            { label: t('breadcrumbProjects'), href: '/projects' },
            { label: projectName ?? t('projectFallback'), href: `/projects/${projectId}` },
            { label: workflow.name },
          ]}
        >
          <ShareAsRecipeButton projectId={projectId} workflowId={workflowId} />
          {/* What the next run should cost, from B7's estimate. Nothing at all without billing (a
              self-host install answers `billingEnabled: false`), and nothing while it is still being
              fetched — the Run button must not shift under the pointer for a figure nobody needs yet. */}
          {costEstimate?.billingEnabled && !costEstimateError && (
            <Text size="sm" c={costEstimate.affordable ? 'dimmed' : 'orange'} data-testid="run-cost-chip">
              {typeof costEstimate.typicalCredits === 'number'
                ? creditEstimateHeadline(costEstimate.typicalCredits, costEstimate.lowCredits, costEstimate.highCredits, tr, fm.locale)
                : // An older backend sends no typical figure: the range alone, as it always read.
                  creditRange(costEstimate.lowCredits, costEstimate.highCredits, tr, fm.locale)}
              {(costEstimate.fixedMaxCredits ?? 0) > 0
                ? t('costPlusUpTo', { amount: formatCreditAmount(costEstimate.fixedMaxCredits ?? 0, { locale: fm.locale }) })
                : ''}
              {costEstimate.affordable ? '' : t('notEnoughCredits')}
            </Text>
          )}
          {/* The form's own submit button, living here so it stays reachable while the canvas
              scrolls. It submits `workflow-edit-form` through the `form` attribute, so the save
              handler, validation and notifications are exactly the ones the bottom button had. */}
          <Button
            type="submit"
            form={WORKFLOW_FORM_ID}
            loading={saving}
            disabled={!dirty}
            // Secondary to Run: two identical violet gradient CTAs left the header with no primary.
            variant="light"
          >
            {t('saveChanges')}
          </Button>
          <Button
            leftSection={<IconPlayerPlay size={16} />}
            onClick={handleExecuteClick}
            loading={executing}
            variant="gradient"
            gradient={{ from: 'violet', to: 'purple' }}
          >
            {t('run')}
          </Button>
          <Indicator disabled={!pendingDiff || assistantOpened} color="violet" size={10} offset={4} withBorder>
            <Button
              leftSection={<IconMessageChatbot size={16} />}
              onClick={openAssistant}
              variant="default"
            >
              {t('assistant')}
              {pendingDiff && <VisuallyHidden>{t('changesWaiting')}</VisuallyHidden>}
            </Button>
          </Indicator>
          {/* Destructive, so tucked away from Run and Save Changes; the confirm dialog still guards it. */}
          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <ActionIcon variant="default" size={44} aria-label={t('actions')}><IconDotsVertical size={18} /></ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item color="red" leftSection={<IconTrash size={15} />} onClick={() => setDeleteOpened(true)}>{t('deleteWorkflow')}</Menu.Item>
            </Menu.Dropdown>
          </Menu>
        </PageHeader>
      </div>

      {insufficient && (
        <InsufficientCreditsAlert data={insufficient} onDismiss={() => setInsufficient(null)} />
      )}

      {/* Source order is the mobile order: the form first, run history after it. The desktop grid
          only promotes the aside into a rail without moving it in the document. */}
      <div ref={layoutRef} className={styles.layout} data-testid="workflow-layout">
        <form id={WORKFLOW_FORM_ID} onSubmit={handleSave}>
          <AppBuilderContext.Provider value={builder}>
          <Stack gap="lg" maw={1200}>
            {appsEnabled && (
              <Button
                variant="default"
                className={restyle.touch}
                aria-expanded={inputsOpen}
                aria-controls="app-inputs-region"
                onClick={() => setInputsOpen((o) => !o)}
                style={{ alignSelf: 'flex-start' }}
              >
                {t('appInputs')}
              </Button>
            )}
            {appsEnabled && (inputsOpen || manifestErrors || (builderOn && draftManifest !== null)) && (
              <div id="app-inputs-region">
                <AppInputsPanel
                  projectId={projectId}
                  workflowId={workflowId}
                  draftSteps={steps.map((s, i) => stepDataToCreateRequest(s, i + 1))}
                  saving={saving}
                  errors={manifestErrors}
                  onSave={(manifest) => saveWorkflow(form.values, builderOn ? manifestForSave(manifest, app?.hasExplicitManifest === true) : manifest)}
                  onReset={() => saveWorkflow(form.values, 'reset')}
                />
              </div>
            )}
            <TextInput label={t('workflowName')} size="lg" {...form.getInputProps('name')} />
            <Switch
              label={t('requireBrief')}
              description={t('requireBriefHint')}
              checked={form.values.requiresUserInput}
              onChange={(e) => form.setFieldValue('requiresUserInput', e.currentTarget.checked)}
            />
            {steps && <FlowchartBuilderWrapper steps={steps} onChange={setSteps} projectId={projectId} pendingAssistantDiff={proposalIsForThisPage ? (pendingDiff ?? undefined) : undefined} stepErrors={stepErrors} />}
          </Stack>
          </AppBuilderContext.Provider>
        </form>

        <aside className={styles.runs} data-testid="workflow-run-history" aria-label={t('runHistory')}>
          <ExecutionHistory projectId={projectId} workflowId={workflowId} compact />
        </aside>
      </div>

      {estimateGate.modal}
      <RunWithInputsModal
        opened={executeModalOpened}
        onClose={() => setExecuteModalOpened(false)}
        manifest={app?.manifest ?? null}
        mediaEgress={app?.mediaEgress ?? null}
        onConfirm={(brief) => {
          setExecuteModalOpened(false);
          if (videoInfo.has) {
            setPendingBrief(brief);
            setRegenModalOpened(true);
          } else {
            void doExecute(brief, false);
          }
        }}
        executing={executing}
        requiresBrief={workflow.requiresUserInput ?? false}
        stepSummaries={describeStepsForRun(workflow, agents)}
        steps={workflow.steps.map((s) => ({
          stepType: s.stepType,
          agentType: agents?.find((a) => a.id === s.agentDefinitionId)?.agentType ?? null,
        }))}
        estimate={estimateRunDuration(executions, workflow.steps.length)}
        workflowName={workflow.name}
        projectId={projectId}
        workflowId={workflowId}
      />

      <RegenerateVideoClipsModal
        opened={regenModalOpened}
        onClose={() => setRegenModalOpened(false)}
        capCredits={videoInfo.capCredits}
        busy={executing}
        onChoose={(regenerate) => {
          setRegenModalOpened(false);
          void doExecute(pendingBrief, regenerate);
        }}
      />

      <ConfirmModal
        opened={deleteOpened}
        onClose={() => setDeleteOpened(false)}
        onConfirm={handleDelete}
        title={t('deleteTitle')}
        message={t('deleteMessage')}
        loading={deleteLoading}
      />
    </>
  );
}