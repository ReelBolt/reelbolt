'use client';

import { useDeviceClass } from '@/lib/hooks/use-device-class';
import { FirstVideoGrantCallout } from '@/components/billing/FirstVideoGrantCallout';
import { PickedPlanNotice } from '@/components/billing/PickedPlanNotice';
import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import { Alert, Button, Group, Skeleton, Stack, Text } from '@mantine/core';
import { IconFolderPlus, IconWand } from '@tabler/icons-react';
import { useRouter } from 'next/navigation';
import { useT, type T } from '@/i18n';
import { useAssistantShell } from '@/components/assistant/AssistantShellProvider';
import { ProjectForm } from '@/components/projects/ProjectForm';
import { FailureCard } from '@/components/shared/FailureCard';
import { useSlow } from '@/lib/hooks/use-slow';
import { Panel } from '@/components/dashboard/Panel';
import { FirstVideoRail } from '@/components/dashboard/FirstVideoRail';
import { GettingStartedCard } from '@/components/onboarding/GettingStartedCard';
import { TourCard } from '@/components/onboarding/TourCard';
import { useOnboardingContext } from '@/components/onboarding/onboarding-context';
import { DashboardRunNudge } from '@/components/workflow-apps/RunNudge';
import { FirstFilmCard } from '@/components/onboarding/FirstFilmCard';
import { NudgeList, type Nudge } from '@/components/dashboard/NudgeButton';
import {
  ContinueProjects, LatestRenders, LiveRuns, NeedsAttention, RecentRuns, SetupChecklist,
} from '@/components/dashboard/sections';
import { loginDestination, navigateToNext } from '@/lib/auth/redirect';
import { useAuth } from '@/lib/hooks/use-auth';
import { useSignupIntent } from '@/lib/hooks/use-signup-intent';
import { useBillingSummary } from '@/lib/hooks/use-billing';
import { CHECKOUT_START_TOKEN_KEY, pickedPlanCheckoutHref } from '@/components/billing/subscription';
import { checklistFallback, mergeChecklist, useOnboarding } from '@/lib/hooks/use-onboarding';
import { shouldOfferFirstFilm } from '@/lib/onboarding/first-film';
import { WELCOME_PATH } from '@/lib/onboarding/welcome';
import { emitFunnelEvent, userKeyFromEmail } from '@/lib/telemetry';
import { useDashboardOverview, useWorkflowEvents, type LiveExecutionState } from '@/lib/hooks/use-dashboard';
import { useInferenceProviders, useSelectableProviders } from '@/lib/hooks/use-inference-providers';
import { useProjects } from '@/lib/hooks/use-projects';
import type { ProjectOverview } from '@/lib/types/dashboard';
import classes from '@/components/dashboard/dashboard.module.css';

/** How long the page waits for the onboarding state before drawing itself regardless. */
const HOLD_MS = 5000;

function greeting(hour: number, t: T) {
  if (hour < 5) return t('greeting', { part: 'night' });
  if (hour < 12) return t('greeting', { part: 'morning' });
  if (hour < 18) return t('greeting', { part: 'afternoon' });
  return t('greeting', { part: 'evening' });
}

/** After 10 s of loading: a plain "still trying" line and a Retry. Renders nothing before that. */
function SlowNotice({ active, onRetry, label, retryLabel }: { active: boolean; onRetry: () => void; label: string; retryLabel: string }) {
  const slow = useSlow(active);
  if (!slow) return null;
  return (
    <Group gap="sm" mt={6} role="status">
      <Text size="sm" c="dimmed">{label}</Text>
      <Button size="xs" variant="light" onClick={onRetry}>{retryLabel}</Button>
    </Group>
  );
}

