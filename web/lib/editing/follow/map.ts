/**
 * Follow subject, pure parts: the camera keys the server returns (anchored to SOURCE seconds) become the clip's
 * ordinary position / scale keyframes on the TIMELINE, and a result is only applied while the clip is still the
 * one it was computed for. No network, no store: both the section and the tests read this.
 */

import type { ClipTransform, Keyframed, MediaClip, Rational } from '../types';
import type { FollowAssumed, FollowKey, SubjectBox } from '../api/follow';
import { canvasToLocal, baseSize, frameFor, type TransformValues } from '../viewer-transform/geometry';
import { fpsOf } from '../preview/scheduler';

/** The facts of a clip a follow result is only valid for. Trimming or moving the clip in time is not among them. */
export type FollowSnapshot = {
  assetRef: string;
  speed: number;
  reverse: boolean;
  hold: boolean;
  crop: string;
  reframe: string;
  stabilize: boolean;
  canvasWidth: number;
  canvasHeight: number;
};

export function snapshotClip(clip: MediaClip, canvas: { width: number; height: number }): FollowSnapshot {
  return {
    assetRef: JSON.stringify(clip.assetRef ?? null),
    speed: typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1,
    reverse: clip.reverse === true,
    hold: clip.hold === true,
    crop: JSON.stringify(clip.crop ?? null),
    reframe: JSON.stringify(clip.reframe ?? null),
    stabilize: clip.stabilize === true,
    canvasWidth: canvas.width,
    canvasHeight: canvas.height,
  };
}

/** True when the clip is no longer what the result was computed for, or the canvas differs from what the server assumed. */
export function isFollowStale(requested: FollowSnapshot, clip: MediaClip, canvas: { width: number; height: number }, assumed?: FollowAssumed | null): boolean {
  const now = snapshotClip(clip, canvas);
  for (const k of Object.keys(requested) as (keyof FollowSnapshot)[]) if (requested[k] !== now[k]) return true;
  if (now.hold) return true;
  if (assumed && (assumed.canvasWidth !== canvas.width || assumed.canvasHeight !== canvas.height)) return true;
  return false;
}

/**
 * The timeline frame (fractional) where the clip shows source second `sourceSec`: the inverse of
 * `sourcePositionFor` (in-point, constant speed, reverse). Null for a held clip (every frame is one source frame).
 */
export function timelineFrameForSource(clip: MediaClip, sourceSec: number, rate: Rational): number | null {
  if (clip.hold === true) return null;
  const sourceIn = typeof clip.sourceIn === 'number' ? clip.sourceIn : 0;
  const speed = typeof clip.playbackRate === 'number' && clip.playbackRate > 0 ? clip.playbackRate : 1;
  const offset = sourceSec * fpsOf(rate) - sourceIn;
  return clip.reverse === true ? clip.recordOut - offset / speed : clip.recordIn + offset / speed;
}

export type FollowTransformResult = { transform: ClipTransform; keyCount: number } | { transform: null; keyCount: 0 };

const keyed = (value: number, keys: { frame: number; value: number }[]): Keyframed<number> => ({
  value,
  keys: keys.map((k) => ({ frame: k.frame, value: k.value, interp: 'linear' as const })),
});

/**
 * The clip's transform with `positionX`, `positionY` and `scale` replaced by linear keyframes at integer timeline
 * frames. Keys outside the clip drop, keys landing on the same frame keep the last, the base value is the first key.
 * Rotation, opacity and anything else in the transform stays as it was.
 */
