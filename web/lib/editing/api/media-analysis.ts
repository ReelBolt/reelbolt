'use client';

/**
 * Analysis on demand per media file (T-013 contract, R09.1) as the editor reads it (R09.2-R09.9).
 *
 * A file can be analysed for six kinds: `silence`, `shots`, `motion`, `events` and `beats` (free) and
 * `transcript` (metered: credits, with an up-front estimate). The API answers with states and summaries; the
 * data itself is fetched per kind only when it is ready. This module is the one place the editor
 * speaks to those routes, and the one place that turns a file's results into the artifact-shaped
 * object the panels already read ({@link mergeFileAnalysis}), so silence removal, the transcript,
 * subtitles, takes and boring shots work on any timeline, seeded or blank.
 */

import { useEffect, useRef } from 'react';
import useSWR, { mutate as globalMutate } from 'swr';
import { ApiError, apiFetch, fetcher } from '@/lib/api/client';
import type { EditTimeline, MediaClip } from '../types';
import type { ResolvedAnalysis } from './analysis';

export type AnalysisKind = 'silence' | 'shots' | 'motion' | 'transcript' | 'beats' | 'events';
export const ANALYSIS_KINDS: readonly AnalysisKind[] = ['silence', 'shots', 'motion', 'transcript', 'beats', 'events'];
export type AnalysisState = 'none' | 'queued' | 'running' | 'ready' | 'failed';

export interface KindStatus {
  state: AnalysisState;
  reason: string | null;
  requestId: string | null;
  updatedAt: string | null;
  summary: Record<string, unknown> | null;
  /** True on a ready `events` result made by an earlier detector: still served, and running the kind again replaces it. */
  outdated?: boolean;
}

export interface MediaAnalysisStatus {
  mediaRef: string;
  kinds: Record<AnalysisKind, KindStatus>;
}

export interface AnalysisEstimate {
  billingEnabled: boolean;
  credits: number | null;
  seconds: number | null;
  balanceCredits: number | null;
  affordable: boolean;
  cached: boolean;
  unavailableReason: string | null;
}

export interface StartResponse {
  requestId: string;
  mediaRef: string;
  states: Partial<Record<AnalysisKind, { state: AnalysisState; reason: string | null }>>;
}

const base = (projectId: string, mediaRef: string) =>
  `/api/v1/projects/${encodeURIComponent(projectId)}/media/${encodeURIComponent(mediaRef)}/analysis`;

export const fileMediaRef = (projectFileId: string): string => `file:${projectFileId}`;
export const stepResultMediaRef = (stepResultId: string): string => `step_result:${stepResultId}`;

/** How many media sources one document reads analysis for (bounds the request volume). */
export const MAX_ANALYSIS_SOURCES = 8;

export const analysisBaseForTest = base;
export const mediaAnalysisKey = (projectId: string, mediaRef: string): string => base(projectId, mediaRef);

export const fetchMediaAnalysisStatus = (projectId: string, mediaRef: string): Promise<MediaAnalysisStatus> => fetcher<MediaAnalysisStatus>(base(projectId, mediaRef));

export const estimateMediaAnalysis = (projectId: string, mediaRef: string, kinds: readonly AnalysisKind[], language: string | null = null): Promise<AnalysisEstimate> =>
  apiFetch<AnalysisEstimate>(`${base(projectId, mediaRef)}/estimate`, { method: 'POST', body: JSON.stringify({ kinds, language }) });

/** Starts an analysis; once it is accepted, every panel that reads analysis looks again (and then polls while it runs). */
export const startMediaAnalysis = async (projectId: string, mediaRef: string, kinds: readonly AnalysisKind[], language: string | null = null): Promise<StartResponse> => {
  const response = await apiFetch<StartResponse>(base(projectId, mediaRef), { method: 'POST', body: JSON.stringify({ kinds, language }) });
  void refreshAnalysis();
  return response;
};

export const fetchAnalysisData = <T>(projectId: string, mediaRef: string, kind: AnalysisKind): Promise<T> => fetcher<T>(`${base(projectId, mediaRef)}/${kind}`);