export default function DashboardPage() {
  const tOnb = useT('onboarding.checklist');
  const t = useT('dashboard.page');
  const tFirst = useT('dashboard.firstVideo');
  const tn = useT('dashboard.nudge');
  const router = useRouter();
  const { user, isPlatformAdmin: isAdmin, isOrgAdmin } = useAuth();
  const { openWithPrompt } = useAssistantShell();
  const { data: projects, isLoading: loadingProjects, error: projectsError, mutate: mutateProjects } = useProjects();
  const { data: overview, isLoading: loadingOverview, error: overviewError, mutate: mutateOverview } = useDashboardOverview();
  const onboarding = useOnboarding();
  const { welcomeEnabled, welcome, beginWelcome } = useOnboardingContext();
  const name = user?.displayName?.trim() || (user?.email ?? '').split('@')[0];
  const isNewUser = !loadingProjects && !projectsError && (projects?.length ?? 0) === 0;

  // A slow or unreachable API must never leave this page blank: the page waits this long for what
  // it needs to choose a layout (or to leave for the welcome questions), then draws itself as it
  // always did. A welcome redirect that only becomes known after that is left for the next visit.
  const [patient, setPatient] = useState(true);
  useEffect(() => {
    const timer = window.setTimeout(() => setPatient(false), HOLD_MS);
    return () => window.clearTimeout(timer);
  }, []);

  // What the person asked for before they had an account (lib/auth/signup-intent.ts). A page they
  // were on their way to (`?next=` on the sign-up link) is where they go now, once: the provider
  // round trip and the emailed link could not carry it, and the dashboard is where both land. It
  // comes before the welcome questions, which are offered on the next visit here instead. A plan
  // they picked on the pricing page stays as a notice below until it is opened or dismissed.
  const signupIntent = useSignupIntent();
  const { clear: clearSignupIntentPart } = signupIntent;
  const nextAfterSignup = signupIntent.intent?.next ?? null;
  const [leavingForNext, setLeavingForNext] = useState(false);
  const leftForNext = useRef(false);
  useEffect(() => {
    if (!nextAfterSignup || leftForNext.current) return;
    leftForNext.current = true;
    clearSignupIntentPart('next');
    // The dashboard itself is not somewhere to leave for.
    const destination = loginDestination(nextAfterSignup);
    if (!destination.fullDocument && (destination.href === '/dashboard' || destination.href === '/')) return;
    // eslint-disable-next-line react-hooks/set-state-in-effect -- the page holds its skeleton while the browser leaves
    setLeavingForNext(true);
    navigateToNext(nextAfterSignup, router);
  }, [nextAfterSignup, clearSignupIntentPart, router]);
  // The plan survives on the server too (set at signup), so it resumes in another browser. Forgetting
  // or using it clears both copies. Only read once onboarding has settled.
  const { clearPickedPlan } = onboarding;
  const serverPickedPlan = onboarding.settled ? onboarding.profile?.pickedPlan ?? undefined : undefined;
  const forgetPickedPlan = useCallback(() => {
    clearSignupIntentPart('plan');
    void clearPickedPlan();
  }, [clearSignupIntentPart, clearPickedPlan]);

  // A plan picked on the pricing page goes straight to its checkout once, right after the first
  // sign-in (the notice below remains for what cannot: a per-seat plan, a member who cannot manage
  // billing). `?next=` has the first word; the welcome questions wait for the answer.
  const pickedPlanKey = signupIntent.intent?.plan ?? serverPickedPlan;
  const pickedSummary = useBillingSummary(!!pickedPlanKey);
  const planDecisionPending = !!pickedPlanKey && !nextAfterSignup && pickedSummary.data === undefined && !pickedSummary.error;
  const checkoutHref = nextAfterSignup || leavingForNext ? null : pickedPlanCheckoutHref(pickedPlanKey, pickedSummary.data, user?.emailVerified);
  const [leavingForCheckout, setLeavingForCheckout] = useState(false);
  const leftForCheckout = useRef(false);
  useEffect(() => {
    if (!checkoutHref || leftForCheckout.current) return;
    leftForCheckout.current = true;
    setLeavingForCheckout(true);
    // Proof for the billing page that this arrival is ours, not a link someone else made.
    try {
      window.sessionStorage.setItem(CHECKOUT_START_TOKEN_KEY, pickedPlanKey ?? '');
    } catch {
      // storage blocked: the billing page then only marks the plan
    }
    // The plan is being used: the server's copy goes too (the billing page forgets the local one).
    void clearPickedPlan();
    router.replace(checkoutHref);
  }, [checkoutHref, pickedPlanKey, router, clearPickedPlan]);

  // The one redirect to the welcome questions (the rule is `welcomeDecision`, decided by the
  // onboarding provider): a brand-new account with no project goes there once. `beginWelcome`
  // records it before the navigation, so coming back here never sends the person again.
  const [goingToWelcome, setGoingToWelcome] = useState(false);
  if (welcome === 'redirect' && patient && !goingToWelcome && signupIntent.ready && !nextAfterSignup && !leavingForNext && !leavingForCheckout && !checkoutHref && !planDecisionPending) setGoingToWelcome(true);
  const leftForWelcome = useRef(false);
  useEffect(() => {
    if (!goingToWelcome || leftForWelcome.current) return;
    leftForWelcome.current = true;
    beginWelcome();
    router.replace(WELCOME_PATH);
  }, [goingToWelcome, beginWelcome, router]);

  // The setup checklist mirrors the step editors (A17-UI): a platform admin reads the
  // platform-managed rows and looks for a default, while a workspace Owner/Admin reads what the
  // workspace can actually run on — platform rows plus its own, which is what the selectable list
  // is. Neither role may read the other's list (both are 403), so exactly one is fetched.
  //
  // A workspace Owner/Admin only acts on it with the plan's own-provider entitlement, which the
  // billing summary does not expose (its `entitlements` carries `maxOutputHeight` only). Until it
  // does, the card is kept away from a workspace Owner/Admin for as long as the dashboard is in
  // "your first video" mode (no project yet, or no run yet): a trial owner on the way to a first
  // video is not shown provider wording the trial plan cannot act on. Zero projects alone was not
  // enough, because the welcome flow creates the first project before this page is ever seen.
  // `showSetupToOrgAdmin` is used by the ordinary layout only; a platform admin always sees it.
  const showSetupToOrgAdmin = isOrgAdmin && !isNewUser;
  const { data: platformProviders } = useInferenceProviders(isAdmin);
  const { data: selectableProviders } = useSelectableProviders(null, isOrgAdmin && !isAdmin);
  const setupCapabilities = isAdmin
    ? platformProviders?.filter((p) => p.isDefault && p.isEnabled).map((p) => p.capability)
    : selectableProviders?.map((p) => p.capability);
  const setupHref = isAdmin ? '/admin/inference-providers' : '/org/inference-providers';
  const [live, setLive] = useState<Record<string, LiveExecutionState>>({});
  const [createOpen, setCreateOpen] = useState(false);

  useWorkflowEvents(setLive, () => { void mutateOverview(); });

  const overviewByProject = useMemo(
    () => new Map<string, ProjectOverview>((overview?.projects ?? []).map((p) => [p.projectId, p])),
    [overview],
  );

  const nudges = useMemo<Nudge[]>(() => {
    const out: Nudge[] = [];
    const list = projects ?? [];
    const noWorkflow = list.find((p) => {
      const o = overviewByProject.get(p.id);
      return o && o.sourceFileCount > 0 && o.workflowCount === 0;
    });
    if (noWorkflow) {
      out.push({
        key: 'draft-workflow',
        label: tn('draftLabel', { project: noWorkflow.name }),
        prompt: tn('draftPrompt', { project: noWorkflow.name, id: noWorkflow.id }),
        context: { projectId: noWorkflow.id },
      });
    }
    const noFiles = list.find((p) => (overviewByProject.get(p.id)?.sourceFileCount ?? 0) === 0);
    if (noFiles) {
      out.push({
        key: 'what-to-upload',
        label: tn('uploadLabel', { project: noFiles.name }),
        prompt: tn('uploadPrompt', { project: noFiles.name, id: noFiles.id }),
        context: { projectId: noFiles.id },
        send: true,
      });
    }
    out.push({
      key: 'plan-promo',
      label: tn('promoLabel'),
      prompt: tn('promoPrompt'),
    });
    if ((overview?.totals.failedLast7Days ?? 0) > 0) {
      out.push({
        key: 'week-failures',
        label: tn('failuresLabel'),
        prompt: tn('failuresPrompt'),
        send: true,
      });
    }
    return out.slice(0, 4);
  }, [projects, overviewByProject, overview, tn]);

  const totals = overview?.totals;
  // A failed refresh keeps the overview already loaded; only a first load that failed has nothing to show.
  const overviewFailed = Boolean(overviewError) && !overview;

  // "Your first video" mode: until the person's first run has started, the three-step rail leads
  // the page instead of the empty panels of a workspace that has made nothing yet. The ticks are
  // the Getting started checklist's own (the per-user answer; what the dashboard loaded stands in
  // only while that answer is unavailable).
  const fallback = useMemo(() => checklistFallback(projects, overview), [projects, overview]);
  const checklist = useMemo(
    () => new Map(mergeChecklist(onboarding.server, onboarding.mirror, fallback).map((i) => [i.key, i.done])),
    [onboarding.server, onboarding.mirror, fallback],
  );
  const runDone = checklist.get('run_video') === true;
  const workspaceHasRuns = overview
    ? overview.recentExecutions.length + overview.activeExecutions.length > 0 || overview.totals.renders > 0
    : undefined;
  // Known once the onboarding state and the overview have each answered (or failed to).
  const firstVideoKnown = (onboarding.settled || !onboarding.userKey) && (overview !== undefined || Boolean(overviewError));
  const firstVideo =
    isNewUser || (firstVideoKnown && !loadingProjects && !projectsError && !runDone && workspaceHasRuns === false);
  // While the welcome decision is open (or the page is leaving for the questions), and while an
  // account that may still be on its first video is loading, nothing is drawn that would flash and
  // change. Someone this browser has seen run a video never waits here.
  const holding =
    goingToWelcome || leavingForNext || leavingForCheckout || nextAfterSignup !== null || (patient && planDecisionPending)
    || (patient && (welcome === 'wait' || (!isNewUser && !projectsError && !runDone && !firstVideoKnown)));
  const profile = onboarding.profile;
  const persona = profile?.persona ?? profile?.suggestedPersona ?? null;
  // The one-line header speaks to what the person starts from as well as who they are: someone who
  // answered "just an idea" or "an app I built" is not told the footage is already on their phone.
  const headerMaterial = profile?.material ?? 'unknown';
  // The first video is made in the newest project (the one the welcome flow just created).
  const firstProject = useMemo(
    () => [...(projects ?? [])].sort((a, b) => Date.parse(b.createdAt) - Date.parse(a.createdAt))[0],
    [projects],
  );
  // "Add" is ticked from THAT project's own files. The checklist's upload mark is account-wide and
  // kept for good (any file, in any project, ever), so it would tick the step for a project that
  // holds nothing and move the main button on to "press Run". The mark only stands in while the
  // overview has no row for the project.
  const firstProjectOverview = firstProject ? overviewByProject.get(firstProject.id) : undefined;
  const railAddDone = firstProjectOverview
    ? firstProjectOverview.sourceFileCount > 0
    : overview === undefined && checklist.get('upload_file') === true;
  // Below the two-column breakpoint the dashboard is one column: the card leads it (collapsed, one
  // line plus progress); on a wide screen it sits at the top of the right column.
  const stacked = useDeviceClass() !== 'desktop';
  // The tour's quiet card rides with Getting started (it renders nothing for anyone the tour opens for).
  const gettingStarted = (
    <>
      <FirstFilmCard projects={projects} overview={overview} />
      <DashboardRunNudge overview={overview} projects={projects} />
      <GettingStartedCard projects={projects} overview={overview} onAskAssistant={() => openWithPrompt(tOnb('assistantPrompt'))} />
      <TourCard />
    </>
  );

  return (
    <>
      <div className={classes.hero}>
        <div>
          <h1 className={classes.greeting} style={{ margin: 0 }} suppressHydrationWarning>
            {name ? t('greetingName', { greeting: greeting(new Date().getHours(), t), name }) : greeting(new Date().getHours(), t)}
          </h1>
          <Text c="dimmed" size="sm" mt={6}>
            {loadingOverview || holding ? t('loading') : firstVideo ? tFirst('header', { persona: persona ?? 'other', material: headerMaterial }) : t('overview')}
          </Text>
          <SlowNotice label={t('slow')} retryLabel={t('retry')} active={loadingOverview || holding} onRetry={() => { void mutateOverview(); void mutateProjects(); }} />
        </div>
        {!isNewUser && !holding && (
          <Group gap="sm">
            <Button leftSection={<IconFolderPlus size={16} />} onClick={() => setCreateOpen(true)} style={{ minHeight: 44 }} data-tour="new-project">{t('newProject')}</Button>
            <Button
              variant="default" leftSection={<IconWand size={16} />} style={{ minHeight: 44 }}
              onClick={() => openWithPrompt(t('askPrompt'))}
            >
              {t('askAssistant')}
            </Button>
          </Group>
        )}
      </div>

      <FirstVideoGrantCallout />
      {!holding && pickedPlanKey && <PickedPlanNotice planKey={pickedPlanKey} onForget={forgetPickedPlan} />}

      {projectsError && (
        <Alert color="red" title={t('loadFailed')} mb="md">
          <Group justify="space-between"><Text size="sm">{t('checkConnection')}</Text>
            <Button size="xs" variant="light" color="red" onClick={() => mutateProjects()}>{t('retry')}</Button></Group>
        </Alert>
      )}

      {holding ? (
        <Skeleton height={220} radius="md" data-testid="dashboard-holding" />
      ) : firstVideo ? (
        <Stack gap="md">
          <FirstVideoRail
            persona={persona}
            material={profile?.material ?? null}
            project={firstProject}
            addDone={railAddDone}
            runDone={runDone}
            welcomeEnabled={welcomeEnabled}
            onCreate={() => setCreateOpen(true)}
            showSampleFilm={!shouldOfferFirstFilm(overview, false)}
          />
          {gettingStarted}
          {(projects?.length ?? 0) > 1 && (
            <ContinueProjects projects={projects ?? []} overview={overviewByProject} loading={loadingProjects} />
          )}
          {isAdmin && <SetupChecklist availableCapabilities={setupCapabilities} providersHref={setupHref} />}
        </Stack>
      ) : (
        <Stack gap="md">
          {stacked && gettingStarted}
          {totals && (
            <div className={classes.figures} aria-label={t('summary')}>
              <Figure label={t('figRunning')} value={overview?.activeExecutions.length ?? 0} />
              <Figure label={t('figPassed')} value={totals.passedLast7Days} />
              <Figure label={t('figFailed')} value={totals.failedLast7Days} />
              <Figure label={t('figRenders')} value={totals.renders} />
              <Figure label={t('figSources')} value={totals.sourceFiles} />
            </div>
          )}
          {overviewFailed && (
            // The overview did not load: its sections would read as "nothing yet", which is not true.
            <FailureCard error={overviewError} onRetry={() => void mutateOverview()} backHref="/projects" backLabel={t('backProjects')} />
          )}
          {!overviewFailed && <NeedsAttention runs={overview?.needsAttention ?? []} />}
          <div className={classes.layout}>
            <div className={classes.col}>
              {!overviewFailed && <LiveRuns runs={overview?.activeExecutions ?? []} live={live} loading={loadingOverview} firstProjectId={projects?.[0]?.id} />}
              {!overviewFailed && <LatestRenders outputs={overview?.recentOutputs ?? []} loading={loadingOverview} />}
              <ContinueProjects projects={projects ?? []} overview={overviewByProject} loading={loadingProjects} />
            </div>
            <div className={classes.col}>
              {!stacked && gettingStarted}
              {(isAdmin || showSetupToOrgAdmin) && <SetupChecklist availableCapabilities={setupCapabilities} providersHref={setupHref} compact={!isAdmin} />}
              <Panel title={t('nudges')} id="nudges">
                {loadingProjects ? <Skeleton height={120} radius="md" /> : <NudgeList nudges={nudges} />}
              </Panel>
              {!overviewFailed && <RecentRuns runs={overview?.recentExecutions ?? []} loading={loadingOverview} />}
            </div>
          </div>
        </Stack>
      )}

      <ProjectForm
        opened={createOpen}
        onClose={() => setCreateOpen(false)}
        onSuccess={(p) => {
          // alpha-launch T-018: the real transition — a project was just created. Once per user (the
          // funnel's marker makes a later creation a no-op), never on a page load, and only with
          // consent; a refusal here changes nothing the person can see.
          emitFunnelEvent('alpha.first_project', userKeyFromEmail(user?.email));
          void mutateProjects();
          void mutateOverview();
          if (p) router.push(`/projects/${p.id}?tab=files`);
        }}
      />
    </>
  );
}

function Figure({ label, value }: { label: string; value: number }) {
  return (
    <div className={classes.figure}>
      <span className={classes.figureValue}>{value}</span>
      <span className={classes.figureLabel}>{label}</span>
    </div>
  );
}
