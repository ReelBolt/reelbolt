'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import dynamic from 'next/dynamic';
import { usePathname } from 'next/navigation';
import useSWR from 'swr';
import { fetcher } from '@/lib/api/client';
import { OnboardingContext, useOnboardingContext, type CardUi } from './onboarding-context';
import { useOnboarding } from '@/lib/hooks/use-onboarding';
import { isWelcomeCandidate, welcomeDecision, type ProjectsKnowledge, type TeamsKnowledge } from '@/lib/onboarding/welcome';
import type { Org } from '@/lib/api/orgs';
import type { Project } from '@/lib/types/project';
import { telemetryEnabled } from '@/lib/telemetry/config';
import { readTelemetryConsent, subscribeTelemetryConsent } from '@/lib/telemetry/consent';
import type { DashboardOverview } from '@/lib/types/dashboard';

// Loaded on first use: the tour costs nothing until someone sees it.
const OnboardingTour = dynamic(() => import('./OnboardingTour'), { ssr: false });

export function useTourLauncher() {
  const { startTour } = useOnboardingContext();
  return { startTour };
}

const EXECUTION_ROUTE = /^\/projects\/[^/]+\/workflows\/[^/]+\/executions\/([^/]+)\/?$/;
const EDITOR_ROUTE = /^\/editor(\/|$)/;

/** Another dialog (or the consent card) is on screen and the tour should wait its turn. */
function anotherModalIsOpen(): boolean {
  // Only a dialog that really takes over the screen counts: not the docked assistant panel
  // (aria-modal="false"), nor menus and popovers.
  return (
    document.querySelector(
      '[role="dialog"]:not([data-onboarding-tour]):not([aria-modal="false"]):not(.mantine-Popover-dropdown):not(.mantine-Menu-dropdown), [aria-modal="true"]:not([data-onboarding-tour])',
    ) !== null
  );
}

/**
 * How many Team workspaces the person belongs to (the switcher's own request, shared cache). Asked
 * only while `enabled`: a member of any team is not a lone new user, even though their own Personal
 * workspace is empty.
 */
function useTeamsKnowledge(enabled: boolean): TeamsKnowledge {
  const { data: orgs, error } = useSWR<Org[]>(enabled ? '/api/v1/orgs' : null, fetcher, { revalidateOnFocus: false });
  if (error) return 'error';
  return Array.isArray(orgs) ? orgs.filter((org) => org.kind === 'Team').length : 'loading';
}

/** The consent card has been answered, or never appears (telemetry off). */
function consentSettled(): boolean {
  return !telemetryEnabled() || readTelemetryConsent() !== null;
}

/**
 * Owns the first run. It decides whether the person on the dashboard belongs at the welcome
 * questions first (the dashboard page does the navigation), and it owns the tour: starts it once
 * for a person whose status is pending (on the dashboard, after the telemetry consent card is
 * answered and while no other dialog is up), re-launches it on request, records how it ended, and
 * reports the two checklist marks only the browser can see.
 *
 * The tour does not start by itself for someone who answered the welcome questions, nor while the
 * welcome decision is still open; for everyone else (the questions skipped, an existing account,
 * the flow switched off) it starts exactly as before.
 */
