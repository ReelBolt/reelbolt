/**
 * Fix exposure (pure): a luma histogram in, one conservative "Auto fix" grade node out (or a reason
 * to do nothing). The node is an ordinary grade node, so the maths uses only the fields the export's
 * single `lutrgb` pass draws, in its order: `x = v*2^exposure + offset`, gain/lift (unused), then
 * `pow(clip(x), 1/(1+gamma))`. That is `f(v) = clip(v*s + t, 0, 1)^k`, s = 2^exposure, t = offset.master,
 * k = 1/(1+gamma.master). Nothing per channel, so no colour cast change.
 */

import type { GradeNode, GradePrimaries, GradeStack } from '../types';
import { MAX_GRADE_NODES } from '../engine/ops';
import { newNode } from './grade';
import type { ScopeData } from './scopes';

export interface LumaHistogram {
  bins: Float64Array | number[];
  total: number;
}

export const FIX_NODE_NAME = 'Auto fix';

const BIN_COUNT = 256;
const TARGET_BLACK = 0.03;
const TARGET_MEDIAN = 0.42;
const TARGET_WHITE = 0.96;

export interface Percentiles {
  p1: number;
  median: number;
  p99: number;
}

export type FixPrimaries = {
  exposure: number;
  offset: { r: 0; g: 0; b: 0; master: number };
  gamma: { r: 0; g: 0; b: 0; master: number };
};

export type ExposureFixResult =
  | { kind: 'noPicture' }
  | { kind: 'flat' }
  | { kind: 'fine' }
  | {
      kind: 'fix';
      primaries: FixPrimaries;
      before: Percentiles;
      after: Percentiles;
      summary: { stops: number; shadows: 'deeper' | 'lifted' | 'same'; highlights: 'brighter' | 'softer' | 'same' };
    };

const clamp = (v: number, lo: number, hi: number): number => Math.min(hi, Math.max(lo, v));
const round3 = (v: number): number => {
  const r = Math.round(v * 1000) / 1000;
  return r === 0 ? 0 : r;
};

/** The sum of several histograms; empty (or malformed) ones are ignored. */
export function mergeHistograms(list: readonly LumaHistogram[]): LumaHistogram {
  const bins = new Array<number>(BIN_COUNT).fill(0);
  let total = 0;
  for (const h of list) {
    if (!(h.total > 0) || h.bins.length !== BIN_COUNT) continue;
    for (let i = 0; i < BIN_COUNT; i++) {
      const c = h.bins[i];
      if (Number.isFinite(c) && c > 0) {
        bins[i] += c;
        total += c;
      }
    }
  }
  return { bins, total };
}

/** The luma histogram of `computeScopes` output, resampled to 256 bins (any source resolution). */
export function histogramFromScopes(data: ScopeData): LumaHistogram {
  const src = data.histogram.y;
  const n = src.length;
  const bins = new Array<number>(BIN_COUNT).fill(0);
  if (n === 0) return { bins, total: 0 };
  for (let i = 0; i < n; i++) {
    const count = src[i];
    if (count <= 0) continue;
    // spread source bin [i/n, (i+1)/n) over the target bins it overlaps
    const lo = (i / n) * BIN_COUNT;
    const hi = ((i + 1) / n) * BIN_COUNT;
    for (let j = Math.floor(lo); j < Math.min(BIN_COUNT, Math.ceil(hi)); j++) {
      const overlap = Math.min(hi, j + 1) - Math.max(lo, j);
      if (overlap > 0) bins[j] += (count * overlap) / (hi - lo);
    }
  }
  let total = 0;
  for (const c of bins) total += c;
  return { bins, total };
}

/** The luma value (0..1) below which a fraction `q` of the mass lies; linear within a bin. */
export function percentile(h: LumaHistogram, q: number): number {
  const n = h.bins.length;
  if (n === 0 || !(h.total > 0)) return 0;
  const target = clamp(q, 0, 1) * h.total;
  let acc = 0;
  for (let i = 0; i < n; i++) {
    const c = h.bins[i];
    if (c > 0 && acc + c >= target) return clamp((i + (target - acc) / c) / n, 0, 1);
    acc += c;
  }
  return 1;
}

