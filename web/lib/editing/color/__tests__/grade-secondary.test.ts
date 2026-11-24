import { describe, expect, it } from 'vitest';
import { maskedMerge, qualifierHsl, qualifierMatte, qualifierParamsOf, secondaryMatte, windowMatte, windowParamsOf } from '../grade-secondary';

describe('qualifierParamsOf', () => {
  it('is null when the qualifier selects everything (no matte, as the export)', () => {
    expect(qualifierParamsOf(null)).toBeNull();
    expect(qualifierParamsOf({})).toBeNull();
    expect(qualifierParamsOf({ hueWidth: 180, satMin: 0, satMax: 1, lumMin: 0, lumMax: 1, invert: false })).toBeNull();
  });
  it('fills the contract defaults and floors softness at 0.002', () => {
    expect(qualifierParamsOf({ hueWidth: 30 })).toEqual({ hueCenter: 0, hueWidth: 30, satMin: 0, satMax: 1, lumMin: 0, lumMax: 1, softness: 0.1, invert: false });
    expect(qualifierParamsOf({ hueWidth: 30, softness: 0 })?.softness).toBe(0.002);
  });
  it('an inverted all-selecting qualifier still makes a matte (it selects nothing)', () => {
    const p = qualifierParamsOf({ invert: true });
    expect(p).not.toBeNull();
    expect(qualifierMatte(p!, [0.3, 0.4, 0.5])).toBe(0);
  });
});

describe('qualifierHsl (the matte expression)', () => {
  it('reads the primaries at 0, 120 and 240 degrees', () => {
    expect(qualifierHsl([1, 0, 0]).hue).toBeCloseTo(0, 3);
    expect(qualifierHsl([0, 1, 0]).hue).toBeCloseTo(120, 3);
    expect(qualifierHsl([0, 0, 1]).hue).toBeCloseTo(240, 3);
  });
  it('a grey has saturation 0 and lightness at its level; black and white are not NaN', () => {
    const g = qualifierHsl([0.5, 0.5, 0.5]);
    expect(g.sat).toBe(0);
    expect(g.lum).toBe(0.5);
    expect(Number.isFinite(qualifierHsl([0, 0, 0]).hue)).toBe(true);
    expect(Number.isFinite(qualifierHsl([1, 1, 1]).sat)).toBe(true);
  });
});

describe('qualifierMatte', () => {
  const red = qualifierParamsOf({ hueCenter: 0, hueWidth: 30, softness: 0.1 })!;
  it('takes the hue inside the width and refuses it far outside', () => {
    expect(qualifierMatte(red, [1, 0, 0])).toBe(1);
    expect(qualifierMatte(red, [0, 1, 0])).toBe(0);
  });
  it('wraps around the hue circle (a centre of 350 takes red)', () => {
    expect(qualifierMatte(qualifierParamsOf({ hueCenter: 350, hueWidth: 30 })!, [1, 0, 0])).toBe(1);
  });
  it('is half-way exactly on the width edge (the ramp is centred on it)', () => {
    // hue 30 (yellow-orange) at distance == width: (30 - 30)/18 + 0.5
    const edge: [number, number, number] = [1, 0.5, 0]; // hue 30
    expect(qualifierMatte(red, edge)).toBeCloseTo(0.5, 2);
  });
  it('invert flips the selection', () => {
    const inv = qualifierParamsOf({ hueCenter: 0, hueWidth: 30, invert: true })!;
    expect(qualifierMatte(inv, [1, 0, 0])).toBe(0);
    expect(qualifierMatte(inv, [0, 1, 0])).toBe(1);
  });
  it('saturation and lightness bands only get an edge where a real limit is set', () => {
    const satMin = qualifierParamsOf({ satMin: 0.5 })!;
    expect(qualifierMatte(satMin, [0.5, 0.5, 0.5])).toBe(0);
    expect(qualifierMatte(satMin, [1, 0, 0])).toBe(1);
    const lumMax = qualifierParamsOf({ lumMax: 0.5 })!;
    expect(qualifierMatte(lumMax, [0.1, 0.1, 0.1])).toBe(1);
    expect(qualifierMatte(lumMax, [0.9, 0.9, 0.9])).toBe(0);
  });
});

