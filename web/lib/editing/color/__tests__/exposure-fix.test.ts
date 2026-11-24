import { describe, expect, it } from 'vitest';
import fc from 'fast-check';
import { MAX_GRADE_NODES } from '../../engine/ops';
import type { GradeNode, GradeStack } from '../../types';
import {
  evalFix,
  exposureFix,
  findAutoFixNode,
  FIX_NODE_NAME,
  mergeHistograms,
  percentile,
  withAutoFixNode,
  type ExposureFixResult,
  type FixPrimaries,
  type LumaHistogram,
} from '../exposure-fix';

/** A histogram with a triangular bump of `width` bins (in 0..1) around `center`, plus optional spikes. */
function bump(center: number, width: number, spikes: Record<number, number> = {}): LumaHistogram {
  const bins = new Array<number>(256).fill(0);
  for (let i = 0; i < 256; i++) {
    const d = Math.abs((i + 0.5) / 256 - center) / width;
    if (d < 1) bins[i] = (1 - d) * 1000;
  }
  for (const [i, c] of Object.entries(spikes)) bins[Number(i)] += c;
  return { bins, total: bins.reduce((a, c) => a + c, 0) };
}

/** Pushes a histogram through f (bin centres) back into 256 bins. */
function applied(h: LumaHistogram, p: FixPrimaries): LumaHistogram {
  const bins = new Array<number>(256).fill(0);
  for (let i = 0; i < 256; i++) {
    const o = Math.min(255, Math.floor(evalFix((i + 0.5) / 256, p) * 256));
    bins[o] += h.bins[i];
  }
  return { bins, total: h.total };
}

const asFix = (r: ExposureFixResult) => {
  if (r.kind !== 'fix') throw new Error(`expected fix, got ${r.kind}`);
  return r;
};

/** The export's lutrgb expression, literally, steps 1 to 4. */
function lutrgb(v: number, p: FixPrimaries): number {
  let x = v * 2 ** p.exposure + p.offset.master;
  const gain = 0;
  const lift = 0;
  x = x * (1 + gain) + lift * (1 - x);
  x = Math.pow(Math.min(1, Math.max(0, x)), 1 / Math.max(0.05, 1 + p.gamma.master));
  x = (x - 0.5) * 1 + 0.5;
  return Math.min(1, Math.max(0, x));
}

