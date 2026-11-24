'use client';

import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import Link from 'next/link';
import { useRouter, useSearchParams } from 'next/navigation';
import useSWR from 'swr';
import { ActionIcon, Alert, Button, Collapse, Group, Menu, Modal, NumberInput, SegmentedControl, Select, Skeleton, Stack, Switch, Text, Textarea } from '@mantine/core';
import { useDebouncedValue, useDocumentTitle, useMediaQuery } from '@mantine/hooks';
import { notifications } from '@mantine/notifications';
import { IconChevronDown, IconDots } from '@tabler/icons-react';
import { PageHeader } from '@/components/shared/PageHeader';
import { ExecutionHistory } from '@/components/workflows/ExecutionHistory';
import { RegenerateVideoClipsModal, getVideoGenerateInfo } from '@/components/workflows/RegenerateVideoClipsModal';
import { SilentRunNotice } from '@/components/workflows/SilentRunNotice';
import { RunStylePicker } from '@/components/styles/RunStylePicker';
import { BILLING_SETTINGS_PATH, NO_STYLE, configFor } from '@/components/styles/style-options';
import { RunCostEstimate } from '@/components/billing/RunCostEstimate';
import { InsufficientCreditsAlert } from '@/components/billing/InsufficientCreditsAlert';
import { notifyInsufficientCredits } from '@/components/billing/insufficientCreditsNotification';
import { useEstimateGate } from '@/components/design-system/EstimateGateModal';
import { useAssistant } from '@/components/assistant/AssistantShellProvider';
import { AssistantPageContext } from '@/components/assistant/AssistantPageContext';
import { useAuth } from '@/lib/hooks/use-auth';
import { useOnboarding } from '@/lib/hooks/use-onboarding';
import { useAgents } from '@/lib/hooks/use-agents';
import { useProject } from '@/lib/hooks/use-projects';
import { useWorkflow } from '@/lib/hooks/use-workflows';
import { describeError } from '@/lib/api/describe-error';
import { useSlow } from '@/lib/hooks/use-slow';
import { FailureCard } from '@/components/shared/FailureCard';
import { useMediaPool } from '@/lib/hooks/use-media';
import { useExecutions } from '@/lib/hooks/use-executions';
import { useProjectFiles } from '@/lib/hooks/use-files';
import { useProjectVoices } from '@/lib/hooks/use-voices';
import { useAvatarRequirements, useProjectAvatars } from '@/lib/hooks/use-avatars';
import { useWorkflowApp } from '@/lib/hooks/use-workflow-apps';
import { inputLabel } from '@/lib/workflow-apps/app-inputs';
import { useChangeCapabilities } from '@/lib/hooks/use-change-requests';
import { useBillingSummary, useWorkflowCostEstimate, useWorkflowCostEstimateWithBrief } from '@/lib/hooks/use-billing';
import { useRunForm } from '@/lib/hooks/use-run-form';
import { ApiError, fetcher } from '@/lib/api/client';
import { planRefusalOf, type PlanRefusal } from '@/lib/utils/plan-refusal';
import { sourceProblemMessage, sourceRefusalMessage } from '@/lib/api/source-limits';
import { executeWorkflow, lintWorkflow } from '@/lib/api/workflows';
import { retryExecution, resumeExecution, stopExecution } from '@/lib/api/executions';
import { uploadFile } from '@/lib/api/files';
import { FIRST_FILM_SAMPLE_PARAM, sampleFootageChoice } from '@/lib/onboarding/first-film';
import { insufficientCredits } from '@/lib/api/billing';
import { emitFunnelEvent, runBlockDetailFromKey, runBlockDetailOf, userKeyFromEmail } from '@/lib/telemetry';
import { formatCreditAmount } from '@/lib/utils/billing';
import { pollInterval } from '@/lib/utils/poll-interval';
import type { InsufficientCredits } from '@/lib/types/billing';
import type { ProjectVoice } from '@/lib/types/voices';
import type { RunOutputFormat, RunResolutionChoice } from '@/lib/types/styles';
import type { AppFileRole, AppManifestInput, RunBrief, WorkflowExecution, WorkflowLintResponse } from '@/lib/types/workflow';
import { AppInputField } from './AppInputField';
import { PermissionBlock } from './PermissionBlock';
import { AppInputChangeModal } from './AppInputChangeModal';
import { canChangeFields } from './app-input-change';
import {
  briefInputs,
  fieldRefusalOf,
  firstMissing,
  initialValues,
  inputFieldId,
  isAppInput,
  isVisible,
  pageLayout,
  pricingValues,
  refusalMessage,
  valuesFromBrief,
  type FieldRefusal,
  type InputValue,
  type InputValues,
  isPermissionInput,
} from './app-input-model';
import { AvatarRunInput } from './AvatarRunInput';
import { BriefExamples } from './BriefExamples';
import { briefFamilyOf, briefPersona, briefPrompt } from './brief-examples';
import { CONFIRM_EMAIL_NOTICE_ID, ConfirmEmailNotice, focusConfirmEmailNotice, isEmailNotVerified } from './ConfirmEmailNotice';
import { thirdStepFor } from './first-result';
import { FileSlot } from './FileSlot';
import { MediaOverlaysCard } from './MediaOverlaysCard';
import { ReadinessStrip } from './ReadinessStrip';
import { RunBar, type RunBlock, type RunShortfall } from './RunBar';
import { RunResult, progressLines } from './RunResult';
import {
  APP_LANGUAGES,
  DEFAULT_MAX_FILES,
  LENGTH_MAX_SEC,
  LENGTH_MIN_SEC,
  SOURCE_LANGUAGES,
  appRoleOf,
  briefInput,
  paceValueOf,
  type PaceValue,
  fileHasRole,
  isActive,
  itemsFromFindings,
  newestFirst,
  pageBrief,
  priceLines,
  roundUpEstimate,
  resolveText,
  needsConfirm,
  confirmCopy,
  acceptTotal,
  sortItems,
  type FixAction,
  type ReadinessItem,
} from './run-model';
import { useFormat, useT } from '@/i18n';
import { msg } from '@/i18n/msg';
import { docsPageHref } from '@/lib/onboarding/guide-links';
import { ShareAsRecipeButton } from '@/components/marketplace/ShareAsRecipeButton';
import { PHONE_QUERY } from '@/lib/breakpoints';
import styles from './AppRunPage.module.css';


/** Puts a just-started run at the top of the cached run list so the page shows it at once. */
function prepend(list: WorkflowExecution[] | undefined, execution: WorkflowExecution): WorkflowExecution[] {
  return [execution, ...(list ?? []).filter((e) => e.id !== execution.id)];
}

/** The brief without its resolution: a plan refusal is cleared by changing anything but the resolution it reset. */
function refusalKey(brief: RunBrief, ignoreResolution: boolean): string {
  return JSON.stringify(ignoreResolution ? { ...brief, resolution: undefined } : brief);
}

/** The run gate's 400 refusals of a step that would certainly fail: code to the message keys (named step, unnamed step). */
const START_REFUSALS: Record<string, [string, string]> = {
  VIDEO_SOURCE_NOT_CHOSEN: ['videoNotChosen', 'videoNotChosenNoStep'],
  AVATAR_NOT_CHOSEN: ['avatarNotChosen', 'avatarNotChosenNoStep'],
  VIDEO_MODEL_UNSUPPORTED: ['modelUnsupported', 'modelUnsupportedNoStep'],
  LIP_SYNC_MODEL_NOT_SET: ['lipSyncModelNotSet', 'lipSyncModelNotSetNoStep'],
};

/** The plan feature a 402 refusal is about (`resolution`, `aiShots`, ...), when the body names one. */
function refusalFeature(err: ApiError): string | null {
  const body = err.body;
  const feature = typeof body === 'object' && body !== null ? (body as { feature?: unknown }).feature : null;
  return typeof feature === 'string' ? feature : null;
}

/** Mirrors the engine's usable-voice rule (ProjectReadinessFacts): selectable, a remote voice id, and a provider for clones. */
export function usableVoiceCount(voices: readonly ProjectVoice[] | undefined): number {
  return (voices ?? []).filter(
    (v) => v.selectable && v.remoteVoiceId.trim() !== '' && (v.source === 'Library' || v.inferenceProviderId !== null),
  ).length;
}

// "Add a clip" and the other per-role fix labels are catalogue messages: apps.run.page.addOne.
interface AppRunPageProps {
  projectId: string;
  workflowId: string;
}

/**
 * The app run page (spec 4.3): the workflow as a title, a few inputs, Run, a price and a result.
 * Loads the effective manifest and the workflow, shows a skeleton with Run disabled while either is
 * still arriving, and hands over to the form once both are here.
 */