/** The fix curve exactly as the export's lutrgb pass draws it (lift/gain 0, contrast 1). */
export function evalFix(v: number, p: FixPrimaries): number {
  const x = v * 2 ** p.exposure + p.offset.master;
  const g = Math.max(0.05, 1 + p.gamma.master);
  return Math.pow(clamp(x, 0, 1), 1 / g);
}

const measure = (h: LumaHistogram): Percentiles => ({ p1: percentile(h, 0.01), median: percentile(h, 0.5), p99: percentile(h, 0.99) });

/** Mass of the histogram that `s*v + t` sends to >= 1 (or <= 0), measured at bin centres. */
function clippedMass(h: LumaHistogram, s: number, t: number, side: 'white' | 'black'): number {
  const n = h.bins.length;
  let mass = 0;
  for (let i = 0; i < n; i++) {
    const x = (s * (i + 0.5)) / n + t;
    if (side === 'white' ? x >= 1 : x <= 0) mass += h.bins[i];
  }
  return mass / h.total;
}

function inputMass(h: LumaHistogram, side: 'white' | 'black'): number {
  const n = h.bins.length;
  let mass = 0;
  for (let i = 0; i < n; i++) {
    const v = (i + 0.5) / n;
    if (side === 'white' ? v >= 254 / 255 : v <= 1 / 255) mass += h.bins[i];
  }
  return mass / h.total;
}

function solve(p: Percentiles, b: number, w: number): { s: number; t: number; k: number } {
  const span = p.p99 - p.p1;
  const at = (k: number) => {
    const ub = b ** (1 / k);
    const uw = w ** (1 / k);
    const s = (uw - ub) / span;
    const t = ub - s * p.p1;
    return { s, t, k, g: Math.pow(clamp(s * p.median + t, 0, 1), k) - TARGET_MEDIAN };
  };
  let lo = 0.5;
  let hi = 2;
  const a = at(lo);
  const z = at(hi);
  if (a.g === 0) return a;
  if (z.g === 0) return z;
  if (Math.sign(a.g) === Math.sign(z.g)) return Math.abs(a.g) <= Math.abs(z.g) ? a : z;
  const increasing = z.g > a.g;
  let best = a;
  for (let i = 0; i < 60; i++) {
    const mid = (lo + hi) / 2;
    best = at(mid);
    if (best.g === 0) break;
    if (best.g > 0 === increasing) hi = mid;
    else lo = mid;
  }
  return best;
}

