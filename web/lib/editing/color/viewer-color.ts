/**
 * The viewer's two colour stages, as pure planning the WebGL compositor executes.
 *
 *   1. Input stage - a clip with `inputProfile` is converted by the SAME table the renderer builds
 *      (`camera-profile-table.ts`), before the grade. Off when the proxy it plays already carries
 *      the conversion (`inputProfileBaked`) or when the clip is HDR (the renderer ignores the profile).
 *   2. Viewing stage - a viewing LUT (a project .cube, or a camera profile used purely as a view)
 *      is the LAST step of the live viewer only. It is never in the document, a render, a still, a
 *      scope or a hand-off, so every export-like readback asks for the picture WITHOUT it.
 */

import type { CubeLut } from './cube-lut';
import { floatToHalf, toRgba16fTexture3dData } from './cube-lut-loader';
import { cameraProfileTableRgba16f, CAMERA_PROFILE_TABLE_DEFAULT_SIZE } from './camera-profile-table';
import { isCameraProfileId } from './camera-profiles';

export interface InputStagePlan {
  on: boolean;
  /** The 3D texture to bind (`profile:<id>`), or null when the stage is off. */
  textureKey: string | null;
}

/** What a layer says about its input stage. `inputProfileBaked`: the converted proxy is the picture being drawn. */
export interface InputStageLayer {
  inputProfile?: string | null;
  inputProfileBaked?: boolean;
}

export function planInputStage(layer: InputStageLayer): InputStagePlan {
  const id = layer.inputProfile;
  if (!id || layer.inputProfileBaked === true || !isCameraProfileId(id)) return { on: false, textureKey: null };
  return { on: true, textureKey: `profile:${id}` };
}

/**
 * The texture unit the picture (`sampler2D uTex`) reads in every program.
 */
export const PICTURE_UNIT = 0;
/**
 * The texture unit the clip program's camera table (`sampler3D uInputLut`) reads. WebGL2 rejects a
 * draw with GL_INVALID_OPERATION when a `sampler2D` and a `sampler3D` in one program point at the
 * same unit, and sampler uniforms default to unit 0 - so every 3D sampler gets its own explicit
 * unit, set once after link. The units stay below the 2D ping-pong targets' 0 and clear of them.
 */
export const INPUT_LUT_UNIT = 4;
/** The texture unit the viewing program's LUT (`sampler3D uLut`) reads; distinct from the picture unit and the clip table. */
export const VIEWING_LUT_UNIT = 5;
/** The texture unit the viewing program's 1D stage (`sampler2D uLut1d`, N x 1) reads; own unit, never shared with a `sampler3D`. */
export const VIEWING_LUT_1D_UNIT = 6;
/** A 1D table longer than this is resampled (linear) for the texture only; the CPU sampler keeps the full table. */
export const VIEWING_LUT_1D_MAX_TEXELS = 2048; // WebGL2's guaranteed MAX_TEXTURE_SIZE

/**
 * Every sampler uniform the compositor programs declare and the unit it reads, set once after link.
 * Units are unique WITHIN a program (a `sampler2D` and a `sampler3D` sharing one is the real-GL
 * failure this plan removes); programs may reuse units because sampler state is per program.
 */
export const SAMPLER_UNIT_PLANS: ReadonlyArray<{ program: 'clip' | 'viewing' | 'effects'; units: Readonly<Record<string, number>> }> = [
  { program: 'clip', units: { uTex: PICTURE_UNIT, uInputLut: INPUT_LUT_UNIT } },
  { program: 'viewing', units: { uTex: PICTURE_UNIT, uLut: VIEWING_LUT_UNIT, uLut1d: VIEWING_LUT_1D_UNIT } },
  { program: 'effects', units: { uTex: PICTURE_UNIT } },
];

/** A viewing LUT ready for the compositor: texels plus the domain it expects its input in. */
export interface ViewingLutSpec {
  /** Stable identity (cache key): same key = same texture. */
  key: string;
  size: number;
  domainMin: readonly [number, number, number];
  domainMax: readonly [number, number, number];
  /** RGBA half-float texels, R fastest. Empty (and `size` 0) for a 1D-only LUT: the 3D stage is off. */
  texels: Uint16Array;
  /** The optional 1D stage, applied BEFORE the 3D one: an N x 1 RGBA half-float row over x in [0,1]. */
  oneD?: { size: number; texels: Uint16Array };
}

export function viewingLutFromCube(key: string, lut: CubeLut): ViewingLutSpec {
  const spec: ViewingLutSpec = { key, size: lut.size, domainMin: lut.domainMin, domainMax: lut.domainMax, texels: lut.size > 0 ? toRgba16fTexture3dData(lut) : new Uint16Array(0) };
  if (lut.oneD) spec.oneD = { size: oneDTextureSize(lut.oneD.size), texels: oneDRgba16f(lut.oneD.data, lut.oneD.size) };
  return spec;
}

