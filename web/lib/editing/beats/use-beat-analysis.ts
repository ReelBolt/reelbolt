'use client';

/**
 * Beat analysis for one music clip: its per-file status, the parsed `BeatAnalysis` once ready, and a
 * `start()` that runs the free `beats` analysis on demand. Status is polled the way AnalysePanel
 * polls (`useMediaAnalysisStatus`, every 2 s while a kind is queued or running); the data is fetched
 * only once the kind is ready and read defensively by `readBeatAnalysis`. A clip that does not show
 * a project file (a step-result render) cannot be analysed per file, so it is `unsupported`.
 */

import { useCallback, useMemo, useState } from 'react';
import useSWR from 'swr';
import {
  describeAnalysisError,
  fetchAnalysisData,
  fileMediaRef,
  startMediaAnalysis,
  useMediaAnalysisStatus,
  type AnalysisState,
} from '../api/media-analysis';
import type { MediaClip } from '../types';
import { readBeatAnalysis, type BeatAnalysis } from './beat-grid';

export type BeatAnalysisState = AnalysisState | 'unsupported';

export interface UseBeatAnalysis {
  state: BeatAnalysisState;
  /** The service's failure reason, verbatim, when the last run failed. */
  reason: string | null;
  /** The parsed analysis, once the file's beat analysis is ready and readable. */
  analysis: BeatAnalysis | null;
  /** Runs (or re-runs) the free beat analysis for this clip's file. */
  start: () => Promise<void>;
  starting: boolean;
  /** A plain sentence about a failed start or read, when there is one. */
  error: string | null;
}

const projectFileOf = (clip: MediaClip | null): string | null => {
  const ref = clip?.assetRef;
  return ref?.kind === 'projectFile' && typeof ref.projectFileId === 'string' && ref.projectFileId.length > 0 ? ref.projectFileId : null;
};

export function useBeatAnalysis(projectId: string, musicClip: MediaClip | null): UseBeatAnalysis {
  const fileId = projectFileOf(musicClip);
  const { data: status, mutate, error: statusError } = useMediaAnalysisStatus(projectId, fileId);
  const [startError, setStartError] = useState<string | null>(null);
  const [starting, setStarting] = useState(false);

  const kind = status?.kinds.beats;
  const state: BeatAnalysisState = musicClip !== null && fileId === null ? 'unsupported' : (kind?.state ?? 'none');

  // A ready kind is read once; the key disappears while it is queued or running, so a re-run refetches.
  const { data: raw, error: dataError } = useSWR(
    projectId && fileId !== null && state === 'ready' ? ['beat-analysis-data', projectId, fileId] : null,
    ([, p, f]: readonly [string, string, string]) => fetchAnalysisData<unknown>(p, fileMediaRef(f), 'beats'),
    { revalidateOnFocus: false },
  );
  const analysis = useMemo(() => (raw === undefined ? null : readBeatAnalysis(raw)), [raw]);

  const start = useCallback(async () => {
    if (!projectId || fileId === null) return;
    setStarting(true);
    setStartError(null);
    try {
      await startMediaAnalysis(projectId, fileMediaRef(fileId), ['beats']);
      await mutate();
    } catch (e) {
      setStartError(describeAnalysisError(e));
    } finally {
      setStarting(false);
    }
  }, [projectId, fileId, mutate]);

  const readError = statusError ?? dataError;
  const error = startError ?? (readError ? describeAnalysisError(readError) : null);
  return { state, reason: kind?.reason ?? null, analysis, start, starting, error };
}
