/**
 * The WebGL2 compositor: each active video layer is a textured quad drawn back-to-front with
 * straight alpha, plus a 2D overlay pass for titles/captions (rasterised by the browser — an
 * `approximate` feature per `parity.ts`).
 *
 * Deliberately small in P3: no transforms, no blend modes, no grade — those are P5/P7 shaders.
 * What it does do it does exactly: the first video track is the bottom layer, the last the top,
 * and a layer whose frame is not ready REPEATS its previously uploaded texture (never a black
 * flash, never a silent 3-layer composite — `spike/BUDGET.md` §5 "the stack is never degraded").
 * A repeat is only ever the SAME clip's picture: textures are keyed by layer and content (clip and
 * source), so a layer that cuts to a clip with no decoded frame yet draws nothing and is reported
 * in {@link DrawResult.blank} — never the previous clip's last picture over the new clip.
 *
 * Returns `null` from `createGlCompositor` when WebGL2 is unavailable, so the session can fall
 * back honestly instead of throwing in a headless or old browser.
 *
 * P5: each layer may carry a {@link LayerTransform} — position, scale, rotation and crop go into
 * the vertex shader as uniforms (rotation in pixel space so the canvas aspect does not shear it),
 * opacity (transform × fades × transition ramp) into the fragment shader. Still no blend modes
 * and no grade (P7).
 */

import type { LayerTransform } from './scheduler';
import { FULL_WINDOW, composeWindows, pinInverseColumnMajor, reframeWindow, type Quad, type ShapeParams, type SourceWindow } from './live-geometry';
import { MAX_MATTE_BOXES, type Matte } from './segmentation-tracks';
import { dimmingOf, outlineBandPx, pixelBlockPx, planRegionBlur, type PreviewRegionEffect } from './region-effects';
import { GRADE_NODE_BOUND, splitGradeNodes, type PreviewGradeNode } from '../color/preview-grade';
import { cameraProfileTableRgba16f, CAMERA_PROFILE_TABLE_DEFAULT_SIZE } from '../color/camera-profile-table';
import { INPUT_LUT_UNIT, PICTURE_UNIT, VIEWING_LUT_1D_UNIT, VIEWING_LUT_UNIT, lutTexelMapping, planInputStage, planViewingPass, viewingLutDomainUniforms, type ViewingLutSpec } from '../color/viewer-color';
import { createBlendPass, isBlendingWord } from './blend-pass';
import type { AspectPolicy, ReframeTarget } from '../types';
import { MAX_BLUR_AMOUNT } from '../engine/ops';
import { createXfadePass, type XfadePass } from './xfade-pass';
import { xfadeIndexOf } from './xfade';
import { createGradePass, TETRAHEDRAL_GLSL, type GradePass, type GradePassHost } from './grade-pass';
import type { RichGradeStage } from './grade-pass-plan';
import { FX_EXTRA_GLSL, grainStrength, sharpenLumaAmount } from './fx-extra';
import type { KeyerParams } from './keyer';

/**
 * The compositor's context attributes — `index.ts` probes with the SAME object.
 * `preserveDrawingBuffer` keeps the last composited frame readable after it is presented, so
 * "Save still", the reference-still grab and the scopes read the picture rather than a cleared,
 * black buffer (PA-9).
 */
export const COMPOSITOR_CONTEXT_ATTRIBUTES: Readonly<WebGLContextAttributes> = Object.freeze({
  alpha: false,
  premultipliedAlpha: false,
  antialias: false,
  preserveDrawingBuffer: true,
  // A hint, never a requirement: on a machine with two graphics chips the compositor asks for the fast one.
  powerPreference: 'high-performance',
});

/** Width and height of anything the decoder hands the compositor (0×0 when unknown). */
export function imageSize(image: CanvasImageSource): [number, number] {
  const i = image as Partial<{ displayWidth: number; displayHeight: number; videoWidth: number; videoHeight: number; naturalWidth: number; naturalHeight: number; width: number | { baseVal: { value: number } }; height: number | { baseVal: { value: number } } }>;
  if (typeof i.displayWidth === 'number' && typeof i.displayHeight === 'number') return [i.displayWidth, i.displayHeight];
  if (typeof i.videoWidth === 'number' && typeof i.videoHeight === 'number') return [i.videoWidth, i.videoHeight];
  if (typeof i.naturalWidth === 'number' && typeof i.naturalHeight === 'number') return [i.naturalWidth, i.naturalHeight];
  if (typeof i.width === 'number' && typeof i.height === 'number') return [i.width, i.height];
  return [0, 0];
}

/**
 * The quad's scale on x and y so a source keeps its shape inside the canvas, per the timeline's
 * aspect policy (the render's `SegmentPlanner` rules): `fit` letterboxes or pillarboxes, `fill`
 * covers the canvas (the overflowing axis is cut by the viewport), `stretch` fills it unchanged.
 * PA-11: before this every clip was stretched to the canvas while the render letterboxed it.
 */
export function aspectScale(sourceWidth: number, sourceHeight: number, canvasWidth: number, canvasHeight: number, policy: AspectPolicy): [number, number] {
  if (policy === 'stretch' || sourceWidth <= 0 || sourceHeight <= 0 || canvasWidth <= 0 || canvasHeight <= 0) return [1, 1];
  const source = sourceWidth / sourceHeight;
  const canvas = canvasWidth / canvasHeight;
  if (Math.abs(source - canvas) < 1e-6) return [1, 1];
  const wider = source > canvas;
  if (policy === 'fill') return wider ? [source / canvas, 1] : [1, canvas / source];
  return wider ? [1, canvas / source] : [source / canvas, 1];
}

/** The effects the live compositor executes (the closed catalogue; an unknown word is skipped and reported). */
export type PreviewEffectType = 'blur' | 'pixelate' | 'mono' | 'vignette' | 'sharpen' | 'grain';
export interface PreviewEffect {
  type: PreviewEffectType;
  /** The renderer's `ClipEffect.amount` (clamped): 0..1, or 0..2 for blur (sigma = amount x 20 on the 1920 reference). */
  amount: number;
}

const PREVIEW_EFFECT_TYPES: ReadonlySet<string> = new Set(['blur', 'pixelate', 'mono', 'vignette', 'sharpen', 'grain']);

/**
 * The clip's effect chain for the compositor, in the renderer's order (`GradeFilterBuilder.Effects`:
 * list order, disabled and zero-amount ones skipped). An effect the preview cannot run (an unknown word) is skipped on its
 * own, never the whole chain: `unpreviewableEffectsOf` names those.
 */
export function previewEffectsOf(effects: ReadonlyArray<{ type: string; amount?: number; enabled?: boolean }> | null | undefined): PreviewEffect[] {
  const out: PreviewEffect[] = [];
  for (const e of effects ?? []) {
    if (e.enabled === false) continue;
    const raw = e.amount ?? 0.5;
    const amount = Math.min(e.type === 'blur' ? MAX_BLUR_AMOUNT : 1, Math.max(0, Number.isFinite(raw) ? raw : 0));
    if (amount === 0) continue;
    if (!PREVIEW_EFFECT_TYPES.has(e.type)) continue;
    out.push({ type: e.type as PreviewEffectType, amount });
  }
  return out;
}

/** The live effects of the clip the preview cannot run (types, in order); empty when the whole chain previews. */
export function unpreviewableEffectsOf(effects: ReadonlyArray<{ type: string; amount?: number; enabled?: boolean }> | null | undefined): string[] {
  const out: string[] = [];
  for (const e of effects ?? []) {
    if (e.enabled === false || !((e.amount ?? 0.5) > 0)) continue;
    if (!PREVIEW_EFFECT_TYPES.has(e.type)) out.push(e.type);
  }
  return out;
}

/** The render's reference size: the long side of the canvas the effect strengths are defined at (1080p = 1920). */
export const EFFECT_REF_LONG_SIDE = 1920;
/** The renderer's blur is `gblur=sigma=amount*20` at the reference size. */
export const BLUR_SIGMA_AT_REF = 20;
const FX_MAX_LONG_SIDE = 960;
const FX_MIN_LONG_SIDE = 160;
/** The working resolution keeps the blur's sigma at or under this many pixels, so the kernel stays small. */
const BLUR_MAX_SIGMA_PX = 4;
const BLUR_MAX_RADIUS = 12;
const FX_BUCKETS: readonly number[] = [960, 800, 640, 480, 384, 320, 240, 160];

/** Viewing-LUT 3D textures kept per context (about 2.2 MB each at the default table size); profile tables are separate. */
export const VIEWING_LUT_TEXTURE_CACHE_MAX = 4;

export interface FxPass {
  kind: 'blurH' | 'blurV' | 'pixelate' | 'mono' | 'vignette' | 'sharpen' | 'grain';
  /** blur: sigma in working pixels; pixelate: block size in reference pixels; mono: amount; vignette: angle (rad). */
  value: number;
}
export interface FxPlan {
  width: number;
  height: number;
  passes: FxPass[];
  /** Texture fetches per frame across all passes (the cost model the frame-budget test bounds). */
  taps: number;
}

/**
 * The passes and working size for a chain on a source of `srcW` x `srcH`. Effects run at a reduced
 * working resolution (long side <= 960, and low enough that the blur sigma is <= 4 px), then the
 * result is scaled up by the final composite - a blur hides the lost detail, which is why it is cheap.
 */
export function planEffectPasses(effects: readonly PreviewEffect[], srcW: number, srcH: number): FxPlan {
  const long = Math.max(srcW, srcH, 1);
  let maxSigmaRef = 0;
  for (const e of effects) if (e.type === 'blur') maxSigmaRef = Math.max(maxSigmaRef, e.amount * BLUR_SIGMA_AT_REF);
  let target = FX_MAX_LONG_SIDE;
  if (maxSigmaRef > 0) target = Math.min(target, Math.max(FX_MIN_LONG_SIDE, Math.round((EFFECT_REF_LONG_SIDE * BLUR_MAX_SIGMA_PX) / maxSigmaRef)));
  // Snap down to a bucket so a changing blur amount does not reallocate the targets on every edit.
  const bucket = FX_BUCKETS.find((b) => b <= target) ?? FX_MIN_LONG_SIDE;
  const workLong = Math.max(1, Math.min(long, bucket));
  const scale = workLong / long;
  const width = Math.max(1, Math.round(srcW * scale));
  const height = Math.max(1, Math.round(srcH * scale));
  const passes: FxPass[] = [];
  let taps = 0;
  for (const e of effects) {
    if (e.type === 'blur') {
      const sigma = (e.amount * BLUR_SIGMA_AT_REF * workLong) / EFFECT_REF_LONG_SIDE;
      if (sigma < 0.05) continue;
      passes.push({ kind: 'blurH', value: sigma }, { kind: 'blurV', value: sigma });
      taps += 2 * width * height * (2 * Math.min(BLUR_MAX_RADIUS, Math.ceil(3 * sigma)) + 1);
    } else if (e.type === 'pixelate') {
      passes.push({ kind: 'pixelate', value: Math.max(2, Math.round(e.amount * 64)) });
      taps += width * height * 9;
    } else if (e.type === 'mono') {
      passes.push({ kind: 'mono', value: e.amount });
      taps += width * height;
    } else if (e.type === 'sharpen') {
      passes.push({ kind: 'sharpen', value: sharpenLumaAmount(e.amount) });
      taps += width * height * 25;
    } else if (e.type === 'grain') {
      passes.push({ kind: 'grain', value: grainStrength(e.amount) });
      taps += width * height;
    } else {
      passes.push({ kind: 'vignette', value: (e.amount * Math.PI) / 2.5 });
      taps += width * height;
    }
  }
  return { width, height, passes, taps };
}

/**
 * Whether the render pads the clip to the canvas with opaque black before its effects: only when
 * the clip has no transform object, no crop object and no fade (`TransformFilterBuilder.Build`,
 * TransformFilterBuilder.cs:61). "Absent" is `null`: an identity transform or an all-zero crop
 * OBJECT takes the unpadded `BuildDepth` path, and so does any fade. Mirror of that one condition.
 */
export function rendersPadded(clip: { transform?: unknown; crop?: unknown; fadeInFrames?: number; fadeOutFrames?: number }): boolean {
  const absent = (v: unknown) => v === null || v === undefined;
  return absent(clip.transform) && absent(clip.crop) && !((clip.fadeInFrames ?? 0) > 0) && !((clip.fadeOutFrames ?? 0) > 0);
}

/**
 * The quad's fit scale for a layer. The render crops FIRST and then fits the cropped picture inside the canvas
 * (TransformFilterBuilder.BuildDepth: `crop` line 91, then `scale=W:H:force_original_aspect_ratio=decrease` line 95, then the
 * transform scale line 101), so a cropped picture that matches the canvas aspect covers it. A transformed layer therefore
 * fits the cropped size with the `fit` rule; an untransformed one keeps the P4 aspect policy on the whole picture.
 */
export function fitFor(layer: { transform?: LayerTransform; aspectPolicy?: AspectPolicy }, sourceWidth: number, sourceHeight: number, canvasWidth: number, canvasHeight: number): [number, number] {
  const t = layer.transform;
  if (t === undefined) return aspectScale(sourceWidth, sourceHeight, canvasWidth, canvasHeight, layer.aspectPolicy ?? 'stretch');
  const w = Math.max(0.01, 1 - t.crop.left - t.crop.right);
  const h = Math.max(0.01, 1 - t.crop.top - t.crop.bottom);
  return aspectScale(sourceWidth * w, sourceHeight * h, canvasWidth, canvasHeight, 'fit');
}

