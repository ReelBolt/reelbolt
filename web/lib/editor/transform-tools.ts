/**
 * The select tool's arithmetic: which handle is under the pointer, and what a drag on it does to a
 * layer's transform. Pure — the canvas component feeds it canvas-space points and writes the result
 * back through the history stack.
 *
 * ## Why "Shift = aspect lock" does not appear on the scale handles
 *
 * The layer model stores ONE uniform `scale` (contract section 2), so a layer's aspect ratio is
 * locked by construction and a corner drag can only ever scale uniformly. Shift therefore does the
 * constraining that a uniform model still has room for: it snaps rotation to 15° steps and pins a
 * move to the horizontal or vertical axis.
 */

import { layerBounds, layerCenter, type PlacedLayer, type Point } from './compose';
import type { EditorTransform } from './model';

export type CornerHandle = 'tl' | 'tr' | 'br' | 'bl';
export type HandleId = CornerHandle | 'rotate';

export const SCALE_MIN = 0.02;
export const SCALE_MAX = 50;
export const ROTATE_SNAP_DEGREES = 15;

const CORNER_ORDER: CornerHandle[] = ['tl', 'tr', 'br', 'bl'];
const OPPOSITE: Record<CornerHandle, CornerHandle> = { tl: 'br', tr: 'bl', br: 'tl', bl: 'tr' };

/** Where each handle sits on the canvas. The rotate handle floats `offset` canvas px above the top edge. */
export function handlePositions(layer: PlacedLayer, offset: number): Record<HandleId, Point> {
  const b = layerBounds(layer);
  const [tl, tr, br, bl] = b.corners;
  const mid = { x: (tl.x + tr.x) / 2, y: (tl.y + tr.y) / 2 };
  // "Up" in the layer's own frame: from the layer centre towards the top edge's midpoint.
  const ux = mid.x - b.center.x;
  const uy = mid.y - b.center.y;
  const len = Math.hypot(ux, uy) || 1;
  return {
    tl,
    tr,
    br,
    bl,
    rotate: { x: mid.x + (ux / len) * offset, y: mid.y + (uy / len) * offset },
  };
}

/** The handle within `tolerance` canvas px of `point`, nearest first, or null. */
export function hitHandle(
  layer: PlacedLayer,
  point: Point,
  tolerance: number,
  rotateOffset: number,
): HandleId | null {
  const positions = handlePositions(layer, rotateOffset);
  let best: { id: HandleId; d: number } | null = null;
  for (const id of [...CORNER_ORDER, 'rotate'] as HandleId[]) {
    const d = Math.hypot(positions[id].x - point.x, positions[id].y - point.y);
    if (d <= tolerance && (best === null || d < best.d)) best = { id, d };
  }
  return best?.id ?? null;
}

const clamp = (n: number, lo: number, hi: number) => Math.min(hi, Math.max(lo, n));

/**
 * A corner drag: scales uniformly about the OPPOSITE corner, which stays where it is on the canvas.
 * The new scale is the pointer's distance from that corner over the layer's diagonal at scale 1.
 */
export function scaleFromCornerDrag(
  layer: PlacedLayer,
  handle: CornerHandle,
  pointer: Point,
): EditorTransform {
  const t = layer.transform;
  const b = layerBounds(layer);
  const anchor = b.corners[CORNER_ORDER.indexOf(OPPOSITE[handle])];
  const diagonal = Math.hypot(layer.size.w, layer.size.h) || 1;
  const next = clamp(Math.hypot(pointer.x - anchor.x, pointer.y - anchor.y) / diagonal, SCALE_MIN, SCALE_MAX);
  const ratio = next / t.scale;
  const center = {
    x: anchor.x + (b.center.x - anchor.x) * ratio,
    y: anchor.y + (b.center.y - anchor.y) * ratio,
  };
  return {
    ...t,
    scale: next,
    x: center.x - (layer.size.w * next) / 2,
    y: center.y - (layer.size.h * next) / 2,
  };
}

/** Normalises degrees into (-180, 180]. */
export function normalizeDegrees(deg: number): number {
  let d = deg % 360;
  if (d > 180) d -= 360;
  if (d <= -180) d += 360;
  return d;
}

/**
 * A rotate-handle drag: the layer turns so its "up" points at the pointer. With `snap` (Shift) the
 * result lands on a multiple of {@link ROTATE_SNAP_DEGREES}.
 */
export function rotationFromDrag(layer: PlacedLayer, pointer: Point, snap: boolean): number {
  const c = layerCenter(layer);
  const raw = (Math.atan2(pointer.y - c.y, pointer.x - c.x) * 180) / Math.PI + 90;
  const rotated = snap ? Math.round(raw / ROTATE_SNAP_DEGREES) * ROTATE_SNAP_DEGREES : raw;
  return normalizeDegrees(Math.round(rotated * 100) / 100);
}

/** A move drag from `start` (the layer's transform at pointer-down) by `delta`; Shift pins one axis. */
export function moveByDelta(start: EditorTransform, delta: Point, axisLock: boolean): EditorTransform {
  let { x, y } = delta;
  if (axisLock) {
    if (Math.abs(x) >= Math.abs(y)) y = 0;
    else x = 0;
  }
  return { ...start, x: start.x + x, y: start.y + y };
}

export type NudgeKey = 'ArrowLeft' | 'ArrowRight' | 'ArrowUp' | 'ArrowDown';

export function isNudgeKey(key: string): key is NudgeKey {
  return key === 'ArrowLeft' || key === 'ArrowRight' || key === 'ArrowUp' || key === 'ArrowDown';
}

/** Arrow-key nudge: 1 canvas px, or 10 with Shift. */
export function nudge(t: EditorTransform, key: NudgeKey, big: boolean): EditorTransform {
  const step = big ? 10 : 1;
  switch (key) {
    case 'ArrowLeft':
      return { ...t, x: t.x - step };
    case 'ArrowRight':
      return { ...t, x: t.x + step };
    case 'ArrowUp':
      return { ...t, y: t.y - step };
    case 'ArrowDown':
      return { ...t, y: t.y + step };
  }
}

/** Whether two transforms are the same placement (so a no-op drag does not create an undo step). */
export function sameTransform(a: EditorTransform, b: EditorTransform): boolean {
  return (
    a.x === b.x &&
    a.y === b.y &&
    a.scale === b.scale &&
    a.rotation === b.rotation &&
    a.flipX === b.flipX &&
    a.flipY === b.flipY
  );
}

/**
 * Where a newly added picture goes: when it is larger than the canvas on either side it is scaled
 * down to fit inside it (contain, never crop) and centred; a picture that already fits keeps its
 * natural size and is centred too. The user can scale it up afterwards with the corner handles.
 *
 * `size` is the picture's own pixel size. Returns a fresh transform with no rotation and no flips.
 */
export function containFitTransform(
  size: { width: number; height: number },
  canvas: { width: number; height: number },
): EditorTransform {
  const w = size.width > 0 ? size.width : 1;
  const h = size.height > 0 ? size.height : 1;
  const fits = w <= canvas.width && h <= canvas.height;
  const scale = fits ? 1 : Math.max(SCALE_MIN, Math.min(canvas.width / w, canvas.height / h));
  return {
    x: Math.round((canvas.width - w * scale) / 2),
    y: Math.round((canvas.height - h * scale) / 2),
    scale,
    rotation: 0,
    flipX: false,
    flipY: false,
  };
}
