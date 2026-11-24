import { apiFetch } from './client';
import type {
  WorkflowDefinition,
  CreateWorkflowRequest,
  UpdateWorkflowRequest,
  WorkflowExecution,
  WorkflowTemplateSummary,
  WorkflowAppsResponse,
  WorkflowAppResponse,
  WorkflowLintResponse,
  LintWorkflowRequest,
  RunBrief,
  AppManifest,
  CreateWorkflowStepRequest,
} from '../types/workflow';
import type { RunChoices } from '../types/styles';

export function getWorkflows(projectId: string): Promise<WorkflowDefinition[]> {
  return apiFetch<WorkflowDefinition[]>(`/api/v1/projects/${projectId}/workflows`);
}

export function getWorkflow(projectId: string, workflowId: string): Promise<WorkflowDefinition> {
  return apiFetch<WorkflowDefinition>(`/api/v1/projects/${projectId}/workflows/${workflowId}`);
}

export function createWorkflow(projectId: string, data: CreateWorkflowRequest): Promise<WorkflowDefinition> {
  return apiFetch<WorkflowDefinition>(`/api/v1/projects/${projectId}/workflows`, {
    method: 'POST',
    body: JSON.stringify(data),
  });
}

export function updateWorkflow(
  projectId: string,
  workflowId: string,
  data: UpdateWorkflowRequest,
): Promise<WorkflowDefinition> {
  return apiFetch<WorkflowDefinition>(`/api/v1/projects/${projectId}/workflows/${workflowId}`, {
    method: 'PUT',
    body: JSON.stringify(data),
  });
}

export function deleteWorkflow(projectId: string, workflowId: string): Promise<void> {
  return apiFetch<void>(`/api/v1/projects/${projectId}/workflows/${workflowId}`, {
    method: 'DELETE',
  });
}

export function executeWorkflow(
  projectId: string,
  workflowId: string,
  userRequest?: string | null,
  regenerateVideoClips = false,
  choices: RunChoices = {},
  acceptEstimateCredits?: number,
  // A text project file (same project, up to 64 KiB) whose content is the run's brief; userRequest, if any,
  // is appended after it as notes for the run.
  briefFileId?: string,
  // The typed run brief (docs/run-brief.md); sent only when given, so a plain run keeps its old body.
  brief?: RunBrief,
): Promise<WorkflowExecution> {
  return apiFetch<WorkflowExecution>(`/api/v1/projects/${projectId}/workflows/${workflowId}/execute`, {
    method: 'POST',
    // A typed brief replaces the legacy members: the API answers 400 for a legacy member plus the same
    // brief field, so with a brief the request text and run choices are not sent at all.
    body: JSON.stringify(brief !== undefined
      ? { regenerateVideoClips, brief,
        ...(acceptEstimateCredits === undefined ? {} : { acceptEstimateCredits }),
        ...(briefFileId === undefined ? {} : { briefFileId }),
      }
      // The style and shape travel only when chosen, so a plain run sends the same body as before.
      : { userRequest: userRequest ?? null, regenerateVideoClips,
        ...choices,
        // After the server asked (409 ESTIMATE_NOT_ACCEPTED), or when the person approved the shown maximum in the run confirm.
        ...(acceptEstimateCredits === undefined ? {} : { acceptEstimateCredits }),
        ...(briefFileId === undefined ? {} : { briefFileId }),
      }),
  });
}

export function getWorkflowExecutions(projectId: string, workflowId: string): Promise<WorkflowExecution[]> {
  return apiFetch<WorkflowExecution[]>(`/api/v1/projects/${projectId}/workflows/${workflowId}/executions`);
}

export function getWorkflowTemplates(projectId: string): Promise<WorkflowTemplateSummary[]> {
  return apiFetch<WorkflowTemplateSummary[]>(`/api/v1/projects/${projectId}/workflows/templates`);
}

export function applyWorkflowTemplate(
  projectId: string,
  templateKey: string,
  skipIfExists = false,
): Promise<WorkflowDefinition> {
  return apiFetch<WorkflowDefinition>(`/api/v1/projects/${projectId}/workflows/templates/${templateKey}/apply`, {
    method: 'POST',
    body: JSON.stringify({ skipIfExists }),
  });
}

export function workflowAppsPath(projectId: string): string {
  return `/api/v1/projects/${projectId}/workflow-apps`;
}

export function workflowAppPath(projectId: string, workflowId: string): string {
  return `/api/v1/projects/${projectId}/workflows/${workflowId}/app`;
}

/** The gallery in one request: the project's apps and the recipes with their fit. */
export function getWorkflowApps(projectId: string): Promise<WorkflowAppsResponse> {
  return apiFetch<WorkflowAppsResponse>(workflowAppsPath(projectId));
}

/** The effective app manifest of one workflow. */
export function getWorkflowApp(projectId: string, workflowId: string): Promise<WorkflowAppResponse> {
  return apiFetch<WorkflowAppResponse>(workflowAppPath(projectId, workflowId));
}

/**
 * "What changes" before Save (docs/design/app-inputs.md 29): the draft app page (as Save would send it) and the draft steps,
 * described against what is stored, one plain sentence per line. Writes nothing; 404 with app inputs off.
 */
export function describeAppChanges(
  projectId: string,
  workflowId: string,
  body: { appManifest?: AppManifest; steps?: CreateWorkflowStepRequest[] },
): Promise<{ lines: string[] }> {
  return apiFetch<{ lines: string[] }>(`${workflowAppPath(projectId, workflowId)}/changes`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}

/** The pre-run check; the optional body adds the brief rules or lints an unsaved draft. */
export function lintWorkflow(
  projectId: string,
  workflowId: string,
  body: LintWorkflowRequest = {},
): Promise<WorkflowLintResponse> {
  return apiFetch<WorkflowLintResponse>(`/api/v1/projects/${projectId}/workflows/${workflowId}/lint`, {
    method: 'POST',
    body: JSON.stringify(body),
  });
}
