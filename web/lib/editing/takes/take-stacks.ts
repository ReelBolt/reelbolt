/**
 * Take stacking (P6): the analysis artifact's near-duplicate groups (`duplicateGroups`, built by
 * `FrameGridAnalyzer` from measured frame similarity) become alternates for the clips on the
 * timeline. Swapping a take is one `replace` op — the clip keeps its slot and duration and only its
 * `sourceIn` moves to the alternate shot's start — so the swap previews and undoes like any edit.
 */

import type { EditOp } from '../engine';
import { mapSourceSilenceToRecord, type SourceMatcher } from '../silence/remove-silence';
import type { AnalysedShot } from '../cut/cut';
import type { EditTimeline, MediaClip } from '../types';

export interface DuplicateGroup {
  id: string;
  shotIds: string[];
  bestShotId: string | null;
  meanSimilarity: number | null;
}

/** The `duplicateGroups` of a VideoAnalyze artifact, read defensively. */
export function readDuplicateGroups(artifact: unknown): DuplicateGroup[] {
  if (typeof artifact !== 'object' || artifact === null) return [];
  const raw = (artifact as Record<string, unknown>).duplicateGroups ?? (artifact as Record<string, unknown>).DuplicateGroups;
  if (!Array.isArray(raw)) return [];
  const out: DuplicateGroup[] = [];
  raw.forEach((entry, index) => {
    if (typeof entry !== 'object' || entry === null) return;
    const e = entry as Record<string, unknown>;
    const idsRaw = e.shotIds ?? e.ShotIds;
    const shotIds = Array.isArray(idsRaw) ? idsRaw.filter((s): s is string => typeof s === 'string') : [];
    if (shotIds.length < 2) return;
    const idRaw = e.id ?? e.Id;
    const bestRaw = e.bestShotId ?? e.BestShotId;
    const simRaw = e.meanSimilarity ?? e.MeanSimilarity;
    out.push({
      id: typeof idRaw === 'string' ? idRaw : `k${index}`,
      shotIds,
      bestShotId: typeof bestRaw === 'string' ? bestRaw : null,
      meanSimilarity: typeof simRaw === 'number' && Number.isFinite(simRaw) ? simRaw : null,
    });
  });
  return out;
}

export interface TakeAlternate {
  shot: AnalysedShot;
  isBest: boolean;
  /** True when the alternate shot is at least as long as the clip, so the swap needs no padding. */
  fits: boolean;
}

export interface TakeStack {
  clipId: string;
  clipName: string;
  recordIn: number;
  recordOut: number;
  groupId: string;
  /** The shot the clip currently shows. */
  currentShot: AnalysedShot;
  alternates: TakeAlternate[];
}

/** The shot whose source window contains the clip's source midpoint (of the clip's own file, with `matches`). */
export function shotShownBy(clip: MediaClip, shots: readonly AnalysedShot[], fps: number, matches?: SourceMatcher): AnalysedShot | null {
  const rate = typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const sourceIn = typeof clip.sourceIn === 'number' ? clip.sourceIn : 0;
  const midSec = (sourceIn + ((clip.recordOut - clip.recordIn) * rate) / 2) / fps;
  return shots.find((s) => midSec >= s.startSec && midSec < s.endSec && (!matches || matches(clip, s.sourceIndex))) ?? null;
}

/** Every clip on the track that has alternates, with the alternates. */
export function buildTakeStacks(doc: EditTimeline, trackId: string, shots: readonly AnalysedShot[], groups: readonly DuplicateGroup[], fps: number, matches?: SourceMatcher): TakeStack[] {
  const track = doc.tracks.find((t) => t.id === trackId);
  if (!track) return [];
  const byId = new Map(shots.map((s) => [s.id, s] as const));
  const stacks: TakeStack[] = [];
  for (const item of track.items) {
    if (item.type !== 'mediaClip') continue;
    const current = shotShownBy(item, shots, fps, matches);
    if (!current) continue;
    const group = groups.find((g) => g.shotIds.includes(current.id));
    if (!group) continue;
    const clipFrames = item.recordOut - item.recordIn;
    const rate = typeof item.playbackRate === 'number' && item.playbackRate > 0 ? item.playbackRate : 1;
    const alternates: TakeAlternate[] = group.shotIds
      .filter((id) => id !== current.id)
      .map((id) => byId.get(id))
      .filter((s): s is AnalysedShot => s !== undefined)
      // A swap only moves `sourceIn`, so an alternate must be in the same file as the clip.
      .filter((s) => s.sourceIndex === current.sourceIndex)
      .map((shot) => ({ shot, isBest: shot.id === group.bestShotId || shot.isBestTake, fits: (shot.endSec - shot.startSec) * fps >= clipFrames * rate }));
    if (alternates.length === 0) continue;
    stacks.push({ clipId: item.id, clipName: item.name, recordIn: item.recordIn, recordOut: item.recordOut, groupId: group.id, currentShot: current, alternates });
  }
  return stacks;
}

/**
 * Swaps the clip's take: a `replace` whose item is the same clip pointing at the alternate shot's
 * start. The slot, duration, transform and audio settings are untouched.
 */
export function compileSwapTake(doc: EditTimeline, clipId: string, alternate: AnalysedShot, fps: number): EditOp | null {
  const clip = doc.tracks.flatMap((t) => t.items).find((i): i is MediaClip => i.id === clipId && i.type === 'mediaClip');
  if (!clip) return null;
  const sourceIn = Math.max(0, Math.round(alternate.startSec * fps));
  if (sourceIn === (clip.sourceIn ?? 0)) return null;
  return { type: 'replace', itemId: clipId, item: { ...clip, sourceIn, provenance: clip.provenance ? { ...clip.provenance, editedByUser: true } : clip.provenance } };
}

/**
 * Why "Use this take" must not swap to `alternate`, as a sentence for the user; null when it may.
 * An alternate shorter than the clip would run the clip past the end of that take into whatever
 * follows it in the source (d4 N5), so it is refused rather than padded.
 */
export function swapTakeRefusal(doc: EditTimeline, clipId: string, alternate: AnalysedShot, fps: number): string | null {
  const clip = doc.tracks.flatMap((t) => t.items).find((i): i is MediaClip => i.id === clipId && i.type === 'mediaClip');
  if (!clip) return 'That clip is no longer on the timeline.';
  const rate = typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const neededSec = ((clip.recordOut - clip.recordIn) * rate) / fps;
  const takeSec = alternate.endSec - alternate.startSec;
  if (takeSec + 1e-6 >= neededSec) return null;
  return `This take is ${takeSec.toFixed(1)} s long but the clip needs ${neededSec.toFixed(1)} s, so it would run into the footage after the take. Shorten the clip first, or pick a longer take.`;
}

/** Where a shot sits on the track, for the stack's "go to" affordance. */
export function shotRecordSpan(doc: EditTimeline, trackId: string, shot: AnalysedShot, fps: number, matches?: SourceMatcher): { startFrame: number; endFrame: number } | null {
  const track = doc.tracks.find((t) => t.id === trackId);
  for (const item of track?.items ?? []) {
    if (item.type !== 'mediaClip') continue;
    const span = mapSourceSilenceToRecord(item, { id: shot.id, startSec: shot.startSec, endSec: shot.endSec, sourceIndex: shot.sourceIndex }, fps, matches);
    if (span) return span;
  }
  return null;
}
