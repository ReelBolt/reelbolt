'use client';

/**
 * The beat grid drawn on the timeline (T-008): the music clip the editor would cut to, its free beat
 * analysis, and the analysis' grid lines mapped onto that clip's record frames. The overlay is derived,
 * never stored in the document; the same frames are offered to snapping while "Show beats" is on.
 */

import { useMemo } from 'react';
import type { EditTimeline, MediaClip } from '../types';
import { buildBeatGrid, gridLines, NO_CORRECTION } from './beat-grid';
import { useBeatChoice } from './beat-choice-store';
import { beatSecondsToRecordFrames, type RecordBeat } from './map-to-record';
import { useBeatAnalysis } from './use-beat-analysis';

const isMedia = (item: { type: string }): item is MediaClip => item.type === 'mediaClip';

/**
 * The clip "Cut to the music" would act on: a selected audio clip first, then any selected media clip,
 * then the first audio clip on the timeline, then any media clip. Holds have no sound and are skipped.
 */
export function pickMusicClip(doc: EditTimeline, selectedItemIds: readonly string[]): MediaClip | null {
  const mediaTracks = doc.tracks.filter((t) => t.items.some(isMedia));
  const candidates = mediaTracks.flatMap((t) =>
    t.items.filter(isMedia).filter((i) => i.hold !== true).map((clip) => ({ clip, track: t })),
  );
  const selected = new Set(selectedItemIds);
  const preferred = candidates.find((c) => selected.has(c.clip.id) && c.track.kind === 'audio')
    ?? candidates.find((c) => selected.has(c.clip.id))
    ?? candidates.find((c) => c.track.kind === 'audio')
    ?? candidates[0];
  return preferred?.clip ?? null;
}

export interface BeatGridOverlay {
  /** The grid's record frames on the music clip, bars flagged; empty until an analysis is ready. */
  beats: readonly RecordBeat[];
  /** True when the music clip's beat analysis is ready and readable. */
  ready: boolean;
  musicClipId: string | null;
}

/** The beat grid overlay for the timeline, from the music clip the selection points at. */
export function useBeatGridOverlay(projectId: string, doc: EditTimeline, selectedItemIds: readonly string[]): BeatGridOverlay {
  const choice = useBeatChoice(projectId);
  const music = useMemo(() => {
    // The dialog's music clip wins while it is still on the timeline; otherwise the selection's pick.
    const chosen = choice ? doc.tracks.flatMap((t) => t.items).find((i): i is MediaClip => i.type === 'mediaClip' && i.id === choice.musicClipId && i.hold !== true) : undefined;
    return chosen ?? pickMusicClip(doc, selectedItemIds);
  }, [doc, selectedItemIds, choice]);
  const correction = choice && music && choice.musicClipId === music.id ? choice.correction : NO_CORRECTION;
  const beat = useBeatAnalysis(projectId, music);
  const fps = doc.settings.rate.den === 0 ? 30 : doc.settings.rate.num / doc.settings.rate.den;

  const beats = useMemo(() => {
    if (!music || !beat.analysis) return [];
    const grid = buildBeatGrid(beat.analysis, correction);
    const mapped = beatSecondsToRecordFrames(music, gridLines(grid, 'beat'), fps);
    return Array.isArray(mapped) ? mapped : [];
  }, [music, beat.analysis, fps, correction]);

  return { beats, ready: beat.analysis !== null, musicClipId: music?.id ?? null };
}
