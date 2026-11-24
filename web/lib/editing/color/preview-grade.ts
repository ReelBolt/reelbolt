/**
 * The compositor's view of a grade (P7): the enabled nodes' PRIMARIES folded into one set of shader
 * uniforms. Curves, LUT nodes and secondaries (qualifier / window) are NOT here: a clip carrying any goes through
 * the grade pass (`preview/grade-pass-plan.ts`) instead, node by node; exposure, offset,
 * lift/gamma/gain (master and per channel), contrast about the pivot, saturation, hue and
 * temperature/tint are. Folding is in application order, so a stack of two nodes previews as the
 * renderer applies it only where the maths commutes (exposure, offsets, lift/gain compose; a second
 * contrast or saturation multiplies into the first) — enough for grading decisions, honest about
 * the rest in the parity badge.
 */

import type { GradeNode, GradePrimaries, GradeStack, GradeWheel } from '../types';
import { isIdentityPrimaries } from './grade';

export interface PreviewGrade {
  /** 2^exposure. */
  exposureGain: number;
  /** Per-channel offset (master folded in), added after exposure. */
  offset: [number, number, number];
  /** Per-channel lift (master folded in). */
  lift: [number, number, number];
  /** Per-channel gain multiplier (1 + gain, master folded in). */
  gain: [number, number, number];
  /** Per-channel gamma exponent (1 / (1 + gamma), master folded in). */
  gamma: [number, number, number];
  contrast: number;
  pivot: number;
  saturation: number;
  /** Degrees. */
  hue: number;
  /** Temperature as a red/blue multiplier pair, tint as a green multiplier. */
  temperature: number;
  tint: number;
}

export const IDENTITY_PREVIEW_GRADE: PreviewGrade = Object.freeze({
  exposureGain: 1,
  offset: [0, 0, 0] as [number, number, number],
  lift: [0, 0, 0] as [number, number, number],
  gain: [1, 1, 1] as [number, number, number],
  gamma: [1, 1, 1] as [number, number, number],
  contrast: 1,
  pivot: 0.5,
  saturation: 1,
  hue: 0,
  temperature: 0,
  tint: 0,
});

export function isIdentityPreviewGrade(g: PreviewGrade): boolean {
  return g.exposureGain === 1 && g.offset.every((v) => v === 0) && g.lift.every((v) => v === 0) && g.gain.every((v) => v === 1) && g.gamma.every((v) => v === 1)
    && g.contrast === 1 && g.saturation === 1 && g.hue === 0 && g.temperature === 0 && g.tint === 0;
}

/** P9: whether a node is confined by an HSL qualifier that selects less than everything, or a window. */
export function isSecondary(node: GradeNode): boolean {
  if (node.window) return true;
  const q = node.qualifier;
  if (!q) return false;
  return (q.hueWidth ?? 180) < 180 || (q.satMin ?? 0) > 0 || (q.satMax ?? 1) < 1 || (q.lumMin ?? 0) > 0 || (q.lumMax ?? 1) < 1 || q.invert === true;
}

/** Folds the enabled nodes' primaries of every stack (in order) into one preview grade. */
export function previewGradeOf(stacks: readonly (GradeStack | null | undefined)[]): PreviewGrade {
  const g: PreviewGrade = { ...IDENTITY_PREVIEW_GRADE, offset: [0, 0, 0], lift: [0, 0, 0], gain: [1, 1, 1], gamma: [1, 1, 1] };
  let any = false;
  for (const stack of stacks) {
    if (!stack || stack.enabled === false || !stack.nodes) continue;
    for (const node of stack.nodes) {
      if (node.enabled === false || !node.primaries || isIdentityPrimaries(node.primaries)) continue;
      // P9: a secondary (qualifier/window) is confined to part of the frame; folding it whole-frame
      // would preview a plausible-but-wrong picture, so it is skipped and the parity says so.
      if (isSecondary(node)) continue;
      fold(g, node.primaries);
      any = true;
    }
  }
  return any ? g : IDENTITY_PREVIEW_GRADE;
}

