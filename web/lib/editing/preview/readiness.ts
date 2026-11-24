/**
 * Preview readiness (R04.1): for every source a timeline plays, is its small preview copy (the proxy)
 * ready, being made, failed or out of reach - and what does the viewer say while it is not.
 *
 * Three pieces, all free of React and the network so they test on their own:
 *   - {@link ReadinessStore}   the derivative pipeline's progress per source (fed from the existing SSE
 *                              parser in `lib/hooks/use-media-derivatives.ts`), plus request failures. The
 *                              viewer subscribes to it directly, so a progress event never re-renders the
 *                              editor shell.
 *   - {@link sourceReadiness}  folds a proxy lookup + that progress into one state a person can read.
 *   - {@link viewerReason}     the line (and progress bar, and Try again) the viewer shows over a picture
 *                              with a hole in it, so the viewer is never black without saying why.
 */

import type { EditTimeline, MediaClip } from '../types';
import { PREPARING_PREVIEW, viewerNotice } from './honesty';
import type { ProxyStatus } from './proxy-urls';
import { mediaRefOf } from './scheduler';
import type { MissingLayer } from './session';

/** The derivative pipeline's stage, as the SSE relay names it (`MediaDerivativeProgress.Stage`). */
export interface PreparationProgress {
  stage: string;
  percent: number | null;
}

export type ClipReadiness =
  /** The proxy is there (`original`: no proxy, but the original file plays). */
  | { state: 'ready'; original: boolean }
  | { state: 'preparing'; percent: number | null }
  | { state: 'failed'; reason: string }
  /** No proxy and no original the preview can read. */
  | { state: 'offline'; reason: string };

export const PREPARE_FAILED_REASON = 'the file could not be converted. It may be damaged or in a format we cannot read.';
export const OPEN_FAILED_REASON = "this clip's media could not be opened for preview. Its format may not be supported in this browser.";
export const REQUEST_FAILED_REASON = 'we could not reach the server to prepare it.';
const OFFLINE_REASON = 'the file is not available right now, so there is nothing to preview.';

const IN_FLIGHT = new Set(['Queued', 'Probing', 'Proxy', 'Filmstrip', 'Peaks', 'KeyframeIndex']);

/** One source's readiness: the proxy lookup, what the pipeline last reported, and a failed request. */
export function sourceReadiness(status: ProxyStatus, progress: PreparationProgress | undefined, requestError: string | undefined): ClipReadiness {
  if (status.state === 'proxy') return { state: 'ready', original: false };
  if (requestError) return { state: 'failed', reason: requestError };
  if (progress?.stage === 'Failed') return { state: 'failed', reason: PREPARE_FAILED_REASON };
  if (status.state === 'unavailable') return { state: 'offline', reason: OFFLINE_REASON };
  if (progress && IN_FLIGHT.has(progress.stage)) return { state: 'preparing', percent: progress.percent };
  // 'Done' with the proxy not looked up yet, or the lookup itself still pending.
  if (status.state === 'pending' || progress?.stage === 'Done') return { state: 'preparing', percent: progress?.stage === 'Done' ? 100 : null };
  return { state: 'ready', original: true };
}

export class ReadinessStore {
  private readonly progress = new Map<string, PreparationProgress>();
  private readonly errors = new Map<string, string>();
  private readonly listeners = new Set<() => void>();
  private version = 0;

  /** A pipeline progress event for one source (null mediaRef: not about a project file - ignored). */
  apply(event: { mediaRef: string | null; stage: string; percentComplete: number | null }): void {
    if (!event.mediaRef) return;
    const previous = this.progress.get(event.mediaRef);
    if (previous && previous.stage === event.stage && previous.percent === event.percentComplete) return;
    this.progress.set(event.mediaRef, { stage: event.stage, percent: event.percentComplete });
    // A fresh run for the source clears an older request failure.
    if (IN_FLIGHT.has(event.stage)) this.errors.delete(event.mediaRef);
    this.bump();
  }

  /** The user asked again for `ref`: forget the old failure; it reads "preparing" until the pipeline answers. */
  restart(ref: string): void {
    this.errors.delete(ref);
    this.progress.set(ref, { stage: 'Queued', percent: null });
    this.bump();
  }

  fail(ref: string, reason: string): void {
    this.errors.set(ref, reason);
    this.bump();
  }

  progressOf(ref: string | null): PreparationProgress | undefined {
    return ref ? this.progress.get(ref) : undefined;
  }

  errorOf(ref: string | null): string | undefined {
    return ref ? this.errors.get(ref) : undefined;
  }

  readinessOf(clip: MediaClip, statusFor: (clip: MediaClip) => ProxyStatus): ClipReadiness {
    const ref = mediaRefOf(clip.assetRef);
    return sourceReadiness(statusFor(clip), this.progressOf(ref), this.errorOf(ref));
  }

  /** `useSyncExternalStore` pair. */
  subscribe = (listener: () => void): (() => void) => {
    this.listeners.add(listener);
    return () => {
      this.listeners.delete(listener);
    };
  };

  getVersion = (): number => this.version;

  private bump(): void {
    this.version += 1;
    for (const listener of [...this.listeners]) listener();
  }
}

export interface PrepareDeps {
  request(mediaRef: string): Promise<unknown>;
  /** Looks the sources up again (the proxy resolver's refresh), so a finished proxy is picked up. */
  refresh(mediaRefs: string[]): void;
}

/**
 * Asks the pipeline to (re)prepare `refs`, one after another. A request that fails is recorded as that
 * source's failure (so the viewer says so and offers Try again); the others still go.
 */
