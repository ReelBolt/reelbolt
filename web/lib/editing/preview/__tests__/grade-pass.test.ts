import { describe, expect, it } from 'vitest';
import { FRAGMENT_SHADER } from '../gl-compositor';
import { GRADE_PASS_FRAGMENT_SHADER, GRADE_PASS_VERTEX_SHADER, TETRAHEDRAL_GLSL } from '../grade-pass';

describe('grade pass shaders', () => {
  it('uses the engine\'s tetrahedral interpolation (all six orderings) with texel fetches', () => {
    expect(TETRAHEDRAL_GLSL).toContain('texelFetch');
    expect(TETRAHEDRAL_GLSL).toContain('vec3 sampleTetra(sampler3D t, vec3 c)');
    expect((TETRAHEDRAL_GLSL.match(/return \(1\.0 - d\./g) ?? []).length).toBe(6);
  });
  it('the camera input stage reads its table tetrahedrally in the compositor and in the pass', () => {
    expect(FRAGMENT_SHADER).toContain('sampleTetra(uInputLut, c.rgb)');
    expect(FRAGMENT_SHADER).not.toContain('texture(uInputLut');
    expect(GRADE_PASS_FRAGMENT_SHADER).toContain('sampleTetra(uInputLut, original)');
  });
  it('keeps the camera table\'s scale and bias declared (the compositor still sets them)', () => {
    for (const name of ['uInputScale', 'uInputBias']) expect(FRAGMENT_SHADER).toContain(name);
  });
  it('the pass merges a secondary back through its matte measured on the entering pixel', () => {
    expect(GRADE_PASS_FRAGMENT_SHADER).toContain('qualifierMatte(original)');
    expect(GRADE_PASS_FRAGMENT_SHADER).toContain('windowMatte()');
    expect(GRADE_PASS_FRAGMENT_SHADER).toContain('x = original + (x - original) * m;');
  });
  it('keeps the source orientation: the vertex shader derives uv from the clip position, no flip', () => {
    expect(GRADE_PASS_VERTEX_SHADER).toContain('vUv = aPos * 0.5 + 0.5;');
  });
  it('declares high precision for the 2D samplers (curve and 1D LUT values are half floats)', () => {
    expect(GRADE_PASS_FRAGMENT_SHADER).toContain('precision highp sampler2D;');
  });
});