/** The state a kind shows when the API has not heard of it. */
export const NO_KIND: KindStatus = { state: 'none', reason: null, requestId: null, updatedAt: null, summary: null };

/** True while any kind is still being worked on, so the panel keeps polling. */
export const isWorking = (status: MediaAnalysisStatus | undefined): boolean => !!status && ANALYSIS_KINDS.some((k) => ['queued', 'running'].includes(status.kinds[k]?.state ?? 'none'));

export const KIND_LABEL: Record<AnalysisKind, string> = {
  silence: 'Silence',
  shots: 'Shots',
  motion: 'Motion',
  transcript: 'Transcript',
  beats: 'Beats',
  events: 'Events',
};

/** The events summary's per-type counts as one short phrase, most frequent first (`4 appear, 2 beat`). */
export function eventTypeCounts(summary: Record<string, unknown> | null): string {
  const by = summary?.byType;
  if (!by || typeof by !== 'object') return '';
  return Object.entries(by as Record<string, unknown>)
    .filter((e): e is [string, number] => typeof e[1] === 'number' && e[1] > 0)
    .sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]))
    .map(([type, count]) => `${count} ${type.replace(/_/g, " ")}`)
    .join(', ');
}

/** One plain sentence per kind and state; never an enum name or an id. */
export function describeKind(kind: AnalysisKind, status: KindStatus): string {
  const s = status.summary ?? {};
  const n = (key: string): number => (typeof s[key] === 'number' ? (s[key] as number) : 0);
  switch (status.state) {
    case 'queued':
      return 'Waiting to start';
    case 'running':
      return 'Working on it';
    case 'failed':
      return status.reason && status.reason.length > 0 ? status.reason : 'It did not work. Try again.';
    case 'ready':
      if (kind === 'silence') return `${n('rangeCount')} quiet stretch${n('rangeCount') === 1 ? '' : 'es'}`;
      if (kind === 'shots') return `${n('shotCount')} shot${n('shotCount') === 1 ? '' : 's'}`;
      if (kind === 'motion') return `${n('stillSeconds').toFixed(0)} s of still picture`;
      if (kind === 'events') {
        const types = eventTypeCounts(status.summary);
        return `${n('eventCount')} event${n('eventCount') === 1 ? '' : 's'}${types ? `: ${types}` : ''}`;
      }
      if (kind === 'beats') {
        const bpm = Math.round(n('bpm'));
        const word = typeof s.tempoWord === 'string' ? s.tempoWord : '';
        return word.length > 0 ? `${bpm} BPM, ${word}` : `${bpm} BPM`;
      }
      return `${n('wordCount')} word${n('wordCount') === 1 ? '' : 's'}${n('estimatedWordCount') > 0 ? `, ${n('estimatedWordCount')} timed roughly` : ''}`;
    default:
      return 'Not done yet';
  }
}

/** A refusal or failure of the API in plain words. */
export function describeAnalysisError(error: unknown): string {
  if (error instanceof ApiError) {
    if (error.status === 402) return 'You do not have enough credits for the transcript. Free analysis can still run.';
    if (error.status === 409) return error.message || 'The transcript is not available for this file right now.';
    if (error.status === 404) return 'That file is no longer in the project.';
    if (error.status === 403) return 'You can only view this project, so analysis cannot run.';
    return error.message || 'The analysis could not start. Try again in a moment.';
  }
  return 'The analysis could not start. Try again in a moment.';
}

/** Refresh every panel that reads analysis (after a run finishes). */
export function refreshAnalysis(): Promise<unknown> {
  return globalMutate((key) => Array.isArray(key) && (key[0] === 'file-analysis' || key[0] === 'file-analysis-status'));
}

/** Status of one media source (a project file id here); polls every 2 s while something is queued or running. */
export function useMediaAnalysisStatus(projectId: string, projectFileId: string | null) {
  return useMediaSourceStatus(projectId, projectFileId ? fileMediaRef(projectFileId) : null);
}

