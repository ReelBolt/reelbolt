'use client';

/**
 * The ONE place an editor panel learns which footage analysis belongs to a timeline (top-10 #4).
 *
 * A timeline is seeded from a **VideoCompile** step result. That result's artifact is the compile's
 * edit decision list (`VideoCompileStepExecutor.BuildEdl`): segments, codec, and a pointer back to
 * the analysis it cut from — `analysisStepResultId` (set only for a cross-run recompile) and
 * `analysisArtifactStorageKey` (always set). It carries no silence, words, shots or duplicate
 * groups, so a panel that reads the seed artifact directly is always empty. This module follows the
 * pointer:
 *
 * 1. the seed artifact already IS an analysis (it has `silenceSpans`/`shots`/`words`/…): use it;
 * 2. the EDL names `analysisStepResultId`: stream that step result's artifact;
 * 3. the EDL names only `analysisArtifactStorageKey`
 *    (`projects/{p}/agentFiles/video-analysis/{executionId}/step-{n}-analysis.json`): read that
 *    run (`GET /projects/{p}/executions/{executionId}`), pick the step result whose
 *    `artifactStorageKey` is exactly that key, and stream its artifact.
 *
 * Every request goes through the existing project-scoped routes, so the API stays the only trust
 * boundary; the storage key is only compared, never fetched or shown.
 *
 * The analysis also lists its sources (`sources[].storageKey`, indexed by `sourceIndex`). Those are
 * matched to the project's files so a span or word is only ever mapped onto clips that show the
 * same file ({@link sourceMatcher}): on a two-file track, file A's silence never cuts file B.
 */

import { useMemo } from 'react';
import useSWR from 'swr';
import { fetcher } from '@/lib/api/client';
import type { SourceMatcher } from '../silence/remove-silence';
import type { EditTimeline, MediaClip } from '../types';
import { clipMediaRef, documentMediaRefs, fileMediaRef, mergeFileAnalysis, useDocumentMediaAnalysis } from './media-analysis';

/** One analysed source and the project file it is, when the project still has that file. */
export interface AnalysisSource {
  sourceIndex: number;
  projectFileId: string | null;
  /** `file:<id>` or `step_result:<id>` when the source is known by ref (a render output has no project file id). */
  mediaRef?: string | null;
}

export interface ResolvedAnalysis {
  /** The VideoAnalyze step result whose artifact this is. */
  stepResultId: string;
  /** The analysis document, read defensively by each panel's own reader. */
  artifact: unknown;
  /** The analysed sources; empty for an analysis written before multi-source support. */
  sources: AnalysisSource[];
}

/** Fetches JSON from a same-origin API path; rejects on a non-2xx answer. */
export type JsonFetch = (url: string) => Promise<unknown>;

const artifactUrl = (projectId: string, stepResultId: string) => `/api/v1/projects/${projectId}/step-results/${stepResultId}/artifact`;

const GUID = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const EXECUTION_IN_KEY = /\/video-analysis\/([0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12})\//i;

const record = (value: unknown): Record<string, unknown> | null => (typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null);
const field = (obj: Record<string, unknown>, camel: string): unknown => obj[camel] ?? obj[camel.charAt(0).toUpperCase() + camel.slice(1)];

const ANALYSIS_KEYS = ['silenceSpans', 'shots', 'words', 'transcript', 'duplicateGroups', 'lookGroups'] as const;

/** True when the document is a VideoAnalyze artifact rather than a compile EDL. */
export function isAnalysisArtifact(artifact: unknown): boolean {
  const obj = record(artifact);
  if (!obj) return false;
  return ANALYSIS_KEYS.some((key) => Array.isArray(field(obj, key)) || record(field(obj, key)) !== null);
}

/** The analysed sources an analysis lists (`sources[].sourceIndex/storageKey`). */
export function readAnalysisSources(artifact: unknown): Array<{ sourceIndex: number; storageKey: string }> {
  const obj = record(artifact);
  const raw = obj ? field(obj, 'sources') : null;
  if (!Array.isArray(raw)) return [];
  const out: Array<{ sourceIndex: number; storageKey: string }> = [];
  raw.forEach((entry, index) => {
    const e = record(entry);
    if (!e) return;
    const idx = field(e, 'sourceIndex');
    const key = field(e, 'storageKey');
    if (typeof key !== 'string' || key.length === 0) return;
    out.push({ sourceIndex: typeof idx === 'number' && Number.isInteger(idx) ? idx : index, storageKey: key });
  });
  return out;
}

/**
 * Finds the analysis for a timeline seeded from `seedStepResultId`; null when the seed points at
 * no analysis. Pure apart from `fetchJson`, so it is tested with a stubbed fetch.
 */
