import type { ActivityJob, ActivityJobState } from '@/lib/activity/types';

/**
 * Local stand-ins for the kit components when the activity list has not caught up (or has no row for the work):
 * an `ActivityJob`-shaped object built from the section's own state. Never listed; only rendered in place.
 */
export function localJob(over: { state: ActivityJobState; type: string; executionId?: string | null; progress?: number | null; title?: string | null; failure?: ActivityJob['failure'] }): ActivityJob {
  return {
    id: `local:${over.type}:${over.executionId ?? ''}`,
    type: over.type,
    state: over.state,
    projectId: '',
    projectName: '',
    title: over.title ?? null,
    executionId: over.executionId ?? null,
    workflowId: null,
    subject: {},
    step: null,
    progress: over.progress ?? null,
    credits: { heldCredits: null, chargedCredits: null },
    failure: over.failure ?? null,
    startedByYou: true,
    cancellable: false,
    createdAt: '',
    startedAt: null,
    endedAt: null,
    origin: 'client',
  };
}
