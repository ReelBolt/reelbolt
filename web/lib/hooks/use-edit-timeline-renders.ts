'use client';

import useSWR from 'swr';
import {
  editTimelineRendersKey,
  hasActiveRender,
  listEditTimelineRenders,
  type EditTimelineRenderSummary,
} from '../editing/sync/api';

/**
 * The render queue of one editor timeline (P4): every `TimelineRender` execution whose step named
 * it, newest first. Polls every 5 s only while a render is queued or running — a finished queue
 * changes only when the user starts another render, and that path calls `mutate` itself.
 */
export function useEditTimelineRenders(projectId: string | null | undefined, timelineId: string | null | undefined) {
  return useSWR<EditTimelineRenderSummary[]>(
    projectId && timelineId ? editTimelineRendersKey(projectId, timelineId) : null,
    () => listEditTimelineRenders(projectId as string, timelineId as string),
    { refreshInterval: (data) => (hasActiveRender(data) ? 5000 : 0) },
  );
}

/**
 * The newest render that finished with an output and whose pinned revision is `revision`. Only
 * that render's region picture describes the document on screen; an older revision's regions would
 * paint cache state for frames that may no longer exist.
 */
export function latestRenderForRevision(
  renders: readonly EditTimelineRenderSummary[] | undefined,
  revision: number,
): EditTimelineRenderSummary | null {
  if (!renders) return null;
  return renders.find((r) => r.status === 'Passed' && r.hasOutput && r.revision === revision && r.regions !== null) ?? null;
}
