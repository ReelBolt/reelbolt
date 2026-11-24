import { useSyncExternalStore } from 'react';
import type { EditOp } from '@/lib/editing/engine';
import type { EditTimeline } from '@/lib/editing/types';

/**
 * What the phone clip sheet needs from the editor to apply an enhancement: the project, the live document and ONE
 * undoable batch. The workspace registers it; nothing else is shared, so no editor file has to grow for the phone.
 */
export interface EnhanceHost {
  projectId: string | null;
  getDoc: () => EditTimeline;
  dispatchBatch: (ops: readonly EditOp[], label: string) => void;
}

let current: EnhanceHost | null = null;
const listeners = new Set<() => void>();

export function registerEnhanceHost(host: EnhanceHost | null): void {
  current = host;
  for (const l of [...listeners]) l();
}

const subscribe = (listener: () => void): (() => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};

export function useEnhanceHost(): EnhanceHost | null {
  return useSyncExternalStore(subscribe, () => current, () => null);
}

/** The registered host right now (for code that is not a component, like the job store). */
export const getEnhanceHost = (): EnhanceHost | null => current;
