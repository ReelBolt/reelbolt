'use client';

import { createContext, useContext, type ReactNode } from 'react';

/**
 * `WORKFLOW_APPS_ENABLED` reaches client components as a value, never as an env read (a browser
 * bundle cannot see a server-side variable): `app/(app)/layout.tsx` reads it once and the shell
 * provides it here, mirroring `VideoEditorFlag`. Default `true`, the flag's own default, so a
 * component rendered outside the shell (a unit test) keeps today's app view.
 */
const WorkflowAppsFlagContext = createContext(true);

export function WorkflowAppsFlagProvider({ enabled, children }: { enabled: boolean; children: ReactNode }) {
  return <WorkflowAppsFlagContext.Provider value={enabled}>{children}</WorkflowAppsFlagContext.Provider>;
}

/** Whether the workflow app view is available, as the shell resolved it on the server. */
export function useWorkflowAppsEnabled(): boolean {
  return useContext(WorkflowAppsFlagContext);
}
