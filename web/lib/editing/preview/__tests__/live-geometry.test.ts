import { describe, expect, it } from 'vitest';
import {
  applyMat3,
  composeWindows,
  evaluateQuad,
  insertQuadAt,
  invertMat3,
  pinInverseColumnMajor,
  pinQuadOf,
  reframeWindow,
  shapeCoverage,
  shapeParamsOf,
  squareToQuad,
  type Quad,
} from '../live-geometry';
import type { QuadCorners } from '../../types';

const corners = (tl: [number, number], tr: [number, number], br: [number, number], bl: [number, number]): QuadCorners => ({
  topLeft: { x: tl[0], y: tl[1] },
  topRight: { x: tr[0], y: tr[1] },
  bottomRight: { x: br[0], y: br[1] },
  bottomLeft: { x: bl[0], y: bl[1] },
});

describe('reframeWindow (GradeFilterBuilder.Reframe, GradeFilterBuilder.cs:350)', () => {
  it('a landscape source reframed to a portrait canvas: w = ih * aspect, centred on the point, clamped inside the picture', () => {
    // 1920x1080 into 1080x1920: aspect 0.5625 -> w = min(1920, 1080 * 0.5625) = 607.5, h = min(1080, 1920 / 0.5625) = 1080.
    const centre = reframeWindow({ x: 0.5, y: 0.5 }, 1920, 1080, 1080, 1920);
    expect(centre.x).toBeCloseTo(656.25 / 1920, 9); // clip(960 - 303.75, 0, 1920 - 607.5)
    expect(centre.w).toBeCloseTo(607.5 / 1920, 9);
    expect(centre.y).toBe(0);
    expect(centre.h).toBe(1);
    expect(reframeWindow({ x: 0, y: 0.5 }, 1920, 1080, 1080, 1920).x).toBe(0); // clip at the left edge
    expect(reframeWindow({ x: 1, y: 0.5 }, 1920, 1080, 1080, 1920).x).toBeCloseTo(1312.5 / 1920, 9); // clip at iw - ow
  });

  it('a portrait source reframed to a landscape canvas crops the height (aspect rounded to 6 places like the render)', () => {
    // 1080x1920 into 1920x1080: aspect = round(1.777777.., 6) = 1.777778 -> h = 1080 / 1.777778.
    const w = reframeWindow({ x: 0.5, y: 0.5 }, 1080, 1920, 1920, 1080);
    expect(w.x).toBe(0);
    expect(w.w).toBe(1);
    expect(w.h).toBeCloseTo(607.4999240625 / 1920, 7);
    expect(w.y).toBeCloseTo((960 - 607.4999240625 / 2) / 1920, 7);
  });

  it('clamps the point to 0..1, defaults a missing coordinate to the centre, and is the whole picture without a reframe', () => {
    expect(reframeWindow({ x: 9, y: -3 }, 1920, 1080, 1080, 1920).x).toBeCloseTo(1312.5 / 1920, 9);
    expect(reframeWindow({}, 1920, 1080, 1080, 1920).x).toBeCloseTo(656.25 / 1920, 9);
    expect(reframeWindow(null, 1920, 1080, 1080, 1920)).toEqual({ x: 0, y: 0, w: 1, h: 1 });
    expect(reframeWindow({ x: 0.5, y: 0.5 }, 0, 0, 1080, 1920)).toEqual({ x: 0, y: 0, w: 1, h: 1 });
    // A source already at the canvas aspect keeps everything.
    const same = reframeWindow({ x: 0.2, y: 0.9 }, 1920, 1080, 1280, 720);
    expect(same.x).toBe(0);
    expect(same.w).toBeCloseTo(1, 6);
  });

  it('composes with the clip crop: the crop is a window inside the reframed picture', () => {
    const outer = { x: 0.25, y: 0, w: 0.5, h: 1 };
    expect(composeWindows(outer, { x: 0.5, y: 0.1, w: 0.5, h: 0.8 })).toEqual({ x: 0.5, y: 0.1, w: 0.25, h: 0.8 });
  });
});

