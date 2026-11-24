/**
 * Ducking as an EDITABLE envelope (P5): the music track's fader automation (`track.gainDb.keys`)
 * is derived from where dialogue plays, and is then an ordinary keyframe curve — every point can be
 * dragged, deleted or re-derived, and the renderer plays exactly what the compositor previews
 * because both read the same keys.
 *
 * The derivation is deterministic and pure: dialogue spans are the enabled media clips of the
 * chosen dialogue tracks (merged when closer than attack + release), the bed level holds between
 * them, and each span gets a linear attack ramp down to `bed + depth` before it starts and a linear
 * release ramp back up after it ends. Frames are strictly increasing by construction (ramps that
 * would collide are merged into one span), so the result always satisfies the keyframe contract.
 */

import type { EditTimeline, Keyframed, Keyframe } from '../types';

export interface DuckingOptions {
  /** Tracks whose clips are the dialogue to duck under. */
  dialogueTrackIds: readonly string[];
  /** The music's level between dialogue, dB (the fader's static value is replaced by this). */
  bedDb: number;
  /** How far below the bed the music sits under dialogue, dB (negative, e.g. -12). */
  depthDb: number;
  /** Ramp down length before each dialogue span, frames. */
  attackFrames: number;
  /** Ramp up length after each dialogue span, frames. */
  releaseFrames: number;
}

export const DEFAULT_DUCKING: Omit<DuckingOptions, 'dialogueTrackIds'> = {
  bedDb: 0,
  depthDb: -12,
  attackFrames: 8,
  releaseFrames: 20,
};

/** Merged `[start, end)` spans where any of the dialogue tracks has an enabled media clip. */
export function dialogueSpans(doc: EditTimeline, dialogueTrackIds: readonly string[], mergeGap: number): Array<[number, number]> {
  const raw: Array<[number, number]> = [];
  for (const track of doc.tracks) {
    if (!dialogueTrackIds.includes(track.id)) continue;
    for (const item of track.items) {
      if (item.type !== 'mediaClip' || item.enabled === false || item.hold === true) continue;
      raw.push([item.recordIn, item.recordOut]);
    }
  }
  raw.sort((a, b) => a[0] - b[0]);
  const merged: Array<[number, number]> = [];
  for (const span of raw) {
    const last = merged[merged.length - 1];
    if (last && span[0] <= last[1] + mergeGap) last[1] = Math.max(last[1], span[1]);
    else merged.push([span[0], span[1]]);
  }
  return merged;
}

/**
 * The music fader curve: `{ value: bed, keys }`. With no dialogue at all the keys are empty and
 * the fader is just the bed. Negative frames are clamped to 0 (the first ramp starts at the head).
 */
export function deriveDucking(doc: EditTimeline, options: DuckingOptions): Keyframed<number> {
  const attack = Math.max(0, Math.round(options.attackFrames));
  const release = Math.max(0, Math.round(options.releaseFrames));
  const bed = clampDb(options.bedDb);
  const ducked = clampDb(options.bedDb + options.depthDb);
  const spans = dialogueSpans(doc, options.dialogueTrackIds, attack + release);
  const keys: Keyframe<number>[] = [];
  const push = (frame: number, value: number) => {
    const f = Math.max(0, frame);
    const last = keys[keys.length - 1];
    if (last && f <= last.frame) {
      // A ramp collapsed onto its neighbour (zero attack, or a span at frame 0): keep the later value.
      last.value = value;
      return;
    }
    keys.push({ frame: f, value, interp: 'linear' });
  };
  for (const [start, end] of spans) {
    push(start - attack, bed);
    push(start, ducked);
    push(end, ducked);
    push(end + release, bed);
  }
  return { value: bed, keys };
}

function clampDb(db: number): number {
  return Math.max(-60, Math.min(20, Number.isFinite(db) ? db : 0));
}
