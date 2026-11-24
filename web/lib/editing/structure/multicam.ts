/**
 * P8: multicam clips — create from synchronised clips, cut live, switch an angle later — each a
 * program of the closed op catalogue.
 *
 * A live cut during playback is ONE `setProperty('cuts')` per press (one undo step each), so a
 * multicam pass is reviewable and reversible cut by cut. Switching later edits the run under the
 * playhead: the clip's `activeAngleId` when the run starts at the clip's head, the cut that opened
 * the run otherwise. A cut on the angle already live is a no-op (`null`), never a redundant cut.
 */

import { EditEngineError, type EditOp } from '../engine';
import type { EditTimeline, MediaClip, MulticamAngle, MulticamClip, MulticamCut } from '../types';
import { angleAt } from './flatten';

/** Finds a multicam clip anywhere on the root tracks. */
export function findMulticam(doc: EditTimeline, itemId: string): MulticamClip | null {
  for (const track of doc.tracks) {
    const item = track.items.find((i) => i.id === itemId);
    if (item?.type === 'multicamClip') return item;
  }
  return null;
}

/**
 * The ops that replace the selected media clips with ONE multicam clip on the first clip's track
 * and span. Each clip becomes an angle reading its own source at `sourceIn + syncOffsets[clip.id]`
 * when the multicam source is at frame 0 — the offsets are what {@link estimateSyncOffset} measured
 * (in timeline frames), 0 for the reference angle.
 */
export function compileCreateMulticam(
  doc: EditTimeline,
  clipIds: readonly string[],
  ids: { multicamId: string; angleIds: readonly string[] },
  syncOffsets: Readonly<Record<string, number>> = {},
  name = 'Multicam',
  /** R08.2: whose sound plays: 'program' (first angle), 'none', or the clip id (of `clipIds`) whose angle supplies it. */
  audioFrom: string | 'program' | 'none' = 'program',
): EditOp[] {
  const clips: { clip: MediaClip; trackId: string }[] = [];
  for (const id of clipIds) {
    const track = doc.tracks.find((t) => t.items.some((i) => i.id === id));
    const item = track?.items.find((i) => i.id === id);
    if (!track || item?.type !== 'mediaClip') throw new EditEngineError(`'${id}' is not a media clip of this timeline`);
    clips.push({ clip: item, trackId: track.id });
  }
  if (clips.length < 2) throw new EditEngineError('a multicam clip needs at least two angles');
  if (ids.angleIds.length < clips.length) throw new EditEngineError('one angle id per clip is required');
  const first = clips[0];
  const angles: MulticamAngle[] = clips.map(({ clip }, i) => ({
    id: ids.angleIds[i],
    name: clip.name || `Angle ${i + 1}`,
    assetRef: clip.assetRef,
    offsetFrames: Math.round((clip.sourceIn ?? 0) + (syncOffsets[clip.id] ?? 0)),
    // P3: the angle keeps its source's camera profile, so every flattened run applies the transform.
    ...(clip.inputProfile != null ? { inputProfile: clip.inputProfile } : {}),
  }));
  const multicam: MulticamClip = {
    id: ids.multicamId,
    type: 'multicamClip',
    name,
    recordIn: first.clip.recordIn,
    recordOut: first.clip.recordOut,
    angles,
    activeAngleId: angles[0].id,
    cuts: [],
    sourceIn: 0,
    enabled: true,
    gainDb: first.clip.gainDb ?? null,
    provenance: null,
    ...(audioFrom === 'program' ? {} : { audioSource: audioFrom === 'none' ? 'none' : (angles[clips.findIndex((c) => c.clip.id === audioFrom)]?.id ?? 'program') }),
  };
  return [...clips.map(({ clip }): EditOp => ({ type: 'lift', itemId: clip.id })), { type: 'placeOnTop', trackId: first.trackId, item: multicam }];
}

/**
 * A live cut to `angleId` at `frame`: inserts (or retargets) the cut at that frame, or sets the
 * active angle when `frame` is the clip's first frame. `null` when that angle is already live.
 */
export function compileLiveCut(clip: MulticamClip, frame: number, angleId: string): EditOp | null {
  if (!clip.angles.some((a) => a.id === angleId)) throw new EditEngineError(`'${angleId}' is not an angle of ${clip.name}`);
  if (frame < clip.recordIn || frame >= clip.recordOut) throw new EditEngineError(`frame ${frame} is outside ${clip.name}`);
  if (angleAt(clip, frame) === angleId) return null;
  if (frame === clip.recordIn) return { type: 'setProperty', itemId: clip.id, key: 'activeAngleId', value: angleId };
  const cuts: MulticamCut[] = (clip.cuts ?? []).filter((c) => c.atFrame !== frame);
  cuts.push({ atFrame: frame, angleId });
  cuts.sort((a, b) => a.atFrame - b.atFrame);
  return { type: 'setProperty', itemId: clip.id, key: 'cuts', value: cuts };
}

/** Switch the angle of the run under `frame` (the "change it later" edit), keeping every cut frame. */
export function compileSwitchAngle(clip: MulticamClip, frame: number, angleId: string): EditOp | null {
  if (!clip.angles.some((a) => a.id === angleId)) throw new EditEngineError(`'${angleId}' is not an angle of ${clip.name}`);
  const cuts = [...(clip.cuts ?? [])].sort((a, b) => a.atFrame - b.atFrame);
  const opener = [...cuts].reverse().find((c) => c.atFrame <= frame);
  if (opener === undefined) {
    return clip.activeAngleId === angleId ? null : { type: 'setProperty', itemId: clip.id, key: 'activeAngleId', value: angleId };
  }
  if (opener.angleId === angleId) return null;
  return { type: 'setProperty', itemId: clip.id, key: 'cuts', value: cuts.map((c) => (c === opener ? { ...c, angleId } : c)) };
}
