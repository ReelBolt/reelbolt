import { describe, expect, it } from 'vitest';
import fc from 'fast-check';
import { describeWheel, isIdentityWheel, nudgeWheel, pointerToDisc, resetWheelColour, wheelFromPuck, wheelPuck } from '../wheel';
import { lookNode } from '../grade';

const disc = fc.record({ x: fc.double({ min: -1, max: 1, noNaN: true }), y: fc.double({ min: -1, max: 1, noNaN: true }) });
const wheelArb = fc.record({
  r: fc.double({ min: -1, max: 1, noNaN: true }),
  g: fc.double({ min: -1, max: 1, noNaN: true }),
  b: fc.double({ min: -1, max: 1, noNaN: true }),
  master: fc.double({ min: -1, max: 1, noNaN: true }),
});
const cbcr = (r: number, g: number, b: number) => ({
  cb: -0.1146 * r - 0.3854 * g + 0.5 * b,
  cr: 0.5 * r - 0.4542 * g - 0.0458 * b,
});
const angDiff = (a: number, b: number) => Math.min(Math.abs(a - b), 360 - Math.abs(a - b));
const residualOf = (w: { r?: number; g?: number; b?: number }) => ((w.r ?? 0) + (w.g ?? 0) + (w.b ?? 0)) / 3;