describe('exposureFix', () => {
  it('reports no picture / flat / fine', () => {
    expect(exposureFix({ bins: new Array<number>(256).fill(0), total: 0 }).kind).toBe('noPicture');
    expect(exposureFix(bump(0.5, 0.002, { 128: 500 })).kind).toBe('flat');
    expect(exposureFix(bump(0.5, 0.5, { 0: 3000, 255: 3000 })).kind).toBe('fine');
  });

  it('dark clip: brightens the median toward 0.42', () => {
    const h = bump(0.2, 0.2);
    const r = asFix(exposureFix(h));
    expect(Math.abs(r.after.median - 0.42)).toBeLessThan(0.03);
    expect(r.summary.stops).toBeGreaterThan(0);
    expect(r.primaries.exposure).toBeGreaterThanOrEqual(-2);
    expect(r.primaries.exposure).toBeLessThanOrEqual(2);
  });

  it('bright clip: darkens the median to 0.42', () => {
    const r = asFix(exposureFix(bump(0.75, 0.25)));
    expect(Math.abs(r.after.median - 0.42)).toBeLessThan(0.03);
    expect(r.summary.stops).toBeLessThan(0);
    expect(r.after.p1).toBeLessThanOrEqual(r.before.p1 + 0.005);
  });

  it('flat-but-not-solid clip: median on 0.42 and the spread widens', () => {
    const r = asFix(exposureFix(bump(0.475, 0.125)));
    expect(Math.abs(r.after.median - 0.42)).toBeLessThan(0.03);
    expect(r.after.p99 - r.after.p1).toBeGreaterThan(r.before.p99 - r.before.p1);
    expect(2 ** r.primaries.exposure).toBeLessThanOrEqual(3.001);
  });

  it('follows the direction of the fix: darkening never lifts shadows, brightening never drops highlights', () => {
    for (const h of [bump(0.2, 0.2), bump(0.75, 0.25), bump(0.475, 0.125), bump(0.3, 0.3), bump(0.7, 0.2)]) {
      const r = exposureFix(h);
      if (r.kind !== 'fix') continue;
      if (r.before.median > 0.42) expect(r.after.p1).toBeLessThanOrEqual(Math.max(r.before.p1, 0.03) + 0.01);
      else expect(r.after.p99).toBeGreaterThanOrEqual(Math.min(r.before.p99, 0.96) - 0.01);
    }
  });

  it('backlit bimodal clip (30% near black, 70% sky) is never brightened away from the target', () => {
    // 30% of the mass at 0.02, 70% at 0.7: the median sits ~0.70, and an unguarded stretch would
    // push the sky to ~0.96 — farther from 0.42 than it started.
    const bins = new Array<number>(256).fill(0);
    bins[5] = 300;
    bins[179] = 700;
    const r = exposureFix({ bins, total: 1000 });
    if (r.kind === 'fix') {
      expect(r.before.median).toBeGreaterThan(0.6);
      expect(r.after.median).toBeLessThanOrEqual(r.before.median);
      expect(Math.abs(r.after.median - 0.42)).toBeLessThanOrEqual(Math.abs(r.before.median - 0.42));
    } else {
      expect(r.kind).toBe('fine');
    }
  });

  it('reports stops the node really applies, bounded to ±2 (not the raw median ratio)', () => {
    for (const h of [bump(0.03, 0.02), bump(0.08, 0.05), bump(0.2, 0.2), bump(0.9, 0.05)]) {
      const r = exposureFix(h);
      if (r.kind !== 'fix') continue;
      expect(Math.abs(r.summary.stops)).toBeLessThanOrEqual(2);
      expect(r.summary.stops).toBeGreaterThanOrEqual(-2);
      // The sign agrees with the direction the median actually moved.
      if (r.after.median > r.before.median + 0.001) expect(r.summary.stops).toBeGreaterThanOrEqual(0);
      if (r.after.median < r.before.median - 0.001) expect(r.summary.stops).toBeLessThanOrEqual(0);
    }
  });

  it('adds no clipping beyond the guard (5% already at 255)', () => {
    const h = bump(0.2, 0.15, { 255: 0.05 * 1000 * 100 });
    const r = exposureFix(h);
    if (r.kind === 'fix') {
      const out = applied(h, r.primaries);
      const top = (out.bins[254] + out.bins[255]) / out.total;
      const inTop = (h.bins[254] + h.bins[255]) / h.total;
      expect(top).toBeLessThanOrEqual(Math.max(inTop, 0.005) + 0.02);
    }
  });

  it('is idempotent: a second pass is fine or shifts the median by under 0.02', () => {
    for (const h of [bump(0.2, 0.2), bump(0.75, 0.25), bump(0.475, 0.125)]) {
      const r = exposureFix(h);
      if (r.kind !== 'fix') continue;
      const again = exposureFix(applied(h, r.primaries));
      if (again.kind === 'fix') expect(Math.abs(again.after.median - again.before.median)).toBeLessThan(0.02);
      else expect(['fine', 'flat']).toContain(again.kind);
    }
  });

  it('the node means what the maths assumed (literal lutrgb order)', () => {
    const r = asFix(exposureFix(bump(0.2, 0.2)));
    for (let v = 0; v <= 1; v += 0.05) expect(lutrgb(v, r.primaries)).toBeCloseTo(evalFix(v, r.primaries), 12);
    expect(lutrgb(r.before.median, r.primaries)).toBeCloseTo(r.after.median, 12);
  });

  it('property: never throws, finite fields, values in range', () => {
    fc.assert(
      fc.property(fc.array(fc.integer({ min: 0, max: 5000 }), { minLength: 256, maxLength: 256 }), (bins) => {
        const total = bins.reduce((a, c) => a + c, 0);
        const r = exposureFix({ bins, total });
        if (r.kind !== 'fix') return true;
        const nums = [r.primaries.exposure, r.primaries.offset.master, r.primaries.gamma.master, r.summary.stops, ...Object.values(r.before), ...Object.values(r.after)];
        expect(nums.every(Number.isFinite)).toBe(true);
        expect(Math.abs(r.primaries.exposure)).toBeLessThanOrEqual(2);
        expect(Math.abs(r.primaries.offset.master)).toBeLessThanOrEqual(1);
        expect(r.primaries.gamma.master).toBeGreaterThanOrEqual(-0.5);
        expect(r.primaries.gamma.master).toBeLessThanOrEqual(1);
        for (let v = 0; v <= 1; v += 0.1) {
          const o = evalFix(v, r.primaries);
          expect(o).toBeGreaterThanOrEqual(0);
          expect(o).toBeLessThanOrEqual(1);
        }
        return true;
      }),
      { numRuns: 200 },
    );
  });
});