export function buildFollowTransform(clip: MediaClip, keys: readonly FollowKey[], rate: Rational): FollowTransformResult {
  const byFrame = new Map<number, FollowKey>();
  for (const k of keys) {
    const f = timelineFrameForSource(clip, k.sourceSec, rate);
    if (f === null || !Number.isFinite(f)) continue;
    let frame = Math.round(f);
    // A key at the very end of the used range lands on recordOut (the first frame after the clip): it belongs to the
    // last frame shown, or a two-key pan would not move at all.
    if (frame === clip.recordOut) frame = clip.recordOut - 1;
    if (frame < clip.recordIn || frame >= clip.recordOut) continue;
    byFrame.set(frame, k);
  }
  const frames = [...byFrame.keys()].sort((a, b) => a - b);
  if (frames.length === 0) return { transform: null, keyCount: 0 };
  const pick = (read: (k: FollowKey) => number) => frames.map((frame) => ({ frame, value: read(byFrame.get(frame)!) }));
  const px = pick((k) => k.positionX);
  const py = pick((k) => k.positionY);
  const sc = pick((k) => k.scale);
  const transform: ClipTransform = {
    ...(clip.transform ?? {}),
    positionX: keyed(px[0].value, px),
    positionY: keyed(py[0].value, py),
    scale: keyed(sc[0].value, sc),
  };
  return { transform, keyCount: frames.length };
}

/** The lost spans (source seconds) as timeline frames, clipped to the clip and ordered; spans with no length in the clip drop. */
export function lostSpansInClip(clip: MediaClip, spans: readonly { fromSec: number; toSec: number }[], rate: Rational): { fromFrame: number; toFrame: number }[] {
  const out: { fromFrame: number; toFrame: number }[] = [];
  for (const s of spans) {
    const a = timelineFrameForSource(clip, s.fromSec, rate);
    const b = timelineFrameForSource(clip, s.toSec, rate);
    if (a === null || b === null) continue;
    const from = Math.max(clip.recordIn, Math.min(a, b));
    const to = Math.min(clip.recordOut, Math.max(a, b));
    if (to > from) out.push({ fromFrame: Math.round(from), toFrame: Math.round(to) });
  }
  return out.sort((x, y) => x.fromFrame - y.fromFrame);
}

/** Canvas pixels -> unclamped fractions of the SOURCE frame, through the clip's fit, crop and transform. Null when the picture has no size. */
export function canvasToSourceMapper(
  clip: MediaClip,
  values: TransformValues,
  canvas: { width: number; height: number },
  source: { width: number; height: number } | null,
  policy: 'fit' | 'fill' | 'stretch',
): ((p: { x: number; y: number }) => { x: number; y: number }) | null {
  // The preview fits a transformed clip by contain, whatever the timeline's aspect policy says.
  const transformed = Object.values(clip.transform ?? {}).some((c) => c !== null && c !== undefined);
  const base = baseSize(canvas, source, transformed ? 'fit' : policy, clip.crop ?? null);
  const frame = frameFor(values, base, canvas);
  if (!(frame.w > 0) || !(frame.h > 0)) return null;
  const edge = (v: number | undefined) => (typeof v === 'number' && Number.isFinite(v) && v > 0 && v < 1 ? v : 0);
  const left = edge(clip.crop?.left);
  const top = edge(clip.crop?.top);
  const wFrac = Math.max(0.01, 1 - left - edge(clip.crop?.right));
  const hFrac = Math.max(0.01, 1 - top - edge(clip.crop?.bottom));
  return (p) => {
    const l = canvasToLocal(frame, p);
    return { x: left + (l.x / frame.w + 0.5) * wFrac, y: top + (l.y / frame.h + 0.5) * hFrac };
  };
}

/**
 * A rectangle dragged on the canvas (two corners, canvas pixels) as a box in fractions of the SOURCE frame, through
 * the clip's fit, crop and transform at that frame. Null when the rectangle is too small, or misses the picture.
 */
export function canvasDragToSourceBox(
  clip: MediaClip,
  values: TransformValues,
  a: { x: number; y: number },
  b: { x: number; y: number },
  canvas: { width: number; height: number },
  source: { width: number; height: number } | null,
  policy: 'fit' | 'fill' | 'stretch',
): SubjectBox | null {
  const toSource = canvasToSourceMapper(clip, values, canvas, source, policy);
  if (!toSource) return null;
  const p = toSource(a);
  const q = toSource(b);
  const clamp01 = (v: number) => Math.min(1, Math.max(0, v));
  const x0 = clamp01(Math.min(p.x, q.x));
  const x1 = clamp01(Math.max(p.x, q.x));
  const y0 = clamp01(Math.min(p.y, q.y));
  const y1 = clamp01(Math.max(p.y, q.y));
  if (x1 - x0 < 0.01 || y1 - y0 < 0.01) return null;
  return { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}
