'use client';

/**
 * The two facts "Cut to the music" takes from data the editor already has (T-008): each source file's
 * probed length (the media pool) and each clip file's free `motion` analysis. Both are keyed by project
 * file id for `CutToBeatDialog`. The analysis is only fetched while the dialog is open.
 */

import { useMemo } from 'react';
import { useMediaPool } from '@/lib/hooks/use-media';
import { documentFileIds, fileMediaRef, useDocumentFileAnalysis } from '../api/media-analysis';
import type { EditTimeline } from '../types';
import type { MotionByFileId, MotionWindow, ProbeDurationsByFileId } from './feeds';

export interface CutToBeatFeeds {
  probeDurations: ProbeDurationsByFileId;
  motion: MotionByFileId;
}

const NO_FEEDS: CutToBeatFeeds = { probeDurations: {}, motion: {} };

/** Probe lengths and motion windows per project file id, read only while `active`. */
export function useCutToBeatFeeds(projectId: string, doc: EditTimeline, active: boolean): CutToBeatFeeds {
  const { data: pool } = useMediaPool(active ? projectId : null);
  const fileIds = useMemo(() => documentFileIds(doc), [doc]);
  const { data: files } = useDocumentFileAnalysis(projectId, active ? fileIds : []);

  const probeDurations = useMemo<ProbeDurationsByFileId>(() => {
    if (!active) return NO_FEEDS.probeDurations;
    const byRef = new Map((pool ?? []).map((item) => [item.mediaRef, item.probe?.durationSec ?? null]));
    const out: Record<string, number | null> = {};
    for (const id of fileIds) out[id] = byRef.get(fileMediaRef(id)) ?? null;
    return out;
  }, [active, pool, fileIds]);

  const motion = useMemo<MotionByFileId>(() => {
    if (!active || !files) return NO_FEEDS.motion;
    const out: Record<string, readonly MotionWindow[] | null> = {};
    for (const file of files) if (file.projectFileId !== null) out[file.projectFileId] = file.motion;
    return out;
  }, [active, files]);

  return { probeDurations, motion };
}
