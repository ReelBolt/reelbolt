import { apiFetch } from './client';
import type { CompositionRevision, CreateRevisionRequest, RevisionHistory } from '../types/revisions';

export type * from '../types/revisions';

/** The revision chain of a run. `executionId` may be the original run or any revision in its lineage. */
export function revisionsPath(projectId: string, executionId: string): string {
  return `/api/v1/projects/${projectId}/executions/${executionId}/revisions`;
}

export function getRevisionHistory(projectId: string, executionId: string): Promise<RevisionHistory> {
  return apiFetch<RevisionHistory>(revisionsPath(projectId, executionId));
}

/**
 * Starts a change request. 201 with the new revision, whose own run is
 * `/projects/{projectId}/workflows/{workflowId}/executions/{executionId}`. Refusals arrive as
 * `ApiError`: 400 validation, 402 `INSUFFICIENT_CREDITS`, 403 email not verified, 404, 409
 * `WORKSPACE_UNAVAILABLE`/`RUN_NOT_FINISHED`, 429 `REVISION_RATE_LIMITED` (`retryAfterSeconds`).
 */
export function createRevision(
  projectId: string,
  executionId: string,
  request: CreateRevisionRequest,
): Promise<CompositionRevision> {
  return apiFetch<CompositionRevision>(revisionsPath(projectId, executionId), {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

/** The execution page of a run that made a version or a change request (relative to the `/app` basePath). */
export function runHref(projectId: string, workflowId: string, executionId: string): string {
  return `/projects/${projectId}/workflows/${workflowId}/executions/${executionId}`;
}