export function AppRunPage({ projectId, workflowId }: AppRunPageProps) {
  const t = useT('apps.run.page');
  const { data: workflow, error: workflowError, mutate: mutateWorkflow } = useWorkflow(projectId, workflowId);
  const { data: app, error: appError, mutate: mutateApp } = useWorkflowApp(projectId, workflowId);
  const { data: project } = useProject(projectId);
  const tState = useT('errors.state');
  const loadFailure: unknown = (workflowError && !workflow ? workflowError : null) ?? (appError && !app ? appError : null);
  const slow = useSlow(!loadFailure && (!workflow || !app));

  if (loadFailure) {
    return (
      <FailureCard
        error={loadFailure}
        onRetry={() => {
          void mutateWorkflow();
          void mutateApp();
        }}
        backHref="/dashboard"
      />
    );
  }
  if (!workflow || !app) {
    return (
      <div className={styles.root} data-testid="app-run-skeleton" aria-busy="true">
        <Skeleton height={32} width="40%" mb="lg" />
        <div className={styles.layout}>
          <div className={styles.inputs}>
            <Skeleton height={56} />
            <Skeleton height={56} />
            <Skeleton height={56} />
            <Text size="sm" className={styles.reason} id="app-run-provisioning" role="status">
              {slow ? tState('slow') : t('gettingReady')}
            </Text>
            {slow && (
              <Button
                variant="light"
                size="xs"
                onClick={() => {
                  void mutateWorkflow();
                  void mutateApp();
                }}
              >
                {tState('retry')}
              </Button>
            )}
            <Button aria-disabled="true" aria-describedby="app-run-provisioning" className={`${styles.runButton} ${styles.blocked}`} size="md">
              {t('run')}
            </Button>
          </div>
          <Skeleton height={240} />
        </div>
      </div>
    );
  }
  return (
    <AppRunForm
      key={workflow.id}
      projectId={projectId}
      workflowId={workflowId}
      workflow={workflow}
      app={app}
      projectName={project?.name}
    />
  );
}

type LoadedWorkflow = NonNullable<ReturnType<typeof useWorkflow>['data']>;
type LoadedApp = NonNullable<ReturnType<typeof useWorkflowApp>['data']>;

