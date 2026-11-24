import { describe, expect, it } from 'vitest';
import fixture from '../__fixtures__/camera-profile-samples.json';
import {
  CAMERA_PROFILE_IDS,
  getCameraProfile,
  isCameraProfileId,
  toRec709,
} from '../camera-profiles';
import {
  CAMERA_PROFILE_TABLE_DEFAULT_SIZE,
  cameraProfileTableRgba16f,
  generateCameraProfileTable,
} from '../camera-profile-table';

interface Sample {
  rgbIn: [number, number, number];
  rgbOut: [number, number, number];
}

const samples = fixture as unknown as Record<string, Sample[]>;
const TOLERANCE = 1e-6;

describe('camera profiles', () => {
  it('ids match the C# fixture keys, in canonical order', () => {
    expect(Object.keys(samples)).toEqual([...CAMERA_PROFILE_IDS]);
  });

  it('every fixture sample matches toRec709 within 1e-6 for all 12 profiles', () => {
    for (const id of CAMERA_PROFILE_IDS) {
      const profile = getCameraProfile(id);
      const profileSamples = samples[id];
      expect(profileSamples, `fixture has samples for ${id}`).toBeDefined();
      for (const sample of profileSamples as Sample[]) {
        const [r, g, b] = sample.rgbIn;
        const actual = toRec709(profile, r, g, b);
        for (let c = 0; c < 3; c += 1) {
          const expected = sample.rgbOut[c] as number;
          const got = actual[c] as number;
          expect(
            Math.abs(got - expected),
            `${id} in [${r}, ${g}, ${b}] channel ${c}: expected ${expected}, got ${got}`,
          ).toBeLessThanOrEqual(TOLERANCE);
        }
      }
    }
  });

  it('isCameraProfileId accepts exactly the closed list', () => {
    for (const id of CAMERA_PROFILE_IDS) expect(isCameraProfileId(id)).toBe(true);
    expect(isCameraProfileId('rec709')).toBe(false);
    expect(isCameraProfileId('toString')).toBe(false);
    expect(isCameraProfileId(42)).toBe(false);
    expect(isCameraProfileId(null)).toBe(false);
  });

  it('profile definitions carry the C# conventions', () => {
    expect(getCameraProfile('djiDLogM').status).toBe('approximate');
    expect(getCameraProfile('appleLog').expectedRange).toBe('legal');
    expect(getCameraProfile('appleLog').expectedYuvMatrix).toBe('bt2020nc');
    expect(getCameraProfile('sonySLog3').gamutToRec709).toHaveLength(9);
  });
});

describe('camera profile table', () => {
  it('lays out size^3*3 floats, R fastest, equal to toRec709 at grid points', () => {
    const size = 5;
    const table = generateCameraProfileTable('panasonicVLog', size);
    expect(table.length).toBe(size ** 3 * 3);

    const profile = getCameraProfile('panasonicVLog');
    const step = 1 / (size - 1);
    const probes: Array<[number, number, number]> = [
      [0, 0, 0],
      [2, 1, 3],
      [4, 4, 4],
      [1, 3, 0],
    ];
    for (const [r, g, b] of probes) {
      const i = ((b * size + g) * size + r) * 3;
      const expected = toRec709(profile, r * step, g * step, b * step);
      expect(table[i]).toBeCloseTo(expected[0], 6);
      expect(table[i + 1]).toBeCloseTo(expected[1], 6);
      expect(table[i + 2]).toBeCloseTo(expected[2], 6);
    }
  });

  it('caches by (id, size)', () => {
    expect(generateCameraProfileTable('arriLogC4', 5)).toBe(generateCameraProfileTable('arriLogC4', 5));
  });

  it('packs RGBA16F with alpha 1', () => {
    const size = 3;
    const texels = cameraProfileTableRgba16f('redLog3G10', size);
    expect(texels.length).toBe(size ** 3 * 4);
    for (let i = 3; i < texels.length; i += 4) expect(texels[i]).toBe(0x3c00);
  });

  it('generates the default 65^3 table for all 12 profiles in under 5 s each', () => {
    expect(CAMERA_PROFILE_TABLE_DEFAULT_SIZE).toBe(65);
    for (const id of CAMERA_PROFILE_IDS) {
      const started = performance.now();
      const table = generateCameraProfileTable(id);
      const elapsed = performance.now() - started;
      console.log(`${id}: ${table.length} floats in ${elapsed.toFixed(1)} ms`);
      expect(table.length).toBe(65 ** 3 * 3);
      expect(elapsed).toBeLessThan(5000);
    }
  }, 60000);
});
