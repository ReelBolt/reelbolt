/**
 * What the grade pass draws for one clip (L3): the clip's enabled nodes as stages, in the export's order
 * (`GradeFilterBuilder.Build`): per node the primaries, then the curves, then the LUT, then - when the node
 * carries a qualifier or a power window - the merge of that correction back through the matte.
 *
 * Pure planning: the GL side (`grade-pass.ts`) executes it, the CPU reference below is what the parity tests pin.
 * A clip whose nodes are all plain primaries (at most GRADE_NODE_BOUND of them) keeps the compositor's single-draw
 * shader; everything else takes this pass, which has no node limit beyond GRADE_NODE_MAX.
 */

import type { AssetRef, GradeNode, GradePrimaries, GradeStack } from '../types';
import { floatToHalf, toRgba16fTexture3dData } from '../color/cube-lut-loader';
import { bakedComposite, CUBE_LUT_BAKE_SIZE, sampleCubeLut, type CubeLut } from '../color/cube-lut';
import { applyCurveBake, bakeCurves, type CurveBake } from '../color/grade-curves';
import { hasCurves, isIdentityPrimaries } from '../color/grade';
import { maskedMerge, qualifierParamsOf, secondaryMatte, windowParamsOf, type QualifierParams, type WindowParams } from '../color/grade-secondary';
import { applyPreviewGradeNode, foldPrimaries, GRADE_NODE_BOUND, GRADE_NODE_MAX, previewGradeNodeOf, type PreviewGradeNode } from '../color/preview-grade';
import { oneDRgba16f, oneDTextureSize } from '../color/viewer-color';
import { evaluateKeyframed } from './scheduler';

type V3 = [number, number, number];

const clamp01 = (v: number) => (v < 0 ? 0 : v > 1 ? 1 : v);

/** A parsed `.cube` the planner can read: the table and a key that changes when the file's content does. */
export interface ResolvedGradeLut {
  /** Identity of the content (asset id + version/size): the same key is the same texture. */
  key: string;
  lut: CubeLut;
}

export type GradeLutResolver = (file: AssetRef) => ResolvedGradeLut | null;

/** How a node's LUT runs on the GPU. */
export interface LutStagePlan {
  /** 0 = nothing, 1 = 3D (tetrahedral), 2 = 1D (linear), 3 = 1D then 3D (shaper). */
  kind: 1 | 2 | 3;
  /** The node's intensity as written in the document (0..1). */
  intensity: number;
  /** Blend toward the unchanged pixel (the node's intensity), 1 = the LUT's output. */
  mix: number;
  /** The 3D stage: cache key (shares the viewing LUT's `view:` limit), grid size, texels (built only on a texture miss). */
  cube: { key: string; size: number; texels: () => Uint16Array } | null;
  /** The 1D stage: cache key, texture width, texels. */
  oneD: { key: string; size: number; texels: () => Uint16Array } | null;
}

/** One node, ready to draw. */
export interface RichGradeStage {
  primaries: PreviewGradeNode | null;
  curves: CurveBake | null;
  lut: LutStagePlan | null;
  qualifier: QualifierParams | null;
  window: WindowParams | null;
}

export interface RichGradePlan {
  stages: RichGradeStage[];
  /** LUT nodes whose file is not loaded yet: drawn WITHOUT the LUT until it arrives (the badge stays approximate). */
  pendingLuts: number;
  /** Nodes past GRADE_NODE_MAX folded into one extra last stage (approximate); 0 when everything fit. */
  folded: number;
  /** Past the cap, nodes with curves, a LUT or a secondary cannot fold: they are not drawn. */
  dropped: number;
}

const lutActive = (node: GradeNode): boolean => !!node.lut && (node.lut.intensity ?? 1) > 0;

const hasMatte = (node: GradeNode): boolean => qualifierParamsOf(node.qualifier) !== null || !!node.window?.shape;

/** A 3D table as RGBA half floats (alpha 1), R fastest, for `texImage3D(RGBA16F, HALF_FLOAT)`. */
function table3dRgba16f(data: Float32Array, size: number): Uint16Array {
  const texels = size * size * size;
  const out = new Uint16Array(texels * 4);
  const one = floatToHalf(1);
  for (let i = 0; i < texels; i += 1) {
    out[i * 4] = floatToHalf(data[i * 3] as number);
    out[i * 4 + 1] = floatToHalf(data[i * 3 + 1] as number);
    out[i * 4 + 2] = floatToHalf(data[i * 3 + 2] as number);
    out[i * 4 + 3] = one;
  }
  return out;
}