function AppRunForm({
  projectId,
  workflowId,
  workflow,
  app,
  projectName,
}: {
  projectId: string;
  workflowId: string;
  workflow: LoadedWorkflow;
  app: LoadedApp;
  projectName: string | undefined;
}) {
  const t = useT('apps.run.page');
  const tr = useT();
  // Linter sentences live under `apps.lint.*`; this translator's `has` probes that namespace.
  const lintT = useT('apps');
  const fm = useFormat();
  const router = useRouter();
  const { user, emailVerified } = useAuth();
  // Only a session that says "not confirmed" warns: a session without the field (an older one) is not nagged.
  const unverified = Boolean(user) && emailVerified === false;
  // The server refused a run for this reason, whatever this browser's session said.
  const [emailRefused, setEmailRefused] = useState(false);
  const showConfirmEmail = Boolean(user) && (unverified || emailRefused);
  // Who the example briefs speak to: the welcome answers, when there are any.
  const persona = briefPersona(useOnboarding().profile);
  const { openWithPrompt } = useAssistant();
  const phone = useMediaQuery(PHONE_QUERY, false, { getInitialValueInEffect: false }) ?? false;
  const manifest = app.manifest;
  // App inputs (docs/design/app-inputs.md 21): essentials shown, the rest under "More options".
  const layout = useMemo(() => pageLayout(manifest), [manifest]);
  const inputs = layout.all;
  const hasAppInputs = inputs.some(isAppInput);
  // The notes box is required only when the page shows one: an app whose own fields carry the brief (the avatar clip's
  // "What they do") has no box, so a workflow-level "needs a description" flag must not block Run on a box that is not there.
  const requiresNotes = (workflow.requiresUserInput ?? false) && inputs.some((i) => i.kind === 'notes');
  // The example briefs exist for the two families they were written for, and only where the box must be filled.
  const briefFamily = requiresNotes ? briefFamilyOf(manifest.category) : null;
  const title = manifest.title?.trim() || app.name;
  useDocumentTitle(t('documentTitle', { title, project: projectName ?? t('projectFallback') }));

  const { data: agents } = useAgents();
  const agentTypeOf = (agentDefinitionId: string): string | null =>
    agents?.find((a) => a.id === agentDefinitionId)?.agentType ?? null;

  // Set below, once the chosen clips and their probes are known: the form reads it for the default shape.
  const [clipsNotVertical, setClipsNotVertical] = useState(false);

  // --- the page's own inputs, seeded once from the manifest's defaults --------------------------
  const languageInput = briefInput(inputs, 'language');
  const sourceLanguageInput = briefInput(inputs, 'sourceLanguage');
  const lengthInput = briefInput(inputs, 'lengthSec');
  const voiceInput = briefInput(inputs, 'voice');
  const designInput = briefInput(inputs, 'designSystem');
  const overlayCardAudioInput = briefInput(inputs, 'overlayCardAudio');
  const avatarInput = briefInput(inputs, 'avatar');
  const paceInput = briefInput(inputs, 'pace');
  const slots = inputs.flatMap((input) => {
    const role = input.kind === 'files' ? appRoleOf(input.role) : null;
    return input.kind === 'files' && role ? [{ input, role }] : [];
  });
  const [language, setLanguage] = useState(String(languageInput?.default ?? 'auto'));
  const [sourceLanguage, setSourceLanguage] = useState(String(sourceLanguageInput?.default ?? 'auto'));
  const [lengthSec, setLengthSec] = useState<number | null>(() => {
    if (!lengthInput) return null;
    const first = lengthInput.choices?.[0];
    return Number(lengthInput.default ?? (typeof first === 'number' ? first : 60));
  });
  const [narrated, setNarrated] = useState<boolean | null>(voiceInput ? voiceInput.default !== false : null);
  const [voiceId, setVoiceId] = useState<string | null>(null);
  const [overlayCardAudio, setOverlayCardAudio] = useState(String(overlayCardAudioInput?.default ?? 'agent'));
  // The avatar picker shows only while the project's avatars answer: with the switch off they are 404.
  const { data: avatarRequirements, error: avatarsOff } = useAvatarRequirements(avatarInput ? projectId : null);
  const avatarsOn = Boolean(avatarInput && avatarRequirements && !avatarsOff);
  const { data: avatars } = useProjectAvatars(projectId, avatarsOn);
  const [avatarId, setAvatarId] = useState<string | null>(null);
  // null = the person has not chosen: the chosen style's dynamics (then the app's default, else Off) decides.
  const [paceChoice, setPaceChoice] = useState<PaceValue | 'off' | null>(null);
  const [design, setDesign] = useState<string | null>(designInput ? (designInput.default === 'none' ? 'none' : 'project') : null);
  // A role the person has not touched uses every fitting project file (up to the slot's maximum).
  const [chosen, setChosen] = useState<Partial<Record<AppFileRole, string[]>>>({});
  const [addRole, setAddRole] = useState<{ role: AppFileRole; signal: number } | null>(null);
  const [uploadingRole, setUploadingRole] = useState<AppFileRole | null>(null);
  // The values of the app's own inputs, seeded from their defaults; a run-gate refusal sits next to its field until that
  // field changes.
  const [inputValues, setInputValues] = useState<InputValues>(() => initialValues(inputs));
  const [fieldRefusal, setFieldRefusal] = useState<FieldRefusal | null>(null);
  const setInputValue = useCallback((id: string, value: InputValue) => {
    setInputValues((prev) => (prev[id] === value ? prev : { ...prev, [id]: value }));
    setFieldRefusal((prev) => (prev?.inputId === id ? null : prev));
  }, []);
  const [modelSignal, setModelSignal] = useState<{ inputId: string; signal: number } | null>(null);
  // The permission to send media sits in its own block above Run. Its "allow it to run" sentence shows under the switch
  // once Run was pressed without it, and goes away when it is switched on.
  const permission = layout.permission;
  const [permissionAsked, setPermissionAsked] = useState(false);
  // "More options" starts closed; the choice is remembered per person and app (best effort).
  const moreKey = `reelbolt:app-more:${userKeyFromEmail(user?.email) || 'anon'}:${workflowId}`;
  // The form mounts only once the app has loaded in the browser, so reading storage here never meets server markup.
  const [moreOpen, setMoreOpen] = useState(() => {
    try {
      return typeof window !== 'undefined' && window.localStorage.getItem(moreKey) === 'open';
    } catch {
      return false;
    }
  });
  const toggleMore = () => {
    setMoreOpen((open) => {
      try {
        window.localStorage.setItem(moreKey, open ? 'closed' : 'open');
      } catch {
        /* storage blocked */
      }
      return !open;
    });
  };
  /** Focuses an app input's field, opening "More options" first when it sits there. */
  const focusInput = (inputId: string) => {
    const under = layout.more.some((i) => i.kind === 'input' && i.id === inputId);
    const focus = () => document.getElementById(inputFieldId(inputId))?.focus();
    if (under && !moreOpen) {
      setMoreOpen(true);
      window.setTimeout(focus, 50);
    } else {
      focus();
    }
  };

  const { data: files, mutate: mutateFiles } = useProjectFiles(projectId);
  // Opened from the guided first film: only the sample clip is chosen for the footage slot (until the person changes it).
  const sampleId = useSearchParams()?.get(FIRST_FILM_SAMPLE_PARAM) ?? null;
  // Footage apps only: the probes say whether the chosen clips are vertical. No pool or no probe changes nothing.
  const hasVideoSlot = slots.some((s) => s.role === 'video');
  const hasFormatInput = inputs.some((i) => i.kind === 'brief' && i.field === 'format');
  const { data: pool } = useMediaPool(hasVideoSlot && hasFormatInput ? projectId : null);
  const { data: voices } = useProjectVoices(voiceInput ? projectId : null);
  // A project voice the run can use makes a missing default speech service a non-issue, so the form
  // only warns about a missing service when the project has no usable voice of its own.
  const form = useRunForm({
    projectId,
    clipsNotVertical,
    requiresBrief: requiresNotes,
    steps: workflow.steps.map((s) => ({ stepType: s.stepType, agentType: agentTypeOf(s.agentDefinitionId) })),
    projectVoiceCount: usableVoiceCount(voices),
  });

  const selectedFor = (role: AppFileRole, max: number | null | undefined): string[] => {
    const cap = max ?? DEFAULT_MAX_FILES;
    const own = chosen[role];
    if (own) return own.slice(0, cap);
    // "What goes on the screen" and the optional picture slot start empty: the person chooses it,
    // never a guess. Empty means the project's own pictures and videos are offered instead.
    if (role === 'insertContent' || role === 'overlay') return [];
    if (role === 'video') {
      const sample = sampleFootageChoice(sampleId, files);
      if (sample) return sample.slice(0, cap);
    }
    const fitting = (files ?? []).filter((f) => fileHasRole(f, role)).map((f) => f.id);
    // Prefer a file the person has not already chosen for another slot.
    const takenElsewhere = new Set(Object.entries(chosen).filter(([r]) => r !== role).flatMap(([, ids]) => ids ?? []));
    const free = fitting.filter((id) => !takenElsewhere.has(id));
    return (free.length > 0 ? free : fitting).slice(0, cap);
  };

  const videoIds = selectedFor('video', slots.find((x) => x.role === 'video')?.input.max).join(',');
  const notVertical = (() => {
    if (!pool || !videoIds) return false;
    const ids = new Set(videoIds.split(','));
    const dims = pool
      .filter((m) => m.mediaRef.startsWith('file:') && ids.has(m.mediaRef.slice(5)))
      .flatMap((m) => (m.probe?.width && m.probe.height ? [m.probe] : []));
    // A phone clip stored sideways reads as landscape here (rotation is not on the wire); "As set up"
    // still follows the source, so that errs toward the clip's own shape.
    return dims.length > 0 && dims.filter((d) => (d.width ?? 0) >= (d.height ?? 0)).length * 2 > dims.length;
  })();
  if (notVertical !== clipsNotVertical) setClipsNotVertical(notVertical);

  // A suggested style is a pre-selection, never forced: applied once, only while nothing is chosen.
  const [preselected, setPreselected] = useState(false);
  const suggested = manifest.suggestedStylePreset;
  if (!preselected && suggested && form.presets) {
    setPreselected(true);
    if (form.style === NO_STYLE && form.presets.some((p) => p.key === suggested)) form.changeStyle(`preset:${suggested}`);
  }

  // The pace the page shows and sends; null is Off (nothing is sent, the compile stays as the workflow made it). A style
  // that carries dynamics always has its pace here: Off cannot switch off what the chosen style asks for.
  const stylePace = paceInput ? paceValueOf(configFor(form.style, form.presets, form.styles)?.dynamics) : null;
  const pace: PaceValue | null = paceInput
    ? paceChoice && paceChoice !== 'off'
      ? paceChoice
      : (stylePace ?? (paceChoice === 'off' ? null : paceValueOf(String(paceInput.default ?? ''))))
    : null;
  // --- the brief this page would send ------------------------------------------------------------
  const brief: RunBrief = form.buildBrief(
    pageBrief({
      language,
      sourceLanguage,
      lengthSec,
      narrated,
      voiceId,
      design,
      overlayCardAudio,
      avatarId: avatarsOn ? avatarId : null,
      pace,
      sourceFiles: slots.map(({ input, role }) => ({ role, fileIds: selectedFor(role, input.max) })),
    }),
  );
  // The app's own inputs ride in `brief.inputs` (checked and bound on the server); an app without any sends none.
  const sentInputs = hasAppInputs ? briefInputs(inputs, inputValues) : undefined;
  if (sentInputs) brief.inputs = sentInputs;
  const briefKey = JSON.stringify(brief);
  // A required field of the app's own with nothing in it. The permission to send media is decided below, once the pre-run
  // check for the current choices is known (a field may pick a branch that does not send anything).
  const missingOwnInput = hasAppInputs ? firstMissing(inputs.filter((i) => !isPermissionInput(i)), inputValues) : null;

  // --- readiness: the pre-run check, debounced, plus what the page knows at once ------------------
  const [debouncedKey] = useDebouncedValue(briefKey, 400);
  const { data: lint, error: lintError } = useSWR<WorkflowLintResponse>(
    ['workflow-lint', projectId, workflowId, debouncedKey],
    () => lintWorkflow(projectId, workflowId, { brief: JSON.parse(debouncedKey) as RunBrief }),
    { keepPreviousData: true, revalidateOnFocus: false, shouldRetryOnError: false },
  );
  // The permission to send media is needed when the pre-run check for the current choices says a step would be refused
  // (WL015); until that answer arrives, what the app said as the page opened.
  const lintNeedsPermission = lint ? lint.findings.some((f) => f.ruleId === 'WL015' && f.severity === 'error') : null;
  const permissionNeeded = permission
    ? app.mediaEgress?.alreadyAllowed !== true && (lintNeedsPermission ?? app.mediaEgress?.required ?? permission.required === true)
    : false;
  const missingInput = missingOwnInput ?? (permission && permissionNeeded && inputValues[permission.id] !== true ? permission : null);

  const items = useMemo(() => {
    const local: ReadinessItem[] = [];
    for (const { input, role } of slots) {
      const min = input.min ?? 1;
      if (selectedFor(role, input.max).length < min) {
        local.push({
          key: `slot:${role}`,
          level: 'fix',
          message: min <= 1 ? msg('apps.run.page.addAtLeastOne', { role }) : msg('apps.run.page.addAtLeast', { role, min }),
          fix: { label: msg('apps.run.page.addOne', { role }), action: 'upload', role },
          focusId: `app-input-files-${role}`,
        });
      }
    }
    // A required app input with nothing in it: the fix names it and goes to it (docs/design/app-inputs.md 11.3).
    if (missingInput) {
      const name = missingInput.label?.trim() || missingInput.id;
      const isPermission = isPermissionInput(missingInput);
      local.push({
        key: `input:missing:${missingInput.id}`,
        level: 'fix',
        message: isPermission ? msg('apps.permission.mediaEgress.required') : msg('apps.run.inputs.missing', { input: name }),
        fix: {
          label: isPermission ? msg('apps.permission.mediaEgress.allow') : msg('apps.run.inputs.fillIn', { input: name }),
          action: 'input',
          inputId: missingInput.id,
        },
        focusId: inputFieldId(missingInput.id),
      });
    }
    // An address that is not confirmed: said before Run is pressed, never a block (the server decides).
    if (showConfirmEmail) {
      local.push({
        key: 'email:unconfirmed',
        level: 'check',
        message: msg('apps.run.confirmEmail.readiness'),
        fix: { label: msg('apps.run.confirmEmail.resend'), action: 'email' },
        focusId: CONFIRM_EMAIL_NOTICE_ID,
      });
    }
    // An empty required notes box is something left to do, so the strip says so instead of "Ready".
    if (form.missingBrief) {
      local.push({
        key: 'notes:required',
        level: 'fix',
        message: msg('apps.run.page.describeAbove'),
        fix: { label: msg('apps.run.page.writeIt'), action: 'notes' },
        focusId: 'app-input-notes',
      });
    }
    if (narrated && form.speechNotice) {
      const noProvider = form.speechNotice.level === 'error';
      local.push({
        key: 'voice:provider',
        level: noProvider ? 'fix' : 'check',
        message: noProvider ? msg('apps.run.page.narrationNeedsVoice') : form.speechNotice.body,
        ...(noProvider ? { fix: { label: msg('apps.run.page.openVoices'), action: 'voice' as FixAction } } : {}),
        focusId: 'app-input-voice',
      });
    }
    // An avatar app run with no avatar shows nobody from the Avatars tab: worth a check, never a block.
    if (avatarsOn && avatars && avatars.some((a) => a.selectable) && !avatarId) {
      local.push({ key: 'avatar:none', level: 'check', message: msg('apps.run.page.avatarNoneChosen'), focusId: 'app-input-avatar' });
    }
    const picked = voiceId ? voices?.find((v) => v.id === voiceId) : undefined;
    if (narrated && picked && !picked.selectable) {
      local.push({
        key: 'voice:consent',
        level: 'fix',
        message: msg('apps.run.page.needConsent'),
        fix: { label: msg('apps.run.page.chooseAnotherVoice'), action: 'voice' },
        focusId: 'app-input-voice',
      });
    }
    // The linter's source and voice-consent findings are the same facts the page already states
    // in its own words, so they are not listed twice.
    const dropped = new Set<string>();
    if (local.some((i) => i.key.startsWith('slot:'))) dropped.add('WL010');
    if (local.some((i) => i.key === 'voice:consent')) dropped.add('WL008');
    if (local.some((i) => i.key === 'voice:provider')) dropped.add('WL006');
    if (local.some((i) => i.key.startsWith('input:missing:'))) dropped.add('WL028');
    // The permission block is the one place this page talks about sending media: the linter's sentence is not repeated.
    if (permission) dropped.add('WL015');
    const fromLint = itemsFromFindings(
      (lint?.findings ?? []).filter((f) => !dropped.has(f.ruleId)),
      slots.map((s) => s.role),
      lintT,
    );
    // Footage the run would refuse: the same sentence as the refusal itself, before Run is pressed.
    const sourceItems: ReadinessItem[] = (lint?.sourceProblems ?? []).map((problem, i) => ({
      key: `source:${problem.code}:${problem.fileName ?? ''}:${i}`,
      level: 'fix' as const,
      message: sourceProblemMessage(problem, tr),
      fix: { label: msg('apps.run.page.addOne', { role: 'video' }), action: 'upload' as FixAction, role: 'video' as AppFileRole },
    }));
    return sortItems([...local, ...sourceItems, ...fromLint]);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [lint, narrated, voiceId, voices, form.speechNotice, form.missingBrief, chosen, files, slots.length, avatarsOn, avatars, avatarId, missingInput, showConfirmEmail]);

  // --- price: only the resolution changes the numbers, so only it is part of the request ---------
  // An app with its own inputs is priced with them (`POST .../estimate`): a model, a length, a shape or a branch switch
  // re-prices after a short pause; typing text does not (docs/design/app-inputs.md 8.6).
  const pricingInputs = hasAppInputs ? pricingValues(inputs, inputValues) : undefined;
  const pricingKey = hasAppInputs
    ? JSON.stringify({
        brief: { ...(pricingInputs ? { inputs: pricingInputs } : {}), ...(brief.format ? { format: brief.format } : {}), ...(brief.sourceFiles ? { sourceFiles: brief.sourceFiles } : {}) },
        ...(form.outputResolution ? { outputResolution: form.outputResolution } : {}),
        ...(videoIds ? { videoFileIds: videoIds.split(',') } : {}),
      })
    : null;
  const [debouncedPricingKey] = useDebouncedValue(pricingKey, 400);
  const byGet = useWorkflowCostEstimate(
    hasAppInputs ? undefined : projectId,
    hasAppInputs ? undefined : workflowId,
    form.outputResolution,
    videoIds ? videoIds.split(',') : undefined,
  );
  const byBrief = useWorkflowCostEstimateWithBrief(
    projectId,
    workflowId,
    hasAppInputs && debouncedPricingKey ? JSON.parse(debouncedPricingKey) : null,
  );
  const { data: estimate, error: estimateError, mutate: mutateEstimate } = hasAppInputs ? byBrief : byGet;
  const price = estimate ? priceLines(estimate, (n) => formatCreditAmount(n, { locale: fm.locale }), app.costClass) : { label: msg('apps.run.price.run'), range: null };
  const shortfall = estimate?.billingEnabled && !estimate.affordable;
  // A Trial workspace cannot buy top-ups (the server says so); its way forward is a plan.
  const { data: billingSummary } = useBillingSummary(Boolean(estimate?.billingEnabled));
  const onTrial = billingSummary?.topUp?.available === false;
  const creditCta = onTrial ? t('choosePlan') : t('addCredits');

  // --- runs ---------------------------------------------------------------------------------------
  const { data: executions, mutate: mutateExecutions } = useExecutions(projectId, workflowId);
  const latest = newestFirst(executions)[0];
  // The poll only needs each step's status and timing, so it asks for the summary detail. It runs only
  // while the latest run is in flight (SWR does not poll a hidden tab by default), and slows down once
  // the run has sat unchanged for a minute.
  const changeRef = useRef({ key: '', at: 0 });
  const { data: detail, mutate: mutateDetail } = useSWR<WorkflowExecution>(
    latest && isActive(latest) ? `/api/v1/projects/${projectId}/executions/${latest.id}?detail=summary` : null,
    fetcher,
    {
      refreshWhenHidden: false,
      refreshInterval: (data) => {
        const key = data ? `${data.id}:${data.status}:${(data.stepResults ?? []).map((r) => r.status).join(',')}` : '';
        if (changeRef.current.key !== key) changeRef.current = { key, at: Date.now() };
        return pollInterval({
          active: !data || isActive(data),
          unchangedForMs: Date.now() - changeRef.current.at,
        });
      },
    },
  );
  // The detail only stands in for the list while both agree the run is going: a run the list still calls
  // Queued or Running is never presented as finished (no "Your video is ready" on a run still improving it).
  const execution = latest && isActive(latest) && detail?.id === latest.id && isActive(detail) ? detail : latest;
  // The overlay report is written into the finished compile's step output, which the run list does
  // not carry. Apps with the picture slot read it from the same execution endpoint the page
  // already uses; every other app makes no extra request.
  const hasOverlayInputs = slots.some(({ role }) => role === 'overlay') || Boolean(overlayCardAudioInput);
  const { data: finishedDetail } = useSWR<WorkflowExecution>(
    hasOverlayInputs && latest?.status === 'Passed'
      ? `/api/v1/projects/${projectId}/executions/${latest.id}`
      : null,
    fetcher,
    { revalidateOnFocus: false },
  );
  const overlayStepResults = finishedDetail && latest && finishedDetail.id === latest.id
    ? finishedDetail.stepResults
    : latest?.status === 'Passed'
      ? execution?.stepResults ?? []
      : [];
  // The summary poll sees a run finish before the (slower) list does: when it does, refresh the list so
  // the run history card stops saying Queued or Running.
  const finishedRunId = detail && !isActive(detail) && latest?.id === detail.id && isActive(latest) ? detail.id : null;
  useEffect(() => {
    if (finishedRunId) void mutateExecutions();
  }, [finishedRunId, mutateExecutions]);
  const running = isActive(execution);
  const [busy, setBusy] = useState(false);
  // The brief is only an error once the person has left the box or pressed Run on an empty one.
  const [briefTouched, setBriefTouched] = useState(false);
  const [retryError, setRetryError] = useState<string | null>(null);
  const [insufficient, setInsufficient] = useState<InsufficientCredits | null>(null);
  const [planRefusal, setPlanRefusal] = useState<{ message: string; key: string; onResolution: boolean; refusal: PlanRefusal | null; plain?: boolean } | null>(null);
  const [regenOpened, setRegenOpened] = useState(false);
  const [addSignal, setAddSignal] = useState(0);
  const [why, setWhy] = useState(false);
  const resultRef = useRef<HTMLDivElement>(null);
  const openedRef = useRef(false);
  const openedUserKey = userKeyFromEmail(user?.email);
  useEffect(() => {
    // Once per mount; waits for the user to be known (the sign-in may resolve after mount).
    if (openedRef.current || !openedUserKey) return;
    openedRef.current = true;
    emitFunnelEvent('alpha.run_page_opened', openedUserKey);
  }, [openedUserKey]);
  const rootRef = useRef<HTMLDivElement>(null);
  // The bottom bar's real height drives the scroll margin, so a focused field never hides under it.
  const barRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const bar = barRef.current;
    const root = rootRef.current;
    if (!bar || !root || typeof ResizeObserver === 'undefined') return;
    const apply = () => root.style.setProperty('--wa-bar-h', `${bar.offsetHeight}px`);
    apply();
    const observer = new ResizeObserver(apply);
    observer.observe(bar);
    return () => observer.disconnect();
  }, []);
  const estimateGate = useEstimateGate(projectId);
  const videoInfo = getVideoGenerateInfo(workflow.steps);

  /**
   * An app-input refusal of the run gate, placed next to its field (which takes focus); false when it is not one. For Retry and
   * Resume (`fromRun`) the refused value is the run's own: the form takes the run's values back so it can be changed in place.
   */
  const refuseOnField = (err: unknown, fromRun = false): boolean => {
    const onField = err instanceof ApiError && err.status === 400 ? fieldRefusalOf(err.body, err.code) : null;
    if (!onField || !inputs.some((i) => i.kind === 'input' && i.id === onField.inputId)) return false;
    if (fromRun && execution?.brief) setInputValues((current) => valuesFromBrief(inputs, execution.brief?.inputs, current));
    setFieldRefusal(fromRun ? { ...onField, fromRun: true } : onField);
    focusInput(onField.inputId);
    return true;
  };

  /** A run refused because the address is not confirmed: the notice next to Run says so and takes focus. */
  const refuseUnconfirmed = (err: unknown): boolean => {
    if (!isEmailNotVerified(err)) return false;
    setEmailRefused(true);
    window.setTimeout(focusConfirmEmailNotice, 0);
    return true;
  };

  const refuse = (err: unknown, fallback: string) => {
    if (refuseUnconfirmed(err)) return;
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
    setRetryError(describeError(tr, err, fallback));
  };

  // One start at a time: covers the confirm dialog, the Run button and the estimate-gate resend.
  const startingRef = useRef(false);
  const acceptRef = useRef<number | undefined>(undefined);
  const doRun = async (regenerate: boolean, accept?: number): Promise<void> => {
    if (startingRef.current) return;
    startingRef.current = true;
    setBusy(true);
    setInsufficient(null);
    setPlanRefusal(null);
    setRetryError(null);
    try {
      // The brief carries everything (style, shape, resolution, notes), so no legacy member rides along.
      const started = await executeWorkflow(projectId, workflowId, null, regenerate, {}, accept, undefined, brief);
      emitFunnelEvent('alpha.first_run', userKeyFromEmail(user?.email));
      void mutateExecutions((list) => prepend(list, started), { revalidate: true });
      // On a phone the result is below the inputs: bring it into view.
      resultRef.current?.scrollIntoView?.({ block: 'start' });
    } catch (err: unknown) {
      // The "accept this estimate" prompt (409 ESTIMATE_NOT_ACCEPTED) is a confirmation, not a refusal.
      if (!(err instanceof ApiError && err.code === 'ESTIMATE_NOT_ACCEPTED')) {
        emitFunnelEvent('alpha.run_blocked', userKeyFromEmail(user?.email), runBlockDetailOf(err));
      }
      if (estimateGate.intercept(err, (accepted) => doRun(regenerate, accepted))) return;
      // An app input the run gate refused: the sentence goes next to that field, which takes focus.
      if (refuseOnField(err)) return;
      // The address is not confirmed: the notice next to Run says so, with the resend, instead of a passing toast.
      if (refuseUnconfirmed(err)) return;
      const refused = insufficientCredits(err);
      if (refused) {
        setInsufficient(refused);
        void mutateEstimate();
      } else if (err instanceof ApiError && err.status === 400 && err.code && err.code in START_REFUSALS) {
        // A step that cannot run (no video, no avatar, a model its provider lacks): the reason sits next to Run (in the active
        // language) until the choice changes.
        const body = typeof err.body === 'object' && err.body !== null ? (err.body as Record<string, unknown>) : {};
        const step = typeof body.stepLabel === 'string' ? body.stepLabel.trim() : '';
        const [withStep, withoutStep] = START_REFUSALS[err.code];
        setPlanRefusal({
          message: step ? t(withStep, { step }) : t(withoutStep),
          key: refusalKey(brief, false),
          onResolution: false,
          plain: true,
          refusal: null,
        });
      } else if (err instanceof ApiError && err.status === 402) {
        // A plan refusal: the sentence from the server, and the allowed alternative (Auto) preselected.
        // Run waits until the person confirms that choice or changes something else.
        const onResolution = refusalFeature(err) === 'resolution';
        setPlanRefusal({
          message: err.message || t('planRefusal'),
          key: refusalKey(brief, onResolution),
          onResolution,
          refusal: planRefusalOf(err),
        });
        // Only a refused resolution is reset; for AI shots or audio generation the person changes that choice.
        if (onResolution) form.setResolution('Auto');
      } else {
        notifications.show({ title: t('errorTitle'), message: sourceRefusalMessage(err, tr) ?? describeError(tr, err), color: 'red' });
      }
    } finally {
      startingRef.current = false;
      setBusy(false);
    }
  };

  const [confirmOpen, setConfirmOpen] = useState(false);
  const run = () => {
    emitFunnelEvent('alpha.run_clicked', userKeyFromEmail(user?.email));
    if (needsConfirm(estimate, app.costClass)) setConfirmOpen(true);
    else startRun();
  };
  // `accept` is the figure the confirm dialog showed (its maximum), sent so the server's own
  // acceptance gate does not ask a second time; the server stays authoritative and re-asks if it needs more.
  const startRun = (accept?: number) => {
    acceptRef.current = accept;
    if (videoInfo.has && (executions?.length ?? 0) > 0) setRegenOpened(true);
    else void doRun(false, accept);
  };

  const doRetry = async (accept?: number): Promise<void> => {
    if (!execution) return;
    setBusy(true);
    setRetryError(null);
    try {
      const started = await retryExecution(projectId, workflowId, execution.id, false, accept);
      void mutateExecutions((list) => prepend(list, started), { revalidate: true });
    } catch (err: unknown) {
      if (estimateGate.intercept(err, (accepted) => doRetry(accepted))) return;
      if (refuseOnField(err, true)) return;
      refuse(err, t('retryFailed'));
    } finally {
      setBusy(false);
    }
  };

  const doResume = async (accept?: number): Promise<void> => {
    if (!execution) return;
    setBusy(true);
    setRetryError(null);
    try {
      const started = await resumeExecution(projectId, workflowId, execution.id, accept);
      void mutateExecutions((list) => prepend(list, started), { revalidate: true });
    } catch (err: unknown) {
      if (estimateGate.intercept(err, (accepted) => doResume(accepted))) return;
      if (refuseOnField(err, true)) return;
      refuse(err, t('resumeFailed'));
    } finally {
      setBusy(false);
    }
  };

  const doStop = async (): Promise<void> => {
    if (!execution) return;
    setBusy(true);
    try {
      await stopExecution(projectId, workflowId, execution.id);
      void mutateExecutions();
      void mutateDetail();
    } catch {
      notifications.show({ title: t('errorTitle'), message: t('stopFailed'), color: 'red' });
    } finally {
      setBusy(false);
    }
  };

  // --- fixes and prefill --------------------------------------------------------------------------
  const runFix = (item: ReadinessItem) => {
    const action = item.fix?.action;
    if (!action) return;
    switch (action) {
      case 'upload': {
        const role = item.fix?.role ?? slots[0]?.role;
        if (role && slots.some((s) => s.role === role)) {
          setAddRole({ role, signal: addSignal + 1 });
          setAddSignal((n) => n + 1);
        } else {
          // No slot for this kind of file here: the project's Files tab is where it is added.
          router.push(`/projects/${projectId}?tab=files`);
        }
        return;
      }
      case 'voice': {
        const select = document.getElementById('app-input-voice');
        if (select && (voices?.length ?? 0) > 0) select.focus();
        else router.push(`/projects/${projectId}?tab=voices`);
        return;
      }
      case 'notes':
        document.getElementById('app-input-notes')?.focus();
        return;
      case 'email':
        focusConfirmEmailNotice();
        return;
      case 'input': {
        const inputId = item.fix?.inputId;
        if (inputId) focusInput(inputId);
        return;
      }
      case 'model': {
        const inputId = item.fix?.inputId ?? inputs.find((i) => i.kind === 'input' && i.type === 'model')?.id;
        if (!inputId) return;
        // On a phone the cards open in their sheet; on a desktop the first card takes focus.
        setModelSignal((prev) => ({ inputId, signal: (prev?.signal ?? 0) + 1 }));
        focusInput(inputId);
        return;
      }
      case 'plan':
        router.push(BILLING_SETTINGS_PATH);
        return;
      case 'design':
        router.push(`/projects/${projectId}?tab=profile`);
        return;
      case 'providers':
        router.push('/admin/inference-providers');
        return;
      case 'advanced':
        router.push(`/projects/${projectId}/workflows/${workflowId}?view=advanced`);
        return;
    }
  };

  const upload = async (role: AppFileRole, file: File) => {
    setUploadingRole(role);
    try {
      const saved = await uploadFile(projectId, file);
      await mutateFiles();
      const slot = slots.find((s) => s.role === role);
      setChosen((prev) => ({ ...prev, [role]: [...selectedFor(role, slot?.input.max), saved.id] }));
    } catch (err: unknown) {
      notifications.show({ title: t('uploadFailedTitle'), message: describeError(tr, err), color: 'red' });
    } finally {
      setUploadingRole(null);
    }
  };

  // "Change one thing" as a change request (docs/design/app-inputs.md 25): for a finished app run whose fields can be
  // changed, a dialog re-makes only what a changed field uses; otherwise (switch off, no app fields) the form is pre-filled.
  // Changing one field of a finished run is for an app's own fields; the permission belongs to the run it was given to.
  const hasOwnInputs = inputs.some((i) => isAppInput(i) && !isPermissionInput(i));
  const changeRunId = hasOwnInputs && execution?.status === 'Passed' ? execution.id : null;
  const { data: changeCaps } = useChangeCapabilities(projectId, changeRunId);
  const changeFields = changeRunId && canChangeFields(changeCaps?.appInputs) ? changeCaps!.appInputs! : null;
  const [changeOpen, setChangeOpen] = useState(false);
  const changeOne = () => (changeFields ? setChangeOpen(true) : prefillFromLastRun());

  /** "Change one thing and re-run": the form takes the previous run's brief back. */
  const prefillFromLastRun = () => {
    const previous = execution?.brief;
    if (!previous && execution?.userRequest) form.setNotes(execution.userRequest);
    if (previous) {
      if (languageInput) setLanguage(previous.language ?? 'auto');
      if (sourceLanguageInput) setSourceLanguage(previous.sourceLanguage ?? 'auto');
      if (lengthInput && typeof previous.lengthSec === 'number') setLengthSec(previous.lengthSec);
      if (voiceInput && previous.voice) {
        setNarrated(previous.voice.mode !== 'None');
        setVoiceId(previous.voice.projectVoiceId ?? null);
      }
      if (designInput) setDesign(previous.designSystem === 'none' ? 'none' : 'project');
      if (overlayCardAudioInput) setOverlayCardAudio(previous.overlayCardAudio ?? 'agent');
      if (avatarInput) setAvatarId(previous.avatar?.avatarId ?? null);
      if (hasAppInputs) setInputValues((current) => valuesFromBrief(inputs, previous.inputs, current));
      if (paceInput) setPaceChoice(paceValueOf(previous.pace) ?? 'off');
      form.changeStyle(previous.stylePreset ? `preset:${previous.stylePreset}` : previous.styleId ? `style:${previous.styleId}` : NO_STYLE);
      if (previous.format) form.changeFormat(previous.format as RunOutputFormat);
      form.setResolution((previous.resolution as RunResolutionChoice | null | undefined) ?? 'Auto');
      // The API returns the notes as the run's request text, not inside the brief.
      form.setNotes(previous.notes ?? execution?.userRequest ?? '');
      const next: Partial<Record<AppFileRole, string[]>> = {};
      for (const group of previous.sourceFiles ?? []) {
        const role = appRoleOf(group.role);
        if (role) next[role] = group.fileIds ?? [];
      }
      setChosen((prev) => ({ ...prev, ...next }));
    }
    document.getElementById('app-run-inputs')?.focus();
  };

  /** "Try another shape": this run's choices come back and the Shape choice takes focus; Run starts the new one. */
  const tryShape = () => {
    prefillFromLastRun();
    const underMore = layout.more.some((i) => i.kind === 'brief' && i.field === 'format');
    if (underMore && !moreOpen) setMoreOpen(true);
    window.setTimeout(() => {
      const field = document.getElementById('app-input-format');
      field?.scrollIntoView?.({ block: 'center' });
      (field?.querySelector<HTMLElement>('input:checked') ?? field?.querySelector<HTMLElement>('input'))?.focus();
    }, underMore && !moreOpen ? 50 : 0);
  };

  const askAssistant = () =>
    openWithPrompt(t('changePrompt'), {
      projectId,
      workflowId,
      ...(execution ? { executionId: execution.id } : {}),
    });

  // Failed insert run: bring the person to the slot that needs changing (the sheet on a phone, the list otherwise).
  const chooseAnotherClip = (role: 'video' | 'insertContent') => {
    const el = document.getElementById(`app-input-files-${role}`);
    el?.scrollIntoView({ block: 'center' });
    el?.focus();
    if (phone) {
      setAddRole({ role, signal: addSignal + 1 });
      setAddSignal((n) => n + 1);
    }
  };

  // --- what blocks Run ----------------------------------------------------------------------------
  const firstError = items.find((i) => i.level === 'fix');
  let block: RunBlock | null = null;
  if (firstError) {
    block = {
      key: firstError.key,
      reason: resolveText(t, firstError.message),
      fixLabel: firstError.fix ? resolveText(t, firstError.fix.label) : undefined,
      onFix: firstError.fix ? () => runFix(firstError) : undefined,
      focusId: firstError.focusId,
      ...(firstError.fix?.inputId
        ? {
            onFocus: () => {
              if (firstError.fix!.inputId === permission?.id) setPermissionAsked(true);
              focusInput(firstError.fix!.inputId!);
            },
          }
        : {}),
    };
  } else if (form.missingBrief) {
    // One plain reason, not an error: the red message lives on the field and only after blur or a press.
    block = {
      key: 'notes:required',
      reason: t('describeAbove'),
      onFix: () => {
        setBriefTouched(true);
        document.getElementById('app-input-notes')?.focus();
      },
      focusId: 'app-input-notes',
    };
  } else if (planRefusal && planRefusal.key === refusalKey(brief, planRefusal.onResolution)) {
    block = {
      key: 'plan',
      reason: planRefusal.message,
      ...(planRefusal.onResolution ? { fixLabel: t('useAuto'), onFix: () => setPlanRefusal(null) } : {}),
      ...(planRefusal.plain ? {} : { upgrade: { feature: planRefusal.refusal?.feature ?? null, planKey: planRefusal.refusal?.unlockPlanKey ?? null, planName: planRefusal.refusal?.unlockPlanName ?? null } }),
    };
  }

  // Not enough credits stands beside any other blocker, so a first-time user sees it before fixing the rest.
  const shortfallNote: RunShortfall | null = shortfall && estimate
    ? {
        reason: t('creditsNeeded', {
          needed: formatCreditAmount(roundUpEstimate(estimate.requiredCredits), { locale: fm.locale }),
          have: formatCreditAmount(estimate.balanceCredits ?? 0, { locale: fm.locale }),
          unit: formatCreditAmount(estimate.balanceCredits ?? 0, { locale: fm.locale }) === '1' ? 'one' : 'other',
        }),
        links: [{ label: creditCta, href: BILLING_SETTINGS_PATH }],
      }
    : null;
  const balanceText = estimate?.billingEnabled && estimate.balanceCredits !== undefined && estimate.balanceCredits !== null
    ? t('balance', {
        have: formatCreditAmount(estimate.balanceCredits, { locale: fm.locale }),
        unit: formatCreditAmount(estimate.balanceCredits, { locale: fm.locale }) === '1' ? 'one' : 'other',
      })
    : null;

  // A press on Run while it is blocked is the "blocked" signal; the closed token never carries the reason text.
  const reportBlockedPress = (key: string) => {
    const detail = runBlockDetailFromKey(key);
    if (detail) emitFunnelEvent('alpha.run_blocked', userKeyFromEmail(user?.email), detail);
  };

  const resultKind = manifest.result?.kind ?? 'video';
  const needs = slots.map(({ input, role }) => input.label?.trim() || t('yourFilesLower', { role }));
  const lines = execution ? progressLines(execution, execution.steps ?? workflow.steps, agentTypeOf, tr) : [];
  const lintSaysSilent = (lint?.findings ?? []).some((f) => f.ruleId === 'WL016');

  const voiceData = [
    { value: '', label: t('defaultVoice') },
    ...(voices ?? []).map((v) => ({ value: v.id, label: v.selectable ? v.name : t('consentNeeded', { name: v.name }), disabled: !v.selectable })),
  ];

  // A green-screen run (a screen-content slot, and for "Effects on a clip" the screen choice) needs a clip with a flat-coloured screen: say so before the run.
  const needsScreenInClip = inputs.some((i) => i.kind === 'files' && i.role === 'insertContent')
    && (!inputs.some((i) => i.kind === 'input' && i.id === 'effect') || inputValues.effect === 'screen');

  const renderInput = (input: AppManifestInput, index: number) => {
    const label = inputLabel(tr, input);
    if (input.kind === 'input') {
      // Hidden by `showWhen`: not rendered at all, so it leaves the tab order too.
      if (!isAppInput(input) || !isVisible(input, inputValues)) return null;
      const refusal = fieldRefusal?.inputId === input.id
        ? tr.msg(refusalMessage(fieldRefusal.code)) + (fieldRefusal.fromRun ? ` ${t('retryFieldNote')}` : '')
        : undefined;
      return (
        <AppInputField
          key={`in-${input.id}`}
          input={input}
          value={inputValues[input.id] ?? null}
          onChange={(value) => setInputValue(input.id, value)}
          error={refusal}
          projectId={projectId}
          workflowId={workflowId}
          phone={phone}
          openSignal={modelSignal?.inputId === input.id ? modelSignal.signal : 0}
        />
      );
    }
    if (input.kind === 'notes') {
      const box = (
        <Textarea
          key={index}
          id="app-input-notes"
          label={label || t('anythingElse')}
          // One line on what to write, in the person's own terms, where the box must be filled.
          description={briefFamily ? tr.msg(briefPrompt(briefFamily, persona)) : undefined}
          minRows={2}
          autosize
          maxRows={8}
          value={form.notes}
          onChange={(e) => form.setNotes(e.currentTarget.value)}
          onBlur={() => setBriefTouched(true)}
          error={briefTouched && form.missingBrief ? t('tellWhatToMake') : undefined}
          required={requiresNotes}
          className={styles.field}
        />
      );
      if (!briefFamily) return box;
      return (
        <div key={index} className={styles.inputs}>
          {box}
          <BriefExamples
            family={briefFamily}
            persona={persona}
            value={form.notes}
            onInsert={(next) => {
              form.setNotes(next);
              // The example is a start, not an answer: the box takes focus so it is edited at once.
              document.getElementById('app-input-notes')?.focus();
            }}
          />
        </div>
      );
    }
    if (input.kind === 'files') {
      const role = appRoleOf(input.role);
      if (!role) return null;
      return (
        <FileSlot
          key={index}
          projectId={projectId}
          input={input}
          role={role}
          files={files ?? []}
          selected={selectedFor(role, input.max)}
          phone={phone}
          uploading={uploadingRole === role}
          onChange={(ids) => setChosen((prev) => ({ ...prev, [role]: ids }))}
          onUpload={(file) => void upload(role, file)}
          inputId={`app-input-files-${role}`}
          addSignal={addRole?.role === role ? addRole.signal : 0}
          hint={role === 'video' && needsScreenInClip
            ? { text: t('screenClipNeed'), linkText: t('screenClipNeedLink'), href: docsPageHref('recipes/tracked-screen-insert#which-clip-works', fm.locale) }
            : undefined}
        />
      );
    }
    switch (input.field) {
      case 'language':
        return (
          <Select
            key={index}
            id="app-input-language"
            label={label || t('language')}
            data={APP_LANGUAGES.map((l) => ({ value: l.value, label: resolveText(t, l.label) }))}
            value={language}
            onChange={(v) => setLanguage(v ?? 'auto')}
            allowDeselect={false}
            comboboxProps={{ withinPortal: true }}
            className={styles.field}
          />
        );
      case 'sourceLanguage':
        return (
          <Select
            key={index}
            id="app-input-source-language"
            label={label || t('spokenLanguage')}
            data={SOURCE_LANGUAGES.map((l) => ({ value: l.value, label: resolveText(t, l.label) }))}
            value={sourceLanguage}
            onChange={(v) => setSourceLanguage(v ?? 'auto')}
            allowDeselect={false}
            comboboxProps={{ withinPortal: true }}
            className={styles.field}
          />
        );
      case 'lengthSec': {
        const choices = (input.choices ?? []).filter((c): c is number => typeof c === 'number');
        return choices.length > 0 ? (
          <div key={index} className={styles.field}>
            <span className={styles.fieldLabel} id="app-input-length-label">
              {label || t('length')}
            </span>
            <SegmentedControl
              aria-labelledby="app-input-length-label"
              className={styles.segments}
              fullWidth
              value={String(lengthSec ?? choices[0])}
              onChange={(v) => setLengthSec(Number(v))}
              data={choices.map((c) => ({ value: String(c), label: t('seconds', { count: c }) }))}
            />
          </div>
        ) : (
          <NumberInput
            key={index}
            id="app-input-length"
            label={label || t('lengthSeconds')}
            min={LENGTH_MIN_SEC}
            max={LENGTH_MAX_SEC}
            step={5}
            value={lengthSec ?? ''}
            onChange={(v) => setLengthSec(typeof v === 'number' ? v : null)}
            className={styles.field}
          />
        );
      }
      case 'format':
        return (
          <div key={index} id="app-input-format" className={`${styles.field} ${styles.segments}`}>
            <RunStylePicker controls={['shape']} format={form.format} onFormatChange={form.changeFormat} formatNote={form.formatFollowsClips ? t('shapeFollowsClips') : undefined} />
          </div>
        );
      case 'resolution':
        return (
          <div key={index} className={`${styles.field} ${styles.segments}`}>
            <RunStylePicker controls={['resolution']} resolution={form.resolution} onResolutionChange={(r) => { setPlanRefusal(null); form.setResolution(r); }} maxOutputHeight={form.maxOutputHeight} />
          </div>
        );
      case 'stylePreset':
      case 'styleId':
      case 'style':
        return (
          <div key={index} className={styles.field}>
            <RunStylePicker controls={['style']} presets={form.presets} styles={form.styles} style={form.style} onStyleChange={form.changeStyle} />
          </div>
        );
      case 'voice':
        return (
          <div key={index} className={styles.field}>
            <Switch
              label={label || t('narration')}
              description={narrated ? t('narrationOn') : t('narrationOff')}
              checked={narrated ?? false}
              onChange={(e) => setNarrated(e.currentTarget.checked)}
              styles={{ root: { minHeight: 44, display: 'flex', alignItems: 'center' } }}
            />
            {narrated && (
              <Select
                id="app-input-voice"
                aria-label={t('voice')}
                mt="xs"
                data={voiceData}
                value={voiceId ?? ''}
                onChange={(v) => setVoiceId(v ? v : null)}
                allowDeselect={false}
                comboboxProps={{ withinPortal: true }}
              />
            )}
          </div>
        );
      case 'designSystem':
        return (
          <Select
            key={index}
            id="app-input-design"
            label={label || t('design')}
            data={[
              { value: 'project', label: t('designProject') },
              { value: 'none', label: t('designNone') },
            ]}
            value={design ?? 'project'}
            onChange={(v) => setDesign(v ?? 'project')}
            allowDeselect={false}
            comboboxProps={{ withinPortal: true }}
            className={styles.field}
          />
        );
      case 'overlayCardAudio':
        return (
          <Select
            key={index}
            id="app-input-overlay-card-audio"
            label={label || t('overlayCardAudio')}
            data={[
              { value: 'agent', label: t('overlayCardAudioAgent') },
              { value: 'keep', label: t('overlayCardAudioKeep') },
              { value: 'mute', label: t('overlayCardAudioMute') },
            ]}
            value={overlayCardAudio}
            onChange={(v) => setOverlayCardAudio(v ?? 'agent')}
            allowDeselect={false}
            comboboxProps={{ withinPortal: true }}
            className={styles.field}
          />
        );
      case 'avatar':
        return avatarsOn ? (
          <AvatarRunInput
            key={index}
            projectId={projectId}
            label={label}
            avatars={avatars}
            value={avatarId}
            onChange={setAvatarId}
            className={styles.field}
          />
        ) : null;
      case 'pace':
        return (
          <div key={index} className={styles.field}>
            <span className={styles.fieldLabel} id="app-input-pace-label">
              {label || t('pace')}
            </span>
            <SegmentedControl
              aria-labelledby="app-input-pace-label"
              className={styles.segments}
              fullWidth
              value={pace ?? 'off'}
              onChange={(v) => setPaceChoice(paceValueOf(v) ?? 'off')}
              data={[
                { value: 'off', label: t('paceOff'), disabled: stylePace !== null },
                { value: 'calm', label: t('paceCalm') },
                { value: 'medium', label: t('paceMedium') },
                { value: 'fast', label: t('paceFast') },
              ]}
            />
            <Text size="xs" c="dimmed" mt={4} id="app-input-pace-help">
              {pace === 'calm' ? t('paceHelpCalm') : pace === 'fast' ? t('paceHelpFast') : pace === 'medium' ? t('paceHelpMedium') : t('paceHelpOff')}
            </Text>
          </div>
        );
      default:
        return null;
    }
  };

  return (
    <div ref={rootRef} className={styles.root}>
      <AssistantPageContext projectId={projectId} workflowId={workflowId} />
      <PageHeader
        title={title}
        breadcrumbs={[
          { label: t('breadcrumbProjects'), href: '/projects' },
          { label: projectName ?? t('projectFallback'), href: `/projects/${projectId}` },
          { label: title },
        ]}
      >
        <ShareAsRecipeButton projectId={projectId} workflowId={workflowId} />
        {manifest.advanced?.allowGraph !== false && (
          <Menu position="bottom-end" withinPortal>
            <Menu.Target>
              <ActionIcon variant="default" size={44} aria-label={t('moreOptions')}>
                <IconDots size={18} />
              </ActionIcon>
            </Menu.Target>
            <Menu.Dropdown>
              <Menu.Item component={Link} href={`/projects/${projectId}/workflows/${workflowId}?view=advanced`}>
                {t('editSteps')}
              </Menu.Item>
            </Menu.Dropdown>
          </Menu>
        )}
      </PageHeader>

      {manifest.tagline && (
        <Text size="sm" className={styles.muted} mb="md">
          {manifest.tagline}
        </Text>
      )}

      {insufficient && onTrial ? (
        <Alert color="yellow" variant="light" title={t('trialTitle')} role="alert" data-testid="insufficient-credits-trial">
          <Text size="sm">{t('trialBody')}</Text>
          <Button component={Link} href={BILLING_SETTINGS_PATH} size="compact-sm" variant="light" mt="xs">
            {t('choosePlan')}
          </Button>
        </Alert>
      ) : (
        insufficient && <InsufficientCreditsAlert data={insufficient} onDismiss={() => setInsufficient(null)} />
      )}

      {/* On a phone the bar is a short sticky strip, so the notice stands above the form instead. */}
      {showConfirmEmail && phone && user && <ConfirmEmailNotice email={user.email} refused={emailRefused} />}

      <div className={styles.layout}>
        <div className={styles.inputs}>
          <div id="app-run-inputs" tabIndex={-1} className={styles.inputs} role="group" aria-label={t('choices')}>
            {layout.shown.map(renderInput)}
            {layout.more.length > 0 && (
              <>
                <Button
                  variant="subtle"
                  className={styles.moreToggle}
                  onClick={toggleMore}
                  aria-expanded={moreOpen}
                  aria-controls="app-run-more"
                  rightSection={<IconChevronDown size={16} style={{ transform: moreOpen ? 'rotate(180deg)' : undefined }} aria-hidden />}
                  data-testid="app-run-more-toggle"
                >
                  {tr('apps.run.inputs.moreOptions', { count: layout.more.length })}
                </Button>
                <Collapse expanded={moreOpen}>
                  <div id="app-run-more" className={styles.moreBody}>
                    {moreOpen && layout.more.map(renderInput)}
                  </div>
                </Collapse>
              </>
            )}
          </div>
          {permission && (
            <PermissionBlock
              input={permission}
              egress={app.mediaEgress}
              required={permissionNeeded}
              checked={inputValues[permission.id] === true}
              onChange={(checked) => setInputValue(permission.id, checked)}
              error={
                fieldRefusal?.inputId === permission.id
                  ? tr.msg(refusalMessage(fieldRefusal.code)) + (fieldRefusal.fromRun ? ` ${t('retryFieldNote')}` : '')
                  : permissionAsked && missingInput?.id === permission.id
                    ? tr('apps.permission.mediaEgress.required')
                    : undefined
              }
            />
          )}
        </div>

        <div ref={barRef} className={styles.bar} data-testid="run-bar">
          {showConfirmEmail && !phone && user && <ConfirmEmailNotice email={user.email} refused={emailRefused} />}
          <ReadinessStrip items={items} unavailable={Boolean(lintError) && !lint} checking={!lint && !lintError} compact={phone} onFix={runFix} />
          {form.silentVideo && !lintSaysSilent && <SilentRunNotice />}
          <RunBar
            label={t.msg(price.label)}
            range={price.range ? t.msg(price.range) : null}
            balance={balanceText}
            shortfall={shortfallNote}
            priceUnavailable={Boolean(estimateError)}
            onRetryPrice={() => void mutateEstimate()}
            block={block}
            showReason={!(firstError && block && block.key === firstError.key)}
            phone={phone}
            busy={busy}
            running={running}
            runningText={running ? t('beingMade') : null}
            onStop={() => void doStop()}
            showBreakdown={Boolean(estimate?.billingEnabled)}
            why={why}
            onToggleWhy={() => setWhy((v) => !v)}
            onRun={run}
            onBlockedPress={reportBlockedPress}
          />
        </div>

        <div className={styles.results}>
          <Collapse expanded={why && Boolean(estimate?.billingEnabled)}>
            <RunCostEstimate
              projectId={projectId}
              workflowId={workflowId}
              {...(form.outputResolution ? { outputResolution: form.outputResolution } : {})}
              {...(hasAppInputs && estimate ? { estimate } : {})}
            />
          </Collapse>
          <div ref={resultRef} style={{ scrollMarginTop: 8 }}>
          <RunResult
            projectId={projectId}
            workflowId={workflowId}
            resultKind={resultKind}
            execution={execution}
            executions={executions}
            billingEnabled={estimate?.billingEnabled === true}
            third={thirdStepFor({ hasClips: hasVideoSlot, hasShape: hasFormatInput })}
            onTryShape={tryShape}
            lines={lines}
            needs={needs}
            busy={busy}
            retryError={retryError}
            onRetry={() => void doRetry()}
            onResume={() => void doResume()}
            onChangeOne={changeOne}
            onAskAssistant={askAssistant}
            onChooseAnotherClip={chooseAnotherClip}
            phone={phone}
          />
          <MediaOverlaysCard stepResults={overlayStepResults} />
          </div>
          <section aria-label={t('history')}>
            <ExecutionHistory projectId={projectId} workflowId={workflowId} compact />
          </section>
        </div>
      </div>

      {estimateGate.modal}
      {changeFields && changeRunId && (
        <AppInputChangeModal
          opened={changeOpen}
          onClose={() => setChangeOpen(false)}
          projectId={projectId}
          workflowId={workflowId}
          executionId={changeRunId}
          appInputs={changeFields}
          onRunAgain={prefillFromLastRun}
          phone={phone}
        />
      )}
      <Modal opened={confirmOpen} onClose={() => setConfirmOpen(false)} title={t('confirmTitle')} centered>
        {estimate?.billingEnabled && (
          <Stack gap="sm" data-testid="run-confirm">
            <Text size="sm">{t.msg(confirmCopy(estimate, (n) => formatCreditAmount(n, { locale: fm.locale })))}</Text>
            <Text size="sm">{t('confirmBalance', { balance: formatCreditAmount(estimate.balanceCredits ?? 0, { locale: fm.locale }) })}</Text>
            <Group justify="flex-end">
              <Button variant="default" onClick={() => setConfirmOpen(false)}>
                {t('cancel')}
              </Button>
              <Button
                loading={busy}
                onClick={() => {
                  if (startingRef.current) return;
                  setConfirmOpen(false);
                  startRun(acceptTotal(estimate));
                }}
              >
                {t('run')}
              </Button>
            </Group>
          </Stack>
        )}
      </Modal>
      <RegenerateVideoClipsModal
        opened={regenOpened}
        onClose={() => setRegenOpened(false)}
        capCredits={videoInfo.capCredits}
        busy={busy}
        onChoose={(regenerate) => {
          setRegenOpened(false);
          void doRun(regenerate, acceptRef.current);
        }}
      />
    </div>
  );
}
