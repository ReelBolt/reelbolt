import { describe, expect, it } from 'vitest';
import { parseCubeLut, sampleCubeLut } from '../../color/cube-lut';
import { applyPreviewGradeNode, GRADE_NODE_BOUND, previewGradeNodeOf } from '../../color/preview-grade';
import type { AssetRef, GradeNode, GradeStack } from '../../types';
import { applyRichGrade, lutStagePlanOf, needsGradePass, richGradePlanOf } from '../grade-pass-plan';

const stack = (...nodes: GradeNode[]): GradeStack => ({ enabled: true, nodes });
const exposure = (id: string, e = 1): GradeNode => ({ id, primaries: { exposure: e } });
const file: AssetRef = { kind: 'projectFile', projectFileId: 'f1' };
const closeTo = (got: readonly number[], want: readonly number[]) => want.forEach((v, i) => expect(got[i]).toBeCloseTo(v, 6));

// 2^3 identity cube except the (1,1,1) corner, which maps to black: tetrahedral and trilinear disagree at the centre.
const HOLE_CUBE = [
  'LUT_3D_SIZE 2',
  '0 0 0', '1 0 0', '0 1 0', '1 1 0', '0 0 1', '1 0 1', '0 1 1', '0 0 0',
].join('\n');

describe('needsGradePass', () => {
  it('plain primaries up to the single-draw bound keep the single-draw shader', () => {
    expect(needsGradePass([stack(exposure('a'), exposure('b'))])).toBe(false);
    expect(needsGradePass([])).toBe(false);
    expect(needsGradePass([null, undefined])).toBe(false);
  });
  it('curves, a LUT, or a secondary take the pass', () => {
    expect(needsGradePass([stack({ id: 'c', curves: { y: [{ x: 0.5, y: 0.6 }] } })])).toBe(true);
    expect(needsGradePass([stack({ id: 'l', lut: { file, intensity: 1 } })])).toBe(true);
    expect(needsGradePass([stack({ ...exposure('w'), window: { shape: { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5 } } })])).toBe(true);
    expect(needsGradePass([stack({ ...exposure('q'), qualifier: { hueWidth: 30 } })])).toBe(true);
  });
  it('a qualifier that selects everything is not a secondary', () => {
    expect(needsGradePass([stack({ ...exposure('q'), qualifier: { hueWidth: 180 } })])).toBe(false);
  });
  it('a LUT at intensity 0, a disabled node and a disabled stack do not count', () => {
    expect(needsGradePass([stack({ id: 'l', lut: { file, intensity: 0 } })])).toBe(false);
    expect(needsGradePass([stack({ id: 'c', enabled: false, curves: { y: [{ x: 0.5, y: 0.6 }] } })])).toBe(false);
    expect(needsGradePass([{ enabled: false, nodes: [{ id: 'c', curves: { y: [{ x: 0.5, y: 0.6 }] } }] }])).toBe(false);
  });
  it('more primaries nodes than the single-draw shader holds take the pass (no fold)', () => {
    const nodes = Array.from({ length: GRADE_NODE_BOUND + 1 }, (_, i) => exposure(`n${i}`, 0.1));
    expect(needsGradePass([stack(...nodes)])).toBe(true);
  });
});