export function OnboardingProvider({
  children,
  editorEnabled = true,
  appsEnabled = true,
  welcomeEnabled = true,
}: {
  children: React.ReactNode;
  editorEnabled?: boolean;
  appsEnabled?: boolean;
  welcomeEnabled?: boolean;
}) {
  const pathname = usePathname() ?? '';
  const onboarding = useOnboarding({ reconcile: true });
  const { offerTour, settled, finishTour, announceTourStarted, mark, server, mirror, userKey, answeredWelcome, markWelcomeOffered } = onboarding;
  const onDashboard = pathname === '/dashboard';

  // The welcome decision. Projects are only asked for (the dashboard's own request, shared cache)
  // while the person is a candidate; for everyone else nothing is fetched and nothing waits.
  // Who this page load already sent to the welcome questions: the browser's stored mark normally
  // says so, and this covers a browser whose storage is blocked, so the redirect can never repeat.
  const [offeredTo, setOfferedTo] = useState('');
  const offeredThisLoad = userKey !== '' && offeredTo === userKey;
  const candidate = onDashboard && !offeredThisLoad && isWelcomeCandidate({ enabled: welcomeEnabled, settled, server, mirror });
  const { data: projects, error: projectsError } = useSWR<Project[]>(candidate ? '/api/v1/projects' : null, fetcher, {
    revalidateOnFocus: false,
  });
  const projectsKnown: ProjectsKnowledge = projectsError ? 'error' : Array.isArray(projects) ? projects.length : 'loading';
  const teamsKnown = useTeamsKnowledge(candidate);
  // Set from `beginWelcome` until the dashboard is left, so nothing starts over a page on its way out.
  const [leaving, setLeaving] = useState(false);
  if (leaving && !onDashboard) setLeaving(false);
  const welcome =
    leaving ? 'redirect'
    : !onDashboard || offeredThisLoad ? 'stay'
    : welcomeDecision({ enabled: welcomeEnabled, settled, server, mirror, projects: projectsKnown, teams: teamsKnown });

  const beginWelcome = useCallback(() => {
    setOfferedTo(userKey);
    markWelcomeOffered();
    setLeaving(true);
  }, [userKey, markWelcomeOffered]);

  const [open, setOpen] = useState(false);
  // Auto-start happens at most once per page load.
  const [autoStarted, setAutoStarted] = useState(false);

  const startTour = useCallback(() => {
    setOpen(true);
    announceTourStarted();
  }, [announceTourStarted]);

  // Auto-start: on the dashboard only, and only when nothing else needs the screen (the telemetry
  // consent card answered, no other dialog up). Polls gently; each check runs in a callback.
  const wantsAutoStart =
    onDashboard && settled && offerTour && !open && !autoStarted && !answeredWelcome && welcome === 'stay';
  useEffect(() => {
    if (!wantsAutoStart) return;
    let timer = 0;
    let first = 0;
    let unsubscribe = () => {};
    const tryStart = () => {
      if (!consentSettled() || anotherModalIsOpen()) return;
      window.clearInterval(timer);
      window.clearTimeout(first);
      unsubscribe();
      setAutoStarted(true);
      startTour();
    };
    timer = window.setInterval(tryStart, 800);
    first = window.setTimeout(tryStart, 0);
    unsubscribe = subscribeTelemetryConsent(tryStart);
    return () => {
      window.clearInterval(timer);
      window.clearTimeout(first);
      unsubscribe();
    };
  }, [wantsAutoStart, startTour]);

  const onClose = useCallback(
    (reason: 'completed' | 'skipped') => {
      setOpen(false);
      finishTour(reason);
    },
    [finishTour],
  );

  // open_editor: being in the editor is enough.
  useEffect(() => {
    if (settled && EDITOR_ROUTE.test(pathname)) mark('open_editor');
  }, [pathname, settled, mark]);

  // open_result: opening a run that finished. Reads the dashboard overview the dashboard already
  // loads (shared SWR cache) and only on a run page, so every other page pays nothing.
  const executionId = EXECUTION_ROUTE.exec(pathname)?.[1];
  const { data: overview } = useSWR<DashboardOverview>(
    settled && executionId ? '/api/v1/dashboard/overview' : null,
    fetcher,
    { revalidateOnFocus: false, shouldRetryOnError: false },
  );
  useEffect(() => {
    if (!executionId || !overview) return;
    const run = [...overview.recentExecutions, ...overview.needsAttention].find((e) => e.id === executionId);
    if (run?.status === 'Passed') mark('open_result');
  }, [executionId, overview, mark]);

  const [cardUi, setCardUiState] = useState<CardUi>({ expanded: null, hidden: false });
  const setCardUi = useCallback((patch: Partial<CardUi>) => setCardUiState((s) => ({ ...s, ...patch })), []);
  const value = useMemo(
    () => ({ startTour, editorEnabled, appsEnabled, welcomeEnabled, welcome, beginWelcome, cardUi, setCardUi }),
    [startTour, editorEnabled, appsEnabled, welcomeEnabled, welcome, beginWelcome, cardUi, setCardUi],
  );
  return (
    <OnboardingContext.Provider value={value}>
      {children}
      {open && <OnboardingTour onClose={onClose} />}
    </OnboardingContext.Provider>
  );
}
