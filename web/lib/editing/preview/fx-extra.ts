/**
 * Sharpen and grain for the effect pass, read off the real ffmpeg filters (`__fixtures__/fx-parity.json`):
 *
 *   sharpen = `unsharp=la=amount*3`: out = in + (in - blur) * la on luma, where blur is a 5x5 binomial (1 4 6 4 1)
 *             kernel at the render's canvas size. In the preview the tap spacing scales with the working size
 *             (long side / 1920), like blur's sigma.
 *   grain   = `noise=alls=round(amount*40):allf=t+u`: uniform noise, width N (values in [-N/2, N/2) on a 0..255
 *             scale), a new pattern every frame. The render's pattern is random, so the preview's is a deterministic
 *             hash seeded by the frame: same statistics, not the same pixels.
 */

/** The 5-tap binomial row; the 5x5 kernel is its outer product. */
export const SHARPEN_TAPS: readonly number[] = [1, 4, 6, 4, 1];
/** unsharp's luma amount for an effect amount. */
export const sharpenLumaAmount = (amount: number): number => Math.round(amount * 3 * 1000) / 1000;
/** noise's `alls` strength for an effect amount. */
export const grainStrength = (amount: number): number => Math.round(amount * 40);

/** The sharpened value of a centre sample given its 5x5 neighbourhood (row-major, 25 values, 0..255 or 0..1). */
export function sharpenSample(neighbourhood: readonly number[], amount: number): number {
  let blur = 0;
  for (let y = 0; y < 5; y += 1) for (let x = 0; x < 5; x += 1) blur += (SHARPEN_TAPS[y] as number) * (SHARPEN_TAPS[x] as number) * (neighbourhood[y * 5 + x] as number);
  blur /= 256;
  const centre = neighbourhood[12] as number;
  return centre + (centre - blur) * sharpenLumaAmount(amount);
}

/** GLSL for the two modes; `uAmount` carries la (sharpen) or N / 255 (grain), `uStep` the scaled texel, `uSeed` the frame. */
export const FX_EXTRA_GLSL = `
uint pcgHash(uint v) {
  uint s = v * 747796405u + 2891336653u;
  uint w = ((s >> ((s >> 28u) + 4u)) ^ s) * 277803737u;
  return (w >> 22u) ^ w;
}
float noiseAt(uvec3 p) {
  return float(pcgHash(p.x ^ pcgHash(p.y ^ pcgHash(p.z)))) / 4294967296.0;
}
vec4 sharpenAt(vec4 c) {
  const float k[5] = float[5](1.0, 4.0, 6.0, 4.0, 1.0);
  float blur = 0.0;
  for (int y = 0; y < 5; y++) {
    for (int x = 0; x < 5; x++) {
      vec3 s = texture(uTex, vUv + uStep * vec2(float(x - 2), float(y - 2))).rgb;
      blur += k[x] * k[y] * dot(s, vec3(0.299, 0.587, 0.114));
    }
  }
  blur /= 256.0;
  float lum = dot(c.rgb, vec3(0.299, 0.587, 0.114));
  return vec4(c.rgb + vec3((lum - blur) * uAmount), c.a);
}
vec4 grainAt(vec4 c) {
  uvec2 px = uvec2(gl_FragCoord.xy);
  uint f = uint(max(uSeed, 0.0));
  vec3 n = vec3(noiseAt(uvec3(px, f * 3u)), noiseAt(uvec3(px, f * 3u + 1u)), noiseAt(uvec3(px, f * 3u + 2u)));
  return vec4(c.rgb + (n - 0.5) * uAmount, c.a);
}
`;