/** The GPU plan of one LUT at an intensity, mirroring `sampleCubeLut` (the engine's lut1d / lut3d tetrahedral chain). */
export function lutStagePlanOf(key: string, lut: CubeLut, intensity: number): LutStagePlan {
  const k = clamp01(intensity);
  const kind = lut.kind ?? '3d';
  const base = `view:node:${key}`;
  const oneD = lut.oneD
    ? { key: `view:1d:node:${key}`, size: oneDTextureSize(lut.oneD.size), texels: () => oneDRgba16f((lut.oneD as NonNullable<CubeLut['oneD']>).data, (lut.oneD as NonNullable<CubeLut['oneD']>).size) }
    : null;
  if (kind === '1d' && oneD) return { kind: 2, intensity: k, mix: k, cube: null, oneD };
  if (kind === 'shaper3d' && oneD) {
    if (k >= 1) return { kind: 3, intensity: k, mix: 1, cube: { key: base, size: lut.size, texels: () => toRgba16fTexture3dData(lut) }, oneD };
    // Below full intensity the engine writes ONE baked 3D table with the blend folded in.
    const size = CUBE_LUT_BAKE_SIZE;
    return { kind: 1, intensity: k, mix: 1, cube: { key: `${base}:k${Math.round(k * 1000)}`, size, texels: () => table3dRgba16f(bakedComposite(lut, k), size) }, oneD: null };
  }
  return { kind: 1, intensity: k, mix: k, cube: { key: base, size: lut.size, texels: () => toRgba16fTexture3dData(lut) }, oneD: null };
}

const curveBakes = new WeakMap<object, CurveBake | null>();

function bakedCurvesOf(node: GradeNode): CurveBake | null {
  if (!node.curves || !hasCurves(node)) return null;
  const hit = curveBakes.get(node.curves);
  if (hit !== undefined) return hit;
  const bake = bakeCurves(node.curves);
  curveBakes.set(node.curves, bake);
  return bake;
}

/**
 * True when the clip needs the grade pass rather than the compositor's single-draw shader: a node with curves, a LUT
 * or a secondary (qualifier / window), or more primaries nodes than the single-draw shader holds.
 */
export function needsGradePass(stacks: readonly (GradeStack | null | undefined)[]): boolean {
  let primaries = 0;
  for (const stack of stacks) {
    if (!stack || stack.enabled === false || !stack.nodes) continue;
    for (const node of stack.nodes) {
      if (node.enabled === false) continue;
      if (hasCurves(node) || lutActive(node)) return true;
      if (node.primaries && !isIdentityPrimaries(node.primaries)) {
        if (hasMatte(node)) return true;
        primaries += 1;
      }
    }
  }
  return primaries > GRADE_NODE_BOUND;
}

/**
 * The stages of the enabled stacks at timeline `frame` (group stack first, then the clip's), at most GRADE_NODE_MAX.
 * A node with no correction (identity primaries, no curves, no LUT) draws nothing, as the export skips it.
 */
export function richGradePlanOf(stacks: readonly (GradeStack | null | undefined)[], frame: number, resolveLut?: GradeLutResolver): RichGradePlan {
  const stages: RichGradeStage[] = [];
  let pendingLuts = 0;
  const overflow: GradePrimaries[] = [];
  let dropped = 0;
  for (const stack of stacks) {
    if (!stack || stack.enabled === false || !stack.nodes) continue;
    for (const node of stack.nodes) {
      if (node.enabled === false) continue;
      if (stages.length >= GRADE_NODE_MAX) {
        if (node.primaries && !isIdentityPrimaries(node.primaries) && !hasCurves(node) && !lutActive(node) && !hasMatte(node)) overflow.push(node.primaries);
        else if ((node.primaries && !isIdentityPrimaries(node.primaries)) || hasCurves(node) || lutActive(node)) dropped += 1;
        continue;
      }
      const primaries = node.primaries && !isIdentityPrimaries(node.primaries) ? previewGradeNodeOf(node.primaries) : null;
      const curves = bakedCurvesOf(node);
      let lut: LutStagePlan | null = null;
      if (node.lut && lutActive(node)) {
        const resolved = resolveLut?.(node.lut.file) ?? null;
        if (resolved) lut = lutStagePlanOf(resolved.key, resolved.lut, node.lut.intensity ?? 1);
        else pendingLuts += 1;
      }
      if (!primaries && !curves && !lut) continue;
      const window = node.window
        ? windowParamsOf(node.window, evaluateKeyframed(node.window.offsetX ?? null, frame, 0), evaluateKeyframed(node.window.offsetY ?? null, frame, 0))
        : null;
      stages.push({ primaries, curves, lut, qualifier: qualifierParamsOf(node.qualifier), window });
    }
  }
  if (overflow.length > 0) stages.push({ primaries: previewGradeNodeOf(foldPrimaries(overflow)), curves: null, lut: null, qualifier: null, window: null });
  return { stages, pendingLuts, folded: overflow.length, dropped };
}

/**
 * The CPU reference of the whole pass on one pixel: every stage in order, each secondary merged through its matte
 * measured on the pixel entering the stage. `x` / `y` are the pixel's position as X/W, Y/H (for windows).
 */
export function applyRichGrade(stages: readonly RichGradeStage[], lut: (stage: RichGradeStage) => CubeLut | null, rgb: V3, x = 0.5, y = 0.5): V3 {
  let v = rgb;
  for (const stage of stages) {
    let c: V3 = v;
    if (stage.primaries) c = applyPreviewGradeNode(stage.primaries, c);
    if (stage.curves) c = applyCurveBake(stage.curves, c);
    if (stage.lut) {
      const cube = lut(stage);
      if (cube) c = sampleCubeLut(cube, c, stage.lut.intensity);
    }
    if (stage.qualifier || stage.window) c = maskedMerge(v, c, secondaryMatte(stage.qualifier, stage.window, v, x, y));
    v = c;
  }
  return v;
}
