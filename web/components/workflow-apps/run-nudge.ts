/**
 * The "you have files but no run yet" nudge: who it is for, and where a dismissal is remembered.
 * Pure rules plus a thin localStorage reader; no network, no event names of its own.
 */
import type { DashboardOverview, ProjectOverview } from '@/lib/types/dashboard';

const DISMISS_PREFIX = 'rf-run-nudge:';

export function runNudgeDismissKey(userKey: string, projectId: string): string {
  return `${DISMISS_PREFIX}${userKey}:${projectId}`;
}

export function isRunNudgeDismissed(userKey: string, projectId: string): boolean {
  if (typeof window === 'undefined' || !userKey) return false;
  try {
    return window.localStorage.getItem(runNudgeDismissKey(userKey, projectId)) !== null;
  } catch {
    return false;
  }
}

export function dismissRunNudge(userKey: string, projectId: string): void {
  if (typeof window === 'undefined' || !userKey) return;
  try {
    window.localStorage.setItem(runNudgeDismissKey(userKey, projectId), new Date().toISOString());
  } catch {
    // Storage unavailable: the card simply comes back next visit.
  }
}

/** A project with files and nothing run, rendered or failed in it. */
export function hasFilesAndNoRun(p: Pick<ProjectOverview, 'sourceFileCount' | 'renderCount' | 'activeExecutionCount' | 'failedExecutionCount'>): boolean {
  return p.sourceFileCount > 0 && p.renderCount + p.activeExecutionCount + p.failedExecutionCount === 0;
}

/**
 * The project the dashboard nudge is about: the most recently active one with files and no run that
 * the person has not dismissed. Null when the workspace has run something already is NOT assumed:
 * a second project without a run is exactly who this is for.
 */
export function nudgeProject(
  overview: Pick<DashboardOverview, 'projects'> | undefined,
  dismissed: (projectId: string) => boolean,
): ProjectOverview | null {
  const candidates = (overview?.projects ?? [])
    .filter((p) => hasFilesAndNoRun(p) && !dismissed(p.projectId))
    .sort((a, b) => Date.parse(b.lastActivityAt) - Date.parse(a.lastActivityAt));
  return candidates[0] ?? null;
}
