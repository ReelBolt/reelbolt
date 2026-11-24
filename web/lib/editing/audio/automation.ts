/**
 * P8: automation lanes — read and latch write — over the faders the document already keyframes.
 *
 * A track's (or a clip's) `gainDb` is a `Keyframed<number>`, so an automation lane IS that channel's
 * keys; there is no second representation. The modes:
 *
 *   - **off** — the fader is the static `value`, keys are ignored by the UI (the renderer still plays
 *     whatever keys exist; turning a lane off never deletes automation);
 *   - **read** — the fader follows the keys during playback (`valueAt`);
 *   - **latch** — while playing, every fader move is recorded; on stop the recorded pass becomes keys
 *     over `[firstTouch, stop)` — previous keys inside that window are replaced, keys outside it kept.
 *
 * A latch pass compiles to ONE program (`compileLatchPass`) of `removeKeyframe`/`setKeyframe` ops,
 * thinned so a held fader writes two keys rather than one per frame, so a whole pass is one undo
 * step and every value passed through the engine's range check.
 */

import { KEYFRAME_PARAM_RANGE, type EditOp, type KeyframeTarget } from '../engine';
import type { Keyframed } from '../types';

export type AutomationMode = 'off' | 'read' | 'latch';

/** One fader sample captured while writing. */
export interface AutomationSample {
  frame: number;
  value: number;
}

/** The channel's value at `frame` (linear between keys, `step` held) — the "read" mode. */
export function valueAt(channel: Keyframed<number> | null | undefined, frame: number, fallback = 0): number {
  const base = typeof channel?.value === 'number' ? channel.value : fallback;
  const keys = channel?.keys ?? [];
  if (keys.length === 0) return base;
  if (frame <= keys[0].frame) return keys[0].value;
  for (let i = 0; i + 1 < keys.length; i++) {
    const a = keys[i];
    const b = keys[i + 1];
    if (frame >= a.frame && frame < b.frame) {
      if (a.interp === 'step') return a.value;
      const t = (frame - a.frame) / (b.frame - a.frame);
      return a.value + (b.value - a.value) * t;
    }
  }
  return keys[keys.length - 1].value;
}

/**
 * Reduces samples to the keys that reproduce them within `toleranceDb` (Ramer–Douglas–Peucker over
 * frame/value), always keeping the first and last sample. Samples must be frame-ordered; duplicate
 * frames keep the last value written.
 */
export function thinSamples(samples: readonly AutomationSample[], toleranceDb = 0.5): AutomationSample[] {
  const byFrame = new Map<number, number>();
  for (const s of samples) byFrame.set(Math.round(s.frame), s.value);
  const points = [...byFrame.entries()].sort((a, b) => a[0] - b[0]).map(([frame, value]) => ({ frame, value }));
  if (points.length <= 2) return points;
  const keep = new Array<boolean>(points.length).fill(false);
  keep[0] = true;
  keep[points.length - 1] = true;
  const stack: [number, number][] = [[0, points.length - 1]];
  while (stack.length > 0) {
    const [lo, hi] = stack.pop() as [number, number];
    let worst = -1;
    let worstAt = -1;
    for (let i = lo + 1; i < hi; i++) {
      const t = (points[i].frame - points[lo].frame) / (points[hi].frame - points[lo].frame);
      const line = points[lo].value + (points[hi].value - points[lo].value) * t;
      const err = Math.abs(points[i].value - line);
      if (err > worst) {
        worst = err;
        worstAt = i;
      }
    }
    if (worst > toleranceDb && worstAt > 0) {
      keep[worstAt] = true;
      stack.push([lo, worstAt], [worstAt, hi]);
    }
  }
  return points.filter((_, i) => keep[i]);
}

/**
 * The program a latch pass writes: remove the existing keys inside `[from, to]`, then set the
 * thinned samples (clamped to the gain range). Empty when nothing was touched.
 */
export function compileLatchPass(target: KeyframeTarget, channel: Keyframed<number> | null | undefined, samples: readonly AutomationSample[], toleranceDb = 0.5): EditOp[] {
  if (samples.length === 0) return [];
  const range = KEYFRAME_PARAM_RANGE.gainDb;
  const thinned = thinSamples(samples, toleranceDb).map((s) => ({ frame: Math.max(0, s.frame), value: Math.min(range.max, Math.max(range.min, Math.round(s.value * 10) / 10)) }));
  const from = thinned[0].frame;
  const to = thinned[thinned.length - 1].frame;
  const ops: EditOp[] = [];
  for (const key of channel?.keys ?? []) {
    if (key.frame >= from && key.frame <= to) ops.push({ type: 'removeKeyframe', target, param: 'gainDb', frame: key.frame });
  }
  for (const s of thinned) ops.push({ type: 'setKeyframe', target, param: 'gainDb', frame: s.frame, value: s.value, interp: 'linear' });
  return ops;
}
