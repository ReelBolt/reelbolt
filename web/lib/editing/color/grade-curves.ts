/**
 * Curves, as the export evaluates them (`GradeFilterBuilder.Curves` / `Points` -> ffmpeg `curves`): the points are
 * ordered, padded with (0,0) and (1,1) when they do not reach the ends, clamped to 0..1, and joined by a NATURAL
 * cubic spline (two points: a straight line). Each channel's curve is applied first, then the master (luma) curve
 * as a second pass over the result ("master ... acts like a post processing LUT").
 *
 * The viewer bakes this into a 256 x 1 table per node (one row of RGB) that the grade pass samples; the table is
 * exact at every 8-bit input, which is where the render evaluates it.
 */

import type { CurvePoint, GradeCurves } from '../types';

/** Samples per channel table: exact at every 8-bit input. */
export const CURVE_TABLE_SIZE = 256;

const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);

/** The points the export hands ffmpeg: ordered by x, end points added, values clamped; a repeated x is dropped. */
export function normalizedCurvePoints(points: readonly CurvePoint[]): CurvePoint[] {
  const ordered = points.map((p) => ({ x: p.x, y: p.y })).sort((a, b) => a.x - b.x);
  if (ordered.length === 0) return [];
  if (ordered[0].x > 0) ordered.unshift({ x: 0, y: 0 });
  if (ordered[ordered.length - 1].x < 1) ordered.push({ x: 1, y: 1 });
  const out: CurvePoint[] = [];
  for (const p of ordered) {
    const x = clamp01(p.x);
    const y = clamp01(p.y);
    const last = out[out.length - 1];
    if (last && x <= last.x) continue;
    out.push({ x, y });
  }
  return out;
}

/** Second derivatives of the natural cubic spline through `pts` (x strictly increasing, at least 2 points). */
function splineSecondDerivatives(pts: readonly CurvePoint[]): number[] {
  const n = pts.length;
  const y2 = new Array<number>(n).fill(0);
  const u = new Array<number>(n).fill(0);
  for (let i = 1; i < n - 1; i += 1) {
    const sig = (pts[i].x - pts[i - 1].x) / (pts[i + 1].x - pts[i - 1].x);
    const p = sig * y2[i - 1] + 2;
    y2[i] = (sig - 1) / p;
    const d = (pts[i + 1].y - pts[i].y) / (pts[i + 1].x - pts[i].x) - (pts[i].y - pts[i - 1].y) / (pts[i].x - pts[i - 1].x);
    u[i] = (6 * d / (pts[i + 1].x - pts[i - 1].x) - sig * u[i - 1]) / p;
  }
  y2[n - 1] = 0;
  for (let k = n - 2; k >= 0; k -= 1) y2[k] = y2[k] * y2[k + 1] + u[k];
  return y2;
}

/** One curve as a CURVE_TABLE_SIZE table over x = i / (size - 1). An empty or degenerate curve is the identity. */
export function curveTable(points: readonly CurvePoint[] | null | undefined): Float32Array {
  const table = new Float32Array(CURVE_TABLE_SIZE);
  const pts = points && points.length > 0 ? normalizedCurvePoints(points) : [];
  if (pts.length < 2) {
    for (let i = 0; i < CURVE_TABLE_SIZE; i += 1) table[i] = i / (CURVE_TABLE_SIZE - 1);
    return table;
  }
  const y2 = splineSecondDerivatives(pts);
  let seg = 0;
  for (let i = 0; i < CURVE_TABLE_SIZE; i += 1) {
    const x = i / (CURVE_TABLE_SIZE - 1);
    while (seg < pts.length - 2 && x > pts[seg + 1].x) seg += 1;
    const p0 = pts[seg];
    const p1 = pts[seg + 1];
    const h = p1.x - p0.x;
    const a = (p1.x - x) / h;
    const b = (x - p0.x) / h;
    const y = a * p0.y + b * p1.y + (((a * a * a - a) * y2[seg] + (b * b * b - b) * y2[seg + 1]) * h * h) / 6;
    table[i] = clamp01(y);
  }
  return table;
}

/** A table read at a 0..1 input, linear between samples. */
export function sampleCurveTable(table: ArrayLike<number>, offset: number, step: number, v: number): number {
  const t = clamp01(v) * (CURVE_TABLE_SIZE - 1);
  const i0 = Math.floor(t);
  const i1 = Math.min(CURVE_TABLE_SIZE - 1, i0 + 1);
  const a = table[offset + i0 * step] as number;
  const b = table[offset + i1 * step] as number;
  return a + (b - a) * (t - i0);
}

/** A node's curves baked for the grade pass. */
export interface CurveBake {
  /** Stable identity of the table (cache key). */
  key: string;
  /** CURVE_TABLE_SIZE x 3 floats, R, G, B interleaved per entry: channel curve, then the master curve. */
  table: Float32Array;
}

const CHANNELS = ['r', 'g', 'b'] as const;

const hasPoints = (points: readonly CurvePoint[] | null | undefined): points is readonly CurvePoint[] => !!points && points.length > 0;

/** Bakes the node's curves, or null when it has none (the export adds no `curves` filter then). */
export function bakeCurves(curves: GradeCurves | null | undefined): CurveBake | null {
  if (!curves || !(hasPoints(curves.y) || hasPoints(curves.r) || hasPoints(curves.g) || hasPoints(curves.b))) return null;
  const master = hasPoints(curves.y) ? curveTable(curves.y) : null;
  const table = new Float32Array(CURVE_TABLE_SIZE * 3);
  const keyParts: string[] = [];
  CHANNELS.forEach((c, ch) => {
    const points = curves[c];
    const channel = hasPoints(points) ? curveTable(points) : null;
    keyParts.push(hasPoints(points) ? JSON.stringify(normalizedCurvePoints(points)) : '-');
    for (let i = 0; i < CURVE_TABLE_SIZE; i += 1) {
      let v = channel ? (channel[i] as number) : i / (CURVE_TABLE_SIZE - 1);
      if (master) v = sampleCurveTable(master, 0, 1, v);
      table[i * 3 + ch] = v;
    }
  });
  keyParts.push(hasPoints(curves.y) ? JSON.stringify(normalizedCurvePoints(curves.y)) : '-');
  return { key: `curves:${keyParts.join('|')}`, table };
}

/** The bake applied to one RGB triple in 0..1 (the CPU reference of the grade pass). */
export function applyCurveBake(bake: CurveBake, rgb: readonly [number, number, number]): [number, number, number] {
  return [sampleCurveTable(bake.table, 0, 3, rgb[0]), sampleCurveTable(bake.table, 1, 3, rgb[1]), sampleCurveTable(bake.table, 2, 3, rgb[2])];
}

/** The table as RGBA half floats (alpha 1) for an N x 1 RGBA16F texture. `toHalf` is `floatToHalf`. */
export function curveBakeRgba16f(bake: CurveBake, toHalf: (v: number) => number): Uint16Array {
  const out = new Uint16Array(CURVE_TABLE_SIZE * 4);
  const one = toHalf(1);
  for (let i = 0; i < CURVE_TABLE_SIZE; i += 1) {
    out[i * 4] = toHalf(bake.table[i * 3] as number);
    out[i * 4 + 1] = toHalf(bake.table[i * 3 + 1] as number);
    out[i * 4 + 2] = toHalf(bake.table[i * 3 + 2] as number);
    out[i * 4 + 3] = one;
  }
  return out;
}