describe('shape masks (GradeFilterBuilder.Mask, GradeFilterBuilder.cs:331)', () => {
  it('applies the C# defaults and floors the feather at 0.0005; a segmentation mask is not a shape', () => {
    expect(shapeParamsOf({})).toEqual({ kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.0005, invert: false });
    expect(shapeParamsOf({ kind: 'ellipse', x: 0.3, feather: 0.2, invert: true })).toEqual({ kind: 'ellipse', x: 0.3, y: 0.5, w: 0.5, h: 0.5, feather: 0.2, invert: true });
    expect(shapeParamsOf({ kind: 'segmentation' })).toBeNull();
    expect(shapeParamsOf(null)).toBeNull();
  });

  it('rect: the smallest edge distance over the feather plus one half (profile samples)', () => {
    const p = shapeParamsOf({ kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.1 })!;
    expect(shapeCoverage(p, 0.5, 0.5)).toBe(1); // centre: 0.25 / 0.1 + 0.5 clamps to 1
    expect(shapeCoverage(p, 0.25, 0.5)).toBeCloseTo(0.5, 9); // on the edge
    expect(shapeCoverage(p, 0.27, 0.5)).toBeCloseTo(0.7, 9); // 0.02 inside
    expect(shapeCoverage(p, 0.22, 0.5)).toBeCloseTo(0.2, 9); // 0.03 outside
    expect(shapeCoverage(p, 0.2, 0.5)).toBeCloseTo(0, 9); // 0.05 outside: the ramp ends (half the feather each side)
    expect(shapeCoverage(p, 0.3, 0.5)).toBeCloseTo(1, 9); // 0.05 inside: the ramp ends
    expect(shapeCoverage(p, 0.5, 0.74)).toBeCloseTo(0.6, 9); // the y edge ramps the same way (distance 0.01 inside)
  });

  it('rect with no feather is a hard edge one pixel wide (feather floor 0.0005)', () => {
    const p = shapeParamsOf({ kind: 'rect', feather: 0 })!;
    expect(shapeCoverage(p, 0.25 + 0.0001, 0.5)).toBeCloseTo(0.7, 9);
    expect(shapeCoverage(p, 0.25 - 0.0001, 0.5)).toBeCloseTo(0.3, 9);
    expect(shapeCoverage(p, 0.1, 0.5)).toBe(0);
  });

  it('ellipse: one minus the normalised radius over the feather plus one half (profile samples)', () => {
    const p = shapeParamsOf({ kind: 'ellipse', x: 0.5, y: 0.5, w: 0.4, h: 0.2, feather: 0.1 })!;
    expect(shapeCoverage(p, 0.5, 0.5)).toBe(1);
    expect(shapeCoverage(p, 0.7, 0.5)).toBeCloseTo(0.5, 9); // r = 1 on the x radius
    expect(shapeCoverage(p, 0.5, 0.6)).toBeCloseTo(0.5, 9); // r = 1 on the y radius
    expect(shapeCoverage(p, 0.69, 0.5)).toBeCloseTo(1, 9); // r = 0.95
    expect(shapeCoverage(p, 0.705, 0.5)).toBeCloseTo(0.25, 9); // r = 1.025
    expect(shapeCoverage(p, 0.71, 0.5)).toBeCloseTo(0, 9); // r = 1.05
  });

  it('invert is one minus the coverage', () => {
    const p = shapeParamsOf({ kind: 'rect', feather: 0.1, invert: true })!;
    expect(shapeCoverage(p, 0.5, 0.5)).toBe(0);
    expect(shapeCoverage(p, 0.25, 0.5)).toBeCloseTo(0.5, 9);
    expect(shapeCoverage(p, 0.27, 0.5)).toBeCloseTo(0.3, 9);
    expect(shapeCoverage(p, 0.05, 0.05)).toBe(1);
  });
});

