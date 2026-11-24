/**
 * The chroma keyer, as the export builds it (`GradeFilterBuilder.Keyer`): ffmpeg `colorkey` on RGBA, then `despill`
 * of the key's dominant channel when spill > 0. Both were read off the real filters (see `__fixtures__/keyer-parity.json`):
 *
 *   colorkey: diff = sqrt((dr^2 + dg^2 + db^2) / (3 * 255^2)) between the pixel and the key colour (RGB, 0..255);
 *             alpha = blend > 0.0001 ? clip((diff - similarity) / blend, 0, 1) : (diff > similarity ? 1 : 0).
 *   despill green: g' = min(g, r * mix + b * (1 - mix)).  blue: spill = max(b - (r * mix + g * (1 - mix)), 0) and the
 *             GREEN channel loses it (g' = g - spill; ffmpeg's blue type leaves b alone). Other channels untouched.
 *             `mix` is the keyer's spill. A key colour that is neither green- nor blue-dominant has no despill.
 *
 * It runs on the SOURCE picture, after the camera input stage and before the grade (inside the grade pass).
 */

import type { ChromaKeyer } from '../types';

const HEX = /^#[0-9a-fA-F]{6}$/;
const clamp = (v: number, lo: number, hi: number) => (v < lo ? lo : v > hi ? hi : v);

export interface KeyerParams {
  /** Key colour 0..1. */
  color: [number, number, number];
  similarity: number;
  blend: number;
  /** 0 = no despill. */
  spill: number;
  /** 0 none, 1 green, 2 blue. */
  spillType: 0 | 1 | 2;
}

/** The keyer's parameters with the export's defaults and clamps, or null when there is no keyer. */
export function keyerParamsOf(keyer: ChromaKeyer | null | undefined): KeyerParams | null {
  if (!keyer) return null;
  const hex = keyer.color && HEX.test(keyer.color) ? keyer.color : '#00FF00';
  const r = parseInt(hex.slice(1, 3), 16);
  const g = parseInt(hex.slice(3, 5), 16);
  const b = parseInt(hex.slice(5, 7), 16);
  const spill = clamp(Number.isFinite(keyer.spill ?? 0) ? (keyer.spill ?? 0) : 0, 0, 1);
  const spillType: 0 | 1 | 2 = spill <= 0 ? 0 : g >= r && g >= b ? 1 : b > r ? 2 : 0;
  return {
    color: [r / 255, g / 255, b / 255],
    similarity: clamp(keyer.similarity ?? 0.3, 0.01, 1),
    blend: clamp(keyer.blend ?? 0, 0, 1),
    spill,
    spillType,
  };
}

/** The key's alpha (0..1) at one pixel (0..1 RGB). */
export function keyAlpha(p: KeyerParams, rgb: readonly [number, number, number]): number {
  const dr = rgb[0] - p.color[0];
  const dg = rgb[1] - p.color[1];
  const db = rgb[2] - p.color[2];
  const diff = Math.sqrt((dr * dr + dg * dg + db * db) / 3);
  if (p.blend > 0.0001) return clamp((diff - p.similarity) / p.blend, 0, 1);
  return diff > p.similarity ? 1 : 0;
}

/** The despilled colour at one pixel. */
export function keyDespill(p: KeyerParams, rgb: readonly [number, number, number]): [number, number, number] {
  const [r, g, b] = rgb;
  if (p.spillType === 1) return [r, Math.min(g, r * p.spill + b * (1 - p.spill)), b];
  if (p.spillType === 2) return [r, g - Math.max(b - (r * p.spill + g * (1 - p.spill)), 0), b];
  return [r, g, b];
}

/** The same maths in GLSL. Needs `uKeyColor` (rgb 0..1) and `uKeyA` (similarity, blend, spill, type 0/1/2). */
export const KEYER_GLSL = `
uniform vec3 uKeyColor;
uniform vec4 uKeyA;
float keyAlpha(vec3 c) {
  float diff = sqrt(dot(c - uKeyColor, c - uKeyColor) / 3.0);
  if (uKeyA.y > 0.0001) return clamp((diff - uKeyA.x) / uKeyA.y, 0.0, 1.0);
  return diff > uKeyA.x ? 1.0 : 0.0;
}
vec3 keyDespill(vec3 c) {
  if (uKeyA.w > 1.5) c.g -= max(c.b - (c.r * uKeyA.z + c.g * (1.0 - uKeyA.z)), 0.0);
  else if (uKeyA.w > 0.5) c.g = min(c.g, c.r * uKeyA.z + c.b * (1.0 - uKeyA.z));
  return c;
}
`;
