/**
 * The grade pass (L3): draws a clip's picture through its whole node chain - primaries, curves, LUTs and the
 * qualifier / power-window secondaries - into a source-sized working texture, node by node (ping-pong), the way
 * the export's filter chain evaluates it. The compositor then composites that texture with no grade of its own.
 *
 * Self-contained: its own program, working targets and curve tables. The compositor lends it only its 3D-texture
 * cache (so node LUTs share the viewing LUT's `VIEWING_LUT_TEXTURE_CACHE_MAX` limit), the camera input table and a
 * dummy 3D texture. Texture units are per program; this program uses 0 (picture), 4 (camera table), 5 (node
 * LUT, 3D), 6 (node 1D LUT) and 7 (curves).
 */

import { INPUT_LUT_UNIT, lutTexelMapping, PICTURE_UNIT, VIEWING_LUT_1D_UNIT, VIEWING_LUT_UNIT, type InputStageLayer } from '../color/viewer-color';
import { curveBakeRgba16f, CURVE_TABLE_SIZE } from '../color/grade-curves';
import { floatToHalf } from '../color/cube-lut-loader';
import type { RichGradeStage } from './grade-pass-plan';
import { KEYER_GLSL, type KeyerParams } from './keyer';

/** The node curves' 2D texture unit (a 256 x 1 row). */
export const CURVES_UNIT = 7;

/** Curve tables kept per context (256 x 1 RGBA16F = 2 KB each). */
export const CURVE_TEXTURE_CACHE_MAX = 32;

/**
 * Tetrahedral interpolation on a `sampler3D` (texel fetches), exactly the engine's `lut3d interp=tetrahedral` and
 * `sampleTetrahedral` in `cube-lut.ts`. R is the fastest axis (texture x), then G (y), then B (z).
 */
export const TETRAHEDRAL_GLSL = `
vec3 lutTexel(sampler3D t, ivec3 i) {
  return texelFetch(t, i, 0).rgb;
}
vec3 sampleTetra(sampler3D t, vec3 c) {
  int n = textureSize(t, 0).x;
  vec3 p = clamp(c, 0.0, 1.0) * float(n - 1);
  vec3 f = floor(p);
  vec3 d = p - f;
  ivec3 i0 = ivec3(f);
  ivec3 i1 = min(i0 + ivec3(1), ivec3(n - 1));
  vec3 c000 = lutTexel(t, i0);
  vec3 c111 = lutTexel(t, i1);
  if (d.r > d.g) {
    if (d.g > d.b) return (1.0 - d.r) * c000 + (d.r - d.g) * lutTexel(t, ivec3(i1.x, i0.y, i0.z)) + (d.g - d.b) * lutTexel(t, ivec3(i1.x, i1.y, i0.z)) + d.b * c111;
    if (d.r > d.b) return (1.0 - d.r) * c000 + (d.r - d.b) * lutTexel(t, ivec3(i1.x, i0.y, i0.z)) + (d.b - d.g) * lutTexel(t, ivec3(i1.x, i0.y, i1.z)) + d.g * c111;
    return (1.0 - d.b) * c000 + (d.b - d.r) * lutTexel(t, ivec3(i0.x, i0.y, i1.z)) + (d.r - d.g) * lutTexel(t, ivec3(i1.x, i0.y, i1.z)) + d.g * c111;
  }
  if (d.b > d.g) return (1.0 - d.b) * c000 + (d.b - d.g) * lutTexel(t, ivec3(i0.x, i0.y, i1.z)) + (d.g - d.r) * lutTexel(t, ivec3(i0.x, i1.y, i1.z)) + d.r * c111;
  if (d.b > d.r) return (1.0 - d.g) * c000 + (d.g - d.b) * lutTexel(t, ivec3(i0.x, i1.y, i0.z)) + (d.b - d.r) * lutTexel(t, ivec3(i0.x, i1.y, i1.z)) + d.r * c111;
  return (1.0 - d.g) * c000 + (d.g - d.r) * lutTexel(t, ivec3(i0.x, i1.y, i0.z)) + (d.r - d.b) * lutTexel(t, ivec3(i1.x, i1.y, i0.z)) + d.b * c111;
}
`;

export const GRADE_PASS_VERTEX_SHADER = `#version 300 es
in vec2 aPos;
out vec2 vUv;
void main() {
  // Row j of the target holds the source at v = (j + 0.5) / H: the picture keeps its orientation, no flip.
  vUv = aPos * 0.5 + 0.5;
  gl_Position = vec4(aPos, 0.0, 1.0);
}`;