describe('richGradePlanOf', () => {
  it('lists stages in application order, group stack first, skipping nodes with no correction', () => {
    const plan = richGradePlanOf([stack(exposure('g')), stack({ id: 'empty', primaries: {} }, { id: 'c', curves: { y: [{ x: 0.5, y: 0.6 }] } })], 0);
    expect(plan.stages).toHaveLength(2);
    expect(plan.stages[0]?.primaries).not.toBeNull();
    expect(plan.stages[1]?.curves).not.toBeNull();
    expect(plan.pendingLuts).toBe(0);
  });
  it('a node with primaries, curves and a LUT is ONE stage (primaries, then curves, then the LUT)', () => {
    const cube = parseCubeLut(HOLE_CUBE);
    const plan = richGradePlanOf([stack({ ...exposure('n'), curves: { y: [{ x: 0.5, y: 0.6 }] }, lut: { file, intensity: 1 } })], 0, () => ({ key: 'f1:1', lut: cube }));
    expect(plan.stages).toHaveLength(1);
    const s = plan.stages[0]!;
    expect(s.primaries && s.curves && s.lut).toBeTruthy();
  });
  it('a LUT that is not loaded yet is counted and the node draws without it', () => {
    const plan = richGradePlanOf([stack({ ...exposure('n'), lut: { file, intensity: 1 } })], 0, () => null);
    expect(plan.pendingLuts).toBe(1);
    expect(plan.stages).toHaveLength(1);
    expect(plan.stages[0]?.lut).toBeNull();
    expect(richGradePlanOf([stack({ id: 'only', lut: { file } })], 0, () => null).stages).toHaveLength(0);
  });
  it('evaluates the window offsets at the timeline frame', () => {
    const node: GradeNode = {
      ...exposure('w'),
      window: {
        shape: { kind: 'rect', x: 0.5, y: 0.5, w: 0.2, h: 0.2 },
        offsetX: { value: 0, keys: [{ frame: 0, value: 0, interp: 'linear' }, { frame: 10, value: 0.2, interp: 'linear' }] },
      },
    };
    expect(richGradePlanOf([stack(node)], 0).stages[0]?.window?.cx).toBeCloseTo(0.5, 9);
    expect(richGradePlanOf([stack(node)], 5).stages[0]?.window?.cx).toBeCloseTo(0.6, 9);
    expect(richGradePlanOf([stack(node)], 10).stages[0]?.window?.cx).toBeCloseTo(0.7, 9);
    expect(richGradePlanOf([stack(node)], 5).stages[0]?.window?.cy).toBe(0.5);
  });
  it('past 16 nodes the extra primaries FOLD into one last stage (counted), not dropped', () => {
    const nodes = Array.from({ length: 20 }, (_, i) => exposure(`n${i}`, 0.1));
    const plan = richGradePlanOf([stack(...nodes)], 0);
    expect(plan.stages).toHaveLength(17);
    expect(plan.folded).toBe(4);
    expect(plan.dropped).toBe(0);
    expect(plan.stages[16]?.primaries?.exposureGain).toBeCloseTo(2 ** 0.4, 6);
  });
  it('past 16 nodes a node with curves cannot fold and is counted as dropped', () => {
    const nodes = [...Array.from({ length: 16 }, (_, i) => exposure(`n${i}`, 0.1)), { id: 'c', curves: { y: [{ x: 0.5, y: 0.6 }] } }];
    const plan = richGradePlanOf([stack(...nodes)], 0);
    expect(plan.stages).toHaveLength(16);
    expect(plan.dropped).toBe(1);
  });
});

describe('lutStagePlanOf', () => {
  const cube = parseCubeLut(HOLE_CUBE);
  it('a 3D LUT is one tetrahedral stage mixed by the intensity', () => {
    const p = lutStagePlanOf('k', cube, 0.4);
    expect(p).toMatchObject({ kind: 1, mix: 0.4, intensity: 0.4, oneD: null });
    expect(p.cube?.size).toBe(2);
    expect(p.cube?.key).toBe('view:node:k');
    expect(p.cube?.texels()).toHaveLength(2 * 2 * 2 * 4);
  });
  it('a 1D LUT is one linear stage with no 3D texture', () => {
    const oneD = parseCubeLut('LUT_1D_SIZE 2\n0 0 0\n1 1 1\n');
    const p = lutStagePlanOf('o', oneD, 1);
    expect(p.kind).toBe(2);
    expect(p.cube).toBeNull();
    expect(p.oneD?.size).toBe(2);
  });
  it('a shaper+3D LUT at full intensity runs the 1D stage then the 3D one', () => {
    const shaper = parseCubeLut('LUT_1D_SIZE 2\nLUT_3D_SIZE 2\n0 0 0\n1 1 1\n' + HOLE_CUBE.split('\n').slice(1).join('\n') + '\n');
    expect(shaper.kind).toBe('shaper3d');
    const full = lutStagePlanOf('s', shaper, 1);
    expect(full.kind).toBe(3);
    expect(full.mix).toBe(1);
    // below full intensity the engine writes ONE baked 3D table with the blend folded in
    const half = lutStagePlanOf('s', shaper, 0.5);
    expect(half).toMatchObject({ kind: 1, mix: 1, oneD: null });
    expect(half.cube?.size).toBe(65);
    expect(half.cube?.key).toBe('view:node:s:k500');
  });
});

