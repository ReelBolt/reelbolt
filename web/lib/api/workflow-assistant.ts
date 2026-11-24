import { apiFetch } from './client';
import type {
  WorkflowStepSchemaDto,
  WorkflowEditDiff,
  ValidateWorkflowResult,
  WorkflowAssistantApplyResponse,
} from '../types/workflow-assistant';
import type { CreateWorkflowStepRequest } from '../types/workflow';

export function getWorkflowStepSchemas(): Promise<WorkflowStepSchemaDto[]> {
  return apiFetch<WorkflowStepSchemaDto[]>('/api/v1/workflow-step-schemas');
}

export function validateWorkflow(
  projectId: string,
  steps: CreateWorkflowStepRequest[],
): Promise<ValidateWorkflowResult> {
  return apiFetch<ValidateWorkflowResult>(`/api/v1/projects/${projectId}/workflows/assistant/validate`, {
    method: 'POST',
    body: JSON.stringify({ steps }),
  });
}

export function proposeWorkflow(
  projectId: string,
  data: {
    workflowId?: string;
    name?: string;
    steps: CreateWorkflowStepRequest[];
  },
): Promise<WorkflowEditDiff> {
  return apiFetch<WorkflowEditDiff>(`/api/v1/projects/${projectId}/workflows/assistant/propose`, {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

export function applyWorkflow(
  projectId: string,
  workflowId: string | undefined,
  name: string,
  steps: CreateWorkflowStepRequest[],
  confirmationToken: string,
): Promise<WorkflowAssistantApplyResponse> {
  const body: Record<string, unknown> = {
    name,
    steps,
    confirmationToken,
  };

  if (workflowId !== undefined) {
    body.workflowId = workflowId;
  }

  return apiFetch<WorkflowAssistantApplyResponse>(`/api/v1/projects/${projectId}/workflows/assistant/apply`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}