/** The texture width for an N-entry 1D table: N, or the cap when N is longer. */
export function oneDTextureSize(size: number): number {
  return Math.min(size, VIEWING_LUT_1D_MAX_TEXELS);
}

/** The table resampled (linear, ends pinned) to `to` entries, RGB interleaved; unchanged when it already fits. */
export function resampleOneD(data: Float32Array, size: number, to: number): Float32Array {
  if (to >= size) return data;
  const out = new Float32Array(to * 3);
  for (let i = 0; i < to; i += 1) {
    const t = (i / (to - 1)) * (size - 1);
    const i0 = Math.floor(t);
    const i1 = Math.min(size - 1, i0 + 1);
    const f = t - i0;
    for (let c = 0; c < 3; c += 1) {
      const v0 = data[i0 * 3 + c] as number;
      out[i * 3 + c] = v0 + ((data[i1 * 3 + c] as number) - v0) * f;
    }
  }
  return out;
}

/** RGBA half-float texels (alpha 1) of the 1D stage, an N x 1 texture, `N <= VIEWING_LUT_1D_MAX_TEXELS`. */
export function oneDRgba16f(data: Float32Array, size: number): Uint16Array {
  const n = oneDTextureSize(size);
  const table = resampleOneD(data, size, n);
  const out = new Uint16Array(n * 4);
  const one = floatToHalf(1);
  for (let i = 0; i < n; i += 1) {
    out[i * 4] = floatToHalf(table[i * 3] as number);
    out[i * 4 + 1] = floatToHalf(table[i * 3 + 1] as number);
    out[i * 4 + 2] = floatToHalf(table[i * 3 + 2] as number);
    out[i * 4 + 3] = one;
  }
  return out;
}

/** A camera profile used purely as a view: its table over the code-value domain 0..1. */
export function viewingLutFromProfile(id: string): ViewingLutSpec {
  return {
    key: `profile:${id}`,
    size: CAMERA_PROFILE_TABLE_DEFAULT_SIZE,
    domainMin: [0, 0, 0],
    domainMax: [1, 1, 1],
    texels: cameraProfileTableRgba16f(id, CAMERA_PROFILE_TABLE_DEFAULT_SIZE),
  };
}

/** The final viewing pass runs only with a LUT set AND the caller not asking for a clean (export-like) picture. */
export function planViewingPass(viewingLut: ViewingLutSpec | null | undefined, applyViewingLut: boolean): boolean {
  return viewingLut != null && applyViewingLut;
}

/** `uv = clamp((c - domMin) * scale, 0, 1)`: the uniforms of a viewing LUT's domain. */
export function viewingLutDomainUniforms(lut: Pick<ViewingLutSpec, 'domainMin' | 'domainMax'>): { min: [number, number, number]; scale: [number, number, number] } {
  const scale = lut.domainMax.map((hi, i) => {
    const span = hi - (lut.domainMin[i] as number);
    return span > 0 ? 1 / span : 1;
  }) as [number, number, number];
  return { min: [lut.domainMin[0], lut.domainMin[1], lut.domainMin[2]], scale };
}

/** Texel-centre mapping of a 0..1 coordinate into an N^3 texture: `p * (N-1)/N + 0.5/N`. */
export function lutTexelMapping(size: number): { scale: number; bias: number } {
  return { scale: (size - 1) / size, bias: 0.5 / size };
}

/**
 * CPU mirror of the shader's table fetch (trilinear on the N^3 grid, texel-centre mapped), for
 * parity tests against `toRec709`. `data` is `size^3 * 3` floats, R fastest.
 */
export function sampleTableTrilinear(data: Float32Array, size: number, rgb: readonly [number, number, number]): [number, number, number] {
  const out: [number, number, number] = [0, 0, 0];
  const pos = rgb.map((v) => Math.min(1, Math.max(0, v)) * (size - 1));
  const i0 = pos.map((p) => Math.min(size - 2, Math.floor(p)));
  const f = pos.map((p, k) => p - (i0[k] as number));
  for (let c = 0; c < 3; c += 1) {
    let acc = 0;
    for (let db = 0; db < 2; db += 1) {
      for (let dg = 0; dg < 2; dg += 1) {
        for (let dr = 0; dr < 2; dr += 1) {
          const w = (dr ? (f[0] as number) : 1 - (f[0] as number)) * (dg ? (f[1] as number) : 1 - (f[1] as number)) * (db ? (f[2] as number) : 1 - (f[2] as number));
          const idx = (((i0[2] as number) + db) * size * size + ((i0[1] as number) + dg) * size + (i0[0] as number) + dr) * 3 + c;
          acc += w * (data[idx] as number);
        }
      }
    }
    out[c] = acc;
  }
  return out;
}
