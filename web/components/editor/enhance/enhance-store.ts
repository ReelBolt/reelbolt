import {
  cancelEnhance,
  describeEnhanceRefusal,
  describeEstimateUnavailable,
  EnhanceFailedError,
  estimateEnhance,
  getEnhanceJob,
  putEnhanceConsent,
  startEnhance,
  waitForEnhance,
  type EnhanceRefusal,
} from '@/lib/editing/enhance/enhance-api';
import { clipsUsingSource, type EnhanceRecord } from '@/lib/editing/enhance/plan';
import { exceedsPlanHeight, type EnhanceEstimate, type EnhanceJob, type EnhanceRequest, type EnhanceStart } from '@/lib/editing/enhance/types';
import { getEnhanceHost } from './enhance-host';

/** Where a job is anchored: fixed when the run starts, so selection changes later cannot move it. */
export interface EnhanceAnchor {
  projectId: string;
  sourceMediaRef: string;
  clipIds: readonly string[];
  /** The clip the run was started from, to name it when another clip is selected. */
  clipName: string;
  request: EnhanceRequest;
}

export type EnhanceJobState =
  | { kind: 'idle' }
  | { kind: 'estimating'; request: EnhanceRequest }
  | { kind: 'ready'; request: EnhanceRequest; estimate: EnhanceEstimate }
  | { kind: 'consent'; request: EnhanceRequest; estimate: EnhanceEstimate; busy: boolean; failed: boolean }
  | { kind: 'running'; request: EnhanceRequest; anchor: EnhanceAnchor; executionId: string | null; progress: number | null }
  | { kind: 'done'; anchor: EnhanceAnchor; job: EnhanceJob }
  | { kind: 'needs-ok'; anchor: EnhanceAnchor; job: EnhanceJob }
  | { kind: 'cancelled'; anchor: EnhanceAnchor }
  | { kind: 'error'; request: EnhanceRequest | null; refusal: EnhanceRefusal | null; failureCode: string | null };

export interface EnhanceJobApi {
  estimate: (projectId: string, mediaRef: string, body: EnhanceRequest) => Promise<EnhanceEstimate>;
  start: (projectId: string, mediaRef: string, body: EnhanceRequest) => Promise<EnhanceStart>;
  wait: (projectId: string, mediaRef: string, executionId: string, signal: AbortSignal, onProgress: (job: EnhanceJob) => void) => Promise<EnhanceJob>;
  cancel: (projectId: string, mediaRef: string, executionId: string) => Promise<unknown>;
  grantConsent: (projectId: string) => Promise<unknown>;
  /** Re-reads a job (after a cancel found it already finished). */
  getJob?: (projectId: string, mediaRef: string, executionId: string) => Promise<EnhanceJob>;
}

export const defaultEnhanceJobApi: EnhanceJobApi = {
  estimate: estimateEnhance,
  start: startEnhance,
  wait: (p, m, id, signal, onProgress) => waitForEnhance(p, m, id, { signal, onProgress }),
  cancel: cancelEnhance,
  grantConsent: putEnhanceConsent,
  getJob: getEnhanceJob,
};

const withMax = (r: EnhanceRefusal, maxSeconds: number | null | undefined): EnhanceRefusal => (r.kind === 'tooLong' && typeof maxSeconds === 'number' && maxSeconds > 0 ? { ...r, maxSeconds } : r);
const isAbort = (error: unknown): boolean => (error as { name?: string } | null)?.name === 'AbortError';

/**
 * One job per (project, source), kept outside any component: it keeps polling when the section unmounts (selection
 * changed, clip deleted) and is shown again when a section mounts for that source. Nothing here applies a result.
 */
export class EnhanceJobEntry {
  state: EnhanceJobState = { kind: 'idle' };
  api: EnhanceJobApi = defaultEnhanceJobApi;
  readOnly = false;
  /** Set by a mounted section: the clips on the source right now. Cleared on unmount (the editor host answers then). */
  private using: (() => readonly string[]) | null = null;
  /** The pool has no clip to apply to: a finished run is simply done. */
  readonly pool: boolean;
  clipName = '';
  /** The result was applied to a clip: it no longer "waits for an OK" even though no clip plays the source now. */
  applied = false;
  /** What Apply changed, kept here (not in the panel) so Use original survives the panel remounting. */
  record: EnhanceRecord | null = null;
  private abort: AbortController | null = null;
  private seq = 0;