export function exposureFix(h: LumaHistogram): ExposureFixResult {
  if (!(h.total > 0)) return { kind: 'noPicture' };
  const before = measure(h);
  if (before.p99 - before.p1 < 0.02) return { kind: 'flat' };
  if (before.p1 <= 0.06 && before.p99 >= 0.9 && before.median >= 0.3 && before.median <= 0.55) return { kind: 'fine' };

  const b = Math.min(before.p1, TARGET_BLACK);
  const w = Math.min(1, Math.max(before.p99, TARGET_WHITE));
  const sol = solve(before, b, w);
  // A solved curve is only accepted when it actually helps the median: it must land closer to
  // 0.42 than the clip already was and must not shoot past the target. A backlit clip (30% near
  // black, 70% sky) otherwise gets its own bright sky pushed even brighter by the stretch.
  const medianAt = (ss: number, tt: number, kk: number): number => Math.pow(clamp(ss * before.median + tt, 0, 1), kk);
  const solvedMedian = medianAt(sol.s, sol.t, sol.k);
  const movesAway = Math.abs(solvedMedian - TARGET_MEDIAN) > Math.abs(before.median - TARGET_MEDIAN) + 1e-9;
  const overshot = before.median < TARGET_MEDIAN
    ? solvedMedian - TARGET_MEDIAN > 0.03
    : before.median > TARGET_MEDIAN ? TARGET_MEDIAN - solvedMedian > 0.03 : false;
  const whiteAllow = Math.max(inputMass(h, 'white'), 0.005);
  const blackAllow = Math.max(inputMass(h, 'black'), 0.005);
  const darkening = before.median > TARGET_MEDIAN;
  const clipOk = (ss: number, tt: number): boolean => clippedMass(h, ss, tt, 'white') <= whiteAllow && clippedMass(h, ss, tt, 'black') <= blackAllow;
  const inLimits = (ss: number, tt: number): boolean => ss >= 2 ** -2 && ss <= 3 && Math.abs(tt) <= 1;

  let s = sol.s;
  let t = sol.t;
  const k = sol.k;
  if (!(inLimits(s, t) && clipOk(s, t)) || movesAway || overshot) {
    // Median-anchored fallback: keep the solved gamma, stretch s in [1, 3], put the median on target,
    // and shrink s toward 1 until the limits and the direction rules hold (at s = 1 take what t gives).
    const tFor = (ss: number): number => TARGET_MEDIAN ** (1 / k) - ss * before.median;
    const ok = (ss: number): boolean => {
      const tt = tFor(ss);
      if (Math.abs(tt) > 1 || !clipOk(ss, tt)) return false;
      // darkening: highlights may come down, shadows must not rise; brightening: the reverse
      if (darkening) return Math.pow(clamp(ss * before.p1 + tt, 0, 1), k) <= before.p1 + 1e-9;
      return Math.pow(clamp(ss * before.p99 + tt, 0, 1), k) >= before.p99 - 1e-9;
    };
    const s0 = clamp(sol.s, 1, 3);
    if (ok(s0)) s = s0;
    else if (!ok(1)) s = 1;
    else {
      let lo = 1;
      let hi = s0;
      for (let i = 0; i < 50; i++) {
        const m = (lo + hi) / 2;
        if (ok(m)) lo = m;
        else hi = m;
      }
      s = lo;
    }
    t = tFor(s);
  }

  const exposure = round3(clamp(Math.log2(Math.max(s, 1e-6)), -2, 2));
  const offset = round3(clamp(t, -1, 1));
  const gamma = round3(clamp(1 / k - 1, -0.5, 1));
  if (exposure === 0 && offset === 0 && gamma === 0) return { kind: 'fine' };

  const primaries: FixPrimaries = { exposure, offset: { r: 0, g: 0, b: 0, master: offset }, gamma: { r: 0, g: 0, b: 0, master: gamma } };
  const after: Percentiles = { p1: evalFix(before.p1, primaries), median: evalFix(before.median, primaries), p99: evalFix(before.p99, primaries) };
  // The fallback failed too (rounding/clipping left the median farther out): write nothing.
  if (Math.abs(after.median - TARGET_MEDIAN) > Math.abs(before.median - TARGET_MEDIAN) + 0.001) return { kind: 'fine' };
  // Stops the node really applies at the median (after/before), clamped to what a +-2 stop
  // control can say, so the line is truthful, bounded and agrees with the direction of the move.
  const moved = Math.log2(Math.max(after.median, 1e-6) / Math.max(before.median, 1e-6));
  const stops = Math.round(clamp(moved, -2, 2) * 10) / 10;
  const dShadow = after.p1 - before.p1;
  const dHigh = after.p99 - before.p99;
  return {
    kind: 'fix',
    primaries,
    before,
    after,
    summary: {
      stops: stops === 0 ? 0 : stops,
      shadows: dShadow < -0.01 ? 'deeper' : dShadow > 0.01 ? 'lifted' : 'same',
      highlights: dHigh > 0.01 ? 'brighter' : dHigh < -0.01 ? 'softer' : 'same',
    },
  };
}

export function findAutoFixNode(stack: GradeStack | null | undefined): GradeNode | null {
  return stack?.nodes?.find((n) => n.name === FIX_NODE_NAME) ?? null;
}

export type AutoFixNodeResult = { ok: true; stack: GradeStack } | { ok: false; reason: 'tooManyNodes' };

/** Replaces the existing "Auto fix" node in place, or inserts a new one first; never drops a user node. */
export function withAutoFixNode(stack: GradeStack | null | undefined, primaries: FixPrimaries, newId: string): AutoFixNodeResult {
  const nodes = stack?.nodes ?? [];
  const enabled = stack?.enabled ?? true;
  const written: GradePrimaries = { exposure: primaries.exposure, offset: { ...primaries.offset }, gamma: { ...primaries.gamma } };
  const index = nodes.findIndex((n) => n.name === FIX_NODE_NAME);
  if (index >= 0) {
    return { ok: true, stack: { enabled, nodes: nodes.map((n, i) => (i === index ? { ...n, enabled: true, primaries: written } : n)) } };
  }
  if (nodes.length >= MAX_GRADE_NODES) return { ok: false, reason: 'tooManyNodes' };
  const node: GradeNode = { ...newNode(newId, FIX_NODE_NAME), primaries: written };
  return { ok: true, stack: { enabled, nodes: [node, ...nodes] } };
}
