/**
 * Click to select, pure parts: one object the user pointed at in one video (a "seed"). Fractions are of the upright
 * SOURCE frame, time is a second of the SOURCE; the same rules the server applies (finite 0..1, box >= 1%).
 * No network, no store: the viewer's picker layer, the Follow and Objects sections and the tests all read this.
 */

import type { MediaClip } from '../types';
import type { SubjectBox } from '../api/follow';
import type { TransformValues } from '../viewer-transform/geometry';
import { canvasToSourceMapper } from '../follow/map';

export type SeedPoint = { x: number; y: number };

export type ObjectSeed = {
  mediaRef: string;
  /** Seconds of the SOURCE, never a timeline frame. */
  atSec: number;
  point: SeedPoint;
  box?: SubjectBox;
  /** True while `box` is only the one laid around a tap: a hosted request then sends the point alone (a box means "drawn"). */
  boxIsDefault?: true;
};

/** The wire shape of a seed in a request body (contract: `seed:{atSec,point:{x,y},box?:{x,y,w,h}}`). */
export type SeedBody = { atSec: number; point: SeedPoint; box?: SubjectBox };

export const MIN_BOX = 0.01;
/** The box laid around a tap, as a fraction of the source frame (each side). */
export const DEFAULT_BOX_SIDE = 0.2;
/** One arrow key press moves the marker this much (1% of the frame). */
export const NUDGE = 0.01;

const frac = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v) && v >= 0 && v <= 1;
const clamp01 = (v: number) => Math.min(1, Math.max(0, v));

export const isValidBox = (b: SubjectBox | undefined | null): b is SubjectBox =>
  !!b && frac(b.x) && frac(b.y) && frac(b.w) && frac(b.h) && b.w >= MIN_BOX && b.h >= MIN_BOX && b.x + b.w <= 1 + 1e-9 && b.y + b.h <= 1 + 1e-9;

export const isValidSeed = (s: ObjectSeed): boolean =>
  s.mediaRef.length > 0 && typeof s.atSec === 'number' && Number.isFinite(s.atSec) && s.atSec >= 0 && frac(s.point.x) && frac(s.point.y) && (s.box === undefined || isValidBox(s.box));

/** A canvas tap as a point in source fractions. Null when it misses the visible picture (outside the clip's frame or its crop window). */
export function canvasPointToSourcePoint(
  clip: MediaClip,
  values: TransformValues,
  p: { x: number; y: number },
  canvas: { width: number; height: number },
  source: { width: number; height: number } | null,
  policy: 'fit' | 'fill' | 'stretch',
): SeedPoint | null {
  const toSource = canvasToSourceMapper(clip, values, canvas, source, policy);
  if (!toSource) return null;
  const q = toSource(p);
  const edge = (v: number | undefined) => (typeof v === 'number' && Number.isFinite(v) && v > 0 && v < 1 ? v : 0);
  const eps = 1e-9;
  const inWindow = q.x >= edge(clip.crop?.left) - eps && q.x <= 1 - edge(clip.crop?.right) + eps && q.y >= edge(clip.crop?.top) - eps && q.y <= 1 - edge(clip.crop?.bottom) + eps;
  return inWindow && frac(q.x) && frac(q.y) ? q : null;
}

/** The box around a tap: a square of {@link DEFAULT_BOX_SIDE}, slid back inside the frame when the tap is near an edge. */
export function defaultBoxAround(point: SeedPoint, side = DEFAULT_BOX_SIDE): SubjectBox {
  const s = Math.min(1, Math.max(MIN_BOX, side));
  return { x: Math.min(1 - s, Math.max(0, point.x - s / 2)), y: Math.min(1 - s, Math.max(0, point.y - s / 2)), w: s, h: s };
}

/** A box clamped inside the frame, or null when it is under 1% on a side. */
export function clampBox(b: SubjectBox): SubjectBox | null {
  if (![b.x, b.y, b.w, b.h].every(Number.isFinite)) return null;
  const x0 = clamp01(b.x);
  const y0 = clamp01(b.y);
  const x1 = clamp01(b.x + b.w);
  const y1 = clamp01(b.y + b.h);
  return x1 - x0 < MIN_BOX || y1 - y0 < MIN_BOX ? null : { x: x0, y: y0, w: x1 - x0, h: y1 - y0 };
}

