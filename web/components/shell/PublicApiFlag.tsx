'use client';

import { createContext, useContext, type ReactNode } from 'react';

/**
 * `PUBLIC_API_ENABLED` reaches client components as a value, never as an env read (a browser bundle
 * cannot see a server-side variable): `app/(app)/layout.tsx` reads it once and the shell provides it
 * here, mirroring `WorkflowAppsFlag`. Default `false`, the flag's own default, so a component
 * rendered outside the shell (a unit test) does not offer an API-keys surface the server may lack.
 */
const PublicApiFlagContext = createContext(false);

export function PublicApiFlagProvider({ enabled, children }: { enabled: boolean; children: ReactNode }) {
  return <PublicApiFlagContext.Provider value={enabled}>{children}</PublicApiFlagContext.Provider>;
}

/** Whether the public API surface is available, as the shell resolved it on the server. */
export function usePublicApiEnabled(): boolean {
  return useContext(PublicApiFlagContext);
}