export interface CompositorLayer {
  layerId: string;
  /**
   * What the picture shows — the clip and its source. A held texture is repeated only for the same
   * `contentKey`; absent = the layer itself is the content (overlays, generators).
   */
  contentKey?: string;
  /** `null` means "keep the texture you have for this content" (a repeat). */
  image: CanvasImageSource | null;
  opacity: number;
  /** P5: position/scale/rotation/crop. Absent = the P3 full-canvas quad. */
  transform?: LayerTransform;
  /** P7: the enabled primaries nodes of the clip's grade stacks, applied in order (the export's maths). Absent = no grade. */
  grade?: readonly PreviewGradeNode[];
  /**
   * L3: the clip's nodes for the grade pass (curves, LUTs, qualifier/window secondaries, or more nodes than `grade` holds). When set,
   * the pass grades the picture first and `grade` is ignored; the layer is then composited with no grade of its own.
   */
  richGrade?: readonly RichGradeStage[];
  /**
   * T-008b: this layer is one leg of an `xfade` transition. The `from` leg and the `to` leg that follows it are each drawn
   * alone onto a black canvas-sized target and mixed by the word's kernel; `t` is the transition's progress 0..1 (after easing).
   */
  xfade?: { word: string; t: number; role: 'from' | 'to' };
  /** The chroma keyer (source geometry, after the camera input stage, before the grade): runs in the grade pass and produces alpha. */
  keyer?: KeyerParams;
  /** Grain's seed: the timeline frame (a new pattern every frame). */
  fxSeed?: number;
  /** PA-11: how the source's shape meets the canvas. Absent = `stretch` (overlays, generators). */
  aspectPolicy?: AspectPolicy;
  /** Clip effects (blur, pixelate, mono, vignette), run in clip space after the grade and before the transform. */
  effects?: readonly PreviewEffect[];
  /** {@link rendersPadded}: the render pads this clip to the canvas with black bars before its effects. Absent = padded. */
  padded?: boolean;
  /** A segmentation mask's tracked boxes at this frame, applied in source geometry before crop and transform. */
  matte?: Matte;
  /** The clip's camera conversion (a camera-profile id): converted through the renderer's table before the grade. */
  inputProfile?: string | null;
  /** The picture drawn is the converted proxy for this same profile: the input stage stays off. */
  inputProfileBaked?: boolean;
  /** T-012: how this layer blends with what is composited below (a `BlendMode` word); absent/`normal` = alpha-over. */
  blendMode?: string;
  /** T-008c: an adjustment clip - no picture; its `richGrade` and `effects` apply to the composite BELOW it at this stack position. */
  adjustment?: boolean;
  /** A reframe: the source is cropped to the canvas aspect around this point BEFORE the crop and the fit (`reframeWindow`). */
  reframe?: ReframeTarget | null;
  /**
   * A shape mask (rect/ellipse): alpha multiplied in the layer's own frame after the grade (`GradeFilterBuilder.Mask`).
   * An untransformed layer masks in canvas fractions (the render pads it to the canvas first), a transformed one in its own.
   */
  shape?: ShapeParams;
  /**
   * Corner pin / insert quad, in canvas fractions (y down): the finished layer (placed on a canvas-shaped target, effects
   * and mask applied) is warped onto this quad with a per-fragment homography. `null` = a pin that cannot be built (draws nothing).
   */
  pin?: Quad | null;
  /**
   * Prompted region effects (magic blur and friends), run on the SOURCE picture before crop, transform, grade and the
   * clip's own effects (the render cuts them at source geometry), each limited by its matte (tracked boxes or a decoded mask video).
   */
  regionEffects?: readonly PreviewRegionEffect[];
}

export interface DrawOptions {
  /** Default true (the live viewer). Every export-like readback of the canvas runs with false. */
  applyViewingLut?: boolean;
}

export interface DrawResult {
  /** Layers that re-showed their content's held picture. */
  repeats: number;
  /** Layer ids that drew NOTHING: no image this frame and no held picture of the same content. */
  blank: readonly string[];
}

export interface Compositor {
  /** The GPU/driver string the browser reports (`WEBGL_debug_renderer_info`), or null when it hides it. */
  readonly renderer: string | null;
  resize(width: number, height: number): void;
  /** Draws the layers back-to-front. */
  draw(layers: readonly CompositorLayer[], options?: DrawOptions): DrawResult;
  /** Sets (or clears) the viewing LUT: the last step of the live viewer, never part of a readback. */
  setViewingLut(lut: ViewingLutSpec | null): void;
  /**
   * Runs `read` (synchronously) while the canvas holds the picture WITHOUT the viewing LUT, then puts the viewed
   * picture back. Stills, reference stills, scopes and thumbnails read through this. With no viewing LUT it just runs.
   */
  withCleanPicture<T>(read: () => T): T;
  /** Draws a 2D overlay canvas on top (titles, captions). */
  /** `cached`: the overlay canvas holds what is already on the overlay texture, so the upload is skipped. */
  drawOverlay(overlay: CanvasImageSource | null, options?: { cached?: boolean }): void;
  clear(): void;
  dispose(): void;
}

export const VERTEX_SHADER = `#version 300 es
in vec2 aPos;
in vec2 aUv;
uniform vec2 uScale;
uniform vec2 uOffset;
uniform float uRotation;
uniform float uAspect;
uniform vec2 uUvOrigin;
uniform vec2 uUvSize;
uniform float uFlipV;
uniform float uLocalMode;
out vec2 vUv;
out vec2 vLocal;
void main() {
  vUv = uUvOrigin + aUv * uUvSize;
  // The frame a shape mask is measured in: the picture's own (0..1, top-left origin), or, for an untransformed layer,
  // the canvas (the render pads such a layer to the canvas before masking).
  vLocal = uLocalMode > 0.5 ? vec2(0.5) + vec2(aPos.x, -aPos.y) * uScale * 0.5 : aUv;
  if (uFlipV > 0.5) vUv.y = 1.0 - vUv.y;
  vec2 p = aPos * uScale;
  // Rotate in pixel space (undo the canvas aspect, rotate, redo it) so a square stays square.
  p.x *= uAspect;
  float c = cos(uRotation);
  float s = sin(uRotation);
  p = vec2(c * p.x - s * p.y, s * p.x + c * p.y);
  p.x /= uAspect;
  gl_Position = vec4(p + uOffset, 0.0, 1.0);
}`;

export const FRAGMENT_SHADER = `#version 300 es
precision mediump float;
precision highp sampler3D;
in vec2 vUv;
in vec2 vLocal;
uniform sampler2D uTex;
uniform float uOpacity;
// Shape mask: uShape = (x, y, w, h) in layer fractions; uShapeP = (feather, invert, ellipse, unused).
uniform float uShapeOn;
uniform vec4 uShape;
uniform vec4 uShapeP;
// Camera input stage (the renderer's table): code values -> Rec.709, before the grade.
uniform float uInputOn;
uniform sampler3D uInputLut;
uniform float uInputScale;
uniform float uInputBias;
// P7 grade (the same maths as preview-grade.ts#applyPreviewGradeNodes, the export's per-node chain).
uniform float uGradeOn;
uniform int uGradeN;
uniform float uFoldOn;
uniform float uFExp;
uniform vec3 uFOffset;
uniform vec3 uFLift;
uniform vec3 uFGain;
uniform vec3 uFGamma;
uniform vec4 uFA; // contrast, pivot, saturation, hue
uniform vec2 uFTT; // temperature, tint
uniform vec4 uGA[${GRADE_NODE_BOUND}];
uniform vec4 uGB[${GRADE_NODE_BOUND}];
uniform vec4 uGC[${GRADE_NODE_BOUND}];
uniform vec3 uGS[${GRADE_NODE_BOUND}];
uniform vec3 uGM[${GRADE_NODE_BOUND}];
uniform vec3 uGH[${GRADE_NODE_BOUND}];
uniform vec3 uGT[${GRADE_NODE_BOUND}];
uniform float uMatteOn;
uniform int uMatteN;
uniform vec4 uMatteBoxes[8];
uniform float uMatteInvert;
uniform float uMatteFeather;
uniform vec2 uSrcSize;
out vec4 outColor;
${TETRAHEDRAL_GLSL}
// Nodes past the loop bound, folded into one stage with the old maths (preview-grade.ts#applyPreviewGrade).
// Uniform budget: 3 vec4 + 4 vec3 arrays x 8 (56 vectors) + fold stage 7 + matte 8 + a few = under 90 of the WebGL2 minimum 224
// MAX_FRAGMENT_UNIFORM_VECTORS.
vec3 gradeFolded(vec3 c) {
  vec3 v = c * uFExp + uFOffset;
  v = v * uFGain + uFLift * (1.0 - v);
  v = pow(max(v, vec3(0.0)), uFGamma);
  v = (v - vec3(uFA.y)) * uFA.x + vec3(uFA.y);
  v.r *= 1.0 + 0.15 * uFTT.x;
  v.b *= 1.0 - 0.15 * uFTT.x;
  v.g *= 1.0 - 0.1 * uFTT.y;
  float y = dot(v, vec3(0.2126, 0.7152, 0.0722));
  v = vec3(y) + (v - vec3(y)) * uFA.z;
  if (uFA.w != 0.0) {
    float a = radians(uFA.w);
    float ca = cos(a);
    float sa = sin(a);
    mat3 m = mat3(
      0.299 + 0.701 * ca + 0.168 * sa, 0.299 - 0.299 * ca - 0.328 * sa, 0.299 - 0.3 * ca + 1.25 * sa,
      0.587 - 0.587 * ca + 0.33 * sa, 0.587 + 0.413 * ca + 0.035 * sa, 0.587 - 0.588 * ca - 1.05 * sa,
      0.114 - 0.114 * ca - 0.497 * sa, 0.114 - 0.114 * ca + 0.292 * sa, 0.114 + 0.886 * ca - 0.203 * sa);
    v = m * v;
  }
  return clamp(v, 0.0, 1.0);
}
vec3 grade(vec3 c) {
  vec3 v = c;
  for (int i = 0; i < ${GRADE_NODE_BOUND}; i++) {
    if (i >= uGradeN) break;
    vec4 a = uGA[i];
    vec4 b = uGB[i];
    vec4 k = uGC[i];
    // lutrgb: exposure, offset, lift/gain masters, gamma master, contrast
    vec3 x = v * a.x + b.xyz;
    if (a.y != 0.0 || a.z != 0.0) x = x * (1.0 + a.z) + a.y * (1.0 - x);
    if (a.w != 0.0) x = pow(clamp(x, 0.0, 1.0), vec3(1.0 / max(0.05, 1.0 + a.w)));
    if (b.w != 1.0) x = (x - vec3(k.x)) * b.w + vec3(k.x);
    x = clamp(x, 0.0, 1.0);
    // colorbalance (preserve_lightness off)
    float l = max(x.r, max(x.g, x.b)) + min(x.r, min(x.g, x.b));
    float ws = clamp((0.333 - l) * 4.0 + 0.5, 0.0, 1.0) * 0.7;
    float wm = clamp((l - 0.333) * 4.0 + 0.5, 0.0, 1.0) * clamp((1.0 - l - 0.333) * 4.0 + 0.5, 0.0, 1.0) * 0.7;
    float wh = clamp((l + 0.333 - 1.0) * 4.0 + 0.5, 0.0, 1.0) * 0.7;
    x = clamp(x + uGS[i] * ws + uGM[i] * wm + uGH[i] * wh, 0.0, 1.0);
    // colortemperature
    x = clamp(x * uGT[i], 0.0, 1.0);
    // hue: BT.601 limited-range YUV, chroma rotated and scaled
    if (k.y != 0.0 || k.z != 1.0) {
      vec3 p = x * 255.0;
      float y = 16.0 + dot(p, vec3(0.2568, 0.5041, 0.0979));
      float u = dot(p, vec3(-0.1482, -0.291, 0.4392));
      float w = dot(p, vec3(0.4392, -0.3678, -0.0714));
      float an = radians(k.y);
      float ca = cos(an);
      float sa = sin(an);
      float u2 = (u * ca - w * sa) * k.z;
      float w2 = (w * ca + u * sa) * k.z;
      float yy = (y - 16.0) * 1.1644;
      x = clamp(vec3(yy + 1.596 * w2, yy - 0.392 * u2 - 0.813 * w2, yy + 2.017 * u2) / 255.0, 0.0, 1.0);
    }
    v = x;
  }
  if (uFoldOn > 0.5) v = gradeFolded(v);
  return v;
}
// Segmentation matte in SOURCE geometry (vUv is the source uv, before the transform): the union of
// feathered boxes, optionally inverted. The signed distance is in source pixels.
float matte() {
  vec2 p = vUv * uSrcSize;
  float cov = 0.0;
  for (int i = 0; i < 8; i++) {
    if (i >= uMatteN) break;
    vec4 b = uMatteBoxes[i];
    vec2 h = 0.5 * b.zw * uSrcSize;
    vec2 q = abs(p - (b.xy + 0.5 * b.zw) * uSrcSize) - h;
    float sd = length(max(q, 0.0)) + min(max(q.x, q.y), 0.0);
    float v = uMatteFeather > 0.0 ? clamp(0.5 - sd / uMatteFeather, 0.0, 1.0) : (sd <= 0.0 ? 1.0 : 0.0);
    cov = max(cov, v);
  }
  return uMatteInvert > 0.5 ? 1.0 - cov : cov;
}
// GradeFilterBuilder.Mask: rect = clip(min edge distance / f + 0.5), ellipse = clip((1 - normalised radius) / f + 0.5), optionally inverted.
float shapeMask() {
  float f = max(0.0005, uShapeP.x);
  float inside;
  if (uShapeP.z > 0.5) {
    vec2 d = (vLocal - uShape.xy) / max(vec2(0.001), uShape.zw * 0.5);
    inside = clamp((1.0 - length(d)) / f + 0.5, 0.0, 1.0);
  } else {
    vec2 lo = uShape.xy - uShape.zw * 0.5;
    vec2 hi = uShape.xy + uShape.zw * 0.5;
    float e = min(min(vLocal.x - lo.x, hi.x - vLocal.x), min(vLocal.y - lo.y, hi.y - vLocal.y));
    inside = clamp(e / f + 0.5, 0.0, 1.0);
  }
  return uShapeP.y > 0.5 ? 1.0 - inside : inside;
}
void main() {
  vec4 c = texture(uTex, vUv);
  if (uInputOn > 0.5) c.rgb = sampleTetra(uInputLut, c.rgb);
  vec3 rgb = uGradeOn > 0.5 ? grade(c.rgb) : c.rgb;
  float m = uMatteOn > 0.5 ? matte() : 1.0;
  if (uShapeOn > 0.5) m *= shapeMask();
  outColor = vec4(rgb, c.a * uOpacity * m);
}`;

/**
 * The viewing pass: the composited picture through the viewing LUT, onto the canvas. Runs only with a viewing LUT on;
 * `uOn` = 0 is a plain copy (a clean readback).
 */
