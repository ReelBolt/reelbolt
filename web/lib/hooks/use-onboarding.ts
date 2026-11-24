'use client';

import { useCallback, useEffect, useMemo, useRef, useSyncExternalStore } from 'react';
import useSWR from 'swr';
import { deletePickedPlan, getOnboarding, putOnboarding, putOnboardingProfile, sendOnboardingEvent } from '../api/onboarding';
import { userKeyFromEmail } from '../telemetry/identity';
import {
  CHECKLIST_KEYS,
  ONBOARDING_URL,
  TOUR_VERSION,
  effectiveTour,
  parseMirror,
  readMirrorRaw,
  shouldOfferTour,
  subscribeMirror,
  updateMirror,
  type ChecklistKey,
  type ChecklistMark,
  type Material,
  type OnboardingMirror,
  type OnboardingProfile,
  type OnboardingServerState,
  type TourStatus,
} from '../onboarding/state';
import { answeredWelcome, starterTemplateKeyFor } from '../onboarding/welcome';
import type { Persona } from '../attribution/persona';
import type { DashboardOverview } from '../types/dashboard';
import type { Project } from '../types/project';
import { useAuth } from './use-auth';

export interface ChecklistItemState {
  key: ChecklistKey;
  done: boolean;
}

/**
 * Items in the API's order. An item is done when the server says so, when the browser already saw it
 * done (sticky), or when `fallback` (what the dashboard already loaded) shows it - the last one is
 * what keeps the card honest while `/api/v1/onboarding` is unavailable.
 */
export function mergeChecklist(
  server: OnboardingServerState | null,
  mirror: OnboardingMirror,
  fallbackIn: Partial<Record<ChecklistKey, boolean>> = {},
): ChecklistItemState[] {
  // The fallback is workspace-wide (teammates' projects and runs): it stands in ONLY while the
  // server's per-user answer is unavailable, never next to it.
  const fallback = server ? {} : fallbackIn;
  const serverDone = new Set((server?.checklist.items ?? []).filter((i) => i.done).map((i) => i.key));
  const seen = new Set(mirror.done ?? []);
  return CHECKLIST_KEYS.map((key) => ({
    key,
    done: serverDone.has(key) || seen.has(key) || fallback[key] === true,
  }));
}

/**
 * What the dashboard already loaded says about the first three items. Workspace-wide, so it only
 * stands in while the per-user answer is unavailable (see {@link mergeChecklist}).
 */
export function checklistFallback(
  projects: Project[] | undefined,
  overview: DashboardOverview | undefined,
): Partial<Record<ChecklistKey, boolean>> {
  if (!projects || !overview) return {};
  return {
    create_project: projects.length > 0,
    upload_file: overview.totals.sourceFiles > 0,
    run_video: overview.recentExecutions.length + overview.activeExecutions.length > 0,
  };
}

/** What saving the two welcome answers gives back to the flow. */
export interface SavedWelcomeAnswer {
  /** The recipe to create the first project with. */
  starterTemplateKey: string;
  /** False when the API could not store the answer; the flow carries on regardless. */
  saved: boolean;
}

/**
 * The caller's onboarding state: tour status, the "Getting started" checklist and the welcome
 * answers.
 *
 * Reads and writes `/api/v1/onboarding` but never depends on it: a per-user localStorage mirror
 * remembers a finished or skipped tour and a dismissed card at once, and keeps doing so when the API
 * answers 404 or is offline. Errors are swallowed - onboarding must never get in anyone's way.
 */
