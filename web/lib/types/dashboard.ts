import type { NeedsAttention } from './workflow';
export interface OverviewOutput {
  stepResultId: string;
  projectId: string;
  projectName: string;
  fileName: string;
  producedAt: string;
}

export interface ProjectOverview {
  projectId: string;
  sourceFileCount: number;
  videoCount: number;
  audioCount: number;
  imageCount: number;
  totalBytes: number;
  workflowCount: number;
  renderCount: number;
  activeExecutionCount: number;
  failedExecutionCount: number;
  lastActivityAt: string;
  latestOutput: OverviewOutput | null;
}

export interface DashboardExecution {
  id: string;
  projectId: string;
  projectName: string;
  workflowDefinitionId: string;
  workflowName: string;
  status: 'Queued' | 'Running' | 'Passed' | 'Failed' | 'Cancelled' | 'Stopped';
  startedAt: string | null;
  completedAt: string | null;
  errorMessage: string | null;
  userRequest: string | null;
  outputStepResultId: string | null;
  outputFileName: string | null;
  runAttention?: NeedsAttention | null;
}

export interface DashboardTotals {
  projects: number;
  sourceFiles: number;
  renders: number;
  passedLast7Days: number;
  failedLast7Days: number;
}

export interface DashboardOverview {
  projects: ProjectOverview[];
  activeExecutions: DashboardExecution[];
  recentExecutions: DashboardExecution[];
  needsAttention: DashboardExecution[];
  recentOutputs: OverviewOutput[];
  totals: DashboardTotals;
}

export function executionHref(e: Pick<DashboardExecution, 'projectId' | 'workflowDefinitionId' | 'id'>): string {
  return `/projects/${e.projectId}/workflows/${e.workflowDefinitionId}/executions/${e.id}`;
}
