/**
 * The viewer's geometry for the picture features the export draws and the live viewer used to skip:
 * reframe, shape masks and corner-pin / insert quads. Pure, so the compositor, the session and the
 * tests share ONE reading of each render rule. Every function names the C# it mirrors
 * (`GradeFilterBuilder.cs`), and the GLSL in `gl-compositor.ts` repeats the same maths.
 */

import type { InsertClip, Keyframed, QuadCorners, ReframeTarget, ShapeMask } from '../types';
import { ease } from './scheduler';

const finite = (v: unknown): v is number => typeof v === 'number' && Number.isFinite(v);
const num = (v: unknown, fallback: number) => (finite(v) ? v : fallback);

// ─────────────────────────────────────────────────────────────────────────────
// Reframe
// ─────────────────────────────────────────────────────────────────────────────

/** A window on the source picture, as fractions of its width and height (top-left origin). */
export type SourceWindow = { x: number; y: number; w: number; h: number };

export const FULL_WINDOW: SourceWindow = Object.freeze({ x: 0, y: 0, w: 1, h: 1 });

/**
 * The reframe crop window (`GradeFilterBuilder.Reframe`, GradeFilterBuilder.cs:350): the largest window of the
 * CANVAS aspect (`round(canvasW / canvasH, 6)`) that fits the source, centred on the subject point (`x`, `y`
 * clamped to 0..1, default 0.5) and kept inside the picture:
 * `w = min(iw, ih * aspect)`, `h = min(ih, iw / aspect)`, `x = clip(iw * rx - w / 2, 0, iw - w)`, same for y.
 * Returned as source fractions. Applied to the SOURCE, before the crop and the fit. A null reframe or an
 * unknown size is the whole picture.
 */
export function reframeWindow(reframe: ReframeTarget | null | undefined, sourceWidth: number, sourceHeight: number, canvasWidth: number, canvasHeight: number): SourceWindow {
  if (!reframe || !(canvasHeight > 0) || !(sourceWidth > 0) || !(sourceHeight > 0)) return FULL_WINDOW;
  const aspect = Math.round((canvasWidth / canvasHeight) * 1e6) / 1e6;
  const rx = Math.min(1, Math.max(0, num(reframe.x, 0.5)));
  const ry = Math.min(1, Math.max(0, num(reframe.y, 0.5)));
  const w = Math.min(sourceWidth, sourceHeight * aspect);
  const h = Math.min(sourceHeight, sourceWidth / aspect);
  const x = Math.min(sourceWidth - w, Math.max(0, sourceWidth * rx - w / 2));
  const y = Math.min(sourceHeight - h, Math.max(0, sourceHeight * ry - h / 2));
  return { x: x / sourceWidth, y: y / sourceHeight, w: w / sourceWidth, h: h / sourceHeight };
}

/**
 * Composes a window inside another: `inner` is in the OUTER window's own fractions (the clip's crop applies to
 * the reframed picture), the result is in source fractions.
 */
export function composeWindows(outer: SourceWindow, inner: SourceWindow): SourceWindow {
  return { x: outer.x + inner.x * outer.w, y: outer.y + inner.y * outer.h, w: inner.w * outer.w, h: inner.h * outer.h };
}

// ─────────────────────────────────────────────────────────────────────────────
// Shape masks
// ─────────────────────────────────────────────────────────────────────────────

/** A shape mask with the C# defaults applied (`ShapeMask` record, Grade.cs:246): centre (0.5, 0.5), 0.5 x 0.5, no feather. */
export type ShapeParams = {
  kind: 'rect' | 'ellipse';
  x: number;
  y: number;
  w: number;
  h: number;
  /** The ramp width in fractions of the layer; the render floors it at 0.0005. */
  feather: number;
  invert: boolean;
};

/** The render's feather floor (`Math.Max(0.0005, mask.Feather)`). */
export const SHAPE_FEATHER_FLOOR = 0.0005;

/** True for a rect/ellipse mask; a segmentation mask (or nothing) is not a shape. */
export function isShapeMask(mask: unknown): mask is ShapeMask {
  return typeof mask === 'object' && mask !== null && (mask as { kind?: unknown }).kind !== 'segmentation';
}

/** The mask the compositor draws; `null` for no mask or a segmentation mask (that one is a matte). */
export function shapeParamsOf(mask: ShapeMask | { kind?: string } | null | undefined): ShapeParams | null {
  if (!isShapeMask(mask)) return null;
  const m = mask as ShapeMask;
  return {
    kind: m.kind === 'ellipse' ? 'ellipse' : 'rect',
    x: num(m.x, 0.5),
    y: num(m.y, 0.5),
    w: num(m.w, 0.5),
    h: num(m.h, 0.5),
    feather: Math.max(SHAPE_FEATHER_FLOOR, num(m.feather, 0)),
    invert: m.invert === true,
  };
}

