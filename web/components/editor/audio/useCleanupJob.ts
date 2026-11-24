'use client';

import { useCallback, useEffect, useRef, useState } from 'react';
import {
  CleanupFailedError,
  describeCleanupRefusal,
  describeEstimateUnavailableReason,
  estimateCleanup,
  startCleanup,
  waitForCleanup,
  type CleanupRefusal,
} from '@/lib/editing/cleanup/cleanup-api';
import { hasCleanupConsent, rememberCleanupConsent } from '@/lib/editing/cleanup/consent';
import { isAiTarget, targetOf, type CleanupChoice, type CleanupEstimate, type CleanupJob, type CleanupOutputKind, type CleanupRequest, type CleanupStart } from '@/lib/editing/cleanup/types';

export type CleanupJobState =
  | { kind: 'idle' }
  | { kind: 'estimating'; choice: CleanupChoice }
  | { kind: 'confirm'; choice: CleanupChoice; estimate: CleanupEstimate }
  | { kind: 'consent'; choice: CleanupChoice; estimate: CleanupEstimate }
  | { kind: 'running'; choice: CleanupChoice }
  | { kind: 'done'; choice: CleanupChoice; job: CleanupJob }
  | { kind: 'error'; choice: CleanupChoice; refusal: CleanupRefusal | null; failureCode: string | null };

export interface CleanupJobApi {
  estimate: (projectId: string, mediaRef: string, body: CleanupRequest) => Promise<CleanupEstimate>;
  start: (projectId: string, mediaRef: string, body: CleanupRequest) => Promise<CleanupStart>;
  wait: (projectId: string, mediaRef: string, executionId: string, signal: AbortSignal) => Promise<CleanupJob>;
}

export const defaultCleanupJobApi: CleanupJobApi = {
  estimate: estimateCleanup,
  start: startCleanup,
  wait: (p, m, id, signal) => waitForCleanup(p, m, id, { signal }),
};

export interface CleanupJobContext {
  projectId: string;
  /** The signed-in user; null with no session, in which case consent is never remembered. */
  userId: string | null;
  /** The active organisation, so one consent never covers another org's projects. */
  orgId: string | null;
  /** What the server should hand back (see `cleanupSubject`). */
  outputKind: CleanupOutputKind;
  readOnly?: boolean;
  /** Called once the server accepted the run, so the editor can follow it even if this panel goes away. */
  onStarted?: (info: { executionId: string; mediaRef: string; outputKind: CleanupOutputKind }) => void;
}

/** User + organisation + project: the exact scope the remembered AI consent covers. */
const consentScope = (c: CleanupJobContext) => ({ user: c.userId, org: c.orgId, project: c.projectId });

/**
 * One cleanup run at a time: estimate (credits or free) -> confirm -> (first AI use only) consent ->
 * run with polling -> done. The AI path never starts before consent; the flag is sent only after it.
 */
export function useCleanupJob(ctx: CleanupJobContext, api: CleanupJobApi = defaultCleanupJobApi) {
  const [state, setStateRaw] = useState<CleanupJobState>({ kind: 'idle' });
  const stateRef = useRef<CleanupJobState>({ kind: 'idle' });
  const setState = useCallback((next: CleanupJobState) => {
    stateRef.current = next;
    setStateRaw(next);
  }, []);
  const abort = useRef<AbortController | null>(null);
  const mediaRefRef = useRef<string>('');
  // Captured with the media ref so a selection change after "Clean it" (but before the run
  // starts, e.g. through the consent dialog) cannot swap the output kind under the job.
  const outputKindRef = useRef<CleanupOutputKind>(ctx.outputKind);
  const ctxRef = useRef(ctx);
  useEffect(() => {
    ctxRef.current = ctx;
  });

  useEffect(() => () => abort.current?.abort(), []);

  const reset = useCallback(() => {
    abort.current?.abort();
    abort.current = null;
    setState({ kind: 'idle' });
  }, [setState]);

  const begin = useCallback(
    async (choice: CleanupChoice, mediaRef: string) => {
      const c = ctxRef.current;
      if (c.readOnly) return;
      mediaRefRef.current = mediaRef;
      outputKindRef.current = c.outputKind;
      setState({ kind: 'estimating', choice });
      try {
        const estimate = await api.estimate(c.projectId, mediaRef, { target: targetOf(choice), outputKind: c.outputKind });
        if (estimate.unavailableReason) {
          setState({ kind: 'error', choice, refusal: describeEstimateUnavailableReason(estimate.unavailableReason), failureCode: null });
          return;
        }
        if (estimate.billingEnabled && !estimate.affordable && typeof estimate.credits === 'number') {
          const missing = Math.max(0, estimate.credits - (estimate.balanceCredits ?? 0));
          setState({ kind: 'error', choice, refusal: { kind: 'credits', ...(missing > 0 ? { missing: Math.ceil(missing * 10) / 10 } : {}) }, failureCode: null });
          return;
        }
        setState({ kind: 'confirm', choice, estimate });
      } catch (error) {
        setState({ kind: 'error', choice, refusal: describeCleanupRefusal(error), failureCode: null });
      }
    },
    [api, setState],
  );

  const run = useCallback(
    async (choice: CleanupChoice, egress: boolean) => {
      const c = ctxRef.current;
      const mediaRef = mediaRefRef.current;
      const target = targetOf(choice);
      const outputKind = outputKindRef.current;
      const controller = new AbortController();
      abort.current?.abort();
      abort.current = controller;
      setState({ kind: 'running', choice });
      try {
        const started = await api.start(c.projectId, mediaRef, { target, outputKind, ...(egress ? { allowSourceMediaEgress: true } : {}) });
        c.onStarted?.({ executionId: started.executionId, mediaRef, outputKind });
        const job = await api.wait(c.projectId, mediaRef, started.executionId, controller.signal);
        if (controller.signal.aborted) return;
        setState({ kind: 'done', choice, job });
      } catch (error) {
        if (controller.signal.aborted || (error as { name?: string } | null)?.name === 'AbortError') return;
        if (error instanceof CleanupFailedError) setState({ kind: 'error', choice, refusal: null, failureCode: error.job.failureCode ?? 'failed' });
        else setState({ kind: 'error', choice, refusal: describeCleanupRefusal(error), failureCode: null });
      }
    },
    [api, setState],
  );

  /** "Clean it": a local run starts at once; the first AI run asks for consent first. */
  const confirm = useCallback(() => {
    const s = stateRef.current;
    if (s.kind !== 'confirm' || ctxRef.current.readOnly) return;
    const ai = isAiTarget(targetOf(s.choice));
    if (ai && !hasCleanupConsent(consentScope(ctxRef.current))) setState({ kind: 'consent', choice: s.choice, estimate: s.estimate });
    else void run(s.choice, ai);
  }, [run, setState]);

  const grantConsent = useCallback(() => {
    const s = stateRef.current;
    if (s.kind !== 'consent' || ctxRef.current.readOnly) return;
    rememberCleanupConsent(consentScope(ctxRef.current));
    void run(s.choice, true);
  }, [run]);

  const declineConsent = useCallback(() => {
    const s = stateRef.current;
    if (s.kind === 'consent') setState({ kind: 'confirm', choice: s.choice, estimate: s.estimate });
  }, [setState]);

  return { state, begin, confirm, grantConsent, declineConsent, reset };
}
