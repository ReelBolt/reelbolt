/**
 * Workflow assistant types - shared contract between workflow assistant API
 * and workflow-builder-ux frontend. Mirrored from backend DTOs.
 */

import type { CreateWorkflowStepRequest } from './workflow';

export interface WorkflowStepSchemaDto {
  stepType: string;
  hasConfig: boolean;
  configTypeName: string | null;
  schema: object | null;
}

export interface WorkflowStepDiffEntry {
  stepOrder: number;
  agentDefinitionId: string;
  stepType: string;
  summary: string;
}

export interface WorkflowEditDiff {
  additions: WorkflowStepDiffEntry[];
  removals: WorkflowStepDiffEntry[];
  modifications: WorkflowStepDiffEntry[];
  confirmationToken: string;
  proposedSteps?: CreateWorkflowStepRequest[];
  /**
   * The workflow this proposal was computed for. `null` means "create a new workflow"; absent means
   * an older server that did not say, which the page reads as "the workflow on screen". Applying to
   * any other workflow than the one named here can never verify, so the page applies to THIS one.
   */
  workflowId?: string | null;
  /** The workflow's name as the proposal names it. Required for a create, optional for an update. */
  name?: string | null;
}

/**
 * Body of POST /workflows/assistant/apply (200 for an existing workflow, 201 for a new one).
 * A summary only: callers must re-fetch the workflow to see its steps.
 */
export interface WorkflowAssistantApplyResponse {
  workflowId: string;
  name: string;
  stepCount: number;
  /**
   * Set (to `true`) only when nothing was written because the workflow already holds exactly the
   * proposed steps — typically because the assistant applied the same proposal earlier in the
   * same turn. It is a success, not a conflict.
   */
  alreadyApplied?: boolean;
}

export interface ValidateWorkflowResult {
  valid: boolean;
  errors: string[];
}
