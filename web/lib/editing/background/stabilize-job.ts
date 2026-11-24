/**
 * The `stabilize` job on the timeline: while a clip with stabilisation on has no stabilised proxy yet, every clip using
 * that source shows a running mark (progress from the stream's `Stabilize` stage); a failed run shows a failed mark the
 * user can retry. Pure state over the job-marks registry; the workspace feeds it the document, the resolver's answer and
 * the derivative progress events.
 *
 * Stream facts (the service's relay): events carry the source's file id and a stage, no derivative kind. `Stabilize`
 * (0-100) is ours; the terminal `Done` / `Skipped` / `Failed` are shared with the plain proxy run, so a terminal event
 * counts for the stabilisation only once we have seen its stage, or when the plain proxy is already there.
 */

import { clearJobMark, getExternalJobMarks, registerJobMark, updateJobMark } from './job-marks';
import { STABILIZE_JOB_KIND, STABILIZE_STAGE } from '../preview/stabilize-proxy';

export const stabilizeMarkId = (mediaRef: string): string => `stabilize:${mediaRef}`;

export interface StabilizeJobDeps {
  /** "Preparing the stabilised preview" in the UI's language. */
  label: string;
  /** The plain sentence a failed run shows. */
  failedMessage: string;
  /** Asks the service again (resolver.retryStabilized). */
  retry(mediaRef: string): void;
}

export interface StabilizeEvent {
  mediaRef: string | null;
  stage: string;
  percentComplete: number | null;
}

export class StabilizeJobs {
  private readonly failed = new Set<string>();
  private readonly seen = new Set<string>();

  constructor(private deps: StabilizeJobDeps) {}

  setDeps(deps: StabilizeJobDeps): void {
    this.deps = deps;
  }

  /**
   * Brings the marks in line with the document: `wanted` is source -> its stabilised clips, `isReady` says the
   * stabilised proxy is listed. A ready source clears its mark; a source nobody stabilises any more drops it.
   */
  sync(wanted: ReadonlyMap<string, readonly string[]>, isReady: (mediaRef: string) => boolean): void {
    const live = new Set<string>();
    for (const [ref, clipIds] of wanted) {
      const id = stabilizeMarkId(ref);
      if (isReady(ref)) {
        this.failed.delete(ref);
        this.seen.delete(ref);
        clearJobMark(id);
        continue;
      }
      live.add(id);
      const existing = getExternalJobMarks().find((m) => m.id === id);
      if (!existing) {
        registerJobMark({ id, jobKind: STABILIZE_JOB_KIND, clipIds, state: this.failed.has(ref) ? 'failed' : 'running', label: this.deps.label, ...(this.failed.has(ref) ? { message: this.deps.failedMessage } : {}) });
      } else if (existing.clipIds.join('|') !== clipIds.join('|')) {
        updateJobMark(id, { clipIds });
      }
    }
    for (const mark of getExternalJobMarks()) if (mark.jobKind === STABILIZE_JOB_KIND && !live.has(mark.id)) clearJobMark(mark.id);
  }

  /** One `media.derivative.progress` event. Returns true when it was about the stabilisation. */
  apply(event: StabilizeEvent, plainProxyReady: (mediaRef: string) => boolean): boolean {
    const ref = event.mediaRef;
    if (!ref) return false;
    const id = stabilizeMarkId(ref);
    const mark = getExternalJobMarks().find((m) => m.id === id);
    if (!mark) return false;
    if (event.stage === STABILIZE_STAGE) {
      this.seen.add(ref);
      this.failed.delete(ref);
      const progress = event.percentComplete === null ? undefined : Math.min(1, Math.max(0, event.percentComplete / 100));
      updateJobMark(id, { state: 'running', label: this.deps.label, message: undefined, ...(progress !== undefined ? { progress } : {}) });
      return true;
    }
    const ours = this.seen.has(ref) || plainProxyReady(ref);
    if (!ours) return false;
    if (event.stage === 'Failed') {
      this.failed.add(ref);
      updateJobMark(id, { state: 'failed', message: this.deps.failedMessage, progress: undefined });
      return true;
    }
    // Done / Skipped: the proxy is about to be listed; the next sync clears the mark.
    return event.stage === 'Done' || event.stage === 'Skipped';
  }

  /** The Retry action of a failed mark: running again, and the request is sent again. */
  retry(mediaRef: string): void {
    this.failed.delete(mediaRef);
    this.seen.delete(mediaRef);
    updateJobMark(stabilizeMarkId(mediaRef), { state: 'running', message: undefined, progress: undefined });
    this.deps.retry(mediaRef);
  }

  /** The source a stabilize mark belongs to (for the Retry click), or null. */
  static sourceOf(markId: string): string | null {
    return markId.startsWith('stabilize:') ? markId.slice('stabilize:'.length) : null;
  }
}