/**
 * The alpha a shape mask leaves at layer-fraction (`u`, `v`), 0..1 (`GradeFilterBuilder.Mask`, GradeFilterBuilder.cs:331):
 *  - rect: `clip(min(min(u - x0, x1 - u), min(v - y0, y1 - v)) / f + 0.5, 0, 1)` (x0 = x - w/2 ...)
 *  - ellipse: `clip((1 - sqrt(((u - x) / max(0.001, w/2))^2 + ((v - y) / max(0.001, h/2))^2)) / f + 0.5, 0, 1)`
 * inverted as `1 - inside`. (u, v) are fractions of the layer's own width and height, v down.
 */
export function shapeCoverage(p: ShapeParams, u: number, v: number): number {
  const clamp01 = (n: number) => Math.min(1, Math.max(0, n));
  let inside: number;
  if (p.kind === 'ellipse') {
    const dx = (u - p.x) / Math.max(0.001, p.w / 2);
    const dy = (v - p.y) / Math.max(0.001, p.h / 2);
    inside = clamp01((1 - Math.sqrt(dx * dx + dy * dy)) / p.feather + 0.5);
  } else {
    const x0 = p.x - p.w / 2;
    const x1 = p.x + p.w / 2;
    const y0 = p.y - p.h / 2;
    const y1 = p.y + p.h / 2;
    inside = clamp01(Math.min(Math.min(u - x0, x1 - u), Math.min(v - y0, y1 - v)) / p.feather + 0.5);
  }
  return p.invert ? 1 - inside : inside;
}

// ─────────────────────────────────────────────────────────────────────────────
// Corner pin and insert quads
// ─────────────────────────────────────────────────────────────────────────────

/** A quad in destination fractions of the canvas (y down): top-left, top-right, bottom-right, bottom-left. */
export type Quad = readonly [readonly [number, number], readonly [number, number], readonly [number, number], readonly [number, number]];

/** `GradeFilterBuilder.CornerPin` clamps every coordinate to -1..2 canvas fractions before use (`P`, GradeFilterBuilder.cs:371). */
export const PIN_CLAMP_MIN = -1;
export const PIN_CLAMP_MAX = 2;

/** The corner pin of a clip as the quad the render pins to (coordinates clamped like the render does), or null. */
export function pinQuadOf(pin: QuadCorners | null | undefined): Quad | null {
  if (!pin) return null;
  const c = (p: { x: number; y: number } | undefined): [number, number] | null =>
    p && finite(p.x) && finite(p.y) ? [Math.min(PIN_CLAMP_MAX, Math.max(PIN_CLAMP_MIN, p.x)), Math.min(PIN_CLAMP_MAX, Math.max(PIN_CLAMP_MIN, p.y))] : null;
  const tl = c(pin.topLeft);
  const tr = c(pin.topRight);
  const br = c(pin.bottomRight);
  const bl = c(pin.bottomLeft);
  return tl && tr && br && bl ? [tl, tr, br, bl] : null;
}

/** A 3x3 matrix, row-major: `[a, b, c, d, e, f, g, h, i]`. */
export type Mat3 = readonly [number, number, number, number, number, number, number, number, number];

/**
 * The projective map from the unit square (u, v: (0,0) top-left, (1,0), (1,1), (0,1)) onto `quad` (Heckbert's
 * square-to-quad). The picture is the whole canvas-shaped layer and ffmpeg's `perspective ... sense=destination`
 * pins its corners (top-left, top-right, bottom-left, bottom-right = x0..x3) onto the quad, which is exactly this map.
 * Plain UV interpolation across two triangles would be an affine map and show a seam on a non-parallelogram quad.
 * Returns null for a degenerate quad (collinear corners).
 */
export function squareToQuad(quad: Quad): Mat3 | null {
  const [[x0, y0], [x1, y1], [x2, y2], [x3, y3]] = quad;
  const sx = x0 - x1 + x2 - x3;
  const sy = y0 - y1 + y2 - y3;
  if (Math.abs(sx) < 1e-12 && Math.abs(sy) < 1e-12) {
    // A parallelogram: the map is affine.
    const m: Mat3 = [x1 - x0, x3 - x0, x0, y1 - y0, y3 - y0, y0, 0, 0, 1];
    return Math.abs(determinant(m)) < 1e-12 ? null : m;
  }
  const dx1 = x1 - x2;
  const dx2 = x3 - x2;
  const dy1 = y1 - y2;
  const dy2 = y3 - y2;
  const den = dx1 * dy2 - dx2 * dy1;
  if (Math.abs(den) < 1e-12) return null;
  const g = (sx * dy2 - dx2 * sy) / den;
  const h = (dx1 * sy - sx * dy1) / den;
  const m: Mat3 = [x1 - x0 + g * x1, x3 - x0 + h * x3, x0, y1 - y0 + g * y1, y3 - y0 + h * y3, y0, g, h, 1];
  return Math.abs(determinant(m)) < 1e-12 ? null : m;
}

function determinant(m: Mat3): number {
  return m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
}