export const VIEW_FRAGMENT_SHADER = `#version 300 es
precision mediump float;
precision highp sampler3D;
in vec2 vUv;
uniform sampler2D uTex;
uniform sampler3D uLut;
uniform sampler2D uLut1d;
uniform float uOn;
uniform float uHas1d;
uniform float uHas3d;
uniform float uLut1dScale;
uniform float uLut1dBias;
uniform vec3 uDomMin;
uniform vec3 uDomScale;
uniform float uLutScale;
uniform float uLutBias;
out vec4 outColor;
void main() {
  vec3 c = texture(uTex, vUv).rgb;
  if (uOn > 0.5) {
    if (uHas1d > 0.5) {
      vec3 u = clamp(c, 0.0, 1.0) * uLut1dScale + uLut1dBias;
      c = vec3(texture(uLut1d, vec2(u.r, 0.5)).r, texture(uLut1d, vec2(u.g, 0.5)).g, texture(uLut1d, vec2(u.b, 0.5)).b);
    }
    if (uHas3d > 0.5) c = texture(uLut, clamp((c - uDomMin) * uDomScale, 0.0, 1.0) * uLutScale + uLutBias).rgb;
  }
  outColor = vec4(c, 1.0);
}`;

/** The effect passes' vertex stage: a full-screen quad whose uv keeps the input's row order (no flip). */
export const FX_VERTEX_SHADER = `#version 300 es
in vec2 aPos;
in vec2 aUv;
out vec2 vUv;
void main() {
  vUv = aPos * 0.5 + 0.5;
  gl_Position = vec4(aPos, 0.0, 1.0);
}`;

/** One shader for every effect pass; uMode picks the maths. */
export const FX_FRAGMENT_SHADER = `#version 300 es
precision mediump float;
precision highp int;
in vec2 vUv;
uniform sampler2D uTex;
uniform int uMode;       // 0 blur, 1 pixelate, 2 mono, 3 vignette, 4 sharpen, 5 grain
uniform vec2 uStep;      // blur: one texel along the pass direction
uniform float uSigma;    // blur: sigma in working pixels
uniform vec2 uCells;     // pixelate: block count on x and y
uniform float uAmount;   // mono: amount; vignette: angle in radians
uniform vec2 uAspect;    // vignette: (width, height) of the working frame
uniform vec2 uOrigin;    // pixelate: top-left of the picture in uv, top-down
uniform vec4 uVig;       // vignette: picture centre and half size in uv (y up)
uniform float uSeed;     // grain: the frame
out vec4 outColor;
${FX_EXTRA_GLSL}
void main() {
  vec4 c = texture(uTex, vUv);
  if (uMode == 0) {
    if (uSigma > 0.01) {
      int radius = int(min(ceil(3.0 * uSigma), ${BLUR_MAX_RADIUS}.0));
      float wsum = 1.0;
      vec4 acc = c;
      for (int i = 1; i <= ${BLUR_MAX_RADIUS}; i++) {
        if (i > radius) break;
        float w = exp(-0.5 * float(i * i) / (uSigma * uSigma));
        acc += w * (texture(uTex, vUv + uStep * float(i)) + texture(uTex, vUv - uStep * float(i)));
        wsum += 2.0 * w;
      }
      c = acc / wsum;
    }
  } else if (uMode == 1) {
    // The block grid starts at the top-left of the frame, like pixelize; the target's rows run bottom-up.
    vec2 t = vec2(vUv.x, 1.0 - vUv.y) - uOrigin;
    vec2 cell = floor(t * uCells);
    vec2 size = 1.0 / uCells;
    vec2 centre = uOrigin + (cell + 0.5) * size;
    vec4 acc = vec4(0.0);
    for (int y = -1; y <= 1; y++) {
      for (int x = -1; x <= 1; x++) {
        vec2 q = centre + vec2(float(x), float(y)) * size / 3.0;
        acc += texture(uTex, vec2(q.x, 1.0 - q.y));
      }
    }
    c = acc / 9.0;
  } else if (uMode == 2) {
    float y = dot(c.rgb, vec3(0.2126, 0.7152, 0.0722));
    c.rgb = mix(vec3(y), c.rgb, 1.0 - uAmount);
  } else if (uMode == 4) {
    c = sharpenAt(c);
  } else if (uMode == 5) {
    c = grainAt(c);
  } else {
    // ffmpeg vignette: cos(angle * distance / cornerDistance)^4.
    vec2 d = (vUv - uVig.xy) * uAspect;
    float dn = min(length(d) / max(length(uVig.zw * uAspect), 1e-4), 1.0);
    float f = cos(uAmount * dn);
    f *= f;
    c.rgb *= f * f;
  }
  outColor = c;
}`;

/**
 * The corner-pin / insert pass: the finished, canvas-shaped layer (a framebuffer texture, rows bottom-up) is drawn onto
 * the quad by inverting the projective map per fragment (`uInv`: destination canvas fraction -> picture fraction, both y
 * down). The perspective divide is per fragment, so a non-parallelogram quad has no affine seam. The quad's edge ramps
 * over one picture pixel (the render clears a one-pixel transparent frame so `perspective`'s clamped edge stays clear).
 */
/** Texture units of the region pass (2D bindings only; the layer program's 3D units are separate targets). */
const REGION_ALT_UNIT = 1;
/** The working picture of the region effects is capped at this long side (the decoded proxy is usually smaller). */
const REGION_MAX_LONG_SIDE = 1280;
/** A tracked box is a rough silhouette: the region matte rounds its corners (a fraction of the half short side). */
const REGION_BOX_ROUNDNESS = 0.45;
const REGION_MASK_UNIT = 2;

export const PIN_FRAGMENT_SHADER = `#version 300 es
precision highp float;
in vec2 vUv;
uniform sampler2D uTex;
uniform mat3 uInv;
uniform float uOpacity;
uniform vec2 uPx;
out vec4 outColor;
void main() {
  vec3 h = uInv * vec3(vUv.x, 1.0 - vUv.y, 1.0);
  if (h.z <= 1e-6) {
    outColor = vec4(0.0);
    return;
  }
  vec2 s = h.xy / h.z;
  float cov = clamp(min(min(s.x, 1.0 - s.x) / uPx.x, min(s.y, 1.0 - s.y) / uPx.y) + 0.5, 0.0, 1.0);
  vec4 c = texture(uTex, vec2(clamp(s.x, 0.0, 1.0), 1.0 - clamp(s.y, 0.0, 1.0)));
  outColor = vec4(c.rgb, c.a * uOpacity * cov);
}`;

export const REGION_FRAGMENT_SHADER = `#version 300 es
precision highp float;
in vec2 vUv; // image orientation: v = 0 is the picture's top row, like an uploaded frame
uniform sampler2D uTex;
uniform sampler2D uAlt;
uniform sampler2D uMask0;
uniform sampler2D uMask1;
uniform sampler2D uMask2;
uniform sampler2D uMask3;
uniform int uMode;      // 0 blur, 1 pixelate, 2 highlight, 3 spotlight, 4 colour pop, 5 outline, 6 cutout (alpha = matte)
uniform vec4 uP;        // pixelate: block px; highlight: gain, contrast, dim; spotlight: dim; outline: band px
uniform vec2 uSize;     // the picture in pixels
uniform float uSrc;     // 0 boxes, 1 mask video
uniform int uBoxN;
uniform vec4 uBoxes[8];
uniform int uMaskN;
uniform float uInvert;
uniform float uFeather; // ramp width (boxes) or blur radius (mask video), picture pixels
uniform float uRound;   // box corner radius as a fraction of the box's half short side
out vec4 outColor;
float maskAt(vec2 uv) {
  float m = 0.0;
  if (uMaskN > 0) m = max(m, texture(uMask0, uv).r);
  if (uMaskN > 1) m = max(m, texture(uMask1, uv).r);
  if (uMaskN > 2) m = max(m, texture(uMask2, uv).r);
  if (uMaskN > 3) m = max(m, texture(uMask3, uv).r);
  return m;
}
// Signed distance (pixels, negative inside) to the union of the rounded boxes.
float boxDistance(vec2 uv) {
  vec2 p = uv * uSize;
  float sd = 1e6;
  for (int i = 0; i < 8; i++) {
    if (i >= uBoxN) break;
    vec4 b = uBoxes[i];
    vec2 h = 0.5 * b.zw * uSize;
    float r = uRound * min(h.x, h.y);
    vec2 q = abs(p - (b.xy + 0.5 * b.zw) * uSize) - (h - vec2(r));
    sd = min(sd, length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r);
  }
  return sd;
}
float coverageAt(vec2 uv) {
  float cov;
  if (uSrc > 0.5) {
    if (uFeather > 0.0) {
      vec2 st = uFeather * 1.2 / uSize;
      float acc = 0.0;
      for (int y = -1; y <= 1; y++) {
        for (int x = -1; x <= 1; x++) acc += maskAt(uv + vec2(float(x), float(y)) * st);
      }
      cov = acc / 9.0;
    } else {
      cov = maskAt(uv);
    }
  } else {
    float sd = boxDistance(uv);
    cov = uFeather > 0.0 ? clamp(0.5 - sd / uFeather, 0.0, 1.0) : (sd <= 0.0 ? 1.0 : 0.0);
  }
  return uInvert > 0.5 ? 1.0 - cov : cov;
}
void main() {
  vec4 base = texture(uTex, vUv);
  vec3 c = base.rgb;
  float cov = coverageAt(vUv);
  if (uMode == 0) {
    c = mix(c, texture(uAlt, vUv).rgb, cov);
  } else if (uMode == 1) {
    vec2 cell = floor(vUv * uSize / uP.x);
    vec2 centre = (cell + 0.5) * uP.x / uSize;
    vec2 stp = uP.x / (3.0 * uSize);
    vec3 acc = vec3(0.0);
    for (int y = -1; y <= 1; y++) {
      for (int x = -1; x <= 1; x++) acc += texture(uTex, centre + vec2(float(x), float(y)) * stp).rgb;
    }
    c = mix(c, acc / 9.0, cov);
  } else if (uMode == 2) {
    // eq brightness/contrast act on luma; the same shift on all channels keeps the chroma.
    float y = dot(c, vec3(0.2126, 0.7152, 0.0722));
    vec3 hl = clamp(c + ((y - 0.5) * uP.y + 0.5 + uP.x - y), 0.0, 1.0);
    vec3 inner = mix(c, hl, cov);
    c = mix(inner, clamp(inner - uP.z, 0.0, 1.0), 1.0 - cov);
  } else if (uMode == 3) {
    c = mix(c, clamp(c - uP.x, 0.0, 1.0), 1.0 - cov);
  } else if (uMode == 4) {
    c = mix(vec3(dot(c, vec3(0.2126, 0.7152, 0.0722))), c, cov);
  } else if (uMode == 6) {
    outColor = vec4(c, base.a * cov);
    return;
  } else {
    float ring;
    if (uSrc > 0.5) {
      // dilation(mask) minus mask: the widest reach of the mask within the band, in 8 directions.
      float reach = cov;
      for (int i = 0; i < 8; i++) {
        float a = float(i) * 0.7853982;
        vec2 d = vec2(cos(a), sin(a)) * uP.x / uSize;
        reach = max(reach, coverageAt(vUv + d));
        reach = max(reach, coverageAt(vUv + 0.5 * d));
      }
      ring = clamp(reach - cov, 0.0, 1.0);
    } else {
      float sd = boxDistance(vUv);
      float s2 = uInvert > 0.5 ? -sd : sd;
      ring = clamp(s2 + 0.5, 0.0, 1.0) * clamp(uP.x - s2 + 0.5, 0.0, 1.0);
    }
    c = mix(c, vec3(1.0), ring);
  }
  outColor = vec4(c, base.a);
}`;

function compile(gl: WebGL2RenderingContext, type: number, source: string, label: string): WebGLShader | null {
  const shader = gl.createShader(type);
  if (!shader) {
    console.error(`[preview] compositor init failed: ${label} shader create returned null`);
    return null;
  }
  gl.shaderSource(shader, source);
  gl.compileShader(shader);
  if (!gl.getShaderParameter(shader, gl.COMPILE_STATUS)) {
    console.error(`[preview] compositor init failed: ${label} shader compile`, gl.getShaderInfoLog(shader));
    gl.deleteShader(shader);
    return null;
  }
  return shader;
}

export interface CompositorHooks {
  /** The WebGL context was lost (the browser may restore it). Drawing is suppressed until then. */
  onContextLost?: () => void;
  /** The context came back and every GL resource was rebuilt (`ok`), or the rebuild failed (`!ok`). */
  onContextRestored?: (ok: boolean) => void;
}