const boxCentre = (b: SubjectBox): SeedPoint => ({ x: b.x + b.w / 2, y: b.y + b.h / 2 });

/** A tap: the point and a default box around it. */
export function seedFromTap(mediaRef: string, atSec: number, point: SeedPoint): ObjectSeed {
  return { mediaRef, atSec, point, box: defaultBoxAround(point), boxIsDefault: true };
}

/** A drawn box: the box, and its centre as the point. Null when the box is too small. */
export function seedFromBox(mediaRef: string, atSec: number, box: SubjectBox): ObjectSeed | null {
  const b = clampBox(box);
  return b ? { mediaRef, atSec, point: boxCentre(b), box: b } : null;
}

/** Move the marker (and its box with it) by whole steps of {@link NUDGE}; the box slides, it never shrinks, and stays inside the frame. */
export function nudgeSeed(seed: ObjectSeed, dx: number, dy: number): ObjectSeed {
  const point = { x: clamp01(seed.point.x + dx * NUDGE), y: clamp01(seed.point.y + dy * NUDGE) };
  if (!seed.box) return { ...seed, point };
  const mx = point.x - seed.point.x;
  const my = point.y - seed.point.y;
  const box = { ...seed.box, x: Math.min(1 - seed.box.w, Math.max(0, seed.box.x + mx)), y: Math.min(1 - seed.box.h, Math.max(0, seed.box.y + my)) };
  return { ...seed, point, box };
}

/** The request-body form of a seed. Throws nothing: an invalid seed gives null so the caller never sends it. */
export function seedBody(seed: ObjectSeed): SeedBody | null {
  if (!isValidSeed(seed)) return null;
  // A tap sends the point alone; only a drawn or resized box is sent as `box`.
  return { atSec: seed.atSec, point: seed.point, ...(seed.box && !seed.boxIsDefault ? { box: seed.box } : {}) };
}

export type Corner = 'nw' | 'ne' | 'sw' | 'se';

/** The seed with its point moved to `to`; the box slides with it (same size, kept inside the frame). */
export function moveSeedTo(seed: ObjectSeed, to: SeedPoint): ObjectSeed {
  const point = { x: clamp01(to.x), y: clamp01(to.y) };
  if (!seed.box) return { ...seed, point };
  const box = {
    ...seed.box,
    x: Math.min(1 - seed.box.w, Math.max(0, seed.box.x + point.x - seed.point.x)),
    y: Math.min(1 - seed.box.h, Math.max(0, seed.box.y + point.y - seed.point.y)),
  };
  return { ...seed, point, box };
}

/** The seed with one corner of its box dragged to `to` (the opposite corner stays). Unchanged when the box would drop under 1%. The point follows the box centre. */
export function resizeSeedCorner(seed: ObjectSeed, corner: Corner, to: SeedPoint): ObjectSeed {
  if (!seed.box) return seed;
  const b = seed.box;
  const fixedX = corner === 'nw' || corner === 'sw' ? b.x + b.w : b.x;
  const fixedY = corner === 'nw' || corner === 'ne' ? b.y + b.h : b.y;
  const next = clampBox({ x: Math.min(fixedX, to.x), y: Math.min(fixedY, to.y), w: Math.abs(to.x - fixedX), h: Math.abs(to.y - fixedY) });
  if (!next) return seed;
  const drawn: ObjectSeed = { ...seed, box: next, point: boxCentre(next) };
  delete drawn.boxIsDefault;
  return drawn;
}

/** The inverse of an affine canvas->source mapper (rotation, scale, shift), or null when it is singular. */
export function invertMapper(f: (p: SeedPoint) => SeedPoint): ((p: SeedPoint) => SeedPoint) | null {
  const o = f({ x: 0, y: 0 });
  const ex = f({ x: 1, y: 0 });
  const ey = f({ x: 0, y: 1 });
  const a = ex.x - o.x;
  const c = ex.y - o.y;
  const b = ey.x - o.x;
  const d = ey.y - o.y;
  const det = a * d - b * c;
  if (!Number.isFinite(det) || Math.abs(det) < 1e-12) return null;
  return (p) => {
    const x = p.x - o.x;
    const y = p.y - o.y;
    return { x: (d * x - b * y) / det, y: (-c * x + a * y) / det };
  };
}