export const GRADE_PASS_FRAGMENT_SHADER = `#version 300 es
precision highp float;
precision highp sampler3D;
precision highp sampler2D;
in vec2 vUv;
uniform sampler2D uTex;
uniform float uInputOn;
uniform sampler3D uInputLut;
uniform sampler3D uLut3;
uniform sampler2D uLut1;
uniform sampler2D uCurves;
// Node primaries (the same maths as gl-compositor's grade(), one node).
uniform float uPrimOn;
uniform vec4 uA; // exposure gain, lift master, gain master, gamma master
uniform vec4 uB; // offset rgb, contrast
uniform vec4 uC; // pivot, hue, saturation
uniform vec3 uS;
uniform vec3 uM;
uniform vec3 uH;
uniform vec3 uT;
uniform float uCurvesOn;
uniform vec2 uCurvesMap;
uniform float uLutKind; // 0 none, 1 3D, 2 1D, 3 1D then 3D
uniform float uLutMix;
uniform vec2 uLut1Map;
uniform float uQualOn;
uniform vec4 uQA; // hue centre, hue width, sat min, sat max
uniform vec4 uQB; // lum min, lum max, softness, invert
uniform float uWinOn;
uniform vec4 uWA; // centre x, centre y, half w, half h
uniform vec4 uWB; // feather, invert, ellipse
uniform vec2 uSize;
out vec4 outColor;
${TETRAHEDRAL_GLSL}
${KEYER_GLSL}
uniform float uKeyOn;
vec3 primaries(vec3 v) {
  // lutrgb: exposure, offset, lift/gain masters, gamma master, contrast
  vec3 x = v * uA.x + uB.xyz;
  if (uA.y != 0.0 || uA.z != 0.0) x = x * (1.0 + uA.z) + uA.y * (1.0 - x);
  if (uA.w != 0.0) x = pow(clamp(x, 0.0, 1.0), vec3(1.0 / max(0.05, 1.0 + uA.w)));
  if (uB.w != 1.0) x = (x - vec3(uC.x)) * uB.w + vec3(uC.x);
  x = clamp(x, 0.0, 1.0);
  // colorbalance (preserve_lightness off)
  float l = max(x.r, max(x.g, x.b)) + min(x.r, min(x.g, x.b));
  float ws = clamp((0.333 - l) * 4.0 + 0.5, 0.0, 1.0) * 0.7;
  float wm = clamp((l - 0.333) * 4.0 + 0.5, 0.0, 1.0) * clamp((1.0 - l - 0.333) * 4.0 + 0.5, 0.0, 1.0) * 0.7;
  float wh = clamp((l + 0.333 - 1.0) * 4.0 + 0.5, 0.0, 1.0) * 0.7;
  x = clamp(x + uS * ws + uM * wm + uH * wh, 0.0, 1.0);
  // colortemperature
  x = clamp(x * uT, 0.0, 1.0);
  // hue: BT.601 limited-range YUV, chroma rotated and scaled
  if (uC.y != 0.0 || uC.z != 1.0) {
    vec3 p = x * 255.0;
    float y = 16.0 + dot(p, vec3(0.2568, 0.5041, 0.0979));
    float u = dot(p, vec3(-0.1482, -0.291, 0.4392));
    float w = dot(p, vec3(0.4392, -0.3678, -0.0714));
    float an = radians(uC.y);
    float ca = cos(an);
    float sa = sin(an);
    float u2 = (u * ca - w * sa) * uC.z;
    float w2 = (w * ca + u * sa) * uC.z;
    float yy = (y - 16.0) * 1.1644;
    x = clamp(vec3(yy + 1.596 * w2, yy - 0.392 * u2 - 0.813 * w2, yy + 2.017 * u2) / 255.0, 0.0, 1.0);
  }
  return x;
}
vec3 curves(vec3 x) {
  vec3 u = clamp(x, 0.0, 1.0) * uCurvesMap.x + uCurvesMap.y;
  return vec3(texture(uCurves, vec2(u.r, 0.5)).r, texture(uCurves, vec2(u.g, 0.5)).g, texture(uCurves, vec2(u.b, 0.5)).b);
}
vec3 oneD(vec3 x) {
  vec3 u = clamp(x, 0.0, 1.0) * uLut1Map.x + uLut1Map.y;
  return vec3(texture(uLut1, vec2(u.r, 0.5)).r, texture(uLut1, vec2(u.g, 0.5)).g, texture(uLut1, vec2(u.b, 0.5)).b);
}
vec3 lutStage(vec3 x) {
  vec3 xc = clamp(x, 0.0, 1.0);
  vec3 y = uLutKind > 2.5 ? sampleTetra(uLut3, oneD(xc)) : uLutKind > 1.5 ? oneD(xc) : sampleTetra(uLut3, xc);
  return xc + (y - xc) * uLutMix;
}
// The qualifier's matte on the pixel entering the node (GradeFilterBuilder.Matte, same constants).
float qualifierMatte(vec3 c) {
  float mx = max(c.r, max(c.g, c.b));
  float mn = min(c.r, min(c.g, c.b));
  float lum = (mx + mn) * 0.5;
  float sat = (mx - mn) / (1.0 - abs(2.0 * lum - 1.0) + 0.00001);
  float hy = 1.7320508 * (c.g - c.b);
  float hx = 2.0 * c.r - c.g - c.b;
  float hue = (hx == 0.0 && hy == 0.0) ? 0.0 : mod(degrees(atan(hy, hx)) + 360.0, 360.0);
  float k = 1.0;
  if (uQA.y < 180.0) {
    float dist = abs(mod(hue - uQA.x + 540.0, 360.0) - 180.0);
    k *= clamp((uQA.y - dist) / max(1.0, uQB.z * 180.0) + 0.5, 0.0, 1.0);
  }
  if (uQA.z > 0.0) k *= clamp((sat - uQA.z) / uQB.z + 0.5, 0.0, 1.0);
  if (uQA.w < 1.0) k *= clamp((uQA.w - sat) / uQB.z + 0.5, 0.0, 1.0);
  if (uQB.x > 0.0) k *= clamp((lum - uQB.x) / uQB.z + 0.5, 0.0, 1.0);
  if (uQB.y < 1.0) k *= clamp((uQB.y - lum) / uQB.z + 0.5, 0.0, 1.0);
  return uQB.w > 0.5 ? 1.0 - k : k;
}
// The power window's matte at this pixel (X/W, Y/H as geq reads them: the pixel index over the size).
float windowMatte() {
  vec2 q = floor(gl_FragCoord.xy) / uSize;
  float inside;
  if (uWB.z > 0.5) {
    vec2 d = (q - uWA.xy) / uWA.zw;
    inside = clamp((1.0 - length(d)) / uWB.x + 0.5, 0.0, 1.0);
  } else {
    float m = min(min(q.x - (uWA.x - uWA.z), (uWA.x + uWA.z) - q.x), min(q.y - (uWA.y - uWA.w), (uWA.y + uWA.w) - q.y));
    inside = clamp(m / uWB.x + 0.5, 0.0, 1.0);
  }
  return uWB.y > 0.5 ? 1.0 - inside : inside;
}
void main() {
  vec4 c = texture(uTex, vUv);
  vec3 original = c.rgb;
  if (uInputOn > 0.5) original = sampleTetra(uInputLut, original);
  // The keyer runs on the source picture, after the camera table and before the grade: alpha from colorkey, then despill.
  float keyed = 1.0;
  if (uKeyOn > 0.5) {
    keyed = keyAlpha(original);
    original = keyDespill(original);
  }
  vec3 x = original;
  if (uPrimOn > 0.5) x = primaries(x);
  if (uCurvesOn > 0.5) x = curves(x);
  if (uLutKind > 0.5) x = lutStage(x);
  if (uQualOn > 0.5 || uWinOn > 0.5) {
    float m = 1.0;
    if (uQualOn > 0.5) m *= qualifierMatte(original);
    if (uWinOn > 0.5) m *= windowMatte();
    x = original + (x - original) * m;
  }
  outColor = vec4(x, c.a * keyed);
}`;