export async function resolveAnalysisArtifact(projectId: string, seedStepResultId: string, fetchJson: JsonFetch): Promise<ResolvedAnalysis | null> {
  const seed = await fetchJson(artifactUrl(projectId, seedStepResultId));
  let stepResultId: string | null = null;
  let artifact: unknown = null;

  if (isAnalysisArtifact(seed)) {
    stepResultId = seedStepResultId;
    artifact = seed;
  } else {
    const edl = record(seed);
    if (!edl) return null;
    const explicitId = field(edl, 'analysisStepResultId');
    const key = field(edl, 'analysisArtifactStorageKey');
    if (typeof explicitId === 'string' && GUID.test(explicitId)) {
      stepResultId = explicitId;
    } else if (typeof key === 'string' && key.length > 0) {
      const execution = EXECUTION_IN_KEY.exec(key)?.[1];
      if (!execution) return null;
      const run = record(await fetchJson(`/api/v1/projects/${projectId}/executions/${execution}`));
      const results = run ? field(run, 'stepResults') : null;
      const match = Array.isArray(results)
        ? results.map(record).find((r): r is Record<string, unknown> => r !== null && field(r, 'artifactStorageKey') === key)
        : undefined;
      const id = match ? field(match, 'id') : null;
      if (typeof id !== 'string' || !GUID.test(id)) return null;
      stepResultId = id;
    } else {
      return null;
    }
    artifact = await fetchJson(artifactUrl(projectId, stepResultId));
    if (!isAnalysisArtifact(artifact)) return null;
  }

  const listed = readAnalysisSources(artifact);
  let sources: AnalysisSource[] = [];
  if (listed.length > 0) {
    const files = await fetchJson(`/api/v1/projects/${projectId}/files`);
    const idByKey = new Map<string, string>();
    if (Array.isArray(files)) {
      for (const f of files) {
        const r = record(f);
        const id = r ? field(r, 'id') : null;
        const storageKey = r ? field(r, 'storageKey') : null;
        if (typeof id === 'string' && typeof storageKey === 'string') idByKey.set(storageKey, id);
      }
    }
    sources = listed.map((s) => ({ sourceIndex: s.sourceIndex, projectFileId: idByKey.get(s.storageKey) ?? null }));
  }
  return { stepResultId, artifact, sources };
}

const clipFileId = (clip: MediaClip): string | null => clipMediaRef(clip);

/**
 * Whether a clip shows an analysed source: its project file is the file the analysis measured. An
 * analysis that lists no sources (written before multi-source support) is trusted only when every
 * media clip in the document shows one and the same file; otherwise nothing is mapped, since a
 * guess could cut the wrong footage.
 */
export function sourceMatcher(analysis: Pick<ResolvedAnalysis, 'sources'> | null | undefined, doc: EditTimeline): SourceMatcher {
  if (!analysis) return () => false;
  if (analysis.sources.length === 0) {
    const files = new Set<string | null>();
    for (const track of doc.tracks) for (const item of track.items) if (item.type === 'mediaClip') files.add(clipFileId(item));
    const only = files.size === 1 ? [...files][0] : null;
    return (clip, sourceIndex) => only !== null && sourceIndex === 0 && clipFileId(clip) === only;
  }
  const bySource = new Map(analysis.sources.map((s) => [s.sourceIndex, s.mediaRef ?? (s.projectFileId ? fileMediaRef(s.projectFileId) : null)] as const));
  return (clip, sourceIndex) => {
    const file = bySource.get(sourceIndex);
    return typeof file === 'string' && clipFileId(clip) === file;
  };
}

/**
 * The analysis for a timeline, cached per seed. `data` is null when nothing carries analysis,
 * undefined while loading or when there is neither a seed nor a document.
 *
 * With `doc`, the per-file analysis a user ran on demand (the Analyse panel) fills in whatever the
 * seed analysis lacks, so a blank timeline - or one whose run never made a transcript - works too.
 */
export function useTimelineAnalysis(projectId: string, seedStepResultId: string | null, doc?: EditTimeline) {
  const seed = useSWR<ResolvedAnalysis | null>(
    projectId && seedStepResultId ? ['timeline-analysis', projectId, seedStepResultId] : null,
    ([, p, s]: readonly [string, string, string]) => resolveAnalysisArtifact(p, s, (url) => fetcher<unknown>(url)),
  );
  const refs = useMemo(() => (doc ? documentMediaRefs(doc) : []), [doc]);
  const files = useDocumentMediaAnalysis(projectId, refs);
  const merged = useMemo(
    () => (doc && files.data ? mergeFileAnalysis(seed.data, files.data, doc) : seed.data),
    [doc, files.data, seed.data],
  );
  return { data: merged, isLoading: seed.isLoading || (doc !== undefined && files.isLoading), error: seed.error as Error | undefined };
}