function fold(g: PreviewGrade, p: GradePrimaries): void {
  g.exposureGain *= Math.pow(2, p.exposure ?? 0);
  const channels = ['r', 'g', 'b'] as const;
  channels.forEach((c, i) => {
    g.offset[i] += (p.offset?.[c] ?? 0) + (p.offset?.master ?? 0);
    g.lift[i] += (p.lift?.[c] ?? 0) + (p.lift?.master ?? 0);
    g.gain[i] *= 1 + (p.gain?.[c] ?? 0) + (p.gain?.master ?? 0);
    const gamma = (p.gamma?.[c] ?? 0) + (p.gamma?.master ?? 0);
    g.gamma[i] *= 1 / Math.max(0.05, 1 + gamma);
  });
  if ((p.contrast ?? 1) !== 1) {
    g.contrast *= p.contrast ?? 1;
    g.pivot = p.pivot ?? 0.5;
  }
  g.saturation *= p.saturation ?? 1;
  g.hue += p.hue ?? 0;
  g.temperature += p.temperature ?? 0;
  g.tint += p.tint ?? 0;
}

/**
 * The same maths as the fragment shader, on one RGB triple in 0..1 — the reference the shader is
 * checked against and what the tests pin.
 */
export function applyPreviewGrade(g: PreviewGrade, rgb: [number, number, number]): [number, number, number] {
  const out: [number, number, number] = [0, 0, 0];
  for (let i = 0; i < 3; i++) {
    let v = rgb[i] * g.exposureGain + g.offset[i];
    v = v * g.gain[i] + g.lift[i] * (1 - v);
    v = Math.pow(Math.max(0, v), g.gamma[i]);
    v = (v - g.pivot) * g.contrast + g.pivot;
    out[i] = v;
  }
  // Temperature: warm pushes red up and blue down; tint: magenta pushes green down.
  out[0] *= 1 + 0.15 * g.temperature;
  out[2] *= 1 - 0.15 * g.temperature;
  out[1] *= 1 - 0.1 * g.tint;
  // Saturation around luma, then hue rotation (YIQ-style).
  const y = 0.2126 * out[0] + 0.7152 * out[1] + 0.0722 * out[2];
  for (let i = 0; i < 3; i++) out[i] = y + (out[i] - y) * g.saturation;
  if (g.hue !== 0) {
    const a = (g.hue * Math.PI) / 180;
    const cosA = Math.cos(a);
    const sinA = Math.sin(a);
    const [r, gg, b] = out;
    out[0] = r * (0.299 + 0.701 * cosA + 0.168 * sinA) + gg * (0.587 - 0.587 * cosA + 0.33 * sinA) + b * (0.114 - 0.114 * cosA - 0.497 * sinA);
    out[1] = r * (0.299 - 0.299 * cosA - 0.328 * sinA) + gg * (0.587 + 0.413 * cosA + 0.035 * sinA) + b * (0.114 - 0.114 * cosA + 0.292 * sinA);
    out[2] = r * (0.299 - 0.3 * cosA + 1.25 * sinA) + gg * (0.587 - 0.588 * cosA - 1.05 * sinA) + b * (0.114 + 0.886 * cosA - 0.203 * sinA);
  }
  return [clamp01(out[0]), clamp01(out[1]), clamp01(out[2])];
}

const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);

// ── Per-node path: the export's maths (GradeFilterBuilder.Primaries), node by node ─────────────────

/** tint and the gamma wheel's green share ONE colorbalance gm (sum). false = tint replaces gamma green. */
export const TINT_SUMS_WITH_GAMMA_GREEN = true;

/**
 * Shader slots per draw of the single-draw shader: the first BOUND-1 nodes run exactly; any further nodes are folded (old maths)
 * into one last stage and the parity says approximate. L3: a clip with more than BOUND nodes goes through the grade pass instead
 * (no uniform limit), so the fold only remains for the SVG fallback and direct compositor callers; the pass holds GRADE_NODE_MAX.
 */
export const GRADE_NODE_BOUND = 8;

/** Enabled primaries nodes kept per clip (group stack first, then the clip's). */
export const GRADE_NODE_MAX = 16;

type V3 = [number, number, number];

/** One node's primaries, pre-digested into what the export's lutrgb / colorbalance / colortemperature / hue evaluate. */
export interface PreviewGradeNode {
  /** The source primaries, kept so nodes past the shader bound can be folded with the old maths. */
  primaries: GradePrimaries;
  /** 2^exposure. */
  exposureGain: number;
  /** Per-channel offset (master folded in). */
  offset: V3;
  liftMaster: number;
  gainMaster: number;
  gammaMaster: number;
  contrast: number;
  pivot: number;
  /** colorbalance shadows / midtones / highlights (lift / gamma / gain per channel; midtone green carries tint). */
  shadows: V3;
  mids: V3;
  highs: V3;
  /** colortemperature's per-channel multiplier (1,1,1 when no temperature). */
  temp: V3;
  /** Degrees. */
  hue: number;
  saturation: number;
}