/** Status of one media source by ref (`file:<id>` or `step_result:<id>`); polls every 2 s while working. */
export function useMediaSourceStatus(projectId: string, mediaRef: string | null) {
  return useSWR<MediaAnalysisStatus>(
    projectId && mediaRef ? ['file-analysis-status', projectId, mediaRef] : null,
    ([, p, r]: readonly [string, string, string]) => fetchMediaAnalysisStatus(p, r),
    { refreshInterval: (latest) => (isWorking(latest) ? 2000 : 0), revalidateOnFocus: false },
  );
}

// ─────────────────────────────────────────────────────────────────────────────
// Document -> files, and per-file results -> an artifact the panels read
// ─────────────────────────────────────────────────────────────────────────────

const clipFile = (clip: MediaClip): string | null => (clip.assetRef?.kind === 'projectFile' && typeof clip.assetRef.projectFileId === 'string' ? clip.assetRef.projectFileId : null);

/** The analysis ref a clip's asset has: `file:<id>` for a project file, `step_result:<id>` for a render output. */
export function clipMediaRef(clip: MediaClip): string | null {
  const ref = clip.assetRef;
  if (!ref) return null;
  if (ref.kind === 'projectFile') return typeof ref.projectFileId === 'string' && ref.projectFileId.length > 0 ? fileMediaRef(ref.projectFileId) : null;
  if (ref.kind === 'stepResult') return typeof ref.stepResultId === 'string' && ref.stepResultId.length > 0 ? stepResultMediaRef(ref.stepResultId) : null;
  return null;
}

/** The distinct project files the timeline's media clips show, in first-use order. */
export function documentFileIds(doc: EditTimeline): string[] {
  const ids: string[] = [];
  for (const track of doc.tracks) for (const item of track.items) if (item.type === 'mediaClip') {
    const id = clipFile(item);
    if (id !== null && !ids.includes(id)) ids.push(id);
  }
  return ids;
}

/** The distinct media sources (project files and render outputs) the timeline shows, first-use order, capped. */
export function documentMediaRefs(doc: EditTimeline): string[] {
  const refs: string[] = [];
  for (const track of doc.tracks) for (const item of track.items) if (item.type === 'mediaClip') {
    const ref = clipMediaRef(item);
    if (ref !== null && !refs.includes(ref)) refs.push(ref);
  }
  return refs.slice(0, MAX_ANALYSIS_SOURCES);
}

export interface FileAnalysisData {
  /** The media source (`file:<id>` or `step_result:<id>`). */
  mediaRef?: string;
  /** The project file id when the source is a file; null for a render output. */
  projectFileId: string | null;
  /** True when the source's status could not be read (the panels say so instead of showing nothing). */
  statusFailed?: boolean;
  silence: Array<{ startSec: number; endSec: number }> | null;
  shots: Array<{ startSec: number; endSec: number }> | null;
  motion: Array<{ startSec: number; endSec: number; mean: number; motionClass: string }> | null;
  transcript: Array<{ text: string; startSec: number; endSec: number; estimated: boolean }> | null;
}

const hasItems = (value: unknown): boolean => Array.isArray(value) && value.length > 0;

/** The mean motion across the windows a shot spans, duration-weighted; null when no window overlaps. */
export function shotMotion(shot: { startSec: number; endSec: number }, windows: NonNullable<FileAnalysisData['motion']>): { mean: number; motionClass: string } | null {
  let total = 0;
  let weighted = 0;
  for (const w of windows) {
    const overlap = Math.min(shot.endSec, w.endSec) - Math.max(shot.startSec, w.startSec);
    if (overlap <= 0) continue;
    total += overlap;
    weighted += overlap * w.mean;
  }
  if (total <= 0) return null;
  const mean = weighted / total;
  return { mean, motionClass: mean < 0.02 ? 'still' : mean < 0.08 ? 'low' : 'high' };
}