type Loc = WebGLUniformLocation | null;

const EMPTY_STAGE: RichGradeStage = { primaries: null, curves: null, lut: null, qualifier: null, window: null };

/** What the compositor lends the pass. */
export interface GradePassHost {
  /** A cached 3D RGBA16F texture (shares the viewing LUT's `view:` cache and its limit). Built on `unit`. */
  lut3d(key: string, size: number, texels: () => Uint16Array, unit: number): WebGLTexture | null;
  /** A cached N x 1 RGBA16F 2D texture on the viewing 1D unit (same cache). */
  lut1d(key: string, size: number, texels: () => Uint16Array): WebGLTexture | null;
  /** The camera input table of the layer (null when its input stage is off or already baked). */
  inputTexture(layer: InputStageLayer): WebGLTexture | null;
  /** Binds a valid 1x1x1 3D texture on `unit` for a sampler3D that is off. */
  dummy3d(unit: number): void;
}

export interface GradePass {
  /**
   * Draws `source` (width x height) through `stages`, returns the texture holding the graded picture (orientation
   * unchanged, alpha unchanged). The caller restores its own program, framebuffer, viewport and blend state.
   * `inputLayer` carries the clip's camera input stage, applied before the first node. Null when it cannot draw.
   */
  run(source: WebGLTexture, width: number, height: number, stages: readonly RichGradeStage[], inputLayer: InputStageLayer | undefined, keyer?: KeyerParams): WebGLTexture | null;
  dispose(): void;
}

