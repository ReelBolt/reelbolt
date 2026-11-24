import { describe, expect, it } from 'vitest';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import { apply } from '../../engine';
import { isIdentityPrimaries, lookNode } from '../grade';
import { applyPreviewGradeNodes } from '../preview-grade';
import {
  applyGradeToClips, clipsInScope, cloneStack, currentLook, FIX_IDENTITY, fixValuesOf, GALLERY_LOOKS, gradeRgba, lookLayerNode, lookPreviewGrade, scalePrimaries, strengthMultiplier, withFix, withLook, withMatch, withoutLook,
} from '../look-gallery';

let n = 0;
const newId = () => `n${++n}`;

describe('look builders and strength interpolation', () => {
  it('strength 0 is identity, 100 equals the Colorist Normal look, 150 equals Strong', () => {
    expect(isIdentityPrimaries(lookLayerNode('a', 'Warm', 0).primaries)).toBe(true);
    expect(lookLayerNode('a', 'Warm', 100).primaries).toEqual(lookNode('a', 'Warm', 'Normal').primaries);
    expect(lookLayerNode('a', 'Cool', 150).primaries).toEqual(lookNode('a', 'Cool', 'Strong').primaries);
    expect(lookLayerNode('a', 'Vibrant', 50).primaries).toEqual(lookNode('a', 'Vibrant', 'Subtle').primaries);
  });
  it('interpolates monotonically between identity and the look', () => {
    const sat = (pct: number) => lookLayerNode('a', 'Vibrant', pct).primaries!.saturation!;
    expect(sat(25)).toBeGreaterThan(1);
    expect(sat(25)).toBeLessThan(sat(75));
    expect(sat(75)).toBeLessThan(sat(100));
    expect(strengthMultiplier(999)).toBe(1.5);
    expect(strengthMultiplier(-5)).toBe(0);
  });
  it('Teal & orange tints shadows teal and highlights orange', () => {
    const p = lookLayerNode('a', 'Teal & orange', 100).primaries!;
    expect(p.lift!.b!).toBeGreaterThan(p.lift!.r!);
    expect(p.gain!.r!).toBeGreaterThan(p.gain!.b!);
  });
  it('every gallery look is a valid named node and makes the picture warmer or different', () => {
    for (const look of GALLERY_LOOKS) {
      const node = lookLayerNode('a', look, 100);
      expect(node.name).toBe(`Look: ${look} 100%`);
      expect(isIdentityPrimaries(node.primaries)).toBe(false);
    }
    const [r, , b] = applyPreviewGradeNodes(lookPreviewGrade('Warm', 100), [0.25, 0.25, 0.25]);
    expect(r / b).toBeGreaterThan(1.05);
    const [r0, , b0] = applyPreviewGradeNodes(lookPreviewGrade('Warm', 0), [0.25, 0.25, 0.25]);
    expect(r0).toBeCloseTo(b0, 5);
  });
  it('withLook replaces the look layer in place and strength 0 removes it', () => {
    const a = withLook(null, newId, 'Warm', 100)!;
    expect(currentLook(a)).toMatchObject({ look: 'Warm', strength: 100 });
    const b = withLook(a, newId, 'Cool', 60)!;
    expect(b.nodes).toHaveLength(1);
    expect(b.nodes![0].id).toBe(a.nodes![0].id);
    expect(currentLook(b)).toMatchObject({ look: 'Cool', strength: 60 });
    expect(withLook(b, newId, 'Cool', 0)).toBeNull();
    expect(withoutLook(b)).toBeNull();
  });
});

describe('the five scalars and match', () => {
  it('Fix node round-trips and identity removes it', () => {
    const s = withFix(null, newId, { ...FIX_IDENTITY, brightness: 0.4, warmth: 0.3 })!;
    expect(fixValuesOf(s)).toMatchObject({ brightness: 0.4, warmth: 0.3, contrast: 1 });
    expect(s.nodes![0].primaries).toMatchObject({ exposure: 0.4, temperature: 0.3 });
    expect(withFix(s, newId, FIX_IDENTITY)).toBeNull();
  });
  it('scalePrimaries moves a match toward identity', () => {
    const full = { exposure: 1, contrast: 1.4, gain: { r: 0.2 } };
    expect(scalePrimaries(full, 0.5)).toMatchObject({ exposure: 0.5, contrast: 1.2, gain: { r: 0.1 } });
    expect(isIdentityPrimaries(scalePrimaries(full, 0))).toBe(true);
    expect(scalePrimaries(full, 1).contrast).toBe(1.4);
    const s = withMatch(withMatch(null, newId, full), newId, full);
    expect(s.nodes).toHaveLength(1);
  });
});

describe('apply to many', () => {
  const d = doc([track('v1', 'video', [media('c1', 0, 30), media('c2', 30, 60)]), track('v2', 'video', [media('c3', 0, 30)])]);
  it('scopes select clips, and one batch grades each', () => {
    expect(clipsInScope(d, 'all', [], null)).toHaveLength(3);
    expect(clipsInScope(d, 'selected', ['c2'], null).map((c) => c.id)).toEqual(['c2']);
    const ops = applyGradeToClips(clipsInScope(d, 'all', [], null), (c) => withLook(c.grade, newId, 'Warm', 100));
    let cur = d;
    for (const op of ops) cur = apply(cur, op).doc;
    expect(clipsInScope(cur, 'all', [], null).every((c) => currentLook(c.grade)?.look === 'Warm')).toBe(true);
  });
  it('cloneStack gives fresh node ids', () => {
    const s = withLook(null, newId, 'Warm', 100)!;
    expect(cloneStack(s, newId).nodes![0].id).not.toBe(s.nodes![0].id);
  });
  it('gradeRgba warms pixels', () => {
    const px = new Uint8ClampedArray([64, 64, 64, 255]);
    gradeRgba(px, lookPreviewGrade('Warm', 100));
    expect(px[0]).toBeGreaterThan(px[2]);
  });
});