export function useOnboarding({ reconcile = false }: { reconcile?: boolean } = {}) {
  const { user } = useAuth();
  const userKey = userKeyFromEmail(user?.email);

  const { data, mutate } = useSWR<OnboardingServerState | null>(
    userKey ? [ONBOARDING_URL, userKey] : null,
    () => getOnboarding(),
    { revalidateOnFocus: false, shouldRetryOnError: false },
  );
  // `undefined` = still asking; `null` = asked, no usable answer (404, offline, malformed).
  const settled = data !== undefined;
  const server = data ?? null;

  const raw = useSyncExternalStore(
    subscribeMirror,
    () => readMirrorRaw(userKey),
    () => '',
  );
  const mirror = useMemo(() => parseMirror(raw), [raw]);

  const tour = effectiveTour(server, mirror);
  const offerTour = Boolean(userKey) && settled && shouldOfferTour(server, mirror);
  const checklistDismissed = mirror.checklistDismissed === true || server?.checklist.dismissed === true;

  const save = useCallback(
    async (patch: Parameters<typeof putOnboarding>[0]) => {
      const next = await putOnboarding(patch);
      // A tour or checklist save answers the whole state; one that leaves the profile out must not
      // make the welcome answers this page already knows disappear.
      if (next) await mutate((current) => ({ ...next, profile: next.profile ?? current?.profile ?? null }), { revalidate: false });
    },
    [mutate],
  );

  /** Forgets the server's copy of the picked plan (best effort), then updates the cached state. */
  const clearPickedPlan = useCallback(async () => {
    await deletePickedPlan();
    await mutate(
      (current) => (current && current.profile ? { ...current, profile: { ...current.profile, pickedPlan: null } } : current),
      { revalidate: false },
    );
  }, [mutate]);

  const finishTour = useCallback(
    (status: Exclude<TourStatus, 'pending'>) => {
      if (!userKey) return;
      updateMirror(userKey, (m) => ({ ...m, tour: { status, version: TOUR_VERSION, at: Date.now() } }));
      void save({ tour: { status, version: TOUR_VERSION } });
      void sendOnboardingEvent(status === 'completed' ? 'tour_completed' : 'tour_skipped');
    },
    [userKey, save],
  );

  /** Remembers, before the redirect, that this browser sent the person to the welcome questions. */
  const markWelcomeOffered = useCallback(() => {
    if (!userKey) return;
    updateMirror(userKey, (m) => (m.welcome ? m : { ...m, welcome: 'offered' }));
  }, [userKey]);

  /** Puts the profile the API just stored into the cached onboarding state, without a refetch. */
  const adoptProfile = useCallback(
    (profile: OnboardingProfile) =>
      mutate((current) => (current ? { ...current, profile } : current), { revalidate: false }),
    [mutate],
  );

  /**
   * Saves the two welcome answers and says which recipe the first project starts with. Never
   * throws and never blocks the flow: when the API cannot store the answer, this browser still
   * remembers it and the contract's own mapping names the recipe.
   */
  const answerWelcome = useCallback(
    async (persona: Persona, material: Material): Promise<SavedWelcomeAnswer> => {
      if (userKey) updateMirror(userKey, (m) => ({ ...m, welcome: 'answered' }));
      const result = await putOnboardingProfile({ persona, material });
      if (result) await adoptProfile(result.profile);
      return {
        starterTemplateKey: result?.starterTemplateKey ?? starterTemplateKeyFor(material),
        saved: result !== null,
      };
    },
    [userKey, adoptProfile],
  );

  /** Remembers, in this browser, the first project the welcome flow created and its recipe. */
  const rememberWelcomeProject = useCallback(
    (id: string, starter: string) => {
      if (!userKey) return;
      updateMirror(userKey, (m) => ({ ...m, welcomeProject: { id, starter } }));
    },
    [userKey],
  );

  /** The first project the welcome flow created from this browser, read fresh (not from a render). */
  const welcomeProject = useCallback(
    () => (userKey ? (parseMirror(readMirrorRaw(userKey)).welcomeProject ?? null) : null),
    [userKey],
  );

  /** "Skip for now": recorded once; someone who already answered has nothing to skip. */
  const skipWelcome = useCallback(async (): Promise<void> => {
    if (!userKey) return;
    if (answeredWelcome(server, parseMirror(readMirrorRaw(userKey)))) return;
    updateMirror(userKey, (m) => ({ ...m, welcome: 'skipped' }));
    const result = await putOnboardingProfile({ skipped: true });
    if (result) await adoptProfile(result.profile);
  }, [userKey, server, adoptProfile]);

  const announceTourStarted = useCallback(() => {
    void sendOnboardingEvent('tour_started');
  }, []);

  const dismissChecklist = useCallback(() => {
    if (!userKey) return;
    updateMirror(userKey, (m) => ({ ...m, checklistDismissed: true }));
    void save({ checklistDismissed: true });
    void sendOnboardingEvent('checklist_dismissed');
  }, [userKey, save]);

  /** Remembers items the dashboard saw done (sticky) and reports each to telemetry once. */
  const noteDone = useCallback(
    (keys: ChecklistKey[]) => {
      if (!userKey || keys.length === 0) return;
      const current = parseMirror(readMirrorRaw(userKey));
      const known = new Set(current.done ?? []);
      const evented = new Set(current.evented ?? []);
      const fresh = keys.filter((k) => !known.has(k) || !evented.has(k));
      if (fresh.length === 0) return;
      updateMirror(userKey, (m) => ({
        ...m,
        done: [...new Set([...(m.done ?? []), ...keys])],
        evented: [...new Set([...(m.evented ?? []), ...keys])],
      }));
      for (const k of fresh) if (!evented.has(k)) void sendOnboardingEvent('checklist_item_done', k);
    },
    [userKey],
  );

  /** `open_result` / `open_editor`: the two things only the browser can report. No-op once known. */
  const mark = useCallback(
    (key: ChecklistMark) => {
      if (!userKey) return;
      const current = parseMirror(readMirrorRaw(userKey));
      if (current.done?.includes(key)) return;
      noteDone([key]);
      void save({ marks: [key] });
    },
    [userKey, save, noteDone],
  );

  // When the server finally answers, push up anything this browser knew first (the API was missing or
  // offline when it happened). Once per user per page load.
  const reconciled = useRef('');
  useEffect(() => {
    if (!reconcile || !userKey || !settled || !server || reconciled.current === userKey) return;
    reconciled.current = userKey;
    const patch: Parameters<typeof putOnboarding>[0] = {};
    const m = mirror.tour;
    if (m && m.status !== 'pending' && (server.tour.status === 'pending' || (server.tour.version ?? 0) < m.version)) {
      if (!(server.tour.updatedAt && Date.parse(server.tour.updatedAt) > m.at)) {
        patch.tour = { status: m.status, version: m.version };
      }
    }
    if (mirror.checklistDismissed && !server.checklist.dismissed) patch.checklistDismissed = true;
    const serverDone = new Set(server.checklist.items.filter((i) => i.done).map((i) => i.key));
    const marks = (['open_result', 'open_editor'] as const).filter(
      (k) => (mirror.done ?? []).includes(k) && !serverDone.has(k),
    );
    if (marks.length > 0) patch.marks = marks;
    if (Object.keys(patch).length > 0) void save(patch);
  }, [reconcile, userKey, settled, server, mirror, save]);

  return {
    /** The person is known and the server has answered (or failed to). */
    settled: Boolean(userKey) && settled,
    userKey,
    server,
    mirror,
    tourStatus: tour.status,
    /** Pending, or finished under an older tour version. */
    offerTour,
    /** The welcome answers as the API has them; `null` when it sent none. */
    profile: server?.profile ?? null,
    /** The welcome questions were answered (per the API, or in this browser). */
    answeredWelcome: answeredWelcome(server, mirror),
    markWelcomeOffered,
    clearPickedPlan,
    answerWelcome,
    skipWelcome,
    rememberWelcomeProject,
    welcomeProject,
    checklistDismissed,
    finishTour,
    announceTourStarted,
    dismissChecklist,
    noteDone,
    mark,
  };
}