  constructor(readonly key: string, readonly projectId: string, readonly sourceMediaRef: string, pool: boolean) {
    this.pool = pool;
  }

  private set(next: EnhanceJobState): void {
    this.state = next;
    notify();
  }

  /** The clips playing the source right now: the mounted section's view, else the editor's live document. */
  clipIdsUsing(): readonly string[] {
    if (this.pool) return ['pool'];
    if (this.using) return this.using();
    const host = getEnhanceHost();
    return host ? clipsUsingSource(host.getDoc(), this.sourceMediaRef).map((c) => c.id) : [];
  }

  /** A mounted section binds its live view and settings; the returned function unbinds it. */
  attach(opts: { api: EnhanceJobApi; readOnly: boolean; clipName: string; using: (() => readonly string[]) | null }): () => void {
    this.api = opts.api;
    this.readOnly = opts.readOnly;
    this.clipName = opts.clipName;
    this.using = opts.using;
    return () => {
      if (this.using === opts.using) this.using = null;
    };
  }

  setRecord = (record: EnhanceRecord | null): void => {
    this.record = record;
    this.applied = record !== null;
    notify();
  };

  markApplied = (applied: boolean): void => {
    this.applied = applied;
    notify();
  };

  reset = (): void => {
    this.seq += 1;
    this.applied = false;
    this.record = null;
    this.abort?.abort();
    this.abort = null;
    this.set({ kind: 'idle' });
  };

  estimate = async (request: EnhanceRequest): Promise<void> => {
    if (this.readOnly) return;
    const seq = ++this.seq;
    this.set({ kind: 'estimating', request });
    try {
      const result = await this.api.estimate(this.projectId, this.sourceMediaRef, request);
      if (seq !== this.seq) return;
      // Hosted without the project consent is not a refusal: the Start button asks.
      if ((result.unavailableReason || result.unavailableCode) && result.unavailableCode !== 'CONSENT_REQUIRED') {
        this.set({ kind: 'error', request, refusal: withMax(describeEstimateUnavailable(result.unavailableCode, result.unavailableReason), result.maxSeconds), failureCode: null });
        return;
      }
      if (exceedsPlanHeight(result)) {
        this.set({ kind: 'error', request, refusal: { kind: 'plan', feature: 'resolution' }, failureCode: null });
        return;
      }
      if (result.billingEnabled && result.hosted && !result.affordable) {
        const missing = Math.max(0, result.credits - (result.balanceCredits ?? 0));
        this.set({ kind: 'error', request, refusal: { kind: 'credits', ...(missing > 0 ? { missing: Math.ceil(missing * 10) / 10 } : {}) }, failureCode: null });
        return;
      }
      this.set({ kind: 'ready', request, estimate: result.unavailableCode === 'CONSENT_REQUIRED' ? { ...result, consented: false } : result });
    } catch (error) {
      if (seq !== this.seq) return;
      this.set({ kind: 'error', request, refusal: describeEnhanceRefusal(error), failureCode: null });
    }
  };

  private async run(request: EnhanceRequest, estimateOf: EnhanceEstimate): Promise<void> {
    const anchor: EnhanceAnchor = { projectId: this.projectId, sourceMediaRef: this.sourceMediaRef, clipIds: [...this.clipIdsUsing()], clipName: this.clipName, request };
    const controller = new AbortController();
    this.abort?.abort();
    this.abort = controller;
    this.set({ kind: 'running', request, anchor, executionId: null, progress: null });
    try {
      const started = await this.api.start(anchor.projectId, anchor.sourceMediaRef, request);
      if (controller.signal.aborted) {
        // Cancelled while the start was in flight: stop the run the server just created.
        void this.api.cancel(anchor.projectId, anchor.sourceMediaRef, started.executionId).catch(() => undefined);
        return;
      }
      this.set({ kind: 'running', request, anchor, executionId: started.executionId, progress: null });
      const job = await this.api.wait(anchor.projectId, anchor.sourceMediaRef, started.executionId, controller.signal, (j) => {
        const s = this.state;
        if (s.kind === 'running' && !controller.signal.aborted) this.set({ ...s, progress: typeof j.progress === 'number' ? j.progress : null });
      });
      if (controller.signal.aborted) return;
      this.set(this.clipIdsUsing().length > 0 ? { kind: 'done', anchor, job } : { kind: 'needs-ok', anchor, job });
    } catch (error) {
      if (controller.signal.aborted || isAbort(error)) return;
      const refusal = error instanceof EnhanceFailedError ? null : describeEnhanceRefusal(error);
      if (refusal?.kind === 'consent') {
        this.set({ kind: 'consent', request, estimate: { ...estimateOf, consented: false }, busy: false, failed: false });
        return;
      }
      if (error instanceof EnhanceFailedError && error.job.state === 'cancelled') {
        this.set({ kind: 'cancelled', anchor });
        return;
      }
      this.set({ kind: 'error', request, refusal, failureCode: error instanceof EnhanceFailedError ? (error.job.failureCode ?? 'failed') : null });
    }
  }