export async function prepareSources(store: ReadinessStore, deps: PrepareDeps, refs: readonly string[]): Promise<void> {
  for (const ref of refs) {
    store.restart(ref);
    try {
      await deps.request(ref);
    } catch {
      store.fail(ref, REQUEST_FAILED_REASON);
    }
  }
  deps.refresh([...refs]);
}

/** The sources a timeline's PICTURE clips read (what "Prepare all" prepares), each once. */
export function pictureSourceRefs(doc: EditTimeline): string[] {
  const refs = new Set<string>();
  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    for (const item of track.items) {
      if (item.type !== 'mediaClip') continue;
      const ref = mediaRefOf(item.assetRef);
      if (ref) refs.add(ref);
    }
  }
  return [...refs];
}

export interface SourceReadinessRow {
  /** The first picture clip that reads this source (a stable thing to jump to). */
  itemId: string;
  name: string;
  mediaRef: string;
  readiness: ClipReadiness;
}

/** One row per source the timeline's picture clips read, in timeline order. */
export function readinessRows(doc: EditTimeline, store: ReadinessStore, statusFor: (clip: MediaClip) => ProxyStatus): SourceReadinessRow[] {
  const seen = new Set<string>();
  const rows: SourceReadinessRow[] = [];
  for (const track of doc.tracks) {
    if (track.kind !== 'video') continue;
    for (const item of track.items) {
      if (item.type !== 'mediaClip') continue;
      const ref = mediaRefOf(item.assetRef);
      if (!ref || seen.has(ref)) continue;
      seen.add(ref);
      rows.push({ itemId: item.id, name: item.name, mediaRef: ref, readiness: store.readinessOf(item, statusFor) });
    }
  }
  return rows;
}

export interface ReadinessSummary {
  total: number;
  ready: number;
  preparing: number;
  failed: number;
  offline: number;
  /** The mean percent of the sources that report one, or null when none does. */
  percent: number | null;
}

/** All picture sources of the timeline, counted by state - the project-level line. */
export function summarizeReadiness(rows: readonly SourceReadinessRow[]): ReadinessSummary {
  const out: ReadinessSummary = { total: rows.length, ready: 0, preparing: 0, failed: 0, offline: 0, percent: null };
  const percents: number[] = [];
  for (const { readiness } of rows) {
    if (readiness.state === 'ready') out.ready += 1;
    else if (readiness.state === 'preparing') {
      out.preparing += 1;
      if (readiness.percent !== null) percents.push(readiness.percent);
    } else if (readiness.state === 'failed') out.failed += 1;
    else out.offline += 1;
  }
  if (percents.length > 0) out.percent = Math.round(percents.reduce((a, b) => a + b, 0) / percents.length);
  return out;
}

/** What a row says, in plain words. */
export function readinessWords(readiness: ClipReadiness): string {
  switch (readiness.state) {
    case 'ready':
      return readiness.original ? 'Plays from the original file' : 'Ready';
    case 'preparing':
      return readiness.percent !== null ? `Preparing preview ${readiness.percent}%` : 'Preparing preview';
    case 'failed':
      return `Preview failed: ${readiness.reason}`;
    case 'offline':
      return `Source offline: ${readiness.reason}`;
  }
}

export interface ViewerReason {
  kind: 'preparing' | 'failed' | 'offline' | 'info';
  /** The sentence, in plain words. */
  text: string;
  percent: number | null;
  /** The source to ask for again (Try again), when asking again can help. */
  retryRef: string | null;
}

/**
 * Why the picture has a hole in it, or `null` when every layer was drawn. A failure outranks "still
 * preparing" because the user can act on it.
 */
export function viewerReason(
  doc: EditTimeline,
  missing: readonly MissingLayer[],
  statusFor: (clip: MediaClip) => ProxyStatus,
  store: ReadinessStore | null,
): ViewerReason | null {
  const text = viewerNotice(doc, missing, statusFor);
  if (!text) return null;
  const clipOf = (itemId: string): MediaClip | null => {
    for (const track of doc.tracks) for (const item of track.items) if (item.id === itemId && item.type === 'mediaClip') return item;
    return null;
  };
  const refOf = (clip: MediaClip | null) => (clip ? mediaRefOf(clip.assetRef) : null);

  const opened = missing.find((layer) => layer.cause === 'openFailed');
  if (opened) return { kind: 'failed', text: `Preview failed: ${OPEN_FAILED_REASON}`, percent: null, retryRef: refOf(clipOf(opened.itemId)) };

  const readings = missing.flatMap((layer) => {
    const clip = clipOf(layer.itemId);
    return clip && store ? [{ clip, readiness: store.readinessOf(clip, statusFor) }] : [];
  });
  const failed = readings.find((r) => r.readiness.state === 'failed');
  if (failed && failed.readiness.state === 'failed') return { kind: 'failed', text: `Preview failed: ${failed.readiness.reason}`, percent: null, retryRef: refOf(failed.clip) };
  const offline = readings.find((r) => r.readiness.state === 'offline');
  if (offline && offline.readiness.state === 'offline') return { kind: 'offline', text: `Source offline: ${offline.readiness.reason}`, percent: null, retryRef: refOf(offline.clip) };
  const preparing = readings.find((r) => r.readiness.state === 'preparing');
  if (preparing && preparing.readiness.state === 'preparing') {
    const percent = preparing.readiness.percent;
    return { kind: 'preparing', text: percent !== null ? `Preparing preview ${percent}%` : PREPARING_PREVIEW, percent, retryRef: null };
  }
  if (text === PREPARING_PREVIEW) return { kind: 'preparing', text, percent: null, retryRef: null };
  // A status the lookup itself reported (e.g. no readable original) or "no picture at this point".
  return { kind: 'info', text, percent: null, retryRef: null };
}
