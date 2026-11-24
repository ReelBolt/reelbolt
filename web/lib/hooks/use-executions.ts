'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';
import { getWorkflows, getWorkflowExecutions } from '../api/workflows';
import { pollInterval } from '../utils/poll-interval';
import type { WorkflowExecution } from '../types/workflow';

/** True while any run in the list is still going, so the list keeps itself current. */
export function hasActiveExecution(executions: WorkflowExecution[] | undefined | null): boolean {
  return (executions ?? []).some((e) => e.status === 'Queued' || e.status === 'Running');
}

/**
 * A workflow's run history. Each run carries its own fields and each step's status, timing and
 * compute target only: `output`, the JSON payloads, tool calls, reasoning and transcripts are empty
 * here (they made one workflow's list 9.8 MB). Read a run's steps from `getExecution`.
 */
export function useExecutions(projectId: string, workflowId: string) {
  return useSWR<WorkflowExecution[]>(
    projectId && workflowId
      ? `/api/v1/projects/${projectId}/workflows/${workflowId}/executions`
      : null,
    fetcher,
    // Poll only while a run is in flight: a finished list never changes on its own.
    {
      refreshWhenHidden: false,
      refreshInterval: (data) =>
        pollInterval({ active: hasActiveExecution(data), unchangedForMs: 0, fastMs: 10000 }),
    },
  );
}

/** A finished-or-running run of any workflow in the project, with the name of the workflow it belongs to. */
export interface ProjectExecution {
  id: string;
  workflowId: string;
  workflowName: string;
  status: WorkflowExecution['status'];
  startedAt: string | null;
}

/**
 * Every run of every workflow in a project, newest first, for pickers that name "an earlier run" (continue from a
 * run's composition). There is no project-wide runs endpoint, so this reads each workflow's run list; it only
 * fetches while `enabled`, so a form that does not need the list costs nothing.
 */
export function useProjectExecutions(projectId: string | undefined, enabled: boolean) {
  return useSWR<ProjectExecution[]>(
    enabled && projectId ? `project-executions:${projectId}` : null,
    async () => {
      const workflows = await getWorkflows(projectId as string);
      const lists = await Promise.all(
        workflows.map(async (w) => {
          const runs = await getWorkflowExecutions(projectId as string, w.id);
          return runs.map((r): ProjectExecution => ({
            id: r.id, workflowId: w.id, workflowName: w.name, status: r.status, startedAt: r.startedAt,
          }));
        }),
      );
      return lists.flat().sort((a, b) => (b.startedAt ?? '').localeCompare(a.startedAt ?? ''));
    },
  );
}
