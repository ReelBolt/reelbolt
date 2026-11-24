/**
 * The Simple preview's grade: a `PreviewGrade` (preview-grade.ts) as an SVG filter a `<video>` can wear
 * (`filter: url(#id)`), so the single-clip fallback shows the same primaries the live compositor does.
 *
 *   1. `feComponentTransfer`, one `type="table"` per channel: exposure, offset, gain, lift, gamma and
 *      contrast about the pivot, sampled at {@link TABLE_SAMPLES} points (exact at every 8-bit value; the
 *      browser interpolates linearly between samples);
 *   2. `feColorMatrix type="matrix"`: temperature/tint multipliers, then saturation about the Rec.709
 *      luma, then the hue rotation, as ONE matrix (the same product `applyPreviewGrade` evaluates).
 *
 * The filter must be told `color-interpolation-filters="sRGB"` so it works on the encoded values the
 * shader works on, not on linear light.
 *
 * Where it differs from `applyPreviewGrade`: the transfer table clamps its output to 0..1 (an SVG filter
 * clamps between primitives), while `applyPreviewGrade` keeps the unclamped value into the matrix. They
 * only disagree for a pixel a channel of which leaves 0..1 after contrast/exposure AND the matrix is not
 * the identity (a saturation or hue change mixes the overshoot into the other channels). Temperature and
 * tint are in the matrix, so for them the clamp is harmless. Pure; no DOM.
 */

import { applyPreviewGrade, isIdentityPreviewGrade, type PreviewGrade } from './preview-grade';

/** Samples per channel table. 256 is exact at every 8-bit input; the minimum for a smooth gamma curve is far lower. */
export const TABLE_SAMPLES = 256;

export interface SvgGradeFilter {
  /** DOM id for `url(#id)`; changes with the grade. */
  id: string;
  /** Equal for equal filters: the viewer re-renders the SVG only when this changes. */
  signature: string;
  /** `tableValues` for the red, green and blue `feFunc*`. */
  tables: [number[], number[], number[]];
  /** The 4x5 `feColorMatrix` values, row-major (alpha row is the identity). */
  matrix: number[];
}

const round5 = (v: number) => Math.round(v * 1e5) / 1e5;
const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);
const LUMA = [0.2126, 0.7152, 0.0722] as const;

/** One channel's per-channel maths, the first half of `applyPreviewGrade`'s loop. */
function channel(g: PreviewGrade, i: number, x: number): number {
  let v = x * g.exposureGain + g.offset[i];
  v = v * g.gain[i] + g.lift[i] * (1 - v);
  v = Math.pow(Math.max(0, v), g.gamma[i]);
  return (v - g.pivot) * g.contrast + g.pivot;
}

function multiply3(a: number[][], b: number[][]): number[][] {
  return a.map((row) => [0, 1, 2].map((j) => row[0] * b[0][j] + row[1] * b[1][j] + row[2] * b[2][j]));
}

function colorMatrixOf(g: PreviewGrade): number[] {
  const diag = [1 + 0.15 * g.temperature, 1 - 0.1 * g.tint, 1 - 0.15 * g.temperature];
  const D = [0, 1, 2].map((i) => [0, 1, 2].map((j) => (i === j ? diag[i] : 0)));
  const S = [0, 1, 2].map((i) => [0, 1, 2].map((j) => (1 - g.saturation) * LUMA[j] + (i === j ? g.saturation : 0)));
  let H = [[1, 0, 0], [0, 1, 0], [0, 0, 1]];
  if (g.hue !== 0) {
    const a = (g.hue * Math.PI) / 180;
    const c = Math.cos(a);
    const s = Math.sin(a);
    H = [
      [0.299 + 0.701 * c + 0.168 * s, 0.587 - 0.587 * c + 0.33 * s, 0.114 - 0.114 * c - 0.497 * s],
      [0.299 - 0.299 * c - 0.328 * s, 0.587 + 0.413 * c + 0.035 * s, 0.114 - 0.114 * c + 0.292 * s],
      [0.299 - 0.3 * c + 1.25 * s, 0.587 - 0.588 * c - 1.05 * s, 0.114 + 0.886 * c - 0.203 * s],
    ];
  }
  const M = multiply3(H, multiply3(S, D));
  const rows = M.map((row) => [...row.map(round5), 0, 0]);
  return [...rows[0], ...rows[1], ...rows[2], 0, 0, 0, 1, 0];
}

function hash(text: string): string {
  let h = 0x811c9dc5;
  for (let i = 0; i < text.length; i++) {
    h ^= text.charCodeAt(i);
    h = Math.imul(h, 0x01000193) >>> 0;
  }
  return h.toString(36);
}

/** The SVG filter for a grade, or `null` at identity (no filter, no cost). */
export function svgGradeFilterOf(g: PreviewGrade): SvgGradeFilter | null {
  if (isIdentityPreviewGrade(g)) return null;
  const tables = [0, 1, 2].map((i) => {
    const values: number[] = [];
    for (let k = 0; k < TABLE_SAMPLES; k++) values.push(round5(clamp01(channel(g, i, k / (TABLE_SAMPLES - 1)))));
    return values;
  }) as [number[], number[], number[]];
  const matrix = colorMatrixOf(g);
  const signature = `${tables.map((t) => t.join(',')).join('|')}#${matrix.join(',')}`;
  return { id: `rf-grade-${hash(signature)}`, signature, tables, matrix };
}

/**
 * What the browser computes for one RGB triple (0..1) through this filter: the table (linear
 * interpolation between samples, per the SVG spec), then the matrix, clamped. The tests compare it
 * with `applyPreviewGrade`.
 */
export function evalSvgGradeFilter(filter: SvgGradeFilter, rgb: readonly [number, number, number]): [number, number, number] {
  const mid = [0, 1, 2].map((i) => {
    const table = filter.tables[i];
    const n = table.length - 1;
    const c = clamp01(rgb[i]);
    const k = Math.min(n - 1, Math.floor(c * n));
    return table[k] + (c - k / n) * n * (table[k + 1] - table[k]);
  });
  const m = filter.matrix;
  const out = [0, 1, 2].map((row) => clamp01(m[row * 5] * mid[0] + m[row * 5 + 1] * mid[1] + m[row * 5 + 2] * mid[2] + m[row * 5 + 4]));
  return [out[0], out[1], out[2]];
}

/** Re-exported so callers (and the tests) compare against the one reference. */
export { applyPreviewGrade };
