import { describe, expect, it } from 'vitest';
import { applyCurveBake, bakeCurves, curveTable, CURVE_TABLE_SIZE, normalizedCurvePoints } from '../grade-curves';

const idx = (x: number) => Math.round(x * (CURVE_TABLE_SIZE - 1));

describe('normalizedCurvePoints (GradeFilterBuilder.Points)', () => {
  it('orders by x, adds (0,0) and (1,1) when the ends are missing, clamps to 0..1', () => {
    expect(normalizedCurvePoints([{ x: 0.8, y: 1.4 }, { x: 0.2, y: -0.3 }])).toEqual([
      { x: 0, y: 0 },
      { x: 0.2, y: 0 },
      { x: 0.8, y: 1 },
      { x: 1, y: 1 },
    ]);
  });
  it('keeps given end points as they are', () => {
    expect(normalizedCurvePoints([{ x: 0, y: 0.1 }, { x: 1, y: 0.9 }])).toEqual([{ x: 0, y: 0.1 }, { x: 1, y: 0.9 }]);
  });
});

describe('curveTable (natural cubic spline, as ffmpeg curves)', () => {
  it('two points is a straight line', () => {
    const t = curveTable([{ x: 0, y: 0 }, { x: 1, y: 1 }]);
    for (let i = 0; i < CURVE_TABLE_SIZE; i += 1) expect(t[i]).toBeCloseTo(i / 255, 5);
  });
  it('collinear points stay a straight line (no overshoot)', () => {
    const t = curveTable([{ x: 0, y: 0 }, { x: 0.5, y: 0.5 }, { x: 1, y: 1 }]);
    for (let i = 0; i < CURVE_TABLE_SIZE; i += 1) expect(t[i]).toBeCloseTo(i / 255, 5);
  });
  it('passes through its knots at 8-bit positions', () => {
    const t = curveTable([{ x: 0.2, y: 0.1 }, { x: 0.6, y: 0.8 }]);
    expect(t[0]).toBeCloseTo(0, 5);
    expect(t[idx(0.2)]).toBeCloseTo(0.1, 5);
    expect(t[idx(0.6)]).toBeCloseTo(0.8, 5);
    expect(t[CURVE_TABLE_SIZE - 1]).toBeCloseTo(1, 5);
  });
  it('matches the hand-solved spline through (0,0) (0.5,0.7) (1,1) at x = 64/255', () => {
    // second derivative at the middle knot = -2.4 (natural ends); value at 64/255 solved by hand = 0.38892
    const t = curveTable([{ x: 0.5, y: 0.7 }]);
    expect(t[64]).toBeCloseTo(0.38892, 3);
    expect(t[127]).toBeLessThan(0.7);
    expect(t[128]).toBeGreaterThan(0.69);
  });
  it('an empty curve is the identity and every value is clamped to 0..1', () => {
    const t = curveTable([]);
    expect(t[10]).toBeCloseTo(10 / 255, 6);
    const steep = curveTable([{ x: 0.1, y: 0 }, { x: 0.2, y: 1 }, { x: 0.3, y: 0 }]);
    for (const v of steep) {
      expect(v).toBeGreaterThanOrEqual(0);
      expect(v).toBeLessThanOrEqual(1);
    }
  });
});

describe('bakeCurves', () => {
  it('is null without any points', () => {
    expect(bakeCurves(null)).toBeNull();
    expect(bakeCurves({ y: [], r: null })).toBeNull();
  });
  it('a channel curve moves only its channel', () => {
    const bake = bakeCurves({ r: [{ x: 0.2, y: 0.1 }, { x: 0.6, y: 0.8 }] });
    expect(bake).not.toBeNull();
    const out = applyCurveBake(bake!, [0.2, 0.2, 0.2]);
    expect(out[0]).toBeCloseTo(0.1, 4);
    expect(out[1]).toBeCloseTo(0.2, 4);
    expect(out[2]).toBeCloseTo(0.2, 4);
  });
  it('the master curve is a second pass over the channel curve', () => {
    const bake = bakeCurves({ y: [{ x: 0.2, y: 0.1 }, { x: 0.6, y: 0.8 }], g: [{ x: 0.2, y: 0.6 }] });
    const out = applyCurveBake(bake!, [0.6, 0.2, 0.6]);
    expect(out[0]).toBeCloseTo(0.8, 3); // master only: 0.6 -> 0.8
    expect(out[1]).toBeCloseTo(0.8, 2); // channel 0.2 -> 0.6, then master 0.6 -> 0.8
  });
  it('the same points give the same key; different points a different one', () => {
    const a = bakeCurves({ y: [{ x: 0.5, y: 0.6 }] });
    const b = bakeCurves({ y: [{ x: 0.5, y: 0.6 }] });
    const c = bakeCurves({ y: [{ x: 0.5, y: 0.7 }] });
    expect(a!.key).toBe(b!.key);
    expect(a!.key).not.toBe(c!.key);
  });
});
