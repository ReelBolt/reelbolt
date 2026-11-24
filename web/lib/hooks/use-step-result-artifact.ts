'use client';

import useSWR from 'swr';
import { fetcher } from '../api/client';

// Backs the "Edit decision list" style panel for a step result's large artifact (e.g. a
// VideoAnalyze analysis JSON or a VideoCompile EDL) — see `WorkflowStepResult.artifactStorageKey`.
// `stepResultId` should be passed as `null` unless the selected step result actually carries an
// artifact, so nothing is fetched otherwise.
export function useStepResultArtifact(projectId: string, stepResultId: string | null) {
  return useSWR<unknown>(
    projectId && stepResultId
      ? `/api/v1/projects/${projectId}/step-results/${stepResultId}/artifact`
      : null,
    fetcher,
  );
}