describe('windowMatte', () => {
  const rect = windowParamsOf({ shape: { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0 } }, 0, 0)!;
  it('a rectangle is 1 inside, 0 outside, 0.5 exactly on its edge', () => {
    expect(windowMatte(rect, 0.5, 0.5)).toBe(1);
    expect(windowMatte(rect, 0.9, 0.5)).toBe(0);
    expect(windowMatte(rect, 0.5, 0.05)).toBe(0);
    expect(windowMatte(rect, 0.75, 0.5)).toBeCloseTo(0.5, 6);
  });
  it('an ellipse is 0.5 on its boundary and feather widens the ramp', () => {
    const ell = windowParamsOf({ shape: { kind: 'ellipse', x: 0.5, y: 0.5, w: 0.4, h: 0.2, feather: 0.1 } }, 0, 0)!;
    expect(windowMatte(ell, 0.5, 0.5)).toBe(1);
    expect(windowMatte(ell, 0.7, 0.5)).toBeCloseTo(0.5, 6);
    // 0.695 is 0.975 of the half width: (1 - 0.975) / 0.1 + 0.5
    expect(windowMatte(ell, 0.695, 0.5)).toBeCloseTo(0.75, 6);
    expect(windowMatte(ell, 0.95, 0.5)).toBe(0);
  });
  it('invert flips it', () => {
    const inv = windowParamsOf({ shape: { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, invert: true } }, 0, 0)!;
    expect(windowMatte(inv, 0.5, 0.5)).toBe(0);
    expect(windowMatte(inv, 0.95, 0.5)).toBe(1);
  });
  it('tracked offsets move the centre only on the axes that are keyed', () => {
    const w = { shape: { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.2, h: 0.2 }, offsetX: { value: 0 } };
    const p = windowParamsOf(w, 0.25, 0.25)!;
    expect(p.cx).toBeCloseTo(0.75, 9);
    expect(p.cy).toBe(0.5);
    expect(windowMatte(p, 0.75, 0.5)).toBe(1);
    expect(windowMatte(p, 0.5, 0.5)).toBe(0);
  });
  it('floors feather at 0.0005 and the half sizes at 0.001', () => {
    const p = windowParamsOf({ shape: { x: 0.5, y: 0.5, w: 0, h: 0, feather: 0 } }, 0, 0)!;
    expect(p.feather).toBe(0.0005);
    expect(p.hw).toBe(0.001);
    expect(p.hh).toBe(0.001);
  });
});

describe('secondaryMatte / maskedMerge', () => {
  it('multiplies the qualifier and the window, a missing part counts as 1', () => {
    const q = qualifierParamsOf({ hueCenter: 0, hueWidth: 30 })!;
    const w = windowParamsOf({ shape: { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5 } }, 0, 0)!;
    expect(secondaryMatte(q, w, [1, 0, 0], 0.5, 0.5)).toBe(1);
    expect(secondaryMatte(q, w, [1, 0, 0], 0.95, 0.5)).toBe(0);
    expect(secondaryMatte(q, w, [0, 1, 0], 0.5, 0.5)).toBe(0);
    expect(secondaryMatte(null, null, [0, 1, 0], 0.5, 0.5)).toBe(1);
  });
  it('merges linearly between the original and the correction', () => {
    expect(maskedMerge([0, 0, 0], [1, 0.5, 0.2], 0)).toEqual([0, 0, 0]);
    expect(maskedMerge([0, 0, 0], [1, 0.5, 0.2], 1)).toEqual([1, 0.5, 0.2]);
    expect(maskedMerge([0, 0, 0], [1, 0.5, 0.2], 0.5)).toEqual([0.5, 0.25, 0.1]);
  });
});
