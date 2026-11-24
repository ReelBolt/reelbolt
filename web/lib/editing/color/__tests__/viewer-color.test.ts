import { describe, expect, it } from 'vitest';
import { FRAGMENT_SHADER, VIEW_FRAGMENT_SHADER } from '../../preview/gl-compositor';
import { generateCameraProfileTable } from '../camera-profile-table';
import { CAMERA_PROFILE_IDS, getCameraProfile, toRec709 } from '../camera-profiles';
import { INPUT_LUT_UNIT, PICTURE_UNIT, SAMPLER_UNIT_PLANS, VIEWING_LUT_UNIT, VIEWING_LUT_1D_UNIT, lutTexelMapping, oneDRgba16f, resampleOneD, planInputStage, planViewingPass, sampleTableTrilinear, viewingLutDomainUniforms, viewingLutFromProfile } from '../viewer-color';

describe('viewer colour stages: shader source', () => {
  it('the clip program carries the input-stage uniforms and the viewing program its LUT uniforms', () => {
    for (const name of ['uInputOn', 'uInputLut', 'uInputScale', 'uInputBias']) expect(FRAGMENT_SHADER).toContain(name);
    expect(FRAGMENT_SHADER).toContain('uniform sampler3D uInputLut');
    expect(FRAGMENT_SHADER).toContain('uniform sampler2D uTex');
    for (const name of ['uLut', 'uOn', 'uDomMin', 'uDomScale', 'uLutScale', 'uLutBias']) expect(VIEW_FRAGMENT_SHADER).toContain(name);
  });
});

describe('sampler unit plans', () => {
  it('maps every sampler uniform of one program to its own distinct texture unit', () => {
    for (const plan of SAMPLER_UNIT_PLANS) {
      const units = Object.values(plan.units);
      expect(units.length).toBeGreaterThan(0);
      expect(new Set(units).size, `${plan.program} reuses a texture unit`).toBe(units.length);
    }
  });
  it('keeps the 3D stages off the picture unit, so a sampler2D and a sampler3D never share one', () => {
    expect(PICTURE_UNIT).not.toBe(INPUT_LUT_UNIT);
    expect(PICTURE_UNIT).not.toBe(VIEWING_LUT_UNIT);
    expect(INPUT_LUT_UNIT).not.toBe(VIEWING_LUT_UNIT);
  });
});

describe('planInputStage', () => {
  it('maps a profile id to its table key', () => {
    expect(planInputStage({ inputProfile: CAMERA_PROFILE_IDS[0] })).toEqual({ on: true, textureKey: `profile:${CAMERA_PROFILE_IDS[0]}` });
  });
  it('is off with no profile, an unknown one, or the converted proxy already baked', () => {
    expect(planInputStage({})).toEqual({ on: false, textureKey: null });
    expect(planInputStage({ inputProfile: null })).toEqual({ on: false, textureKey: null });
    expect(planInputStage({ inputProfile: 'nope' })).toEqual({ on: false, textureKey: null });
    expect(planInputStage({ inputProfile: CAMERA_PROFILE_IDS[0], inputProfileBaked: true })).toEqual({ on: false, textureKey: null });
  });
});

describe('planViewingPass', () => {
  const lut = viewingLutFromProfile(CAMERA_PROFILE_IDS[0]);
  it('runs only with a LUT and not for an export-like readback', () => {
    expect(planViewingPass(lut, true)).toBe(true);
    expect(planViewingPass(lut, false)).toBe(false);
    expect(planViewingPass(null, true)).toBe(false);
  });
  it('a profile view uses the 0..1 code domain; a .cube honours its own domain', () => {
    expect(viewingLutDomainUniforms(lut)).toEqual({ min: [0, 0, 0], scale: [1, 1, 1] });
    expect(viewingLutDomainUniforms({ domainMin: [-1, 0, 0], domainMax: [1, 0.5, 1] })).toEqual({ min: [-1, 0, 0], scale: [0.5, 2, 1] });
  });
  it('maps coordinates to texel centres', () => {
    const m = lutTexelMapping(65);
    expect(m.scale * 0 + m.bias).toBeCloseTo(0.5 / 65, 12);
    expect(m.scale * 1 + m.bias).toBeCloseTo(1 - 0.5 / 65, 12);
  });
});

describe('CPU reference of the shader fetch', () => {
  const points: Array<[number, number, number]> = [[0.5, 0.5, 0.5], [0.2, 0.4, 0.6], [0.7, 0.3, 0.1], [1, 1, 1], [0.05, 0.05, 0.05], [0.35, 0.45, 0.25]];
  it.each(CAMERA_PROFILE_IDS.slice(0, 3))('%s: trilinear on 65^3 matches toRec709 within 5e-3', (id) => {
    const table = generateCameraProfileTable(id, 65);
    const profile = getCameraProfile(id);
    for (const p of points) {
      const got = sampleTableTrilinear(table, 65, p);
      const want = toRec709(profile, p[0], p[1], p[2]);
      for (let c = 0; c < 3; c += 1) expect(Math.abs((got[c] as number) - (want[c] as number))).toBeLessThan(5e-3);
    }
  });
});

describe('viewing LUT 1D stage', () => {
  it('has its own sampler unit in the viewing program', () => {
    const units = SAMPLER_UNIT_PLANS.find((p) => p.program === 'viewing')!.units;
    expect(units.uLut1d).toBe(VIEWING_LUT_1D_UNIT);
    expect(new Set(Object.values(units)).size).toBe(Object.values(units).length);
    expect(VIEW_FRAGMENT_SHADER).toContain('uniform sampler2D uLut1d');
  });
  it('resamples long tables to at most 4096 texels, ends pinned', () => {
    const n = 10000;
    const data = new Float32Array(n * 3);
    for (let i = 0; i < n; i += 1) data.fill(i / (n - 1), i * 3, i * 3 + 3);
    const out = resampleOneD(data, n, 4096);
    expect(out.length).toBe(4096 * 3);
    expect(out[0]).toBe(0);
    expect(out[4095 * 3]).toBeCloseTo(1, 6);
    expect(oneDRgba16f(data, n).length).toBe(2048 * 4);
    expect(oneDRgba16f(new Float32Array(9), 3).length).toBe(12);
  });
});