const clamp = (v: number, lo: number, hi: number) => (v < lo ? lo : v > hi ? hi : v);

/** ffmpeg colortemperature's kelvin -> rgb (0..1). */
function kelvinRgb(kelvin: number): V3 {
  const t = kelvin / 100;
  let r: number;
  let g: number;
  let b: number;
  if (t <= 66) {
    r = 255;
    g = 99.4708025861 * Math.log(t) - 161.1195681661;
    b = t <= 19 ? 0 : 138.5177312231 * Math.log(t - 10) - 305.0447927307;
  } else {
    r = 329.698727446 * Math.pow(t - 60, -0.1332047592);
    g = 288.1221695283 * Math.pow(t - 60, -0.0755148492);
    b = 255;
  }
  return [clamp(r, 0, 255) / 255, clamp(g, 0, 255) / 255, clamp(b, 0, 255) / 255];
}

export function previewGradeNodeOf(p: GradePrimaries): PreviewGradeNode {
  const offsetMaster = p.offset?.master ?? 0;
  const tint = p.tint ?? 0;
  const gammaG = p.gamma?.g ?? 0;
  const midGreen = TINT_SUMS_WITH_GAMMA_GREEN ? gammaG + -tint * 0.5 : tint !== 0 ? -tint * 0.5 : gammaG;
  const wheel = (w: GradeWheel | null | undefined, g: number): V3 => [clamp(w?.r ?? 0, -1, 1), clamp(g, -1, 1), clamp(w?.b ?? 0, -1, 1)];
  const temperature = p.temperature ?? 0;
  return {
    primaries: p,
    exposureGain: Math.pow(2, p.exposure ?? 0),
    offset: [offsetMaster + (p.offset?.r ?? 0), offsetMaster + (p.offset?.g ?? 0), offsetMaster + (p.offset?.b ?? 0)],
    liftMaster: p.lift?.master ?? 0,
    gainMaster: p.gain?.master ?? 0,
    gammaMaster: p.gamma?.master ?? 0,
    contrast: p.contrast ?? 1,
    pivot: p.pivot ?? 0.5,
    shadows: wheel(p.lift, p.lift?.g ?? 0),
    mids: wheel(p.gamma, midGreen),
    highs: wheel(p.gain, p.gain?.g ?? 0),
    temp: temperature !== 0 ? kelvinRgb(Math.round(6500 - temperature * 2500)) : [1, 1, 1],
    hue: p.hue ?? 0,
    saturation: p.saturation ?? 1,
  };
}

/** The enabled, non-identity, non-secondary primaries nodes of the enabled stacks, in application order (max 16). */
export function previewGradeNodesOf(stacks: readonly (GradeStack | null | undefined)[]): PreviewGradeNode[] {
  const out: PreviewGradeNode[] = [];
  for (const stack of stacks) {
    if (!stack || stack.enabled === false || !stack.nodes) continue;
    for (const node of stack.nodes) {
      if (node.enabled === false || !node.primaries || isIdentityPrimaries(node.primaries) || isSecondary(node)) continue;
      if (out.length >= GRADE_NODE_MAX) return out;
      out.push(previewGradeNodeOf(node.primaries));
    }
  }
  return out;
}

/** colorbalance (ffmpeg vf_colorbalance.c, preserve_lightness off) weights for the pixel entering the filter. */
function balance(n: PreviewGradeNode, rgb: V3): V3 {
  const l = Math.max(rgb[0], rgb[1], rgb[2]) + Math.min(rgb[0], rgb[1], rgb[2]);
  const a = 4;
  const b = 0.333;
  const scale = 0.7;
  const ws = clamp((b - l) * a + 0.5, 0, 1) * scale;
  const wm = clamp((l - b) * a + 0.5, 0, 1) * clamp((1 - l - b) * a + 0.5, 0, 1) * scale;
  const wh = clamp((l + b - 1) * a + 0.5, 0, 1) * scale;
  return [0, 1, 2].map((i) => clamp(rgb[i] + n.shadows[i] * ws + n.mids[i] * wm + n.highs[i] * wh, 0, 1)) as V3;
}

