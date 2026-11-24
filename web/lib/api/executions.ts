import { apiFetch } from './client';
import type { WorkflowExecution, WorkflowStepResult } from '../types/workflow';

/** `summary` omits the heavy per-step fields (see `detailOmitted`); the default is the full payload. */
export function getExecution(
  projectId: string,
  executionId: string,
  options: { detail?: 'summary' } = {},
): Promise<WorkflowExecution> {
  // workflowId is not required; the inference API exposes a root-level endpoint
  // that does not include the workflow segment.
  return apiFetch<WorkflowExecution>(
    `/api/v1/projects/${projectId}/executions/${executionId}${options.detail === 'summary' ? '?detail=summary' : ''}`,
  );
}

/** One step result in full, for the step the user opened. */
export function getStepResult(
  projectId: string,
  executionId: string,
  resultId: string,
): Promise<WorkflowStepResult> {
  return apiFetch<WorkflowStepResult>(
    `/api/v1/projects/${projectId}/executions/${executionId}/step-results/${resultId}`,
  );
}

export function stopExecution(
  projectId: string,
  workflowId: string,
  executionId: string,
): Promise<void> {
  // stopping an execution is now handled by the Inference API directly,
  // so there is no longer any special nginx proxying hack.  we keep the
  // same URL format as before for compatibility with existing clients.
  return apiFetch<void>(
    `/api/v1/projects/${projectId}/workflows/${workflowId}/executions/${executionId}/stop`,
    { method: 'POST' },
  );
}

export function retryExecution(
  projectId: string,
  workflowId: string,
  executionId: string,
  regenerateVideoClips = false,
  acceptEstimateCredits?: number,
): Promise<WorkflowExecution> {
  return apiFetch<WorkflowExecution>(
    `/api/v1/projects/${projectId}/workflows/${workflowId}/executions/${executionId}/retry`,
    { method: 'POST', body: JSON.stringify({
        regenerateVideoClips,
        // Only after the server asked for it (409 ESTIMATE_NOT_ACCEPTED).
        ...(acceptEstimateCredits === undefined ? {} : { acceptEstimateCredits }),
      }),
    },
  );
}

/**
 * Continues a Failed or Cancelled run from the step that stopped; finished steps are kept and not
 * charged again. Refusals: 402 `INSUFFICIENT_CREDITS` / `PLAN_NOT_ENTITLED`, 409 `RUN_NOT_RESUMABLE`,
 * `RESUME_UNAVAILABLE` or `RUN_STILL_STOPPING`.
 */
export function resumeExecution(
  projectId: string,
  workflowId: string,
  executionId: string,
  // Only after the server asked for it (409 ESTIMATE_NOT_ACCEPTED), as on Retry.
  acceptEstimateCredits?: number,
): Promise<WorkflowExecution> {
  return apiFetch<WorkflowExecution>(
    `/api/v1/projects/${projectId}/workflows/${workflowId}/executions/${executionId}/resume`,
    { method: 'POST', body: JSON.stringify(acceptEstimateCredits === undefined ? {} : { acceptEstimateCredits }) },
  );
}
