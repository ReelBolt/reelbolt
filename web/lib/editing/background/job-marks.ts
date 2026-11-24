'use client';

/**
 * What a running (or just finished) background job looks like ON its clips, and one place any job kind can mark clips.
 *
 *   { id, jobKind, clipIds, state, progress?: 0..1, label }
 *
 * Jobs that live in the results store (magic blur, audio cleanup, proposals) are turned into marks by
 * `marksFromEntries`; jobs that do not (follow a subject today) register their own with `registerJobMark`.
 *
 * HOOK for the jobs that arrive later (video enhance, stabilise - L4): call ONE line when the job starts, update it with
 * the progress, clear it when it ends:
 *
 *   registerJobMark({ id, jobKind: 'enhance', clipIds: [clipId], state: 'running', progress, label: 'Enhancing' });
 *
 * and add the kind's row to `JOB_READS` in `./job-protection.ts` so edits that change what the job reads are held.
 */

import { useSyncExternalStore } from 'react';
import { nestedClipIds } from './edit-guard';
import { allMediaClips, rebaseAnchor } from './anchor';
import type { BackgroundEntry } from './results-store';
import type { EditTimeline } from '../types';

export type JobMarkState = 'running' | 'needsReview' | 'failed';

export interface JobMark {
  /** Unique per job. A results-store entry uses its own id. */
  id: string;
  /** `objectEffect`, `audioCleanup`, `proposal`, `follow`, ... (the key of `JOB_READS`). */
  jobKind: string;
  clipIds: readonly string[];
  state: JobMarkState;
  /** 0..1 when the job reports it; absent = indeterminate. */
  progress?: number;
  /** The job's name in plain words (the tooltip and the guard's title use it). */
  label: string;
  /** Why it failed, for the hover text. */
  message?: string;
}

/** What the painter needs for one clip. */
export interface ClipJobPaint {
  state: JobMarkState;
  progress?: number;
  label: string;
  message?: string;
  jobKind: string;
  markId: string;
}

// ── jobs that are not results-store entries ──────────────────────────────────────

type Listener = () => void;
let external: readonly JobMark[] = Object.freeze([]);
const listeners = new Set<Listener>();
const emit = () => {
  for (const l of listeners) l();
};

/** Adds the mark, or replaces the one with the same id. */
export function registerJobMark(mark: JobMark): void {
  external = Object.freeze([...external.filter((m) => m.id !== mark.id), mark]);
  emit();
}
export function updateJobMark(id: string, patch: Partial<Omit<JobMark, 'id'>>): void {
  if (!external.some((m) => m.id === id)) return;
  external = Object.freeze(external.map((m) => (m.id === id ? { ...m, ...patch } : m)));
  emit();
}
export function clearJobMark(id: string): void {
  if (!external.some((m) => m.id === id)) return;
  external = Object.freeze(external.filter((m) => m.id !== id));
  emit();
}
export const getExternalJobMarks = (): readonly JobMark[] => external;
export const subscribeJobMarks = (listener: Listener): (() => void) => {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
};
/** Tests only. */
export function resetJobMarks(): void {
  external = Object.freeze([]);
  emit();
}
const serverMarks: readonly JobMark[] = Object.freeze([]);
export function useExternalJobMarks(): readonly JobMark[] {
  return useSyncExternalStore(subscribeJobMarks, getExternalJobMarks, () => serverMarks);
}

// ── review requests (a "Ready to review" badge was clicked) ──────────────────────

const reviewListeners = new Set<(entryId: string) => void>();
/** Asks the pending-results list to open (at that result). */
export function requestReview(entryId: string): void {
  for (const l of reviewListeners) l(entryId);
}
export function onReviewRequest(listener: (entryId: string) => void): () => void {
  reviewListeners.add(listener);
  return () => {
    reviewListeners.delete(listener);
  };
}

// ── results-store entries -> marks ───────────────────────────────────────────────

/** Marks for the entries that are running, waiting for the user's OK, or failed. `labelOf` names a tool in plain words. */
export function marksFromEntries(entries: readonly BackgroundEntry[], doc: EditTimeline, labelOf: (tool: string) => string): JobMark[] {
  const present = new Set(allMediaClips(doc).map((c) => c.id));
  const out: JobMark[] = [];
  for (const entry of entries) {
    if (entry.state !== 'running' && entry.state !== 'needsReview' && entry.state !== 'failed') continue;
    const ids = new Set<string>();
    if (entry.state === 'needsReview' && entry.review) for (const id of entry.review.applicable) ids.add(id);
    if (entry.state === 'running') {
      const outcome = rebaseAnchor(doc, entry.anchor);
      for (const id of outcome.kind === 'apply' ? outcome.clipIds : outcome.applicable) ids.add(id);
    }
    // A target that cannot take the result later is still the clip the user is waiting on.
    for (const id of entry.anchor.clipIds) if (present.has(id)) ids.add(id);
    if (ids.size === 0) continue;
    out.push({
      id: entry.id,
      jobKind: entry.tool,
      clipIds: [...ids],
      state: entry.state,
      ...(typeof entry.progress === 'number' ? { progress: entry.progress } : {}),
      label: labelOf(entry.tool),
      ...(entry.state === 'failed' && entry.failure?.message ? { message: entry.failure.message } : {}),
    });
  }
  return out;
}

const STATE_RANK: Record<JobMarkState, number> = { running: 2, needsReview: 1, failed: 0 };

/**
 * The one mark each clip shows (running beats needs-review beats failed; the first of equals wins). A compound shows
 * the mark of a clip nested inside it, like the busy mark always did.
 */
export function marksByClip(marks: readonly JobMark[], doc: EditTimeline): Map<string, ClipJobPaint> {
  const out = new Map<string, ClipJobPaint>();
  const put = (clipId: string, mark: JobMark) => {
    const prev = out.get(clipId);
    if (prev && STATE_RANK[prev.state] >= STATE_RANK[mark.state]) return;
    out.set(clipId, { state: mark.state, ...(mark.progress !== undefined ? { progress: Math.min(1, Math.max(0, mark.progress)) } : {}), label: mark.label, ...(mark.message ? { message: mark.message } : {}), jobKind: mark.jobKind, markId: mark.id });
  };
  for (const mark of marks) for (const id of mark.clipIds) put(id, mark);
  if (out.size > 0) {
    for (const item of doc.tracks.flatMap((t) => t.items)) {
      if (item.type !== 'compoundClip') continue;
      const nested = nestedClipIds(doc, item.id).map((id) => out.get(id)).filter((p): p is ClipJobPaint => p !== undefined);
      const best = nested.sort((a, b) => STATE_RANK[b.state] - STATE_RANK[a.state])[0];
      if (best && !out.has(item.id)) out.set(item.id, best);
    }
  }
  return out;
}
