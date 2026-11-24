/**
 * Secondary selections, as the export builds them (`GradeFilterBuilder.Matte`): an HSL qualifier and a (tracked)
 * power window each give a 0..1 matte per pixel, measured on the UNGRADED pixels; the node's correction is merged
 * back through the product of the mattes (`maskedmerge`). Every constant below is the export's.
 *
 * The same maths runs in the grade-pass fragment shader; these are the CPU references the shader is checked against.
 */

import type { GradeQualifier, GradeWindow } from '../types';

const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);
const mod = (a: number, b: number) => a - b * Math.floor(a / b);

/** A qualifier with every default filled in (the contract's defaults). */
export interface QualifierParams {
  hueCenter: number;
  hueWidth: number;
  satMin: number;
  satMax: number;
  lumMin: number;
  lumMax: number;
  /** Softness as the export reads it: at least 0.002. */
  softness: number;
  invert: boolean;
}

/** The qualifier's parameters, or null when it selects everything (the export adds no matte for it). */
export function qualifierParamsOf(q: GradeQualifier | null | undefined): QualifierParams | null {
  if (!q) return null;
  const p: QualifierParams = {
    hueCenter: q.hueCenter ?? 0,
    hueWidth: q.hueWidth ?? 180,
    satMin: q.satMin ?? 0,
    satMax: q.satMax ?? 1,
    lumMin: q.lumMin ?? 0,
    lumMax: q.lumMax ?? 1,
    softness: Math.max(0.002, q.softness ?? 0.1),
    invert: q.invert === true,
  };
  if (p.hueWidth >= 180 && p.satMin <= 0 && p.satMax >= 1 && p.lumMin <= 0 && p.lumMax >= 1 && !p.invert) return null;
  return p;
}

/** HSL of an RGB triple in 0..1 exactly as the matte expression derives it (hue in degrees, 0..360). */
export function qualifierHsl(rgb: readonly [number, number, number]): { hue: number; sat: number; lum: number } {
  const [r, g, b] = rgb;
  const max = Math.max(r, g, b);
  const min = Math.min(r, g, b);
  const lum = (max + min) / 2;
  const sat = (max - min) / (1 - Math.abs(2 * lum - 1) + 0.00001);
  const hue = mod((Math.atan2(1.7320508 * (g - b), 2 * r - g - b) * 57.2957795) + 360, 360);
  return { hue, sat, lum };
}

/** The qualifier's matte (0..1, 1 = fully corrected) at one UNGRADED pixel. */
export function qualifierMatte(p: QualifierParams, rgb: readonly [number, number, number]): number {
  const { hue, sat, lum } = qualifierHsl(rgb);
  let selected = 1;
  if (p.hueWidth < 180) {
    const distance = Math.abs(mod(hue - p.hueCenter + 540, 360) - 180);
    selected *= clamp01((p.hueWidth - distance) / Math.max(1, p.softness * 180) + 0.5);
  }
  // A bound at the end of the scale selects nothing out: only a real limit gets an edge.
  if (p.satMin > 0) selected *= clamp01((sat - p.satMin) / p.softness + 0.5);
  if (p.satMax < 1) selected *= clamp01((p.satMax - sat) / p.softness + 0.5);
  if (p.lumMin > 0) selected *= clamp01((lum - p.lumMin) / p.softness + 0.5);
  if (p.lumMax < 1) selected *= clamp01((p.lumMax - lum) / p.softness + 0.5);
  return p.invert ? 1 - selected : selected;
}

/** A power window at one frame: centre and half sizes in canvas fractions, tracked offset already added. */
export interface WindowParams {
  ellipse: boolean;
  cx: number;
  cy: number;
  hw: number;
  hh: number;
  /** Feather as the export reads it: at least 0.0005. */
  feather: number;
  invert: boolean;
}

/**
 * The window's parameters at a frame. `offsetX` / `offsetY` are the keyframed offsets already evaluated at the
 * frame (timeline frames, canvas fractions). Null when the node has no window shape.
 */
export function windowParamsOf(w: GradeWindow | null | undefined, offsetX: number, offsetY: number): WindowParams | null {
  const shape = w?.shape;
  if (!shape) return null;
  return {
    ellipse: shape.kind === 'ellipse',
    cx: (shape.x ?? 0.5) + (w?.offsetX ? offsetX : 0),
    cy: (shape.y ?? 0.5) + (w?.offsetY ? offsetY : 0),
    hw: Math.max(0.001, (shape.w ?? 0.5) / 2),
    hh: Math.max(0.001, (shape.h ?? 0.5) / 2),
    feather: Math.max(0.0005, shape.feather ?? 0),
    invert: shape.invert === true,
  };
}

/** The window's matte at a pixel whose position is `x` = X/W, `y` = Y/H (pixel index over size, as `geq` reads it). */
export function windowMatte(p: WindowParams, x: number, y: number): number {
  const inside = p.ellipse
    ? clamp01((1 - Math.sqrt(((x - p.cx) / p.hw) ** 2 + ((y - p.cy) / p.hh) ** 2)) / p.feather + 0.5)
    : clamp01(Math.min(Math.min(x - (p.cx - p.hw), p.cx + p.hw - x), Math.min(y - (p.cy - p.hh), p.cy + p.hh - y)) / p.feather + 0.5);
  return p.invert ? 1 - inside : inside;
}

/** Both mattes multiplied, as the export's `255*a*b`. A missing part contributes 1. */
export function secondaryMatte(q: QualifierParams | null, w: WindowParams | null, ungraded: readonly [number, number, number], x: number, y: number): number {
  return (q ? qualifierMatte(q, ungraded) : 1) * (w ? windowMatte(w, x, y) : 1);
}

/** `maskedmerge`: the original where the matte is 0, the corrected pixel where it is 1, linear between. */
export function maskedMerge(original: readonly [number, number, number], corrected: readonly [number, number, number], matte: number): [number, number, number] {
  return [original[0] + (corrected[0] - original[0]) * matte, original[1] + (corrected[1] - original[1]) * matte, original[2] + (corrected[2] - original[2]) * matte];
}