  /** "Start": Fast starts at once; AI without the project's consent asks first. Read-only editors cannot start. */
  start = (): void => {
    const s = this.state;
    if (s.kind !== 'ready' || this.readOnly) return;
    if (s.request.engine === 'Hosted' && !s.estimate.consented) this.set({ kind: 'consent', request: s.request, estimate: s.estimate, busy: false, failed: false });
    else void this.run(s.request, s.estimate);
  };

  /** The explicit yes: record the project consent on the server, then run. */
  grantConsent = async (): Promise<void> => {
    const s = this.state;
    if (s.kind !== 'consent' || this.readOnly) return;
    this.set({ ...s, busy: true, failed: false });
    try {
      await this.api.grantConsent(this.projectId);
    } catch {
      const now = this.state;
      if (now.kind === 'consent') this.set({ ...now, busy: false, failed: true });
      return;
    }
    await this.run(s.request, { ...s.estimate, consented: true });
  };

  declineConsent = (): void => {
    const s = this.state;
    if (s.kind === 'consent') this.set({ kind: 'ready', request: s.request, estimate: s.estimate });
  };

  cancel = (): void => {
    const s = this.state;
    if (s.kind !== 'running') return;
    this.abort?.abort();
    this.abort = null;
    this.set({ kind: 'cancelled', anchor: s.anchor });
    const id = s.executionId;
    if (!id) return;
    const { projectId, sourceMediaRef } = s.anchor;
    void this.api
      .cancel(projectId, sourceMediaRef, id)
      .then(async (answer) => {
        // 409 ENHANCE_ALREADY_FINISHED (the wrapper answers null): the run ended first, so show what it made.
        if (answer !== null || !this.api.getJob) return;
        const job = await this.api.getJob(projectId, sourceMediaRef, id);
        const now = this.state;
        if (job.state === 'ready' && job.outputFileId && now.kind === 'cancelled' && now.anchor === s.anchor) {
          this.set(this.clipIdsUsing().length > 0 ? { kind: 'done', anchor: s.anchor, job } : { kind: 'needs-ok', anchor: s.anchor, job });
        }
      })
      .catch(() => undefined);
  };
}

const entries = new Map<string, EnhanceJobEntry>();
const listeners = new Set<() => void>();
let version = 0;

function notify(): void {
  version += 1;
  for (const l of [...listeners]) l();
}

export const subscribeEnhance = (listener: () => void): (() => void) => {
  listeners.add(listener);
  return () => listeners.delete(listener);
};
export const enhanceVersion = (): number => version;

export function enhanceEntry(projectId: string, sourceMediaRef: string, pool = false): EnhanceJobEntry {
  const key = `${pool ? 'pool' : 'clip'}|${projectId}|${sourceMediaRef}`;
  let e = entries.get(key);
  if (!e) {
    e = new EnhanceJobEntry(key, projectId, sourceMediaRef, pool);
    entries.set(key, e);
  }
  return e;
}

/** Clip-scoped jobs of a project, for the "other jobs" list. */
export function projectEntries(projectId: string): EnhanceJobEntry[] {
  return [...entries.values()].filter((e) => !e.pool && e.projectId === projectId);
}

/** Test hook: drop every job (aborting polls). */
export function resetEnhanceStore(): void {
  for (const e of entries.values()) e.reset();
  entries.clear();
  version += 1;
}

/** The entry whose applied result is the file `mediaRef` names (a clip now plays the enhanced file). */
export function entryByOutput(projectId: string, mediaRef: string): EnhanceJobEntry | null {
  for (const e of entries.values()) {
    if (e.pool || e.projectId !== projectId || !e.record) continue;
    const s = e.state;
    const out = s.kind === 'done' || s.kind === 'needs-ok' ? s.job.outputFileId : null;
    if (out && `file:${out}` === mediaRef) return e;
  }
  return null;
}