describe('histogram helpers', () => {
  it('merges and ignores empty histograms; percentile is linear in a bin', () => {
    const a = bump(0.3, 0.1);
    const empty: LumaHistogram = { bins: new Array<number>(256).fill(0), total: 0 };
    const m = mergeHistograms([a, empty, a]);
    expect(m.total).toBe(a.total * 2);
    expect(percentile(m, 0.5)).toBeCloseTo(percentile(a, 0.5), 6);
    const one = new Array<number>(256).fill(0);
    one[100] = 10;
    expect(percentile({ bins: one, total: 10 }, 0.5)).toBeCloseTo(100.5 / 256, 6);
  });
});

describe('withAutoFixNode', () => {
  const p: FixPrimaries = { exposure: 0.5, offset: { r: 0, g: 0, b: 0, master: 0.01 }, gamma: { r: 0, g: 0, b: 0, master: 0.1 } };
  const user = (id: string): GradeNode => ({ id, name: `User ${id}`, enabled: true, primaries: { saturation: 1.2 }, curves: null, lut: null });

  it('inserts first and leaves other nodes untouched', () => {
    const stack: GradeStack = { enabled: true, nodes: [user('a'), user('b')] };
    const r = withAutoFixNode(stack, p, 'fix1');
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(r.stack.nodes?.map((n) => n.id)).toEqual(['fix1', 'a', 'b']);
    expect(r.stack.nodes?.[1]).toBe(stack.nodes?.[0]);
    expect(findAutoFixNode(r.stack)?.name).toBe(FIX_NODE_NAME);
  });

  it('handles a null stack', () => {
    const r = withAutoFixNode(null, p, 'fix1');
    expect(r.ok && r.stack.nodes?.length).toBe(1);
  });

  it('replaces in place keeping id and position', () => {
    const first = withAutoFixNode({ enabled: true, nodes: [user('a')] }, p, 'fix1');
    if (!first.ok) throw new Error('unexpected');
    const stack: GradeStack = { enabled: true, nodes: [user('x'), ...(first.stack.nodes ?? [])] };
    const r = withAutoFixNode(stack, { ...p, exposure: 1 }, 'ignored');
    expect(r.ok).toBe(true);
    if (!r.ok) return;
    expect(r.stack.nodes?.map((n) => n.id)).toEqual(['x', 'fix1', 'a']);
    expect(r.stack.nodes?.[1].primaries?.exposure).toBe(1);
  });

  it('refuses at the cap without dropping a node, but still replaces at the cap', () => {
    const nodes = Array.from({ length: MAX_GRADE_NODES }, (_, i) => user(`n${i}`));
    const r = withAutoFixNode({ enabled: true, nodes }, p, 'fix1');
    expect(r).toEqual({ ok: false, reason: 'tooManyNodes' });
    const full: GradeStack = { enabled: true, nodes: [{ ...user('f'), name: FIX_NODE_NAME }, ...nodes.slice(1)] };
    expect(withAutoFixNode(full, p, 'z').ok).toBe(true);
  });
});