export function createGlCompositor(canvas: HTMLCanvasElement, hooks?: CompositorHooks): Compositor | null {
  let gl: WebGL2RenderingContext | null = null;
  try {
    gl = canvas.getContext('webgl2', COMPOSITOR_CONTEXT_ATTRIBUTES);
  } catch {
    gl = null;
  }
  if (!gl) {
    console.error('[preview] compositor init failed: getContext returned null (context could not be created with the compositor attributes)', { width: canvas.width, height: canvas.height, hasWebGL2Ctor: typeof (globalThis as { WebGL2RenderingContext?: unknown }).WebGL2RenderingContext });
    return null;
  }

  let renderer: string | null = null;
  try {
    const info = gl.getExtension('WEBGL_debug_renderer_info');
    renderer = info ? String(gl.getParameter(info.UNMASKED_RENDERER_WEBGL)) : null;
  } catch {
    renderer = null;
  }

  const ctx: WebGL2RenderingContext = gl;
  type Loc = WebGLUniformLocation | null;
  // Everything below up to `initGl` is GL-owned state: it is rebuilt after a context restore.
  let program: WebGLProgram | null = null;
  let vao: WebGLVertexArrayObject | null = null;
  let vbo: WebGLBuffer | null = null;
  let aPos = 0;
  let aUv = 0;
  let uTex: Loc = null;
  let uOpacity: Loc = null;
  let uScale: Loc = null;
  let uOffset: Loc = null;
  let uRotation: Loc = null;
  let uAspect: Loc = null;
  let uUvOrigin: Loc = null;
  let uUvSize: Loc = null;
  let uFlipV: Loc = null;
  let uLocalMode: Loc = null;
  let uShape = {} as Record<'on' | 'rect' | 'params', Loc>;
  let uMatte = {} as Record<'on' | 'n' | 'boxes' | 'invert' | 'feather' | 'size', Loc>;
  const matteBuffer = new Float32Array(32);
  // Effect passes: their own program and two working textures (ping-pong) at the plan's size.
  let fxProgram: WebGLProgram | null = null;
  const fxLoc = {} as Record<'tex' | 'mode' | 'step' | 'sigma' | 'cells' | 'amount' | 'aspect' | 'vig' | 'origin' | 'seed', Loc>;
  // Region effects: their own program, full-size ping-pong targets (the effected picture) and small ones (the blur's working copy).
  let regionProgram: WebGLProgram | null = null;
  const regionLoc = {} as Record<'tex' | 'alt' | 'm0' | 'm1' | 'm2' | 'm3' | 'mode' | 'p' | 'size' | 'src' | 'boxN' | 'boxes' | 'maskN' | 'invert' | 'feather' | 'round', Loc>;
  type TargetPair = { w: number; h: number; textures: [WebGLTexture, WebGLTexture]; fbos: [WebGLFramebuffer, WebGLFramebuffer] };
  const regionPairs: { full: TargetPair | null; low: TargetPair | null } = { full: null, low: null };
  const maskTextures = new Map<string, { texture: WebGLTexture; uploaded: CanvasImageSource | null }>();
  let blackTexture: WebGLTexture | null = null;
  const regionBoxes = new Float32Array(32);
  // Pin pass: its own program (a projective warp of a canvas-shaped target onto a quad).
  let pinProgram: WebGLProgram | null = null;
  const pinLoc = {} as Record<'tex' | 'inv' | 'opacity' | 'px', Loc>;
  let fxTargets: { w: number; h: number; textures: [WebGLTexture, WebGLTexture]; fbos: [WebGLFramebuffer, WebGLFramebuffer] } | null = null;
  let uInput = {} as Record<'on' | 'lut' | 'scale' | 'bias', Loc>;
  // Viewing pass: the composite goes to an offscreen target first, then through the viewing LUT onto the canvas.
  let viewProgram: WebGLProgram | null = null;
  const viewLoc = {} as Record<'tex' | 'lut' | 'lut1d' | 'has1d' | 'has3d' | 'lut1dScale' | 'lut1dBias' | 'on' | 'domMin' | 'domScale' | 'lutScale' | 'lutBias', Loc>;
  let viewTarget: { w: number; h: number; texture: WebGLTexture; fbo: WebGLFramebuffer } | null = null;
  // 3D textures (camera tables, the viewing LUT), built lazily and dropped with the context. Camera tables are tiny
  // and stay cached by profile id; the viewing LUT is per-file and is evicted LRU so a run of choices cannot grow GPU
  // memory without bound (about 2.2 MB per table).
  const lutTextures = new Map<string, WebGLTexture>();
  // A 1x1x1 3D texture bound whenever a sampler3D stage is off, so its program never leaves a sampler3D on unit 0.
  let dummy3d: WebGLTexture | null = null;
  let viewingLut: ViewingLutSpec | null = null;
  let viewActive = false;
  let viewHasFrame = false;
  let lastLayers: readonly CompositorLayer[] | null = null;
  let overlayShown = false;
  let uGrade = {} as Record<'on' | 'n' | 'fo' | 'fe' | 'fof' | 'fl' | 'fg' | 'fm' | 'fa' | 'ft' | 'a' | 'b' | 'c' | 's' | 'm' | 'h' | 't', Loc>;
  let lost = false;
  // Blend modes: a frame with a blending layer composites through a backdrop ping-pong (blend-pass.ts); other frames never touch it.
  const blend = createBlendPass(ctx, () => {
    ctx.useProgram(program);
    activate(PICTURE_UNIT);
  });

  const gradeBuf = {
    a: new Float32Array(GRADE_NODE_BOUND * 4), b: new Float32Array(GRADE_NODE_BOUND * 4), c: new Float32Array(GRADE_NODE_BOUND * 4),
    s: new Float32Array(GRADE_NODE_BOUND * 3), m: new Float32Array(GRADE_NODE_BOUND * 3), h: new Float32Array(GRADE_NODE_BOUND * 3), t: new Float32Array(GRADE_NODE_BOUND * 3),
  };
  const setGrade = (grade: readonly PreviewGradeNode[] | undefined) => {
    const { head: nodes, tail } = splitGradeNodes(grade ?? []);
    const n = nodes.length;
    gl!.uniform1f(uGrade.on, n > 0 || tail ? 1 : 0);
    gl!.uniform1i(uGrade.n, n);
    gl!.uniform1f(uGrade.fo, tail ? 1 : 0);
    if (tail) {
      gl!.uniform1f(uGrade.fe, tail.exposureGain);
      gl!.uniform3fv(uGrade.fof, tail.offset);
      gl!.uniform3fv(uGrade.fl, tail.lift);
      gl!.uniform3fv(uGrade.fg, tail.gain);
      gl!.uniform3fv(uGrade.fm, tail.gamma);
      gl!.uniform4f(uGrade.fa, tail.contrast, tail.pivot, tail.saturation, tail.hue);
      gl!.uniform2f(uGrade.ft, tail.temperature, tail.tint);
    }
    if (n === 0) return;
    const { a, b, c, s, m, h, t } = gradeBuf;
    for (let i = 0; i < n; i++) {
      const g = nodes[i];
      a.set([g.exposureGain, g.liftMaster, g.gainMaster, g.gammaMaster], i * 4);
      b.set([g.offset[0], g.offset[1], g.offset[2], g.contrast], i * 4);
      c.set([g.pivot, g.hue, g.saturation, 0], i * 4);
      s.set(g.shadows, i * 3);
      m.set(g.mids, i * 3);
      h.set(g.highs, i * 3);
      t.set(g.temp, i * 3);
    }
    gl!.uniform4fv(uGrade.a, a);
    gl!.uniform4fv(uGrade.b, b);
    gl!.uniform4fv(uGrade.c, c);
    gl!.uniform3fv(uGrade.s, s);
    gl!.uniform3fv(uGrade.m, m);
    gl!.uniform3fv(uGrade.h, h);
    gl!.uniform3fv(uGrade.t, t);
  };
  let aspect = 16 / 9;

  const activate = (unit: number) => gl!.activeTexture(gl!.TEXTURE0 + unit);

  /** A 3D RGBA16F texture of `size`^3 texels, keyed and cached for this context, built on `unit`. */
  const lut3dTexture = (key: string, size: number, texels: () => Uint16Array, unit: number): WebGLTexture | null => {
    const hit = lutTextures.get(key);
    if (hit) {
      // Re-insert so the Map's iteration order is the LRU order.
      lutTextures.delete(key);
      lutTextures.set(key, hit);
      return hit;
    }
    const texture = gl!.createTexture();
    if (!texture) return null;
    activate(unit);
    gl!.bindTexture(gl!.TEXTURE_3D, texture);
    gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_MIN_FILTER, gl!.LINEAR);
    gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_MAG_FILTER, gl!.LINEAR);
    gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_WRAP_S, gl!.CLAMP_TO_EDGE);
    gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_WRAP_T, gl!.CLAMP_TO_EDGE);
    gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_WRAP_R, gl!.CLAMP_TO_EDGE);
    gl!.texImage3D(gl!.TEXTURE_3D, 0, gl!.RGBA16F, size, size, size, 0, gl!.RGBA, gl!.HALF_FLOAT, texels());
    activate(PICTURE_UNIT);
    lutTextures.set(key, texture);
    // Only viewing LUTs (`view:` keys) are per-file and unbounded in number; profile tables stay by id.
    if (key.startsWith('view:')) evictOldViewingLutTextures();
    return texture;
  };

  /** An N x 1 RGBA16F 2D texture (the viewing LUT's 1D stage), cached with the viewing LUTs. Texel centres via the bias in the shader. */
  const lut1dTexture = (key: string, size: number, texels: Uint16Array | (() => Uint16Array)): WebGLTexture | null => {
    const hit = lutTextures.get(key);
    if (hit) {
      lutTextures.delete(key);
      lutTextures.set(key, hit);
      return hit;
    }
    const texture = gl!.createTexture();
    if (!texture) return null;
    activate(VIEWING_LUT_1D_UNIT);
    gl!.bindTexture(gl!.TEXTURE_2D, texture);
    gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MIN_FILTER, gl!.LINEAR);
    gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MAG_FILTER, gl!.LINEAR);
    gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_S, gl!.CLAMP_TO_EDGE);
    gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_T, gl!.CLAMP_TO_EDGE);
    gl!.texImage2D(gl!.TEXTURE_2D, 0, gl!.RGBA16F, size, 1, 0, gl!.RGBA, gl!.HALF_FLOAT, typeof texels === 'function' ? texels() : texels);
    activate(PICTURE_UNIT);
    lutTextures.set(key, texture);
    evictOldViewingLutTextures();
    return texture;
  };

  /** Deletes the least-recently-used viewing-LUT textures beyond the cap. Their GL objects are freed at once. */
  const evictOldViewingLutTextures = () => {
    let views = 0;
    for (const key of lutTextures.keys()) if (key.startsWith('view:')) views += 1;
    while (views > VIEWING_LUT_TEXTURE_CACHE_MAX) {
      let oldest: string | null = null;
      for (const key of lutTextures.keys()) {
        if (key.startsWith('view:')) {
          oldest = key;
          break;
        }
      }
      if (oldest === null) break;
      const texture = lutTextures.get(oldest);
      if (texture) gl!.deleteTexture(texture);
      lutTextures.delete(oldest);
      views -= 1;
    }
  };

  /**
   * A valid 1x1x1 RGBA8 3D texture for a sampler3D whose stage is off. Binding it keeps the program in a
   * state WebGL2 accepts: the sampler's explicit unit holds a 3D texture even though the shader branch
   * never samples it, and no empty unit is left to default to the picture's. Built once per context.
   */
  const dummy3dTexture = (unit: number): WebGLTexture | null => {
    if (!dummy3d) {
      const texture = gl!.createTexture();
      if (!texture) return null;
      dummy3d = texture;
      activate(unit);
      gl!.bindTexture(gl!.TEXTURE_3D, texture);
      gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_MIN_FILTER, gl!.NEAREST);
      gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_MAG_FILTER, gl!.NEAREST);
      gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_WRAP_S, gl!.CLAMP_TO_EDGE);
      gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_WRAP_T, gl!.CLAMP_TO_EDGE);
      gl!.texParameteri(gl!.TEXTURE_3D, gl!.TEXTURE_WRAP_R, gl!.CLAMP_TO_EDGE);
      gl!.texImage3D(gl!.TEXTURE_3D, 0, gl!.RGBA, 1, 1, 1, 0, gl!.RGBA, gl!.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 255]));
    } else {
      activate(unit);
      gl!.bindTexture(gl!.TEXTURE_3D, dummy3d);
    }
    activate(PICTURE_UNIT);
    return dummy3d;
  };

  /** The layer's camera input stage: binds its table on its own unit and sets the uniforms, or switches the stage off. */
  const setInput = (layer: CompositorLayer | undefined) => {
    const plan = layer ? planInputStage(layer) : { on: false, textureKey: null };
    const id = plan.on && layer ? (layer.inputProfile as string) : null;
    const texture = id ? lut3dTexture(plan.textureKey as string, CAMERA_PROFILE_TABLE_DEFAULT_SIZE, () => cameraProfileTableRgba16f(id, CAMERA_PROFILE_TABLE_DEFAULT_SIZE), INPUT_LUT_UNIT) : null;
    if (!texture) {
      // Off: still bind a valid 3D texture on the sampler's unit, so it never shares unit 0 with `uTex`.
      dummy3dTexture(INPUT_LUT_UNIT);
      gl!.uniform1f(uInput.on, 0);
      return;
    }
    const map = lutTexelMapping(CAMERA_PROFILE_TABLE_DEFAULT_SIZE);
    activate(INPUT_LUT_UNIT);
    gl!.bindTexture(gl!.TEXTURE_3D, texture);
    activate(PICTURE_UNIT);
    gl!.uniform1f(uInput.scale, map.scale);
    gl!.uniform1f(uInput.bias, map.bias);
    gl!.uniform1f(uInput.on, 1);
  };

  let viewProgramFailed = false;
  /** Builds the viewing program once; false when it cannot (the viewing LUT then stays off, the picture is unchanged). */
  const ensureViewProgram = (): boolean => {
    if (viewProgram) return true;
    if (viewProgramFailed) return false;
    const viewVs = compile(ctx, gl!.VERTEX_SHADER, FX_VERTEX_SHADER, 'viewing vertex');
    const viewFs = compile(ctx, gl!.FRAGMENT_SHADER, VIEW_FRAGMENT_SHADER, 'viewing fragment');
    const viewProg = gl!.createProgram();
    if (viewVs && viewFs && viewProg) {
      gl!.bindAttribLocation(viewProg, 0, 'aPos');
      gl!.bindAttribLocation(viewProg, 1, 'aUv');
      gl!.attachShader(viewProg, viewVs);
      gl!.attachShader(viewProg, viewFs);
      gl!.linkProgram(viewProg);
      if (gl!.getProgramParameter(viewProg, gl!.LINK_STATUS)) {
        viewProgram = viewProg;
        viewLoc.tex = gl!.getUniformLocation(viewProg, 'uTex');
        viewLoc.lut = gl!.getUniformLocation(viewProg, 'uLut');
        viewLoc.lut1d = gl!.getUniformLocation(viewProg, 'uLut1d');
        viewLoc.has1d = gl!.getUniformLocation(viewProg, 'uHas1d');
        viewLoc.has3d = gl!.getUniformLocation(viewProg, 'uHas3d');
        viewLoc.lut1dScale = gl!.getUniformLocation(viewProg, 'uLut1dScale');
        viewLoc.lut1dBias = gl!.getUniformLocation(viewProg, 'uLut1dBias');
        viewLoc.on = gl!.getUniformLocation(viewProg, 'uOn');
        viewLoc.domMin = gl!.getUniformLocation(viewProg, 'uDomMin');
        viewLoc.domScale = gl!.getUniformLocation(viewProg, 'uDomScale');
        viewLoc.lutScale = gl!.getUniformLocation(viewProg, 'uLutScale');
        viewLoc.lutBias = gl!.getUniformLocation(viewProg, 'uLutBias');
        // Every sampler gets its explicit unit once, after link (defaults to unit 0 and collides with the picture).
        gl!.useProgram(viewProg);
        gl!.uniform1i(viewLoc.tex, PICTURE_UNIT);
        gl!.uniform1i(viewLoc.lut, VIEWING_LUT_UNIT);
        gl!.uniform1i(viewLoc.lut1d, VIEWING_LUT_1D_UNIT);
        gl!.useProgram(program);
      } else {
        console.error('[preview] viewing program link failed; the viewing LUT will not preview', gl!.getProgramInfoLog(viewProg));
      }
    }
    if (!viewProgram) viewProgramFailed = true;
    return viewProgram !== null;
  };

  const releaseViewTarget = () => {
    if (!viewTarget) return;
    gl!.deleteTexture(viewTarget.texture);
    gl!.deleteFramebuffer(viewTarget.fbo);
    viewTarget = null;
  };
  const viewTargetFor = (w: number, h: number) => {
    if (viewTarget && viewTarget.w === w && viewTarget.h === h) return viewTarget;
    releaseViewTarget();
    const texture = gl!.createTexture()!;
    gl!.bindTexture(gl!.TEXTURE_2D, texture);
    gl!.texImage2D(gl!.TEXTURE_2D, 0, gl!.RGBA, w, h, 0, gl!.RGBA, gl!.UNSIGNED_BYTE, null);
    gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MIN_FILTER, gl!.NEAREST);
    gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MAG_FILTER, gl!.NEAREST);
    gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_S, gl!.CLAMP_TO_EDGE);
    gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_T, gl!.CLAMP_TO_EDGE);
    const fbo = gl!.createFramebuffer()!;
    gl!.bindFramebuffer(gl!.FRAMEBUFFER, fbo);
    gl!.framebufferTexture2D(gl!.FRAMEBUFFER, gl!.COLOR_ATTACHMENT0, gl!.TEXTURE_2D, texture, 0);
    viewTarget = { w, h, texture, fbo };
    return viewTarget;
  };
  /** Where layers and the overlay are drawn: the offscreen target while the viewing pass is active, else the canvas. */
  /** While an xfade leg is drawn, everything lands on that leg's target instead of the main one. */
  let legTarget: WebGLFramebuffer | null = null;
  const bindMain = () => {
    if (blend.active && !legTarget) {
      blend.bindCurrent();
      return;
    }
    if (legTarget) gl!.bindFramebuffer(gl!.FRAMEBUFFER, legTarget);
    else if (viewActive && viewTarget) gl!.bindFramebuffer(gl!.FRAMEBUFFER, viewTarget.fbo);
    else gl!.bindFramebuffer(gl!.FRAMEBUFFER, null);
  };
  /** The last step: the composited target through the viewing LUT (`useLut` false = a plain copy) onto the canvas. */
  const presentView = (useLut: boolean) => {
    if (!viewActive || !viewTarget || !viewProgram || !viewingLut) return;
    const spec = viewingLut;
    const lut = spec.size > 0 ? lut3dTexture(`view:${spec.key}`, spec.size, () => spec.texels, VIEWING_LUT_UNIT) : null;
    const lut1d = spec.oneD ? lut1dTexture(`view:1d:${spec.key}`, spec.oneD.size, spec.oneD.texels) : null;
    gl!.bindFramebuffer(gl!.FRAMEBUFFER, null);
    gl!.viewport(0, 0, canvasWidth, canvasHeight);
    gl!.disable(gl!.BLEND);
    gl!.useProgram(viewProgram);
    activate(PICTURE_UNIT);
    gl!.bindTexture(gl!.TEXTURE_2D, viewTarget.texture);
    // The 1D stage's unit always holds a valid 2D texture (the picture's target when the stage is off).
    activate(VIEWING_LUT_1D_UNIT);
    gl!.bindTexture(gl!.TEXTURE_2D, lut1d ?? viewTarget.texture);
    activate(PICTURE_UNIT);
    if (lut1d && spec.oneD) {
      const map1 = lutTexelMapping(spec.oneD.size);
      gl!.uniform1f(viewLoc.lut1dScale, map1.scale);
      gl!.uniform1f(viewLoc.lut1dBias, map1.bias);
    }
    gl!.uniform1f(viewLoc.has1d, lut1d ? 1 : 0);
    gl!.uniform1f(viewLoc.has3d, lut ? 1 : 0);
    if (lut) {
      const dom = viewingLutDomainUniforms(spec);
      const map = lutTexelMapping(spec.size);
      activate(VIEWING_LUT_UNIT);
      gl!.bindTexture(gl!.TEXTURE_3D, lut);
      activate(PICTURE_UNIT);
      gl!.uniform3f(viewLoc.domMin, dom.min[0], dom.min[1], dom.min[2]);
      gl!.uniform3f(viewLoc.domScale, dom.scale[0], dom.scale[1], dom.scale[2]);
      gl!.uniform1f(viewLoc.lutScale, map.scale);
      gl!.uniform1f(viewLoc.lutBias, map.bias);
    } else {
      dummy3dTexture(VIEWING_LUT_UNIT);
    }
    gl!.uniform1f(viewLoc.on, useLut && (lut || lut1d) ? 1 : 0);
    gl!.drawArrays(gl!.TRIANGLES, 0, 6);
    gl!.enable(gl!.BLEND);
    gl!.useProgram(program);
  };

  const textures = new Map<string, { texture: WebGLTexture; hasImage: boolean; width: number; height: number; uploaded: CanvasImageSource | null }>();
  let overlayTexture: WebGLTexture | null = null;
  let overlayUploaded = false;

  const textureKey = (layer: CompositorLayer) => (layer.contentKey === undefined ? layer.layerId : `${layer.layerId}\u0000${layer.contentKey}`);
  const textureFor = (key: string) => {
    let entry = textures.get(key);
    if (!entry) {
      const texture = gl!.createTexture()!;
      gl!.bindTexture(gl!.TEXTURE_2D, texture);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MIN_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MAG_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_S, gl!.CLAMP_TO_EDGE);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_T, gl!.CLAMP_TO_EDGE);
      entry = { texture, hasImage: false, width: 0, height: 0, uploaded: null };
      textures.set(key, entry);
    }
    return entry;
  };

  let canvasWidth = canvas.width;
  let canvasHeight = canvas.height;
  const drawQuad = (texture: WebGLTexture, opacity: number, transform?: LayerTransform, grade?: readonly PreviewGradeNode[], fit: [number, number] = [1, 1], flipV = false, matte?: Matte, srcSize: [number, number] = [1, 1], inputStage?: CompositorLayer, pictureWindow: SourceWindow = FULL_WINDOW, shape?: ShapeParams) => {
    // The input stage binds a 3D texture on its own unit; bind the picture last so unit 0 holds it.
    setInput(inputStage);
    activate(PICTURE_UNIT);
    gl!.bindTexture(gl!.TEXTURE_2D, texture);
    gl!.uniform1f(uOpacity, opacity);
    setGrade(grade);
    gl!.uniform1f(uAspect, aspect);
    gl!.uniform1f(uFlipV, flipV ? 1 : 0);
    if (matte) {
      matteBuffer.fill(0);
      const n = Math.min(MAX_MATTE_BOXES, matte.boxes.length);
      for (let i = 0; i < n; i += 1) matteBuffer.set([matte.boxes[i].x, matte.boxes[i].y, matte.boxes[i].w, matte.boxes[i].h], i * 4);
      gl!.uniform1f(uMatte.on, 1);
      gl!.uniform1i(uMatte.n, n);
      gl!.uniform4fv(uMatte.boxes, matteBuffer);
      gl!.uniform1f(uMatte.invert, matte.invert ? 1 : 0);
      // The render blurs the mask with gblur sigma = featherPx (source pixels). A linear ramp 3.2 sigma wide has the same
      // 10-90% width as that gaussian edge (2.56 sigma); scaled from source pixels to the decoded proxy's.
      const proxyScale = matte.sourceWidth && matte.sourceWidth > 0 ? srcSize[0] / matte.sourceWidth : 1;
      gl!.uniform1f(uMatte.feather, 3.2 * matte.featherPx * proxyScale);
      gl!.uniform2f(uMatte.size, Math.max(1, srcSize[0]), Math.max(1, srcSize[1]));
    } else {
      gl!.uniform1f(uMatte.on, 0);
    }
    if (shape) {
      gl!.uniform1f(uShape.on, 1);
      gl!.uniform4f(uShape.rect, shape.x, shape.y, shape.w, shape.h);
      gl!.uniform4f(uShape.params, shape.feather, shape.invert ? 1 : 0, shape.kind === 'ellipse' ? 1 : 0, 0);
      // The render's rule (TransformFilterBuilder.Build): the canvas frame only with no transform, no crop and no fade at all
      // (`padded`); an identity transform object, a zero crop or a fade alone take the unpadded path, masked in the picture's own frame.
      gl!.uniform1f(uLocalMode, (inputStage?.padded ?? transform === undefined) ? 1 : 0);
    } else {
      gl!.uniform1f(uShape.on, 0);
    }
    if (transform === undefined) {
      gl!.uniform2f(uScale, fit[0], fit[1]);
      gl!.uniform2f(uOffset, 0, 0);
      gl!.uniform1f(uRotation, 0);
      gl!.uniform2f(uUvOrigin, pictureWindow.x, pictureWindow.y);
      gl!.uniform2f(uUvSize, pictureWindow.w, pictureWindow.h);
    } else {
      const { crop } = transform;
      const w = Math.max(0.01, 1 - crop.left - crop.right);
      const h = Math.max(0.01, 1 - crop.top - crop.bottom);
      // The clip's crop applies to the reframed picture: it is a window inside the reframe window.
      const placed = composeWindows(pictureWindow, { x: crop.left, y: crop.top, w, h });
      // `fit` is already the CROPPED picture's fit to the canvas (crop, then fit, then scale: TransformFilterBuilder.cs:91,95,101),
      // so the crop window must not shrink the quad a second time.
      gl!.uniform2f(uScale, transform.scale * fit[0], (transform.scaleY ?? transform.scale) * fit[1]);
      // Clip space: +x right, +y up; the document's y is positive downwards.
      gl!.uniform2f(uOffset, transform.x * 2, -transform.y * 2);
      // Clockwise in screen space is a negative angle in a y-up clip space.
      gl!.uniform1f(uRotation, (-transform.rotation * Math.PI) / 180);
      gl!.uniform2f(uUvOrigin, placed.x, placed.y);
      gl!.uniform2f(uUvSize, placed.w, placed.h);
    }
    gl!.drawArrays(gl!.TRIANGLES, 0, 6);
  };

  const initGl = (): boolean => {
    const vs = compile(ctx, ctx.VERTEX_SHADER, VERTEX_SHADER, 'vertex');
    const fs = compile(ctx, ctx.FRAGMENT_SHADER, FRAGMENT_SHADER, 'fragment');
    program = ctx.createProgram();
    if (!vs || !fs || !program) return false;
    ctx.bindAttribLocation(program, 0, 'aPos');
    ctx.bindAttribLocation(program, 1, 'aUv');
    ctx.attachShader(program, vs);
    ctx.attachShader(program, fs);
    ctx.linkProgram(program);
    if (!ctx.getProgramParameter(program, ctx.LINK_STATUS)) {
      console.error('[preview] compositor init failed: program link', ctx.getProgramInfoLog(program));
      return false;
    }

    vao = ctx.createVertexArray();
    vbo = ctx.createBuffer();
    ctx.bindVertexArray(vao);
    ctx.bindBuffer(ctx.ARRAY_BUFFER, vbo);
    // x, y, u, v for a full-screen quad (two triangles). v is flipped so textures are upright.
    ctx.bufferData(
      ctx.ARRAY_BUFFER,
      new Float32Array([-1, -1, 0, 1, 1, -1, 1, 1, -1, 1, 0, 0, -1, 1, 0, 0, 1, -1, 1, 1, 1, 1, 1, 0]),
      ctx.STATIC_DRAW,
    );
    aPos = ctx.getAttribLocation(program, 'aPos');
    aUv = ctx.getAttribLocation(program, 'aUv');
    ctx.enableVertexAttribArray(aPos);
    ctx.vertexAttribPointer(aPos, 2, ctx.FLOAT, false, 16, 0);
    ctx.enableVertexAttribArray(aUv);
    ctx.vertexAttribPointer(aUv, 2, ctx.FLOAT, false, 16, 8);

    uTex = ctx.getUniformLocation(program, 'uTex');
    uOpacity = ctx.getUniformLocation(program, 'uOpacity');
    uScale = ctx.getUniformLocation(program, 'uScale');
    uOffset = ctx.getUniformLocation(program, 'uOffset');
    uRotation = ctx.getUniformLocation(program, 'uRotation');
    uAspect = ctx.getUniformLocation(program, 'uAspect');
    uUvOrigin = ctx.getUniformLocation(program, 'uUvOrigin');
    uUvSize = ctx.getUniformLocation(program, 'uUvSize');
    uFlipV = ctx.getUniformLocation(program, 'uFlipV');
    uLocalMode = ctx.getUniformLocation(program, 'uLocalMode');
    uShape = {
      on: ctx.getUniformLocation(program, 'uShapeOn'),
      rect: ctx.getUniformLocation(program, 'uShape'),
      params: ctx.getUniformLocation(program, 'uShapeP'),
    };
    uMatte = {
      on: ctx.getUniformLocation(program, 'uMatteOn'),
      n: ctx.getUniformLocation(program, 'uMatteN'),
      boxes: ctx.getUniformLocation(program, 'uMatteBoxes'),
      invert: ctx.getUniformLocation(program, 'uMatteInvert'),
      feather: ctx.getUniformLocation(program, 'uMatteFeather'),
      size: ctx.getUniformLocation(program, 'uSrcSize'),
    };
    uGrade = {
      on: ctx.getUniformLocation(program, 'uGradeOn'),
      n: ctx.getUniformLocation(program, 'uGradeN'),
      fo: ctx.getUniformLocation(program, 'uFoldOn'),
      fe: ctx.getUniformLocation(program, 'uFExp'),
      fof: ctx.getUniformLocation(program, 'uFOffset'),
      fl: ctx.getUniformLocation(program, 'uFLift'),
      fg: ctx.getUniformLocation(program, 'uFGain'),
      fm: ctx.getUniformLocation(program, 'uFGamma'),
      fa: ctx.getUniformLocation(program, 'uFA'),
      ft: ctx.getUniformLocation(program, 'uFTT'),
      a: ctx.getUniformLocation(program, 'uGA'),
      b: ctx.getUniformLocation(program, 'uGB'),
      c: ctx.getUniformLocation(program, 'uGC'),
      s: ctx.getUniformLocation(program, 'uGS'),
      m: ctx.getUniformLocation(program, 'uGM'),
      h: ctx.getUniformLocation(program, 'uGH'),
      t: ctx.getUniformLocation(program, 'uGT'),
    };

    uInput = {
      on: ctx.getUniformLocation(program, 'uInputOn'),
      lut: ctx.getUniformLocation(program, 'uInputLut'),
      scale: ctx.getUniformLocation(program, 'uInputScale'),
      bias: ctx.getUniformLocation(program, 'uInputBias'),
    };

    // The viewing program is built on first use (`ensureViewProgram`): most sessions never set a viewing LUT.
    viewProgram = null;
    viewProgramFailed = false;

    // The effect program: same quad, same attribute slots. If it fails the clip simply draws without effects.
    fxProgram = null;
    const fxVs = compile(ctx, ctx.VERTEX_SHADER, FX_VERTEX_SHADER, 'effect vertex');
    const fxFs = compile(ctx, ctx.FRAGMENT_SHADER, FX_FRAGMENT_SHADER, 'effect fragment');
    const fxProg = ctx.createProgram();
    if (fxVs && fxFs && fxProg) {
      ctx.bindAttribLocation(fxProg, 0, 'aPos');
      ctx.bindAttribLocation(fxProg, 1, 'aUv');
      ctx.attachShader(fxProg, fxVs);
      ctx.attachShader(fxProg, fxFs);
      ctx.linkProgram(fxProg);
      if (ctx.getProgramParameter(fxProg, ctx.LINK_STATUS)) {
        fxProgram = fxProg;
        fxLoc.tex = ctx.getUniformLocation(fxProg, 'uTex');
        fxLoc.mode = ctx.getUniformLocation(fxProg, 'uMode');
        fxLoc.step = ctx.getUniformLocation(fxProg, 'uStep');
        fxLoc.sigma = ctx.getUniformLocation(fxProg, 'uSigma');
        fxLoc.cells = ctx.getUniformLocation(fxProg, 'uCells');
        fxLoc.amount = ctx.getUniformLocation(fxProg, 'uAmount');
        fxLoc.aspect = ctx.getUniformLocation(fxProg, 'uAspect');
        fxLoc.vig = ctx.getUniformLocation(fxProg, 'uVig');
        fxLoc.origin = ctx.getUniformLocation(fxProg, 'uOrigin');
        fxLoc.seed = ctx.getUniformLocation(fxProg, 'uSeed');
        ctx.useProgram(fxProg);
        ctx.uniform1i(fxLoc.tex, PICTURE_UNIT);
      } else {
        console.error('[preview] effect program link failed; clip effects will not preview', ctx.getProgramInfoLog(fxProg));
      }
    }

    // The region program: same full-screen vertex stage. If it fails, region effects simply do not preview.
    regionProgram = null;
    const regVs = compile(ctx, ctx.VERTEX_SHADER, FX_VERTEX_SHADER, 'region vertex');
    const regFs = compile(ctx, ctx.FRAGMENT_SHADER, REGION_FRAGMENT_SHADER, 'region fragment');
    const regProg = ctx.createProgram();
    if (regVs && regFs && regProg) {
      ctx.bindAttribLocation(regProg, 0, 'aPos');
      ctx.bindAttribLocation(regProg, 1, 'aUv');
      ctx.attachShader(regProg, regVs);
      ctx.attachShader(regProg, regFs);
      ctx.linkProgram(regProg);
      if (ctx.getProgramParameter(regProg, ctx.LINK_STATUS)) {
        regionProgram = regProg;
        const names = { tex: 'uTex', alt: 'uAlt', m0: 'uMask0', m1: 'uMask1', m2: 'uMask2', m3: 'uMask3', mode: 'uMode', p: 'uP', size: 'uSize', src: 'uSrc', boxN: 'uBoxN', boxes: 'uBoxes', maskN: 'uMaskN', invert: 'uInvert', feather: 'uFeather', round: 'uRound' } as const;
        for (const key of Object.keys(names) as (keyof typeof names)[]) regionLoc[key] = ctx.getUniformLocation(regProg, names[key]);
        ctx.useProgram(regProg);
        ctx.uniform1i(regionLoc.tex, PICTURE_UNIT);
        ctx.uniform1i(regionLoc.alt, REGION_ALT_UNIT);
        ctx.uniform1i(regionLoc.m0, REGION_MASK_UNIT);
        ctx.uniform1i(regionLoc.m1, REGION_MASK_UNIT + 1);
        ctx.uniform1i(regionLoc.m2, REGION_MASK_UNIT + 2);
        ctx.uniform1i(regionLoc.m3, REGION_MASK_UNIT + 3);
      } else {
        console.error('[preview] region program link failed; region effects will not preview', ctx.getProgramInfoLog(regProg));
      }
    }

    // The pin program: same full-screen vertex stage as the effects. If it fails a pinned layer draws unpinned.
    pinProgram = null;
    const pinVs = compile(ctx, ctx.VERTEX_SHADER, FX_VERTEX_SHADER, 'pin vertex');
    const pinFs = compile(ctx, ctx.FRAGMENT_SHADER, PIN_FRAGMENT_SHADER, 'pin fragment');
    const pinProg = ctx.createProgram();
    if (pinVs && pinFs && pinProg) {
      ctx.bindAttribLocation(pinProg, 0, 'aPos');
      ctx.bindAttribLocation(pinProg, 1, 'aUv');
      ctx.attachShader(pinProg, pinVs);
      ctx.attachShader(pinProg, pinFs);
      ctx.linkProgram(pinProg);
      if (ctx.getProgramParameter(pinProg, ctx.LINK_STATUS)) {
        pinProgram = pinProg;
        pinLoc.tex = ctx.getUniformLocation(pinProg, 'uTex');
        pinLoc.inv = ctx.getUniformLocation(pinProg, 'uInv');
        pinLoc.opacity = ctx.getUniformLocation(pinProg, 'uOpacity');
        pinLoc.px = ctx.getUniformLocation(pinProg, 'uPx');
        ctx.useProgram(pinProg);
        ctx.uniform1i(pinLoc.tex, PICTURE_UNIT);
      } else {
        console.error('[preview] pin program link failed; corner pins and inserts will not warp', ctx.getProgramInfoLog(pinProg));
      }
    }

    ctx.enable(ctx.BLEND);
    ctx.blendFunc(ctx.SRC_ALPHA, ctx.ONE_MINUS_SRC_ALPHA);
    ctx.useProgram(program);
    // Set every sampler's unit once, after link: the 3D samplers default to unit 0, which would collide
    // with `uTex` on the first stage-off draw (GL_INVALID_OPERATION).
    ctx.uniform1i(uTex, PICTURE_UNIT);
    ctx.uniform1i(uInput.lut, INPUT_LUT_UNIT);
    ctx.bindVertexArray(vao);
    return true;
  };

  const releaseFxTargets = () => {
    if (!fxTargets) return;
    for (const t of fxTargets.textures) gl!.deleteTexture(t);
    for (const f of fxTargets.fbos) gl!.deleteFramebuffer(f);
    fxTargets = null;
  };
  const fxTargetsFor = (w: number, h: number) => {
    if (fxTargets && fxTargets.w === w && fxTargets.h === h) return fxTargets;
    releaseFxTargets();
    const make = () => {
      const texture = gl!.createTexture()!;
      gl!.bindTexture(gl!.TEXTURE_2D, texture);
      gl!.texImage2D(gl!.TEXTURE_2D, 0, gl!.RGBA, w, h, 0, gl!.RGBA, gl!.UNSIGNED_BYTE, null);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MIN_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MAG_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_S, gl!.CLAMP_TO_EDGE);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_T, gl!.CLAMP_TO_EDGE);
      const fbo = gl!.createFramebuffer()!;
      gl!.bindFramebuffer(gl!.FRAMEBUFFER, fbo);
      gl!.framebufferTexture2D(gl!.FRAMEBUFFER, gl!.COLOR_ATTACHMENT0, gl!.TEXTURE_2D, texture, 0);
      return { texture, fbo };
    };
    const a = make();
    const b = make();
    gl!.bindFramebuffer(gl!.FRAMEBUFFER, null);
    fxTargets = { w, h, textures: [a.texture, b.texture], fbos: [a.fbo, b.fbo] };
    return fxTargets;
  };

  /**
   * Runs the clip's effect chain in the render's geometry. The layer (crop, fit, transform, grade)
   * is drawn into a canvas-shaped working target first - an untransformed clip padded with opaque
   * black bars, a transformed one on transparency - then each effect runs on that, in list order,
   * with strengths in output pixels (the canvas is the 1920-long-side reference). Returns the
   * texture to composite full-canvas with its rows flipped (`flipV`), or `null` to draw normally.
   */
  const applyEffects = (layer: CompositorLayer, source: WebGLTexture, fit: [number, number], srcSize: [number, number], pictureWindow: SourceWindow): WebGLTexture | null => {
    const effects = layer.effects;
    if (!fxProgram || !effects || effects.length === 0 || canvasWidth <= 0 || canvasHeight <= 0) return null;
    const plan = planEffectPasses(effects, canvasWidth, canvasHeight);
    if (plan.passes.length === 0) return null;
    const targets = fxTargetsFor(plan.width, plan.height);
    gl!.disable(gl!.BLEND);
    gl!.viewport(0, 0, plan.width, plan.height);
    gl!.bindFramebuffer(gl!.FRAMEBUFFER, targets.fbos[0]);
    // The render pads an untransformed clip to the canvas with opaque black; a transformed one has no pad.
    const padded = layer.padded ?? layer.transform === undefined;
    // The render swaps the opaque pad for a transparent one when the clip carries alpha (a mask, keyer or corner pin:
    // RegionFilterGraphBuilder `alpha && UsesP4Fit` -> TransparentFit; alpha = keyer, mask or corner pin): geometry stays padded, the bars do not paint.
    gl!.clearColor(0, 0, 0, padded && !layer.matte && !layer.keyer && !layer.shape && layer.pin === undefined ? 1 : 0);
    gl!.clear(gl!.COLOR_BUFFER_BIT);
    drawQuad(source, 1, layer.transform, layer.grade, fit, false, layer.matte, srcSize, layer, pictureWindow, layer.shape);
    // The vignette and the pixel grid use the picture's own frame: the canvas when padded, else the placed picture.
    let vig: [number, number, number, number] = [0.5, 0.5, 0.5, 0.5];
    if (layer.transform !== undefined) {
      vig = [0.5 + layer.transform.x, 0.5 - layer.transform.y, 0.5 * layer.transform.scale * fit[0], 0.5 * layer.transform.scale * fit[1]];
    } else if (!padded) {
      vig = [0.5, 0.5, 0.5 * fit[0], 0.5 * fit[1]];
    }
    const origin: [number, number] = padded ? [0, 0] : [vig[0] - vig[2], 1 - vig[1] - vig[3]];
    let input = targets.textures[0];
    let next = 1;
    gl!.useProgram(fxProgram);
    gl!.uniform1f(fxLoc.seed, layer.fxSeed ?? 0);
    for (const pass of plan.passes) {
      gl!.bindFramebuffer(gl!.FRAMEBUFFER, targets.fbos[next]);
      activate(PICTURE_UNIT);
      gl!.bindTexture(gl!.TEXTURE_2D, input);
      switch (pass.kind) {
        case 'blurH':
        case 'blurV':
          gl!.uniform1i(fxLoc.mode, 0);
          gl!.uniform1f(fxLoc.sigma, pass.value);
          gl!.uniform2f(fxLoc.step, pass.kind === 'blurH' ? 1 / plan.width : 0, pass.kind === 'blurV' ? 1 / plan.height : 0);
          break;
        case 'pixelate': {
          gl!.uniform1i(fxLoc.mode, 1);
          // Blocks are output pixels at the reference size (canvas long side = 1920), like pixelize.
          const long = Math.max(canvasWidth, canvasHeight);
          gl!.uniform2f(fxLoc.origin, origin[0], origin[1]);
          gl!.uniform2f(fxLoc.cells, (EFFECT_REF_LONG_SIDE * (canvasWidth / long)) / pass.value, (EFFECT_REF_LONG_SIDE * (canvasHeight / long)) / pass.value);
          break;
        }
        case 'mono':
          gl!.uniform1i(fxLoc.mode, 2);
          gl!.uniform1f(fxLoc.amount, pass.value);
          break;
        case 'sharpen': {
          // The 5x5 kernel is defined at the render's canvas size: its tap spacing scales with the working size, like blur's sigma.
          const scale = Math.max(plan.width, plan.height) / EFFECT_REF_LONG_SIDE;
          gl!.uniform1i(fxLoc.mode, 4);
          gl!.uniform1f(fxLoc.amount, pass.value);
          gl!.uniform2f(fxLoc.step, scale / plan.width, scale / plan.height);
          break;
        }
        case 'grain':
          gl!.uniform1i(fxLoc.mode, 5);
          gl!.uniform1f(fxLoc.amount, pass.value / 255);
          break;
        case 'vignette':
          gl!.uniform1i(fxLoc.mode, 3);
          gl!.uniform1f(fxLoc.amount, pass.value);
          gl!.uniform2f(fxLoc.aspect, plan.width, plan.height);
          gl!.uniform4f(fxLoc.vig, vig[0], vig[1], vig[2], vig[3]);
          break;
      }
      gl!.drawArrays(gl!.TRIANGLES, 0, 6);
      input = targets.textures[next];
      next = 1 - next;
    }
    gl!.useProgram(program);
    bindMain();
    gl!.viewport(0, 0, canvasWidth, canvasHeight);
    gl!.enable(gl!.BLEND);
    return input;
  };

  const makePair = (w: number, h: number): TargetPair => {
    const make = () => {
      const texture = gl!.createTexture()!;
      gl!.bindTexture(gl!.TEXTURE_2D, texture);
      gl!.texImage2D(gl!.TEXTURE_2D, 0, gl!.RGBA, w, h, 0, gl!.RGBA, gl!.UNSIGNED_BYTE, null);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MIN_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MAG_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_S, gl!.CLAMP_TO_EDGE);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_T, gl!.CLAMP_TO_EDGE);
      const fbo = gl!.createFramebuffer()!;
      gl!.bindFramebuffer(gl!.FRAMEBUFFER, fbo);
      gl!.framebufferTexture2D(gl!.FRAMEBUFFER, gl!.COLOR_ATTACHMENT0, gl!.TEXTURE_2D, texture, 0);
      return { texture, fbo };
    };
    const a = make();
    const b = make();
    return { w, h, textures: [a.texture, b.texture], fbos: [a.fbo, b.fbo] };
  };
  const releasePair = (pair: TargetPair | null) => {
    if (!pair) return;
    for (const t of pair.textures) gl!.deleteTexture(t);
    for (const f of pair.fbos) gl!.deleteFramebuffer(f);
  };
  const pairFor = (slot: 'full' | 'low', w: number, h: number): TargetPair => {
    const hit = regionPairs[slot];
    if (hit && hit.w === w && hit.h === h) return hit;
    releasePair(hit);
    regionPairs[slot] = makePair(w, h);
    return regionPairs[slot]!;
  };
  const releaseRegionTargets = () => {
    releasePair(regionPairs.full);
    releasePair(regionPairs.low);
    regionPairs.full = null;
    regionPairs.low = null;
  };
  /** A 2D texture on `unit` for `image` (uploaded once per distinct image), or the black texture when there is none. */
  const bindMaskFrame = (unit: number, key: string | null, image: CanvasImageSource | null) => {
    activate(unit);
    if (key === null || image === null) {
      if (!blackTexture) {
        blackTexture = gl!.createTexture();
        gl!.bindTexture(gl!.TEXTURE_2D, blackTexture);
        gl!.texImage2D(gl!.TEXTURE_2D, 0, gl!.RGBA, 1, 1, 0, gl!.RGBA, gl!.UNSIGNED_BYTE, new Uint8Array([0, 0, 0, 255]));
        gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MIN_FILTER, gl!.NEAREST);
        gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MAG_FILTER, gl!.NEAREST);
      }
      gl!.bindTexture(gl!.TEXTURE_2D, blackTexture);
      return;
    }
    let entry = maskTextures.get(key);
    if (!entry) {
      const texture = gl!.createTexture()!;
      gl!.bindTexture(gl!.TEXTURE_2D, texture);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MIN_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MAG_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_S, gl!.CLAMP_TO_EDGE);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_T, gl!.CLAMP_TO_EDGE);
      entry = { texture, uploaded: null };
      maskTextures.set(key, entry);
    }
    gl!.bindTexture(gl!.TEXTURE_2D, entry.texture);
    if (entry.uploaded !== image) {
      try {
        gl!.texImage2D(gl!.TEXTURE_2D, 0, gl!.RGBA, gl!.RGBA, gl!.UNSIGNED_BYTE, image as TexImageSource);
        entry.uploaded = image;
      } catch {
        // a frame that cannot be uploaded keeps the previous mask picture
      }
    }
  };

  /**
   * Runs the clip's region effects on its SOURCE picture, in order, each through its matte, and returns the texture the
   * layer then draws from (image orientation, like an uploaded frame - no flip). Boxes give a rounded, feathered box; a mask
   * video gives the decoded outline. Without the region program the source is returned unchanged.
   */
  const applyRegionEffects = (layer: CompositorLayer, source: WebGLTexture, srcW: number, srcH: number, usedMasks: Set<string>): WebGLTexture => {
    const list = layer.regionEffects;
    if (!list || list.length === 0 || !regionProgram || !fxProgram || srcW <= 0 || srcH <= 0) return source;
    const long = Math.max(srcW, srcH);
    const k = Math.min(1, REGION_MAX_LONG_SIDE / long);
    const fw = Math.max(1, Math.round(srcW * k));
    const fh = Math.max(1, Math.round(srcH * k));
    const full = pairFor('full', fw, fh);
    gl!.disable(gl!.BLEND);
    let current = source;
    let slot = 0;
    for (const e of list.slice(0, 5)) {
      const short = Math.min(fw, fh);
      let alt: WebGLTexture | null = null;
      if (e.effect === 'blur') {
        const plan = planRegionBlur(fw, fh, e.strength);
        const low = pairFor('low', plan.width, plan.height);
        gl!.useProgram(fxProgram);
        gl!.uniform1i(fxLoc.mode, 0);
        gl!.uniform1f(fxLoc.sigma, plan.sigma);
        gl!.viewport(0, 0, plan.width, plan.height);
        gl!.bindFramebuffer(gl!.FRAMEBUFFER, low.fbos[0]);
        activate(PICTURE_UNIT);
        gl!.bindTexture(gl!.TEXTURE_2D, current);
        gl!.uniform2f(fxLoc.step, 1 / plan.width, 0);
        gl!.drawArrays(gl!.TRIANGLES, 0, 6);
        gl!.bindFramebuffer(gl!.FRAMEBUFFER, low.fbos[1]);
        gl!.bindTexture(gl!.TEXTURE_2D, low.textures[0]);
        gl!.uniform2f(fxLoc.step, 0, 1 / plan.height);
        gl!.drawArrays(gl!.TRIANGLES, 0, 6);
        alt = low.textures[1];
      }
      gl!.useProgram(regionProgram);
      gl!.viewport(0, 0, fw, fh);
      gl!.bindFramebuffer(gl!.FRAMEBUFFER, full.fbos[slot]);
      activate(PICTURE_UNIT);
      gl!.bindTexture(gl!.TEXTURE_2D, current);
      activate(REGION_ALT_UNIT);
      gl!.bindTexture(gl!.TEXTURE_2D, alt ?? current);
      const modes: Record<string, number> = { blur: 0, pixelate: 1, highlight: 2, spotlight: 3, colorpop: 4, outline: 5, cutout: 6 };
      gl!.uniform1i(regionLoc.mode, modes[e.effect] ?? 0);
      const dim = dimmingOf(e.effect, e.strength);
      if (e.effect === 'pixelate') gl!.uniform4f(regionLoc.p, pixelBlockPx(short, e.strength), 0, 0, 0);
      else if (e.effect === 'highlight') gl!.uniform4f(regionLoc.p, dim.gain, dim.contrast, dim.dim, 0);
      else if (e.effect === 'spotlight') gl!.uniform4f(regionLoc.p, dim.dim, 0, 0, 0);
      else if (e.effect === 'outline') gl!.uniform4f(regionLoc.p, outlineBandPx(short, e.strength), 0, 0, 0);
      else gl!.uniform4f(regionLoc.p, 0, 0, 0, 0);
      gl!.uniform2f(regionLoc.size, fw, fh);
      const m = e.matte;
      if (m.kind === 'maskVideo') {
        gl!.uniform1f(regionLoc.src, 1);
        gl!.uniform1i(regionLoc.maskN, Math.min(4, m.frames.length));
        gl!.uniform1f(regionLoc.invert, m.invert ? 1 : 0);
        const proxyScale = m.sourceWidth && m.sourceWidth > 0 ? fw / m.sourceWidth : 1;
        gl!.uniform1f(regionLoc.feather, m.featherPx * proxyScale);
        for (let i = 0; i < 4; i += 1) {
          const frame = m.frames[i];
          const key = frame ? `${layer.layerId}\u0000${e.key}\u0000${i}` : null;
          if (key) usedMasks.add(key);
          bindMaskFrame(REGION_MASK_UNIT + i, key, frame ? frame.image : null);
        }
        gl!.uniform1i(regionLoc.boxN, 0);
      } else {
        gl!.uniform1f(regionLoc.src, 0);
        gl!.uniform1i(regionLoc.maskN, 0);
        for (let i = 0; i < 4; i += 1) bindMaskFrame(REGION_MASK_UNIT + i, null, null);
        regionBoxes.fill(0);
        const n = Math.min(MAX_MATTE_BOXES, m.matte.boxes.length);
        for (let i = 0; i < n; i += 1) regionBoxes.set([m.matte.boxes[i].x, m.matte.boxes[i].y, m.matte.boxes[i].w, m.matte.boxes[i].h], i * 4);
        gl!.uniform1i(regionLoc.boxN, n);
        gl!.uniform4fv(regionLoc.boxes, regionBoxes);
        gl!.uniform1f(regionLoc.invert, m.matte.invert ? 1 : 0);
        const proxyScale = m.matte.sourceWidth && m.matte.sourceWidth > 0 ? fw / m.matte.sourceWidth : 1;
        // The same ramp as the segmentation box matte: 3.2 sigma wide (equal 10-90% width to the render's gaussian edge).
        gl!.uniform1f(regionLoc.feather, 3.2 * m.matte.featherPx * proxyScale);
      }
      gl!.uniform1f(regionLoc.round, REGION_BOX_ROUNDNESS);
      gl!.drawArrays(gl!.TRIANGLES, 0, 6);
      current = full.textures[slot];
      slot = 1 - slot;
    }
    activate(PICTURE_UNIT);
    gl!.useProgram(program);
    bindMain();
    gl!.viewport(0, 0, canvasWidth, canvasHeight);
    gl!.enable(gl!.BLEND);
    return current;
  };

  /**
   * Draws a layer through the pin pass: the layer (placed, graded, masked) is rendered on a canvas-shaped transparent
   * target unless `placed` already is one (the effects pass's output, rows bottom-up like any target), then warped onto
   * the quad. A degenerate quad or a missing pin program draws nothing / the unpinned picture.
   */
  const drawPinned = (layer: CompositorLayer, texture: WebGLTexture, placed: WebGLTexture | null, fit: [number, number], srcSize: [number, number], pictureWindow: SourceWindow, opacity: number): void => {
    const inverse = layer.pin ? pinInverseColumnMajor(layer.pin) : null;
    if (!layer.pin || !inverse) return; // a pin the render cannot build leaves nothing on screen
    if (!pinProgram || canvasWidth <= 0 || canvasHeight <= 0) {
      if (placed) drawQuad(placed, opacity, undefined, undefined, [1, 1], true);
      else drawQuad(texture, opacity, layer.transform, layer.grade, fit, false, layer.matte, srcSize, layer, pictureWindow, layer.shape);
      return;
    }
    let source = placed;
    if (!source) {
      const targets = fxTargetsFor(canvasWidth, canvasHeight);
      gl!.disable(gl!.BLEND);
      gl!.viewport(0, 0, canvasWidth, canvasHeight);
      gl!.bindFramebuffer(gl!.FRAMEBUFFER, targets.fbos[0]);
      gl!.clearColor(0, 0, 0, 0);
      gl!.clear(gl!.COLOR_BUFFER_BIT);
      drawQuad(texture, 1, layer.transform, layer.grade, fit, false, layer.matte, srcSize, layer, pictureWindow, layer.shape);
      source = targets.textures[0];
    }
    const px = placed ? fxTargets : null;
    gl!.useProgram(pinProgram);
    bindMain();
    gl!.viewport(0, 0, canvasWidth, canvasHeight);
    gl!.enable(gl!.BLEND);
    activate(PICTURE_UNIT);
    gl!.bindTexture(gl!.TEXTURE_2D, source);
    gl!.uniformMatrix3fv(pinLoc.inv, false, inverse);
    gl!.uniform1f(pinLoc.opacity, opacity);
    // The picture's own pixel in picture fractions: the target's size (the effects target when the layer ran effects).
    gl!.uniform2f(pinLoc.px, 1 / Math.max(1, px ? px.w : canvasWidth), 1 / Math.max(1, px ? px.h : canvasHeight));
    gl!.drawArrays(gl!.TRIANGLES, 0, 6);
    gl!.useProgram(program);
  };

  if (!initGl()) return null;

  // Chrome restores a lost context only if the page cancels the loss event.
  const handleLost = (event: Event) => {
    event.preventDefault();
    lost = true;
    hooks?.onContextLost?.();
  };
  const handleRestored = () => {
    // Every texture, buffer and program died with the context: forget the handles, then rebuild.
    textures.clear();
    fxTargets = null;
    regionPairs.full = null;
    regionPairs.low = null;
    maskTextures.clear();
    blackTexture = null;
    lutTextures.clear();
    dummy3d = null;
    gradePass = null;
    gradePassFailed = false;
    xfadePass = null;
    xfadePassFailed = false;
    legTarget = null;
    viewTarget = null;
    viewHasFrame = false;
    overlayTexture = null;
    overlayUploaded = false;
    blend.reset();
    const ok = initGl();
    if (ok) {
      ctx.viewport(0, 0, canvasWidth, canvasHeight);
      lost = false;
    } else {
      console.error('[preview] compositor rebuild after context restore failed');
    }
    hooks?.onContextRestored?.(ok);
  };
  canvas.addEventListener?.('webglcontextlost', handleLost);
  canvas.addEventListener?.('webglcontextrestored', handleRestored);

  // L3 grade pass (curves, LUT nodes, secondaries): built on first use, dropped with the context.
  let gradePass: GradePass | null = null;
  let gradePassFailed = false;
  const gradeHost: GradePassHost = {
    lut3d: (key, size, texels, unit) => lut3dTexture(key, size, texels, unit),
    lut1d: (key, size, texels) => lut1dTexture(key, size, texels),
    inputTexture: (layer) => {
      const plan = planInputStage(layer);
      const id = layer.inputProfile;
      return plan.on && id ? lut3dTexture(plan.textureKey as string, CAMERA_PROFILE_TABLE_DEFAULT_SIZE, () => cameraProfileTableRgba16f(id, CAMERA_PROFILE_TABLE_DEFAULT_SIZE), INPUT_LUT_UNIT) : null;
    },
    dummy3d: (unit) => {
      dummy3dTexture(unit);
    },
  };
  // T-008b: the xfade transitions, built on first use.
  let xfadePass: XfadePass | null = null;
  let xfadePassFailed = false;
  const xfadeFor = (): XfadePass | null => {
    if (!xfadePass && !xfadePassFailed) {
      xfadePass = createXfadePass(ctx);
      xfadePassFailed = xfadePass === null;
    }
    return xfadePass;
  };
  /** Points drawing at an xfade leg's target (cleared to opaque black, as the render's padded legs are); null returns to the main target. */
  const enterLeg = (target: WebGLFramebuffer | null) => {
    legTarget = target;
    bindMain();
    gl!.viewport(0, 0, canvasWidth, canvasHeight);
    if (target) {
      gl!.clearColor(0, 0, 0, 1);
      gl!.clear(gl!.COLOR_BUFFER_BIT);
    }
  };
  /** The layer's picture graded by the pass (source-sized texture), or null when the layer has no rich grade / the pass cannot run. */
  const richGradeFor = (layer: CompositorLayer, entry: { texture: WebGLTexture; width: number; height: number }): WebGLTexture | null => {
    if ((!layer.richGrade || layer.richGrade.length === 0) && !layer.keyer) return null;
    if (!gradePass && !gradePassFailed) {
      gradePass = createGradePass(ctx, gradeHost);
      gradePassFailed = gradePass === null;
    }
    if (!gradePass) return null;
    const out = gradePass.run(entry.texture, entry.width, entry.height, layer.richGrade ?? [], layer, layer.keyer);
    gl!.useProgram(program);
    gl!.bindVertexArray(vao);
    bindMain();
    gl!.viewport(0, 0, canvasWidth, canvasHeight);
    gl!.enable(gl!.BLEND);
    return out;
  };

  /** T-008c: grades and effects the composite below (the blend pass's current target) in place; no backdrop pass = nothing to adjust. */
  const applyAdjustment = (layer: CompositorLayer) => {
    const back = blend.currentTexture();
    if (!back) return;
    const graded = (layer.richGrade?.length ?? 0) > 0 ? richGradeFor({ ...layer, keyer: undefined }, { texture: back, width: canvasWidth, height: canvasHeight }) : null;
    const source = graded ?? back;
    // The effect passes read their source as a top-down picture; the backdrop is bottom-up, so their result is drawn unflipped.
    const fx = applyEffects(layer, source, [1, 1], [canvasWidth, canvasHeight], FULL_WINDOW);
    if (fx) {
      blend.replaceBegin();
      drawQuad(fx, 1, undefined, undefined, [1, 1], false);
      blend.replaceEnd();
    } else if (source !== back) blend.replaceWith(source);
    bindMain();
  };

  /** Composites the layers (onto the offscreen target when the viewing pass is active), then runs the viewing pass. */
  const drawLayers = (layers: readonly CompositorLayer[], applyViewingLut: boolean): DrawResult => {
    viewActive = planViewingPass(viewingLut, applyViewingLut) && ensureViewProgram();
    if (viewActive) viewTargetFor(canvasWidth, canvasHeight);
    const blending = layers.some((l) => isBlendingWord(l.blendMode) || l.adjustment) && blend.begin(canvasWidth, canvasHeight);
    bindMain();
    gl!.clearColor(0, 0, 0, 1);
    gl!.clear(gl!.COLOR_BUFFER_BIT);
    overlayShown = false;
    let repeats = 0;
    const blank: string[] = [];
    const used = new Set<string>();
    const usedMasks = new Set<string>();
    let pass: XfadePass | null = null;
    for (const layer of layers) {
      if (layer.adjustment) {
        if (blending) applyAdjustment(layer);
        continue;
      }
      const key = textureKey(layer);
      used.add(key);
      // T-008b: an xfade pair draws each leg onto its own target; the `to` leg then mixes both onto the main target.
      const leg = layer.xfade && xfadeIndexOf(layer.xfade.word) >= 0 ? layer.xfade : null;
      const legPass = leg ? (pass ??= xfadeFor()) : null;
      if (leg && legPass) enterLeg(legPass.targets(canvasWidth, canvasHeight).fbos[leg.role === 'from' ? 0 : 1]);
      const entry = textureFor(key);
      if (layer.image && layer.image === entry.uploaded && entry.hasImage) {
        // The very image already on this texture (a held frame at a render rate above the source's): no re-upload.
      } else if (layer.image) {
        gl!.bindTexture(gl!.TEXTURE_2D, entry.texture);
        try {
          gl!.texImage2D(gl!.TEXTURE_2D, 0, gl!.RGBA, gl!.RGBA, gl!.UNSIGNED_BYTE, layer.image as TexImageSource);
          entry.uploaded = layer.image;
          entry.hasImage = true;
          [entry.width, entry.height] = imageSize(layer.image);
        } catch {
          // An image that cannot be uploaded (e.g. a closed VideoFrame) is a repeat.
          repeats += 1;
        }
      } else if (entry.hasImage) {
        repeats += 1;
      }
      // Region effects change the source picture itself (before crop, transform, grade and the clip's own effects).
      let sourceTexture = entry.texture;
      if (entry.hasImage && layer.regionEffects && layer.regionEffects.length > 0) sourceTexture = applyRegionEffects(layer, entry.texture, entry.width, entry.height, usedMasks);
      // A reframe crops the SOURCE to the canvas aspect first; the clip's crop and the fit then work on that picture.
      const pictureWindow = reframeWindow(layer.reframe, entry.width, entry.height, canvasWidth, canvasHeight);
      const fit = fitFor(layer, entry.width * pictureWindow.w, entry.height * pictureWindow.h, canvasWidth, canvasHeight);
      if (entry.hasImage) {
        // No xfade program (it failed to build): the incoming leg dissolves in, as before T-008b.
        const opacity = Math.min(1, Math.max(0, leg && !legPass && leg.role === 'to' ? layer.opacity * leg.t : layer.opacity));
        // L3: a rich grade runs first, on the (region-effected) source picture, camera table included; the layer then carries no grade or input stage.
        const graded = richGradeFor(layer, { texture: sourceTexture, width: entry.width, height: entry.height });
        const drawn: CompositorLayer = graded ? { ...layer, grade: undefined, inputProfile: undefined } : layer;
        const source = graded ?? sourceTexture;
        const blendWord = blending && !(leg && legPass) && isBlendingWord(layer.blendMode) ? layer.blendMode : undefined;
        if (blendWord) blend.layerBegin();
        const fx = applyEffects(drawn, source, fit, [entry.width, entry.height], pictureWindow);
        if (layer.pin !== undefined) drawPinned(drawn, source, fx, fit, [entry.width, entry.height], pictureWindow, opacity);
        // With effects the layer was already placed, graded and effected on the canvas: composite it full-canvas.
        else if (fx) drawQuad(fx, opacity, undefined, undefined, [1, 1], true);
        else drawQuad(source, opacity, drawn.transform, drawn.grade, fit, false, drawn.matte, [entry.width, entry.height], drawn, pictureWindow, drawn.shape);
        if (blendWord) {
          blend.layerEnd(blendWord);
          bindMain();
        }
      } else blank.push(layer.layerId);
      if (leg && legPass) {
        enterLeg(null);
        if (leg.role === 'to') {
          legPass.draw(xfadeIndexOf(leg.word), 1 - leg.t, canvasWidth, canvasHeight);
          gl!.useProgram(program);
          gl!.bindVertexArray(vao);
          gl!.enable(gl!.BLEND);
        } else {
          gl!.useProgram(program);
          gl!.bindVertexArray(vao);
        }
      }
    }
    // A texture whose content left the screen is never shown again: free it.
    for (const [key, entry] of textures) {
      if (used.has(key)) continue;
      gl!.deleteTexture(entry.texture);
      textures.delete(key);
    }
    for (const [key, entry] of maskTextures) {
      if (usedMasks.has(key)) continue;
      gl!.deleteTexture(entry.texture);
      maskTextures.delete(key);
    }
    if (blending) blend.end(bindMain);
    viewHasFrame = viewActive;
    lastLayers = layers.map((l) => ({ ...l, image: null }));
    presentView(true);
    return { repeats, blank };
  };

  const drawOverlayInner = (overlay: CanvasImageSource, options?: { cached?: boolean }) => {
    if (options?.cached && overlayTexture && overlayUploaded) {
      drawQuad(overlayTexture, 1);
      return;
    }
    if (!overlayTexture) {
      overlayTexture = gl!.createTexture();
      gl!.bindTexture(gl!.TEXTURE_2D, overlayTexture);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MIN_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_MAG_FILTER, gl!.LINEAR);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_S, gl!.CLAMP_TO_EDGE);
      gl!.texParameteri(gl!.TEXTURE_2D, gl!.TEXTURE_WRAP_T, gl!.CLAMP_TO_EDGE);
    }
    gl!.bindTexture(gl!.TEXTURE_2D, overlayTexture);
    try {
      gl!.texImage2D(gl!.TEXTURE_2D, 0, gl!.RGBA, gl!.RGBA, gl!.UNSIGNED_BYTE, overlay as TexImageSource);
      overlayUploaded = true;
    } catch {
      return;
    }
    drawQuad(overlayTexture!, 1);
  };

  const compositor: Compositor = {
    renderer,
    resize(width, height) {
      const changed = canvas.width !== width || canvas.height !== height;
      if (changed) viewHasFrame = false;
      if (canvas.width !== width) canvas.width = width;
      if (canvas.height !== height) canvas.height = height;
      aspect = height > 0 ? width / height : 16 / 9;
      canvasWidth = width;
      canvasHeight = height;
      if (lost) return;
      gl!.viewport(0, 0, width, height);
      // Setting a canvas's size clears it. The picture that was on screen is repainted at the new size from the held
      // textures (no new decode), so an adaptive size change while playing, or going back to full size on pause, never flashes black.
      if (changed && lastLayers) {
        const hadOverlay = overlayShown;
        drawLayers(lastLayers, true);
        if (hadOverlay && overlayTexture) {
          bindMain();
          drawQuad(overlayTexture, 1);
          overlayShown = true;
          presentView(true);
        }
      }
    },
    draw(layers, options) {
      if (lost) return { repeats: 0, blank: [] };
      return drawLayers(layers, options?.applyViewingLut ?? true);
    },
    setViewingLut(lut) {
      const before = viewingLut;
      viewingLut = lut;
      if (lost || (before?.key ?? null) === (lut?.key ?? null)) return;
      // Repaint what is on screen with the new view (held textures: no new decode), so a paused frame changes at once.
      if (lastLayers) {
        drawLayers(lastLayers, true);
        if (overlayShown && overlayTexture) {
          bindMain();
          drawQuad(overlayTexture, 1);
          presentView(true);
        }
      }
    },
    withCleanPicture(read) {
      if (lost || !viewActive || !viewHasFrame) return read();
      presentView(false);
      try {
        return read();
      } finally {
        presentView(true);
      }
    },
    drawOverlay(overlay, options) {
      if (!overlay || lost) return;
      // Titles and captions go on the same target the layers did, then the viewing pass runs again over everything.
      bindMain();
      drawOverlayInner(overlay, options);
      overlayShown = true;
      presentView(true);
    },
    clear() {
      if (lost) return;
      viewActive = false;
      viewHasFrame = false;
      lastLayers = null;
      overlayShown = false;
      gl!.bindFramebuffer(gl!.FRAMEBUFFER, null);
      gl!.clearColor(0, 0, 0, 1);
      gl!.clear(gl!.COLOR_BUFFER_BIT);
    },
    dispose() {
      canvas.removeEventListener?.('webglcontextlost', handleLost);
      canvas.removeEventListener?.('webglcontextrestored', handleRestored);
      if (lost) {
        textures.clear();
        return;
      }
      for (const entry of textures.values()) gl!.deleteTexture(entry.texture);
      textures.clear();
      for (const t of lutTextures.values()) gl!.deleteTexture(t);
      lutTextures.clear();
      if (dummy3d) gl!.deleteTexture(dummy3d);
      dummy3d = null;
      releaseViewTarget();
      if (viewProgram) gl!.deleteProgram(viewProgram);
      if (overlayTexture) gl!.deleteTexture(overlayTexture);
      releaseFxTargets();
      blend.dispose();
      gradePass?.dispose();
      xfadePass?.dispose();
      gradePass = null;
      gl!.deleteProgram(program);
      if (fxProgram) gl!.deleteProgram(fxProgram);
      if (pinProgram) gl!.deleteProgram(pinProgram);
      if (regionProgram) gl!.deleteProgram(regionProgram);
      releaseRegionTargets();
      for (const entry of maskTextures.values()) gl!.deleteTexture(entry.texture);
      maskTextures.clear();
      if (blackTexture) gl!.deleteTexture(blackTexture);
      gl!.deleteBuffer(vbo);
      gl!.deleteVertexArray(vao);
      cleanPictureOf.delete(canvas);
    },
  };
  cleanPictureOf.set(canvas, compositor.withCleanPicture);
  // Export-like readbacks of this canvas (a still, a reference still, a thumbnail) never carry the viewing LUT.
  const rawToDataURL = typeof canvas.toDataURL === 'function' ? canvas.toDataURL.bind(canvas) : null;
  if (rawToDataURL) canvas.toDataURL = ((...args: Parameters<HTMLCanvasElement['toDataURL']>) => compositor.withCleanPicture(() => rawToDataURL(...args))) as HTMLCanvasElement['toDataURL'];
  const rawToBlob = typeof canvas.toBlob === 'function' ? canvas.toBlob.bind(canvas) : null;
  if (rawToBlob) {
    // `toBlob` snapshots the bitmap synchronously, so the clean picture only needs to be on the canvas for the call.
    canvas.toBlob = ((...args: Parameters<HTMLCanvasElement['toBlob']>) => compositor.withCleanPicture(() => rawToBlob(...args))) as HTMLCanvasElement['toBlob'];
  }
  return compositor;
}

const cleanPictureOf = new WeakMap<HTMLCanvasElement, Compositor['withCleanPicture']>();

/**
 * Reads `read` against `canvas` with the viewing LUT off when `canvas` is a compositor canvas (scopes, look thumbnails,
 * stats of a still); any other canvas just runs it.
 */
export function readCleanPicture<T>(canvas: HTMLCanvasElement, read: () => T): T {
  const clean = cleanPictureOf.get(canvas);
  return clean ? clean(read) : read();
}

/** True for a CPU rasteriser (SwiftShader, llvmpipe, Mesa softpipe, a generic "Software" renderer): uploads and blends run on the CPU. */
export function isSoftwareRenderer(renderer: string | null): boolean {
  return renderer !== null && /swiftshader|llvmpipe|softpipe|software/i.test(renderer);
}