describe('applyRichGrade (the CPU reference of the pass)', () => {
  const lutOf = (cube: ReturnType<typeof parseCubeLut>) => () => cube;
  it('tetrahedral LUT: the centre of the hole cube is black, not the trilinear 0.375', () => {
    const cube = parseCubeLut(HOLE_CUBE);
    const plan = richGradePlanOf([stack({ id: 'l', lut: { file, intensity: 1 } })], 0, () => ({ key: 'k', lut: cube }));
    const out = applyRichGrade(plan.stages, lutOf(cube), [0.5, 0.5, 0.5]);
    expect(out[0]).toBeCloseTo(0, 6);
    expect(out[1]).toBeCloseTo(0, 6);
    expect(out[2]).toBeCloseTo(0, 6);
    expect(out).toEqual(sampleCubeLut(cube, [0.5, 0.5, 0.5], 1));
  });
  it('LUT intensity blends toward the unchanged pixel', () => {
    const cube = parseCubeLut(HOLE_CUBE);
    const plan = richGradePlanOf([stack({ id: 'l', lut: { file, intensity: 0.5 } })], 0, () => ({ key: 'k', lut: cube }));
    const out = applyRichGrade(plan.stages, lutOf(cube), [0.5, 0.5, 0.5]);
    expect(out[0]).toBeCloseTo(0.25, 6);
  });
  it('curves run after the primaries in the same node', () => {
    const node: GradeNode = { ...exposure('n', 0), primaries: { exposure: 1 }, curves: { y: [{ x: 0.2, y: 0.1 }, { x: 0.6, y: 0.8 }] } };
    const plan = richGradePlanOf([stack(node)], 0);
    const afterPrimaries = applyPreviewGradeNode(previewGradeNodeOf({ exposure: 1 }), [0.1, 0.1, 0.1]);
    const out = applyRichGrade(plan.stages, () => null, [0.1, 0.1, 0.1]);
    // 0.1 * 2^1 = 0.2 -> the curve's knot (0.2 -> 0.1)
    expect(afterPrimaries[0]).toBeCloseTo(0.2, 2);
    expect(out[0]).toBeCloseTo(0.1, 2);
  });
  it('a power window confines the primaries: inside = corrected, outside = untouched', () => {
    const node: GradeNode = { ...exposure('w', 1), window: { shape: { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0 } } };
    const plan = richGradePlanOf([stack(node)], 0);
    const corrected = applyPreviewGradeNode(previewGradeNodeOf({ exposure: 1 }), [0.2, 0.2, 0.2]);
    closeTo(applyRichGrade(plan.stages, () => null, [0.2, 0.2, 0.2], 0.5, 0.5), corrected);
    expect(applyRichGrade(plan.stages, () => null, [0.2, 0.2, 0.2], 0.95, 0.5)).toEqual([0.2, 0.2, 0.2]);
  });
  it('a qualifier confines the correction to the selected hue', () => {
    const node: GradeNode = { ...exposure('q', 1), qualifier: { hueCenter: 0, hueWidth: 30 } };
    const plan = richGradePlanOf([stack(node)], 0);
    const red: [number, number, number] = [0.4, 0, 0];
    const green: [number, number, number] = [0, 0.4, 0];
    closeTo(applyRichGrade(plan.stages, () => null, red), applyPreviewGradeNode(previewGradeNodeOf({ exposure: 1 }), red));
    expect(applyRichGrade(plan.stages, () => null, green)).toEqual(green);
  });
  it('the matte of a later node is measured on the pixel the earlier nodes produced', () => {
    // node 1 turns the pixel red-ish by lifting red; node 2 corrects only reds
    const first: GradeNode = { id: 'a', primaries: { offset: { r: 0.4 } } };
    const second: GradeNode = { id: 'b', primaries: { exposure: 1 }, qualifier: { hueCenter: 0, hueWidth: 40 } };
    const both = richGradePlanOf([stack(first, second)], 0).stages;
    const alone = richGradePlanOf([stack(second)], 0).stages;
    // a cyan-ish pixel (hue 180) is outside the red qualifier on its own; after node 1 lifts its red it is red-ish and inside
    const cyan: [number, number, number] = [0.2, 0.3, 0.3];
    expect(applyRichGrade(alone, () => null, cyan)).toEqual(cyan);
    const afterFirst = applyPreviewGradeNode(previewGradeNodeOf({ offset: { r: 0.4 } }), cyan);
    const afterBoth = applyRichGrade(both, () => null, cyan);
    expect(afterBoth[0]).toBeGreaterThan(afterFirst[0] as number);
  });
});