describe('projective warp (GradeFilterBuilder.CornerPin: perspective ... sense=destination)', () => {
  const quad: Quad = [[0.1, 0.2], [0.9, 0.1], [0.8, 0.9], [0.2, 0.8]];

  it('maps the unit square corners onto the quad corners', () => {
    const m = squareToQuad(quad)!;
    const at = (u: number, v: number) => applyMat3(m, u, v)!;
    expect(at(0, 0)[0]).toBeCloseTo(0.1, 9);
    expect(at(0, 0)[1]).toBeCloseTo(0.2, 9);
    expect(at(1, 0)[0]).toBeCloseTo(0.9, 9);
    expect(at(1, 0)[1]).toBeCloseTo(0.1, 9);
    expect(at(1, 1)[0]).toBeCloseTo(0.8, 9);
    expect(at(1, 1)[1]).toBeCloseTo(0.9, 9);
    expect(at(0, 1)[0]).toBeCloseTo(0.2, 9);
    expect(at(0, 1)[1]).toBeCloseTo(0.8, 9);
    // Golden values from the same maths, worked by hand: g = -2/7, h = 2/7.
    expect(m[6]).toBeCloseTo(-0.2857142857, 9);
    expect(m[7]).toBeCloseTo(0.2857142857, 9);
    expect(at(0.5, 0.5)[0]).toBeCloseTo(0.45, 9);
    expect(at(0.5, 0.5)[1]).toBeCloseTo(0.55, 9);
  });

  it('is NOT the affine (two-triangle) map on a trapezoid: the centre is pulled toward the far edge', () => {
    // Narrow top (y 0.2, x 0.3..0.7), wide bottom (y 0.8, x 0.1..0.9). Plain UV interpolation puts the centre at y = 0.5.
    const trapezoid: Quad = [[0.3, 0.2], [0.7, 0.2], [0.9, 0.8], [0.1, 0.8]];
    const m = squareToQuad(trapezoid)!;
    const centre = applyMat3(m, 0.5, 0.5)!;
    expect(centre[0]).toBeCloseTo(0.5, 9);
    expect(centre[1]).toBeCloseTo(0.4, 9);
    expect(applyMat3(m, 0.5, 0.25)![1]).toBeCloseTo(0.2857142857, 9);
    expect(applyMat3(m, 0.25, 0.5)![0]).toBeCloseTo(0.3666666667, 9);
    expect(applyMat3(m, 0.25, 0.5)![1]).toBeCloseTo(0.4, 9);
  });

  it('a parallelogram is the exact affine map and the identity quad is the identity', () => {
    const m = squareToQuad([[0, 0], [1, 0], [1, 1], [0, 1]])!;
    expect(Array.from(m)).toEqual([1, 0, 0, 0, 1, 0, 0, 0, 1]);
    const shear = squareToQuad([[0.1, 0.1], [0.6, 0.1], [0.8, 0.5], [0.3, 0.5]])!;
    expect(shear[6]).toBe(0);
    expect(shear[7]).toBe(0);
    expect(applyMat3(shear, 0.5, 0.5)![0]).toBeCloseTo(0.45, 9);
    expect(applyMat3(shear, 0.5, 0.5)![1]).toBeCloseTo(0.3, 9);
  });

  it('the inverse takes the quad corners back to the unit square, and the shader matrix is column-major', () => {
    const forward = squareToQuad(quad)!;
    const inverse = invertMat3(forward)!;
    const back = applyMat3(inverse, 0.9, 0.1)!;
    expect(back[0]).toBeCloseTo(1, 9);
    expect(back[1]).toBeCloseTo(0, 9);
    const centreBack = applyMat3(inverse, 0.45, 0.55)!;
    expect(centreBack[0]).toBeCloseTo(0.5, 9);
    expect(centreBack[1]).toBeCloseTo(0.5, 9);
    const cm = pinInverseColumnMajor(quad)!;
    expect(cm).toBeInstanceOf(Float32Array);
    expect(cm).toHaveLength(9);
    // Column-major: element (row r, col c) at c * 3 + r.
    expect(cm[1 * 3 + 0]).toBeCloseTo(inverse[1], 5);
    expect(cm[0 * 3 + 2]).toBeCloseTo(inverse[6], 5);
    expect(cm[2 * 3 + 1]).toBeCloseTo(inverse[5], 5);
  });

  it('a degenerate quad (collinear corners) has no map, so the layer draws nothing', () => {
    expect(squareToQuad([[0, 0], [0.5, 0.5], [1, 1], [0.25, 0.25]])).toBeNull();
    expect(pinInverseColumnMajor([[0.2, 0.2], [0.2, 0.2], [0.2, 0.2], [0.2, 0.2]])).toBeNull();
  });

  it('pinQuadOf clamps each coordinate to -1..2 like the render, and rejects a quad with a bad corner', () => {
    expect(pinQuadOf(corners([-5, 0.1], [3, 0.1], [0.9, 0.9], [0.1, 9]))).toEqual([[-1, 0.1], [2, 0.1], [0.9, 0.9], [0.1, 2]]);
    expect(pinQuadOf(null)).toBeNull();
    expect(pinQuadOf({ ...corners([0, 0], [1, 0], [1, 1], [0, 1]), topLeft: { x: Number.NaN, y: 0 } })).toBeNull();
  });
});

