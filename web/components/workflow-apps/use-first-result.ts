'use client';

import { useCallback, useEffect, useMemo, useState } from 'react';
import useSWR from 'swr';
import { fetcher } from '@/lib/api/client';
import { useOnboarding } from '@/lib/hooks/use-onboarding';
import type { DashboardOverview } from '@/lib/types/dashboard';
import type { WorkflowExecution } from '@/lib/types/workflow';
import {
  isFirstResult,
  isFirstRunFailed,
  mayBeFirstResult,
  openedAResultBefore,
  readFirstResultMark,
  writeFirstResultMark,
} from './first-result';

type RunLike = Pick<WorkflowExecution, 'id' | 'status'>;

export interface FirstResultState {
  /** Show the "your first video is ready" moment for the run on screen. */
  active: boolean;
  /** Closes the moment for good. */
  dismiss: () => void;
  /** The failed run on screen is the person's first run to end, with no finished video before it. */
  failedFirst: boolean;
}

/**
 * Decides, once, whether the finished run on screen is the person's first finished video, and
 * remembers the answer. The moment is shown for that one run, in the browser that first showed it,
 * until it is closed or a newer run takes its place; the account's "opened a result" mark is set at
 * the same time, so no other browser ever shows it again.
 *
 * The workspace's recent runs and outputs, and the person's workspaces, are only asked for while
 * the cheap checks say "maybe" (never for anyone who has opened a result before), and every doubt
 * answers no (see `isFirstResult`).
 */
export function useFirstResult({
  execution,
  executions,
  videoReady = true,
}: {
  execution: RunLike | null | undefined;
  executions: readonly RunLike[] | null | undefined;
  /**
   * The finished run's video is on the page to be played. The moment is about a video: a run that
   * made something else, or whose file is not listed yet, neither shows it nor uses it up.
   */
  videoReady?: boolean;
}): FirstResultState {
  const { server, mirror, settled, userKey, mark } = useOnboarding();
  // Asked once, when the onboarding state is first known, and kept: the shell records "opened a
  // result" during this very visit, which must not make the visit count against itself.
  const [firstSight, setFirstSight] = useState<{ openedBefore: boolean | null } | null>(null);
  if (settled && firstSight === null) setFirstSight({ openedBefore: openedAResultBefore(server, mirror) });
  const openedBefore = firstSight?.openedBefore ?? null;
  const stored = useMemo(() => readFirstResultMark(userKey), [userKey]);
  // What this page load decided: covers a browser whose storage is blocked.
  const [shownFor, setShownFor] = useState<string | null>(null);
  const [closedFor, setClosedFor] = useState<string | null>(null);

  const id = execution?.status === 'Passed' ? execution.id : null;
  const remembered = id !== null && (shownFor === id || stored?.id === id);
  // A mark for any run means the moment was already had: it is never offered for a second one.
  const candidate = id !== null && videoReady && !remembered && stored === null && shownFor === null
    && mayBeFirstResult({ execution, executions, openedBefore });
  const { data: overview } = useSWR<DashboardOverview>(candidate ? '/api/v1/dashboard/overview' : null, fetcher, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
  // The person's workspaces (the switcher's own request, shared cache): asked only for a candidate.
  const { data: orgs } = useSWR<unknown[]>(candidate ? '/api/v1/orgs' : null, fetcher, {
    revalidateOnFocus: false,
    shouldRetryOnError: false,
  });
  const workspaceCount = Array.isArray(orgs) ? orgs.length : null;
  const first = candidate && isFirstResult({ execution, executions, openedBefore, overview, workspaceCount });
  if (first && id !== null) setShownFor(id);

  // Remembered at the moment it is first shown, in this browser and on the account.
  useEffect(() => {
    if (!shownFor) return;
    // Written once: a later re-run of this effect must not reopen a moment the person closed.
    if (readFirstResultMark(userKey)?.id !== shownFor) writeFirstResultMark(userKey, { id: shownFor, dismissed: false });
    mark('open_result');
  }, [shownFor, userKey, mark]);

  const dismissed = id !== null && (closedFor === id || (stored?.id === id && stored.dismissed));
  const dismiss = useCallback(() => {
    if (id === null) return;
    setClosedFor(id);
    writeFirstResultMark(userKey, { id, dismissed: true });
  }, [id, userKey]);

  return {
    active: id !== null && (remembered || first) && !dismissed,
    dismiss,
    failedFirst: isFirstRunFailed({ execution, executions, openedBefore }),
  };
}