/** The inverse of a 3x3 (adjugate over determinant); null when singular. */
export function invertMat3(m: Mat3): Mat3 | null {
  const det = determinant(m);
  if (Math.abs(det) < 1e-12) return null;
  const inv = 1 / det;
  return [
    (m[4] * m[8] - m[5] * m[7]) * inv, (m[2] * m[7] - m[1] * m[8]) * inv, (m[1] * m[5] - m[2] * m[4]) * inv,
    (m[5] * m[6] - m[3] * m[8]) * inv, (m[0] * m[8] - m[2] * m[6]) * inv, (m[2] * m[3] - m[0] * m[5]) * inv,
    (m[3] * m[7] - m[4] * m[6]) * inv, (m[1] * m[6] - m[0] * m[7]) * inv, (m[0] * m[4] - m[1] * m[3]) * inv,
  ];
}

/** Applies a homography to a point (divides by w); null when the point is on the vanishing line. */
export function applyMat3(m: Mat3, x: number, y: number): [number, number] | null {
  const w = m[6] * x + m[7] * y + m[8];
  if (Math.abs(w) < 1e-12) return null;
  return [(m[0] * x + m[1] * y + m[2]) / w, (m[3] * x + m[4] * y + m[5]) / w];
}

/**
 * What the pin shader needs: the INVERSE of the square-to-quad map (destination canvas fraction -> picture fraction,
 * both y down) in column-major order for `uniformMatrix3fv`. Null when the quad is degenerate (the layer then draws nothing).
 */
export function pinInverseColumnMajor(quad: Quad): Float32Array | null {
  const forward = squareToQuad(quad);
  const inv = forward ? invertMat3(forward) : null;
  if (!inv) return null;
  // Column-major: element (row r, column c) at c * 3 + r.
  return new Float32Array([inv[0], inv[3], inv[6], inv[1], inv[4], inv[7], inv[2], inv[5], inv[8]]);
}

/**
 * A `Keyframed<QuadCorners>` at timeline `frame`: the base value before the first key (or with no keys), the last key
 * after the last, and in between each corner interpolated with the EARLIER key's easing - the same rule as
 * `evaluateKeyframed` for numbers. Keys are in timeline frames (`TimelineFlattener.ShiftKeys`).
 */
export function evaluateQuad(value: Keyframed<QuadCorners> | null | undefined, frame: number): QuadCorners | null {
  if (!value) return null;
  const keys = value.keys;
  if (!keys || keys.length === 0) return value.value ?? null;
  if (frame < keys[0].frame) return value.value ?? keys[0].value;
  for (let i = 0; i < keys.length - 1; i += 1) {
    const a = keys[i];
    const b = keys[i + 1];
    if (frame >= a.frame && frame < b.frame) {
      const span = b.frame - a.frame;
      const t = span <= 0 ? 1 : ease(a.interp, (frame - a.frame) / span);
      const mix = (p: { x: number; y: number }, q: { x: number; y: number }) => ({ x: p.x + (q.x - p.x) * t, y: p.y + (q.y - p.y) * t });
      return {
        topLeft: mix(a.value.topLeft, b.value.topLeft),
        topRight: mix(a.value.topRight, b.value.topRight),
        bottomRight: mix(a.value.bottomRight, b.value.bottomRight),
        bottomLeft: mix(a.value.bottomLeft, b.value.bottomLeft),
      };
    }
  }
  return keys[keys.length - 1].value;
}

/**
 * An insert's quad at `frame` in canvas fractions. The document stores either fractions or pixels (the 2D overlay
 * raster has always read a quad whose coordinates are all in 0..1 as fractions and anything else as pixels), so the
 * same rule is kept here, decided ONCE across the base value and every key (`InsertLayerBuilder.AllPoints`), so a quad
 * that overshoots 1 at a later key is pixels at every frame, as in the export. Null for no quad or a degenerate one.
 */
export function insertQuadAt(insert: Pick<InsertClip, 'quad'>, frame: number, canvasWidth: number, canvasHeight: number): Quad | null {
  const q = evaluateQuad(insert.quad, frame);
  if (!q) return null;
  const pts = [q.topLeft, q.topRight, q.bottomRight, q.bottomLeft];
  if (!pts.every((p) => finite(p.x) && finite(p.y))) return null;
  const all = [insert.quad?.value, ...(insert.quad?.keys ?? []).map((k) => k.value)].filter((c): c is QuadCorners => !!c).flatMap((c) => [c.topLeft, c.topRight, c.bottomRight, c.bottomLeft]);
  const normalized = all.every((p) => p.x >= 0 && p.x <= 1 && p.y >= 0 && p.y <= 1);
  const sx = normalized ? 1 : canvasWidth > 0 ? 1 / canvasWidth : 1;
  const sy = normalized ? 1 : canvasHeight > 0 ? 1 / canvasHeight : 1;
  const scaled = (p: { x: number; y: number }): [number, number] => [p.x * sx, p.y * sy];
  return pinQuadOf({ topLeft: { x: scaled(q.topLeft)[0], y: scaled(q.topLeft)[1] }, topRight: { x: scaled(q.topRight)[0], y: scaled(q.topRight)[1] }, bottomRight: { x: scaled(q.bottomRight)[0], y: scaled(q.bottomRight)[1] }, bottomLeft: { x: scaled(q.bottomLeft)[0], y: scaled(q.bottomLeft)[1] } });
}