describe('insert quads', () => {
  const key = (frame: number, tl: [number, number]) => ({ frame, value: corners(tl, [0.9, 0.1], [0.9, 0.9], [0.1, 0.9]), interp: 'linear' as const });

  it('evaluateQuad: the base before the first key, linear between keys, the last key after', () => {
    const quad = { value: corners([0.1, 0.1], [0.9, 0.1], [0.9, 0.9], [0.1, 0.9]), keys: [key(10, [0.1, 0.1]), key(20, [0.3, 0.5])] };
    expect(evaluateQuad(quad, 0)!.topLeft).toEqual({ x: 0.1, y: 0.1 });
    expect(evaluateQuad(quad, 15)!.topLeft.x).toBeCloseTo(0.2, 9);
    expect(evaluateQuad(quad, 15)!.topLeft.y).toBeCloseTo(0.3, 9);
    expect(evaluateQuad(quad, 15)!.topRight).toEqual({ x: 0.9, y: 0.1 });
    expect(evaluateQuad(quad, 99)!.topLeft).toEqual({ x: 0.3, y: 0.5 });
    expect(evaluateQuad(null, 3)).toBeNull();
    expect(evaluateQuad({ value: corners([0, 0], [1, 0], [1, 1], [0, 1]) }, 3)!.bottomRight).toEqual({ x: 1, y: 1 });
  });

  it('insertQuadAt reads 0..1 coordinates as canvas fractions and anything else as pixels (as the overlay raster always did)', () => {
    expect(insertQuadAt({ quad: { value: corners([0.1, 0.2], [0.9, 0.2], [0.9, 0.8], [0.1, 0.8]) } }, 0, 1920, 1080)).toEqual([[0.1, 0.2], [0.9, 0.2], [0.9, 0.8], [0.1, 0.8]]);
    const px = insertQuadAt({ quad: { value: corners([192, 216], [1728, 216], [1728, 864], [192, 864]) } }, 0, 1920, 1080)!;
    expect(px[0][0]).toBeCloseTo(0.1, 9);
    expect(px[0][1]).toBeCloseTo(0.2, 9);
    expect(px[2][0]).toBeCloseTo(0.9, 9);
    expect(px[2][1]).toBeCloseTo(0.8, 9);
    expect(insertQuadAt({ quad: null }, 0, 1920, 1080)).toBeNull();
  });
});
