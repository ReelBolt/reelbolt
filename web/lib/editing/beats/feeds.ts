/**
 * The two facts "Cut to the music" needs besides the beat grid, read from data the editor already has:
 * how long each clip's source file is (so `fit` may lengthen a clip) and how much motion each clip
 * shows (so the strongest clip can be put on the drop). Pure; nothing here fetches.
 *
 * ## Probe durations
 * The editor already reads source lengths from the project media pool. `useMediaPool(projectId)`
 * (`lib/hooks/use-media.ts`) returns `MediaPoolItem[]`; for a project file the item's `mediaRef` is
 * `fileMediaRef(projectFileId)` (from `lib/editing/api/media-analysis`) and `probe?.durationSec` is
 * the source length in seconds — `probe === null` (never analysed) or a null field means the length
 * is unknown, never zero. A caller builds the map this file expects without parsing the opaque
 * `mediaRef`: for each project-file id on the timeline, find the item whose `mediaRef` equals
 * `fileMediaRef(id)` and take its `probe?.durationSec`.
 *
 * ## Motion
 * The free `motion` analysis answers `{ windows: [{ startSec, endSec, mean, motionClass }] }`, read
 * into `FileAnalysisData['motion']` by `lib/editing/api/media-analysis.ts`, keyed by project file id.
 */

import type { EditTimeline, MediaClip } from '../types';

/** One motion-analysis window, in source seconds of its file (`AnalysisMotionResult`'s shape). */
export interface MotionWindow {
  startSec: number;
  endSec: number;
  mean: number;
  motionClass?: string;
}

/** Probed source lengths in seconds, keyed by project file id; null/absent means never probed. */
export type ProbeDurationsByFileId = Readonly<Record<string, number | null | undefined>>;

/** Motion windows keyed by project file id; null/absent means no motion analysis for that file. */
export type MotionByFileId = Readonly<Record<string, readonly MotionWindow[] | null | undefined>>;

const projectFileOf = (clip: MediaClip): string | null => {
  const ref = clip.assetRef;
  return ref.kind === 'projectFile' && typeof ref.projectFileId === 'string' && ref.projectFileId.length > 0 ? ref.projectFileId : null;
};

/**
 * `compileCutToBeat`'s `sourceFrames`: the clip's file length in source frames at `fps` (before
 * playbackRate), or null when the clip is not a project file, its file was never probed, or the
 * rate is unusable. A length of zero is treated as unknown, so a clip is never lengthened on it.
 */
export function sourceFramesFromProbe(durations: ProbeDurationsByFileId, fps: number): (clip: MediaClip) => number | null {
  const usableFps = Number.isFinite(fps) && fps > 0 ? fps : null;
  return (clip) => {
    if (usableFps === null) return null;
    const fileId = projectFileOf(clip);
    if (fileId === null) return null;
    const seconds = durations[fileId];
    if (typeof seconds !== 'number' || !Number.isFinite(seconds) || seconds <= 0) return null;
    return Math.round(seconds * usableFps);
  };
}

/** Duration-weighted mean of the motion windows overlapping `[startSec, endSec)`; null when none is. */
function meanMotion(startSec: number, endSec: number, windows: readonly MotionWindow[]): number | null {
  let total = 0;
  let weighted = 0;
  for (const w of windows) {
    if (!Number.isFinite(w.startSec) || !Number.isFinite(w.endSec) || !Number.isFinite(w.mean)) continue;
    const overlap = Math.min(endSec, w.endSec) - Math.max(startSec, w.startSec);
    if (overlap <= 0) continue;
    total += overlap;
    weighted += overlap * w.mean;
  }
  return total > 0 ? weighted / total : null;
}

/**
 * Mean motion per media clip of one track, keyed by item id (higher is stronger). A clip's source
 * window is `sourceIn` plus its record length times playbackRate, converted to source seconds at the
 * document rate; reversed clips touch the same window (`sourceIn` stays the lowest source frame).
 * Clips whose file has no motion analysis, or whose window no window overlaps, are absent.
 */
export function motionRankForClips(doc: EditTimeline, trackId: string, motionByFileId: MotionByFileId): Record<string, number> {
  const rank: Record<string, number> = {};
  const track = doc.tracks.find((t) => t.id === trackId);
  const rate = doc.settings.rate;
  const fps = rate.den > 0 ? rate.num / rate.den : 0;
  if (!track || !Number.isFinite(fps) || fps <= 0) return rank;
  for (const item of track.items) {
    if (item.type !== 'mediaClip') continue;
    const fileId = projectFileOf(item);
    const windows = fileId === null ? null : motionByFileId[fileId];
    if (!windows || windows.length === 0) continue;
    const playbackRate = typeof item.playbackRate === 'number' && item.playbackRate > 0 ? item.playbackRate : 1;
    const startSec = (typeof item.sourceIn === 'number' ? item.sourceIn : 0) / fps;
    const endSec = startSec + ((item.recordOut - item.recordIn) * playbackRate) / fps;
    const mean = meanMotion(startSec, endSec, windows);
    if (mean !== null) rank[item.id] = mean;
  }
  return rank;
}