describe('wheel maths', () => {
  it('round-trips puck -> wheel -> puck within rounding', () => {
    fc.assert(
      fc.property(fc.double({ min: 0.3, max: 0.9, noNaN: true }), fc.double({ min: 0, max: 6.28, noNaN: true }), (r, t) => {
        const x = r * Math.cos(t);
        const y = r * Math.sin(t);
        const w = wheelFromPuck(x, y, { r: 0, g: 0, b: 0, master: 0 });
        const p = wheelPuck(w);
        expect(Math.hypot(p.x - x, p.y - y)).toBeLessThan(0.03);
      }),
    );
  });

  it('never leaves [-1, 1] and preserves master and residual', () => {
    fc.assert(
      fc.property(wheelArb, disc, (prev, { x, y }) => {
        const w = wheelFromPuck(x, y, prev);
        for (const v of [w.r, w.g, w.b]) {
          expect(v).toBeGreaterThanOrEqual(-1);
          expect(v).toBeLessThanOrEqual(1);
        }
        expect(w.master).toBe(prev.master);
      }),
    );
    const prev = { r: 0.1, g: 0.1, b: 0.1, master: 0.2 };
    const w = wheelFromPuck(0.4, 0.3, prev);
    expect(wheelPuck(w).residual).toBeCloseTo(0.1, 2);
    expect(w.master).toBe(0.2);
  });

  it('keeps the stored residual and the puck hue at the rim, shrinking the cast instead', () => {
    fc.assert(
      fc.property(wheelArb, disc, (prev, { x, y }) => {
        const target = wheelFromPuck(x, y, { r: 0, g: 0, b: 0, master: 0 });
        const w = wheelFromPuck(x, y, prev);
        const p = wheelPuck(w);
        for (const v of [w.r, w.g, w.b]) {
          expect(v).toBeGreaterThanOrEqual(-1);
          expect(v).toBeLessThanOrEqual(1);
        }
        // The brightness shift is carried through untouched, never clamped away.
        expect(p.residual).toBeCloseTo(residualOf(prev), 3);
        // The cast only gets shorter, never a different hue.
        if (p.strength > 0.01) expect(angDiff(p.hue, wheelPuck(target).hue)).toBeLessThan(1);
      }),
      { numRuns: 300 },
    );
  });

  it('scales a rim cast down beside a brightness shift instead of clamping each channel', () => {
    // residual 0.5 would push a positive channel past 1; the old per-channel clamp changed the
    // residual (0.5 -> 0.33) and the hue (200 -> 192 deg). The cast must shrink along its hue instead.
    const prev = { r: 0.5, g: 0.5, b: 0.5, master: 0 };
    const target = wheelFromPuck(1, 0, { r: 0, g: 0, b: 0, master: 0 });
    const rim = wheelFromPuck(1, 0, prev);
    expect(residualOf(rim)).toBeCloseTo(0.5, 3);
    expect(angDiff(wheelPuck(rim).hue, wheelPuck(target).hue)).toBeLessThan(0.5);
    expect(wheelPuck(rim).strength).toBeLessThan(wheelPuck(target).strength);
    expect(Math.max(rim.r ?? 0, rim.g ?? 0, rim.b ?? 0)).toBeLessThanOrEqual(1);
  });

  it('lands red, green, blue at the vectorscope angles', () => {
    for (const [v, ] of [[[1, -0.5, -0.5]], [[-0.5, 1, -0.5]], [[-0.5, -0.5, 1]]] as number[][][]) {
      const { cb, cr } = cbcr(v[0], v[1], v[2]);
      const exact = (Math.atan2(cr, cb) * 180) / Math.PI;
      const p = wheelPuck({ r: v[0], g: v[1], b: v[2] });
      expect(angDiff(p.angle, ((exact % 360) + 360) % 360)).toBeLessThan(1);
    }
    expect(wheelPuck({ r: 1, g: -0.5, b: -0.5 }).angle).toBeGreaterThan(95);
    expect(wheelPuck({ r: 1, g: -0.5, b: -0.5 }).angle).toBeLessThan(110);
  });

  it('snaps the centre and optionally the angle', () => {
    expect(wheelFromPuck(0.03, 0.02, { master: 0.1 })).toEqual({ r: 0, g: 0, b: 0, master: 0.1 });
    const off = wheelFromPuck(Math.cos(((103 + 2) * Math.PI) / 180) * 0.6, Math.sin(((103 + 2) * Math.PI) / 180) * 0.6, {}, { snapAngle: true });
    const exact = wheelFromPuck(Math.cos((103.4 * Math.PI) / 180) * 0.6, Math.sin((103.4 * Math.PI) / 180) * 0.6, {}, { snapAngle: true });
    expect(off).toEqual(exact);
    expect(off.r).toBeGreaterThan(off.g ?? 0);
  });

  it('nudges, including out of the centre', () => {
    const a = nudgeWheel({ r: 0, g: 0, b: 0, master: 0 }, 1, 0);
    expect(isIdentityWheel(a)).toBe(false);
    const p = wheelPuck(a);
    expect(p.x).toBeGreaterThan(0);
    const b = nudgeWheel(a, 1, 0);
    expect(wheelPuck(b).x).toBeGreaterThan(p.x);
  });

  it('clamps pointerToDisc to the unit disc, y up', () => {
    const rect = { left: 0, top: 0, width: 100, height: 100 };
    expect(pointerToDisc(50, 50, rect)).toEqual({ x: 0, y: -0 });
    const far = pointerToDisc(1000, -1000, rect);
    expect(Math.hypot(far.x, far.y)).toBeCloseTo(1, 6);
    expect(far.y).toBeGreaterThan(0);
  });

  it('resets colour, keeps master, and reads back a Warm look as red-yellow', () => {
    expect(resetWheelColour({ r: 0.2, g: 0, b: 0, master: 0.3 })).toEqual({ r: 0, g: 0, b: 0, master: 0.3 });
    const warm = lookNode('n', 'Warm').primaries?.lift ?? lookNode('n', 'Warm').primaries?.gamma;
    expect(warm).toBeTruthy();
    const p = wheelPuck(warm ?? {});
    expect(p.hue).toBeGreaterThan(0);
    expect(p.hue).toBeLessThan(60);
    expect(describeWheel(warm ?? {}).hue).toBe('orange');
    expect(describeWheel({ master: 0.2 }).hue).toBe('neutral');
  });
});