/** One node, exactly as the export's filter chain evaluates it (0..1 in, 0..1 out). */
export function applyPreviewGradeNode(n: PreviewGradeNode, rgb: V3): V3 {
  // lutrgb
  const lut: V3 = [0, 0, 0];
  for (let i = 0; i < 3; i++) {
    let x = rgb[i] * n.exposureGain + n.offset[i];
    if (n.liftMaster !== 0 || n.gainMaster !== 0) x = x * (1 + n.gainMaster) + n.liftMaster * (1 - x);
    if (n.gammaMaster !== 0) x = Math.pow(clamp(x, 0, 1), 1 / Math.max(0.05, 1 + n.gammaMaster));
    if (n.contrast !== 1) x = (x - n.pivot) * n.contrast + n.pivot;
    lut[i] = clamp(x, 0, 1);
  }
  // colorbalance
  const cb = balance(n, lut);
  // colortemperature
  const t: V3 = [clamp(cb[0] * n.temp[0], 0, 1), clamp(cb[1] * n.temp[1], 0, 1), clamp(cb[2] * n.temp[2], 0, 1)];
  if (n.hue === 0 && n.saturation === 1) return t;
  // hue: BT.601 limited-range YUV, chroma rotated by h and scaled by s
  const [r, g, b] = [t[0] * 255, t[1] * 255, t[2] * 255];
  const y = 16 + 0.2568 * r + 0.5041 * g + 0.0979 * b;
  const u = -0.1482 * r - 0.291 * g + 0.4392 * b;
  const v = 0.4392 * r - 0.3678 * g - 0.0714 * b;
  const a = (n.hue * Math.PI) / 180;
  const c = Math.cos(a);
  const s = Math.sin(a);
  const u2 = (u * c - v * s) * n.saturation;
  const v2 = (v * c + u * s) * n.saturation;
  const yy = (y - 16) * 1.1644;
  return [clamp((yy + 1.596 * v2) / 255, 0, 1), clamp((yy - 0.392 * u2 - 0.813 * v2) / 255, 0, 1), clamp((yy + 2.017 * u2) / 255, 0, 1)];
}

/**
 * Past the shader bound: the first GRADE_NODE_BOUND - 1 nodes run per node, the rest fold (old `fold` maths) into ONE extra
 * stage run last. Shared by the CPU reference and the compositor so both agree. Null when everything fits.
 */
export function splitGradeNodes(nodes: readonly PreviewGradeNode[]): { head: readonly PreviewGradeNode[]; tail: PreviewGrade | null } {
  if (nodes.length <= GRADE_NODE_BOUND) return { head: nodes, tail: null };
  const g: PreviewGrade = { ...IDENTITY_PREVIEW_GRADE, offset: [0, 0, 0], lift: [0, 0, 0], gain: [1, 1, 1], gamma: [1, 1, 1] };
  for (const n of nodes.slice(GRADE_NODE_BOUND - 1)) fold(g, n.primaries);
  return { head: nodes.slice(0, GRADE_NODE_BOUND - 1), tail: g };
}

/** The export's grade on one RGB triple: every node in order (nodes past the bound fold into one last approximate stage). */
export function applyPreviewGradeNodes(nodes: readonly PreviewGradeNode[], rgb: V3): V3 {
  const { head, tail } = splitGradeNodes(nodes);
  let v = rgb;
  for (const n of head) v = applyPreviewGradeNode(n, v);
  return tail ? applyPreviewGrade(tail, v) : v;
}

/**
 * Several nodes' primaries combined into ONE set (the old `fold` maths: exposure, offsets, lift add; gains and
 * saturation multiply; gamma exponents multiply; contrast multiplies about the last pivot). Past GRADE_NODE_MAX the
 * grade pass draws the extra nodes as this single stage instead of dropping them; the parity says approximate.
 */
export function foldPrimaries(list: readonly GradePrimaries[]): GradePrimaries {
  const g: PreviewGrade = { ...IDENTITY_PREVIEW_GRADE, offset: [0, 0, 0], lift: [0, 0, 0], gain: [1, 1, 1], gamma: [1, 1, 1] };
  for (const p of list) fold(g, p);
  return {
    exposure: Math.log2(g.exposureGain),
    offset: { r: g.offset[0], g: g.offset[1], b: g.offset[2] },
    lift: { r: g.lift[0], g: g.lift[1], b: g.lift[2] },
    gain: { r: g.gain[0] - 1, g: g.gain[1] - 1, b: g.gain[2] - 1 },
    gamma: { r: 1 / g.gamma[0] - 1, g: 1 / g.gamma[1] - 1, b: 1 / g.gamma[2] - 1 },
    contrast: g.contrast,
    pivot: g.pivot,
    saturation: g.saturation,
    hue: g.hue,
    temperature: g.temperature,
    tint: g.tint,
  };
}
