import { apiFetch } from './client';
import type {
  ChangeRequestBody,
  StepChangeEstimatePreview,
  StepChangeRequest,
  StepChangeRequestList,
} from '../types/change-requests';

export type * from '../types/change-requests';

function base(projectId: string, executionId: string): string {
  return `/api/v1/projects/${projectId}/executions/${executionId}`;
}

export function changeCapabilitiesPath(projectId: string, executionId: string): string {
  return `${base(projectId, executionId)}/change-capabilities`;
}

export function changeRequestsPath(projectId: string, executionId: string): string {
  return `${base(projectId, executionId)}/change-requests`;
}

export function getChangeRequests(projectId: string, executionId: string): Promise<StepChangeRequestList> {
  return apiFetch<StepChangeRequestList>(changeRequestsPath(projectId, executionId));
}

/** Price and replay without writing anything. */
export function estimateChangeRequest(
  projectId: string,
  executionId: string,
  request: ChangeRequestBody,
): Promise<StepChangeEstimatePreview> {
  return apiFetch<StepChangeEstimatePreview>(`${changeRequestsPath(projectId, executionId)}/estimate`, {
    method: 'POST',
    body: JSON.stringify(request),
  });
}

/**
 * Starts a change request (201). Refusals arrive as `ApiError`: 400 validation, 402
 * `INSUFFICIENT_CREDITS`, 409 `RUN_NOT_FINISHED` / `STEP_NOT_COMPLETED` / `USE_COMPOSITION_REVISION`,
 * 429 `REVISION_RATE_LIMITED`, `PLAN_NOT_ENTITLED`.
 */
export function createChangeRequest(
  projectId: string,
  executionId: string,
  request: ChangeRequestBody,
): Promise<StepChangeRequest> {
  return apiFetch<StepChangeRequest>(changeRequestsPath(projectId, executionId), {
    method: 'POST',
    body: JSON.stringify(request),
  });
}