/**
 * Fills the analysis the panels read with the per-file results where the seed analysis has nothing
 * for that kind. The seed analysis wins when it has data (it is the established path); a kind it
 * lacks (a blank timeline, or a seed that never had a transcript) is supplied by the files, with
 * each file given its own source index so one file's seconds never land on another file's clips.
 */
export function mergeFileAnalysis(seed: ResolvedAnalysis | null | undefined, files: readonly FileAnalysisData[], doc: EditTimeline): ResolvedAnalysis | null {
  const artifact: Record<string, unknown> = seed && typeof seed.artifact === 'object' && seed.artifact !== null ? { ...(seed.artifact as Record<string, unknown>) } : {};
  const pick = (key: string): unknown => artifact[key] ?? artifact[key.charAt(0).toUpperCase() + key.slice(1)];
  const needSilence = !hasItems(pick('silenceSpans')) && files.some((f) => hasItems(f.silence));
  const needWords = !hasItems(pick('words')) && !hasItems(pick('segments')) && files.some((f) => hasItems(f.transcript));
  const needShots = !hasItems(pick('shots')) && files.some((f) => hasItems(f.shots));
  if (!needSilence && !needWords && !needShots) return seed ?? null;

  const sources = seed ? seed.sources.map((s) => ({ ...s })) : [];
  if (seed && seed.sources.length === 0) {
    const only = documentMediaRefs(doc);
    // A seed written before multi-source support describes the run's input files, never a render output.
    if (only.length === 1 && only[0].startsWith('file:')) sources.push({ sourceIndex: 0, projectFileId: only[0].slice(5) });
  }
  const refOfSource = (s: { projectFileId: string | null; mediaRef?: string | null }): string | null => s.mediaRef ?? (s.projectFileId ? fileMediaRef(s.projectFileId) : null);
  const indexFor = (f: FileAnalysisData): number => {
    const fRef = f.mediaRef ?? (f.projectFileId ? fileMediaRef(f.projectFileId) : '');
    const existing = sources.find((s) => refOfSource(s) === fRef);
    if (existing) return existing.sourceIndex;
    const next = sources.reduce((m, s) => Math.max(m, s.sourceIndex + 1), 0);
    sources.push(f.projectFileId ? { sourceIndex: next, projectFileId: f.projectFileId } : { sourceIndex: next, projectFileId: null, mediaRef: fRef });
    return next;
  };

  if (needSilence) {
    let n = 0;
    artifact.silenceSpans = files.flatMap((f) => (f.silence ?? []).map((r) => ({ id: `g${n++}`, startSec: r.startSec, endSec: r.endSec, sourceIndex: indexFor(f) })));
  }
  if (needWords) {
    let n = 0;
    artifact.words = files.flatMap((f) => (f.transcript ?? []).map((w) => ({ id: `w${n++}`, text: w.text, startSec: w.startSec, endSec: w.endSec, estimated: w.estimated, sourceIndex: indexFor(f) })));
  }
  if (needShots) {
    let n = 0;
    artifact.shots = files.flatMap((f) =>
      (f.shots ?? []).map((s) => {
        const motion = f.motion ? shotMotion(s, f.motion) : null;
        return { id: `s${n++}`, startSec: s.startSec, endSec: s.endSec, sourceIndex: indexFor(f), visual: motion ? { motionMean: motion.mean, motionClass: motion.motionClass } : null };
      }),
    );
  }
  return { stepResultId: seed?.stepResultId ?? '', artifact, sources };
}

