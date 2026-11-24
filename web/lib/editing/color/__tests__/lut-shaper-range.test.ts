import { describe, expect, it } from 'vitest';
import { parseCubeLut, sampleCubeLut } from '../cube-lut';

// QA pin (lut-1d-support): plan.md "Shaper + 3D": shaper outputs s map to 3D coordinates (s-c)/(d-c), clamped 0..1.
// The engine (CubeLut.cs Normalise(values, 0, rows1, c, d - c)) does exactly that; a shaper output above 1 inside a
// LUT_3D_INPUT_RANGE wider than 0..1 must NOT be clamped to 1 before the mapping.
const identity3d = Array.from({ length: 8 }, (_, i) => `${i & 1} ${(i >> 1) & 1} ${(i >> 2) & 1}`).join('\n');
const cube = `LUT_1D_SIZE 2\nLUT_3D_SIZE 2\nLUT_3D_INPUT_RANGE 0 2\n0 0 0\n2 2 2\n${identity3d}\n`;

describe('shaper + 3D with a LUT_3D_INPUT_RANGE wider than 0..1', () => {
  it('maps shaper outputs above 1 onto the 3D input range as the spec and the engine do', () => {
    const lut = parseCubeLut(cube);
    expect(lut.kind).toBe('shaper3d');
    // x=1: shaper s=2, (2-0)/(2-0) = 1 -> identity 3D -> 1.
    const top = sampleCubeLut(lut, [1, 1, 1]);
    expect(top[0]).toBeCloseTo(1, 5);
    // x=0.5: shaper s=1, (1-0)/2 = 0.5 -> 0.5.
    const mid = sampleCubeLut(lut, [0.5, 0.5, 0.5]);
    expect(mid[0]).toBeCloseTo(0.5, 5);
  });
});