const UNIFORMS = [
  'uTex', 'uInputOn', 'uInputLut', 'uLut3', 'uLut1', 'uCurves', 'uPrimOn', 'uA', 'uB', 'uC', 'uS', 'uM', 'uH', 'uT', 'uCurvesOn', 'uCurvesMap',
  'uLutKind', 'uLutMix', 'uLut1Map', 'uQualOn', 'uQA', 'uQB', 'uWinOn', 'uWA', 'uWB', 'uSize', 'uKeyOn', 'uKeyColor', 'uKeyA',
] as const;

function compileShader(gl: WebGL2RenderingContext, type: number, source: string): WebGLShader | null {
  const shader = gl.createShader(type);
  if (!shader) return null;
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    console.error('[preview] grade pass shader failed', gl.getShaderInfoLog(shader));
    gl.deleteShader(shader);
    return null;
  }
  return shader;
}

/** Builds the pass; null when its program cannot be built (the clip then draws ungraded; the badge says so). */
export function createGradePass(gl: WebGL2RenderingContext, host: GradePassHost): GradePass | null {
  const vs = compileShader(gl, gl.VERTEX_SHADER, GRADE_PASS_VERTEX_SHADER);
  const fs = compileShader(gl, gl.FRAGMENT_SHADER, GRADE_PASS_FRAGMENT_SHADER);
  const program = gl.createProgram();
  if (!vs || !fs || !program) return null;
  gl.bindAttribLocation(program, 0, 'aPos');
  gl.attachShader(program, vs);
  gl.attachShader(program, fs);
  gl.linkProgram(program);
  if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
    console.error('[preview] grade pass link failed', gl.getProgramInfoLog(program));
    gl.deleteProgram(program);
    return null;
  }
  const loc = {} as Record<(typeof UNIFORMS)[number], Loc>;
  for (const name of UNIFORMS) loc[name] = gl.getUniformLocation(program, name);

  const vao = gl.createVertexArray();
  const vbo = gl.createBuffer();
  gl.bindVertexArray(vao);
  gl.bindBuffer(gl.ARRAY_BUFFER, vbo);
  gl.bufferData(gl.ARRAY_BUFFER, new Float32Array([-1, -1, 1, -1, -1, 1, -1, 1, 1, -1, 1, 1]), gl.STATIC_DRAW);
  gl.enableVertexAttribArray(0);
  gl.vertexAttribPointer(0, 2, gl.FLOAT, false, 0, 0);
  gl.bindVertexArray(null);

  gl.useProgram(program);
  gl.uniform1i(loc.uTex, PICTURE_UNIT);
  gl.uniform1i(loc.uInputLut, INPUT_LUT_UNIT);
  gl.uniform1i(loc.uLut3, VIEWING_LUT_UNIT);
  gl.uniform1i(loc.uLut1, VIEWING_LUT_1D_UNIT);
  gl.uniform1i(loc.uCurves, CURVES_UNIT);
  const curveMap = lutTexelMapping(CURVE_TABLE_SIZE);
  gl.uniform2f(loc.uCurvesMap, curveMap.scale, curveMap.bias);

  // Two working targets at the source's size (ping-pong), rebuilt when the size changes.
  let targets: { w: number; h: number; textures: [WebGLTexture, WebGLTexture]; fbos: [WebGLFramebuffer, WebGLFramebuffer] } | null = null;
  const releaseTargets = () => {
    if (!targets) return;
    for (const t of targets.textures) gl.deleteTexture(t);
    for (const f of targets.fbos) gl.deleteFramebuffer(f);
    targets = null;
  };
  const targetsFor = (w: number, h: number) => {
    if (targets && targets.w === w && targets.h === h) return targets;
    releaseTargets();
    const make = () => {
      const texture = gl.createTexture() as WebGLTexture;
      gl.bindTexture(gl.TEXTURE_2D, texture);
      gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, w, h, 0, gl.RGBA, gl.UNSIGNED_BYTE, null);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
      gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
      const fbo = gl.createFramebuffer() as WebGLFramebuffer;
      gl.bindFramebuffer(gl.FRAMEBUFFER, fbo);
      gl.framebufferTexture2D(gl.FRAMEBUFFER, gl.COLOR_ATTACHMENT0, gl.TEXTURE_2D, texture, 0);
      return { texture, fbo };
    };
    const a = make();
    const b = make();
    targets = { w, h, textures: [a.texture, b.texture], fbos: [a.fbo, b.fbo] };
    return targets;
  };

  // A 1x1 2D texture for a sampler2D whose stage is off (never leaves a unit empty).
  const dummy2d = gl.createTexture() as WebGLTexture;
  gl.bindTexture(gl.TEXTURE_2D, dummy2d);
  gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA, 1, 1, 0, gl.RGBA, gl.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 255]));
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.NEAREST);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
  gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);

  // Curve tables by content key, LRU, own cache (they are tiny).
  const curveTextures = new Map<string, WebGLTexture>();
  const curveTexture = (stage: RichGradeStage): WebGLTexture | null => {
    const bake = stage.curves;
    if (!bake) return null;
    const hit = curveTextures.get(bake.key);
    if (hit) {
      curveTextures.delete(bake.key);
      curveTextures.set(bake.key, hit);
      return hit;
    }
    const texture = gl.createTexture();
    if (!texture) return null;
    gl.activeTexture(gl.TEXTURE0 + CURVES_UNIT);
    gl.bindTexture(gl.TEXTURE_2D, texture);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MIN_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_MAG_FILTER, gl.LINEAR);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_S, gl.CLAMP_TO_EDGE);
    gl.texParameteri(gl.TEXTURE_2D, gl.TEXTURE_WRAP_T, gl.CLAMP_TO_EDGE);
    gl.texImage2D(gl.TEXTURE_2D, 0, gl.RGBA16F, CURVE_TABLE_SIZE, 1, 0, gl.RGBA, gl.HALF_FLOAT, curveBakeRgba16f(bake, floatToHalf));
    curveTextures.set(bake.key, texture);
    while (curveTextures.size > CURVE_TEXTURE_CACHE_MAX) {
      const oldest = curveTextures.keys().next().value;
      if (oldest === undefined) break;
      const t = curveTextures.get(oldest);
      if (t) gl.deleteTexture(t);
      curveTextures.delete(oldest);
    }
    return texture;
  };

  const bind2d = (unit: number, texture: WebGLTexture | null) => {
    gl.activeTexture(gl.TEXTURE0 + unit);
    gl.bindTexture(gl.TEXTURE_2D, texture ?? dummy2d);
  };

  return {
    run(source, width, height, requested, inputLayer, keyer) {
      if ((requested.length === 0 && !keyer) || width <= 0 || height <= 0) return null;
      // A keyer with no grade nodes still needs one draw: an empty stage.
      const stages = requested.length > 0 ? requested : [EMPTY_STAGE];
      const t = targetsFor(width, height);
      gl.disable(gl.BLEND);
      gl.useProgram(program);
      gl.bindVertexArray(vao);
      gl.viewport(0, 0, width, height);
      gl.uniform2f(loc.uSize, width, height);
      const inputTexture = inputLayer ? host.inputTexture(inputLayer) : null;
      let input = source;
      let next = 0;
      for (let i = 0; i < stages.length; i += 1) {
        const stage = stages[i] as RichGradeStage;
        gl.bindFramebuffer(gl.FRAMEBUFFER, t.fbos[next] as WebGLFramebuffer);
        // The camera table binds on its own unit; the picture binds last so unit 0 holds it.
        if (i === 0 && inputTexture) {
          gl.activeTexture(gl.TEXTURE0 + INPUT_LUT_UNIT);
          gl.bindTexture(gl.TEXTURE_3D, inputTexture);
          gl.uniform1f(loc.uInputOn, 1);
        } else {
          host.dummy3d(INPUT_LUT_UNIT);
          gl.uniform1f(loc.uInputOn, 0);
        }
        // Keyer (first draw only: alpha rides on the picture after that)
        gl.uniform1f(loc.uKeyOn, i === 0 && keyer ? 1 : 0);
        if (i === 0 && keyer) {
          gl.uniform3f(loc.uKeyColor, keyer.color[0], keyer.color[1], keyer.color[2]);
          gl.uniform4f(loc.uKeyA, keyer.similarity, keyer.blend, keyer.spill, keyer.spillType);
        }
        // Primaries
        const p = stage.primaries;
        gl.uniform1f(loc.uPrimOn, p ? 1 : 0);
        if (p) {
          gl.uniform4f(loc.uA, p.exposureGain, p.liftMaster, p.gainMaster, p.gammaMaster);
          gl.uniform4f(loc.uB, p.offset[0], p.offset[1], p.offset[2], p.contrast);
          gl.uniform4f(loc.uC, p.pivot, p.hue, p.saturation, 0);
          gl.uniform3fv(loc.uS, p.shadows);
          gl.uniform3fv(loc.uM, p.mids);
          gl.uniform3fv(loc.uH, p.highs);
          gl.uniform3fv(loc.uT, p.temp);
        }
        // Curves
        const curves = curveTexture(stage);
        bind2d(CURVES_UNIT, curves);
        gl.uniform1f(loc.uCurvesOn, curves ? 1 : 0);
        // LUT
        const lut = stage.lut;
        const cube = lut?.cube ? host.lut3d(lut.cube.key, lut.cube.size, lut.cube.texels, VIEWING_LUT_UNIT) : null;
        const oneD = lut?.oneD ? host.lut1d(lut.oneD.key, lut.oneD.size, lut.oneD.texels) : null;
        const lutOk = !!lut && (lut.cube ? !!cube : true) && (lut.oneD ? !!oneD : true);
        if (lut && lutOk) {
          if (cube) {
            gl.activeTexture(gl.TEXTURE0 + VIEWING_LUT_UNIT);
            gl.bindTexture(gl.TEXTURE_3D, cube);
          } else host.dummy3d(VIEWING_LUT_UNIT);
          bind2d(VIEWING_LUT_1D_UNIT, oneD);
          const map = lutTexelMapping(lut.oneD?.size ?? 2);
          gl.uniform2f(loc.uLut1Map, map.scale, map.bias);
          gl.uniform1f(loc.uLutKind, lut.kind);
          gl.uniform1f(loc.uLutMix, lut.mix);
        } else {
          host.dummy3d(VIEWING_LUT_UNIT);
          bind2d(VIEWING_LUT_1D_UNIT, null);
          gl.uniform1f(loc.uLutKind, 0);
          gl.uniform1f(loc.uLutMix, 0);
        }
        // Secondaries
        const q = stage.qualifier;
        gl.uniform1f(loc.uQualOn, q ? 1 : 0);
        if (q) {
          gl.uniform4f(loc.uQA, q.hueCenter, q.hueWidth, q.satMin, q.satMax);
          gl.uniform4f(loc.uQB, q.lumMin, q.lumMax, q.softness, q.invert ? 1 : 0);
        }
        const w = stage.window;
        gl.uniform1f(loc.uWinOn, w ? 1 : 0);
        if (w) {
          gl.uniform4f(loc.uWA, w.cx, w.cy, w.hw, w.hh);
          gl.uniform4f(loc.uWB, w.feather, w.invert ? 1 : 0, w.ellipse ? 1 : 0, 0);
        }
        gl.activeTexture(gl.TEXTURE0 + PICTURE_UNIT);
        gl.bindTexture(gl.TEXTURE_2D, input);
        gl.drawArrays(gl.TRIANGLES, 0, 6);
        input = t.textures[next] as WebGLTexture;
        next = 1 - next;
      }
      gl.bindVertexArray(null);
      return input;
    },
    dispose() {
      releaseTargets();
      for (const tex of curveTextures.values()) gl.deleteTexture(tex);
      curveTextures.clear();
      gl.deleteTexture(dummy2d);
      gl.deleteProgram(program);
      gl.deleteVertexArray(vao);
      gl.deleteBuffer(vbo);
    },
  };
}