async function loadSource(projectId: string, ref: string): Promise<FileAnalysisData> {
  const out: FileAnalysisData = { mediaRef: ref, projectFileId: ref.startsWith('file:') ? ref.slice(5) : null, silence: null, shots: null, motion: null, transcript: null };
  let status: MediaAnalysisStatus;
  try {
    status = await fetchMediaAnalysisStatus(projectId, ref);
  } catch {
    out.statusFailed = true;
    return out;
  }
  const ready = (k: AnalysisKind) => status.kinds[k]?.state === 'ready';
  const [silence, shots, motion, transcript] = await Promise.all([
    ready('silence') ? fetchAnalysisData<{ ranges: Array<{ startSec: number; endSec: number }> }>(projectId, ref, 'silence').catch(() => null) : null,
    ready('shots') ? fetchAnalysisData<{ shots: Array<{ startSec: number; endSec: number }> }>(projectId, ref, 'shots').catch(() => null) : null,
    ready('motion') ? fetchAnalysisData<{ windows: NonNullable<FileAnalysisData['motion']> }>(projectId, ref, 'motion').catch(() => null) : null,
    ready('transcript') ? fetchAnalysisData<{ words: NonNullable<FileAnalysisData['transcript']> }>(projectId, ref, 'transcript').catch(() => null) : null,
  ]);
  out.silence = silence?.ranges ?? null;
  out.shots = shots?.shots ?? null;
  out.motion = motion?.windows ?? null;
  out.transcript = transcript?.words ?? null;
  return out;
}

/**
 * The ready results of every media source (files and render outputs) the timeline shows. Cheap when
 * nothing was analysed (one status call per source). While any source is queued or running the key
 * is read again every 2 s, and a finished run elsewhere refreshes it via {@link refreshAnalysis}.
 */
export function useDocumentMediaAnalysis(projectId: string, mediaRefs: readonly string[]) {
  const refs = mediaRefs.slice(0, MAX_ANALYSIS_SOURCES);
  const { data: statuses } = useDocumentSourceStatuses(projectId, refs);
  const working = (statuses ?? []).some((s) => isWorking(s ?? undefined));
  const result = useSWR<FileAnalysisData[]>(
    projectId && refs.length > 0 ? ['file-analysis', projectId, refs.join(',')] : null,
    ([, p, ids]: readonly [string, string, string]) => Promise.all(ids.split(',').map((r) => loadSource(p, r))),
    { revalidateOnFocus: true, keepPreviousData: true, refreshInterval: working ? 2000 : 0 },
  );
  // The status poll can see a run finish before this key's own poll does (and stopping the poll cancels it), so
  // whenever the set of source states changes, read the results again: the words are always fetched after completion.
  const signature = (statuses ?? []).map((s) => (s ? ANALYSIS_KINDS.map((k) => s.kinds[k]?.state ?? 'none').join('.') : 'x')).join('|');
  const { mutate } = result;
  const last = useRef(signature);
  // A source whose status says ready while the cached results lack that kind (a stale empty answer) is read once more per state.
  const loaded = result.data;
  const staleReady = !!loaded && (statuses ?? []).some((s, i) => !!s && !!loaded[i] && !loaded[i].statusFailed && (['silence', 'shots', 'motion', 'transcript'] as const).some((k) => s.kinds[k]?.state === 'ready' && loaded[i][k] === null));
  const retried = useRef<string | null>(null);
  useEffect(() => {
    if (last.current !== signature) {
      last.current = signature;
      retried.current = null;
      void mutate();
    } else if (staleReady && retried.current !== signature) {
      retried.current = signature;
      void mutate();
    }
  }, [signature, staleReady, mutate]);
  return result;
}

/** The status of each media source (null where it could not be read); polls every 2 s while any is queued or running. */
export function useDocumentSourceStatuses(projectId: string, mediaRefs: readonly string[]) {
  const refs = mediaRefs.slice(0, MAX_ANALYSIS_SOURCES);
  return useSWR<Array<MediaAnalysisStatus | null>>(
    projectId && refs.length > 0 ? ['file-analysis-status', projectId, 'all', refs.join(',')] : null,
    ([, p, , ids]: readonly [string, string, string, string]) => Promise.all(ids.split(',').map((r) => fetchMediaAnalysisStatus(p, r).catch(() => null))),
    { refreshInterval: (latest) => ((latest ?? []).some((s) => isWorking(s ?? undefined)) ? 2000 : 0), revalidateOnFocus: true },
  );
}

/** The same results for project files only (callers that key by file id). */
export function useDocumentFileAnalysis(projectId: string, fileIds: readonly string[]) {
  return useDocumentMediaAnalysis(projectId, fileIds.map(fileMediaRef));
}
