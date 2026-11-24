/**
 * TypeScript mirror of the renderer's 3D table builder
 * (inference/src/ReelBolt.Shared/Editing/Color/CameraProfileTable.cs).
 *
 * Generates the camera-code -> Rec.709 table the viewer will feed to a `sampler3D`. All maths is
 * double precision through `toRec709`, with a single rounding to float at the end, matching the C#
 * side; the fixture in `__fixtures__/camera-profile-samples.json` detects any drift.
 */

import { toRgba16fTexture3dData } from './cube-lut-loader';
import type { CubeLut } from './cube-lut';
import { getCameraProfile, isCameraProfileId, toRec709, type CameraProfileId } from './camera-profiles';

export const CAMERA_PROFILE_TABLE_MIN_SIZE = 2;
export const CAMERA_PROFILE_TABLE_MAX_SIZE = 65;
export const CAMERA_PROFILE_TABLE_DEFAULT_SIZE = 65;

const tableCache = new Map<string, Float32Array>();
const rgbaCache = new Map<string, Uint16Array>();

function cacheKey(id: CameraProfileId, size: number): string {
  return `${id}:${size}`;
}

/**
 * Generates `size^3 * 3` floats, red fastest (cube order). The input domain 0..1 is the profile's
 * normalised code value on every axis.
 */
export function generateCameraProfileTable(id: string, size = CAMERA_PROFILE_TABLE_DEFAULT_SIZE): Float32Array {
  if (!isCameraProfileId(id)) throw new Error(`unknown camera profile '${id}'`);
  if (!Number.isInteger(size) || size < CAMERA_PROFILE_TABLE_MIN_SIZE || size > CAMERA_PROFILE_TABLE_MAX_SIZE) {
    throw new RangeError(`size must be an integer in [${CAMERA_PROFILE_TABLE_MIN_SIZE}, ${CAMERA_PROFILE_TABLE_MAX_SIZE}]`);
  }
  const key = cacheKey(id, size);
  const hit = tableCache.get(key);
  if (hit) return hit;

  const profile = getCameraProfile(id);
  const table = new Float32Array(size * size * size * 3);
  const step = 1.0 / (size - 1);
  let i = 0;
  for (let b = 0; b < size; b += 1) {
    for (let g = 0; g < size; g += 1) {
      for (let r = 0; r < size; r += 1) {
        const [outR, outG, outB] = toRec709(profile, r * step, g * step, b * step);
        table[i++] = outR;
        table[i++] = outG;
        table[i++] = outB;
      }
    }
  }

  tableCache.set(key, table);
  return table;
}

/** RGBA half-float texels (alpha 1), R fastest, ready for `texImage3D(RGBA16F, HALF_FLOAT)`. */
export function cameraProfileTableRgba16f(
  id: string,
  size = CAMERA_PROFILE_TABLE_DEFAULT_SIZE,
): Uint16Array {
  if (!isCameraProfileId(id)) throw new Error(`unknown camera profile '${id}'`);
  if (!Number.isInteger(size) || size < CAMERA_PROFILE_TABLE_MIN_SIZE || size > CAMERA_PROFILE_TABLE_MAX_SIZE) {
    throw new RangeError(`size must be an integer in [${CAMERA_PROFILE_TABLE_MIN_SIZE}, ${CAMERA_PROFILE_TABLE_MAX_SIZE}]`);
  }
  const key = cacheKey(id, size);
  const hit = rgbaCache.get(key);
  if (hit) return hit;

  const data = generateCameraProfileTable(id, size);
  const lut: CubeLut = { size, domainMin: [0, 0, 0], domainMax: [1, 1, 1], data };
  const texels = toRgba16fTexture3dData(lut);
  rgbaCache.set(key, texels);
  return texels;
}
