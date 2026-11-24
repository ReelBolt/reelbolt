'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { workflowAppPath, workflowAppsPath } from '../api/workflows';
import type { WorkflowAppResponse, WorkflowAppsResponse } from '../types/workflow';

/** The project's gallery (apps and recipes with fit) in one request: no per-card fan-out. */
export function useWorkflowApps(projectId: string | undefined) {
  return useSWR<WorkflowAppsResponse>(projectId ? workflowAppsPath(projectId) : null, fetcher);
}

/** The effective manifest of one workflow. */
export function useWorkflowApp(projectId: string | undefined, workflowId: string | undefined) {
  return useSWR<WorkflowAppResponse>(
    projectId && workflowId ? workflowAppPath(projectId, workflowId) : null,
    fetcher,
  );
}
