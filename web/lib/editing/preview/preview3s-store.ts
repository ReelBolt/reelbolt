'use client';

/**
 * The one "Preview 3 s" session of the editor (R10.4): a tiny external store over the pure reducer, so the
 * button (in the viewer header, Compose or Color) and the overlay player (over the viewer) share one state.
 * It never touches the document. Polling stops on close, on unmount of the host and on a new timeline.
 */

import { useSyncExternalStore } from 'react';
import { ApiError } from '@/lib/api/client';
import { humanizeError } from '../humanize-error';
import { preview3sApi, type Preview3sApi } from '../api/preview3s';
import { PREVIEW_SECONDS, pollDelayMs, previewReducer, type PreviewState } from './preview3s';

export interface Preview3sSession {
  projectId: string;
  timelineId: string;
  jobId: string | null;
}

export interface Preview3sStore {
  getState: () => PreviewState;
  getSession: () => Preview3sSession | null;
  subscribe: (listener: () => void) => () => void;
  start: (projectId: string, timelineId: string, atFrame: number) => Promise<void>;
  /** Stop polling and return to idle (Close preview, Back to editing, unmount). */
  close: () => void;
}

const SORRY = 'The preview could not be made. Try again.';

export function createPreview3sStore(api: Preview3sApi = preview3sApi, sleep: (ms: number) => Promise<void> = (ms) => new Promise((r) => setTimeout(r, ms))): Preview3sStore {
  let state: PreviewState = { phase: 'idle' };
  let session: Preview3sSession | null = null;
  let run = 0;
  const listeners = new Set<() => void>();
  const emit = () => listeners.forEach((l) => l());
  const dispatch = (event: Parameters<typeof previewReducer>[1]) => {
    state = previewReducer(state, event);
    emit();
  };
  const errorEvent = (error: unknown) => ({
    type: 'error' as const,
    status: error instanceof ApiError ? error.status : null,
    code: error instanceof ApiError ? error.code : null,
    message: humanizeError(error, SORRY),
  });

  return {
    getState: () => state,
    getSession: () => session,
    subscribe: (l) => {
      listeners.add(l);
      return () => listeners.delete(l);
    },
    close: () => {
      run += 1;
      session = null;
      dispatch({ type: 'close' });
    },
    start: async (projectId, timelineId, atFrame) => {
      if (state.phase === 'starting' || state.phase === 'rendering') return;
      const mine = ++run;
      session = { projectId, timelineId, jobId: null };
      dispatch({ type: 'start' });
      try {
        const { executionId } = await api.start(projectId, timelineId, { atFrame: Math.max(0, Math.round(atFrame)), seconds: PREVIEW_SECONDS });
        if (mine !== run) return;
        session = { projectId, timelineId, jobId: executionId };
        dispatch({ type: 'started', jobId: executionId });
        for (let attempt = 0; mine === run; attempt += 1) {
          await sleep(pollDelayMs(attempt));
          if (mine !== run) return;
          try {
            const s = await api.status(projectId, timelineId, executionId);
            if (mine !== run) return;
            dispatch({ type: 'status', status: s.status, ready: s.ready, errorMessage: s.errorMessage });
            if ((state as PreviewState).phase !== 'rendering') return;
          } catch (error) {
            if (mine !== run) return;
            if (error instanceof ApiError && error.status === 404) {
              dispatch({ type: 'error', status: 404, code: null, message: 'This preview is gone. Make a new one.' });
              return;
            }
            if (attempt > 20) {
              dispatch(errorEvent(error));
              return;
            }
          }
        }
      } catch (error) {
        if (mine === run) dispatch(errorEvent(error));
      }
    },
  };
}

/** The editor's single session. */
export const preview3sStore: Preview3sStore = createPreview3sStore();

export function usePreview3sState(store: Preview3sStore = preview3sStore): PreviewState {
  return useSyncExternalStore(store.subscribe, store.getState, store.getState);
}
