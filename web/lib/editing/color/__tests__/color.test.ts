/**
 * P7 colour + compose — the pure modules: grade helpers and look presets, colour groups from look
 * groups, shot match, the scopes maths, the preview-grade fold and its reference application, the
 * parity classes of the new fields, and the engine's `setColorGroup` op plus the new property keys.
 */

import { describe, expect, it } from 'vitest';
import { apply, EditEngineError } from '../../engine';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';
import { classifyItem } from '../../preview/parity';
import { colorGroupOf, colorGroupsFromLookGroups, effectiveGrade, isActiveGrade, isIdentityPrimaries, lookNode, readLookGroups, shotMatchPrimaries, withNode, withoutNode } from '../grade';
import { computeScopes, luma709 } from '../scopes';
import { applyPreviewGrade, IDENTITY_PREVIEW_GRADE, isIdentityPreviewGrade, previewGradeOf } from '../preview-grade';
import type { AdjustmentClip, ColorGroup, GradeStack, MediaClip } from '../../types';

const stack = (...nodes: GradeStack['nodes'] & object): GradeStack => ({ enabled: true, nodes });

describe('grade helpers', () => {
  it('knows identity from active, builds and removes nodes', () => {
    expect(isIdentityPrimaries({})).toBe(true);
    expect(isIdentityPrimaries({ lift: { r: 0 }, contrast: 1 })).toBe(true);
    expect(isIdentityPrimaries({ saturation: 1.1 })).toBe(false);
    expect(isActiveGrade(null)).toBe(false);
    expect(isActiveGrade({ enabled: false, nodes: [{ id: 'a', primaries: { saturation: 2 } }] })).toBe(false);
    expect(isActiveGrade(stack({ id: 'a', primaries: {} }))).toBe(false);
    expect(isActiveGrade(stack({ id: 'a', primaries: { exposure: 0.5 } }))).toBe(true);
    expect(isActiveGrade(stack({ id: 'a', curves: { y: [{ x: 0.5, y: 0.6 }] } }))).toBe(true);
    const s = withNode(null, { id: 'a', name: 'A' });
    expect(withNode(s, { id: 'a', name: 'A2' }).nodes?.map((n) => n.name)).toEqual(['A2']);
    expect(withNode(s, { id: 'b' }).nodes).toHaveLength(2);
    expect(withoutNode(s, 'a')).toBeNull();
  });

  it('turns the Colorist words into primaries that mirror the compile tables and scale by strength', () => {
    const warm = lookNode('n', 'Warm');
    expect(warm.primaries?.lift).toEqual({ r: 0.1, b: -0.1, master: 0 });
    expect(warm.primaries?.gamma).toEqual({ r: 0.05, b: -0.05, master: 0 });
    expect(warm.primaries?.saturation).toBe(1.06);
    const subtle = lookNode('n', 'Warm', 'Subtle', 'Lifted', 'Softened');
    expect(subtle.primaries?.lift).toEqual({ r: 0.05, b: -0.05, master: 0.02 });
    expect(subtle.primaries?.gain).toEqual({ master: -0.025 });
    expect(subtle.primaries?.saturation).toBe(1.03);
    const mono = lookNode('n', 'Mono', 'Strong');
    expect(mono.primaries?.saturation).toBe(0);
    expect(mono.primaries?.contrast).toBe(1.09);
    expect(lookNode('n', 'Filmic').primaries?.gamma).toEqual({ r: 0, b: 0, master: -0.03 });
  });

  it('resolves the effective grade as the group stack then the clip stack', () => {
    const clipGrade = stack({ id: 'c', primaries: { saturation: 1.2 } });
    const groupGrade = stack({ id: 'g', primaries: { temperature: 0.3 } });
    const d = { ...doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0, { grade: clipGrade }), media('c2', 60, 120, 0)])]), colorGroups: [{ id: 'g1', name: 'G', clipIds: ['c1'], grade: groupGrade } satisfies ColorGroup] };
    const c1 = d.tracks[0].items[0] as MediaClip;
    expect(colorGroupOf(d, 'c1')?.name).toBe('G');
    expect(colorGroupOf(d, 'c2')).toBeNull();
    expect(effectiveGrade(d, c1)).toEqual([groupGrade, clipGrade]);
    expect(effectiveGrade(d, d.tracks[0].items[1] as MediaClip)).toEqual([]);
  });

  it('pre-populates colour groups from the analysis look groups, one per look, ungrouped clips only', () => {
    const artifact = {
      shots: [{ id: 's0', startSec: 0, endSec: 2 }, { id: 's1', startSec: 2, endSec: 4 }, { id: 's2', startSec: 10, endSec: 12 }],
      lookGroups: [{ id: 'k0', shotIds: ['s0', 's1'], representativeShotId: 's0', cohesion: 0.9, colorTemperatureClass: 'Warm', toneClass: 'Normal' }, { id: 'k1', shotIds: ['s2'], representativeShotId: 's2', cohesion: 0.8 }],
    };
    const looks = readLookGroups(artifact);
    expect(looks).toHaveLength(2);
    const d = doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0), media('c2', 60, 120, 60), media('c3', 120, 180, 300)])]);
    let n = 0;
    const groups = colorGroupsFromLookGroups(d, TRACKS.v1, [{ id: 's0', startSec: 0, endSec: 2, sourceIndex: 0, motionMean: null, motionClass: null, duplicateGroupId: null, isBestTake: false }, { id: 's1', startSec: 2, endSec: 4, sourceIndex: 0, motionMean: null, motionClass: null, duplicateGroupId: null, isBestTake: false }, { id: 's2', startSec: 10, endSec: 12, sourceIndex: 0, motionMean: null, motionClass: null, duplicateGroupId: null, isBestTake: false }], looks, 30, () => `g${++n}`);
    expect(groups.map((g) => [g.lookGroupId, g.clipIds, g.name])).toEqual([['k0', ['c1', 'c2'], 'k0 · Warm / Normal'], ['k1', ['c3'], 'k1']]);
    // A second pass creates nothing: every look is already represented.
    expect(colorGroupsFromLookGroups({ ...d, colorGroups: groups }, TRACKS.v1, [], looks, 30, () => 'x')).toEqual([]);
  });

  it('shot match derives exposure, gain balance and contrast from two frames’ statistics, clamped', () => {
    const target = { meanR: 0.2, meanG: 0.2, meanB: 0.2, meanLuma: 0.2, lumaSpread: 0.1 };
    const reference = { meanR: 0.44, meanG: 0.4, meanB: 0.36, meanLuma: 0.4, lumaSpread: 0.2 };
    const p = shotMatchPrimaries(target, reference);
    expect(p.exposure).toBeCloseTo(1, 1);
    expect(p.contrast).toBeCloseTo(2, 1);
    expect(p.pivot).toBe(0.4);
    expect(p.gain?.r).toBeGreaterThan(0);
    expect(p.gain?.b).toBeLessThan(0);
    expect(shotMatchPrimaries(target, { ...reference, meanLuma: 1, lumaSpread: 5 }).contrast).toBe(3);
    expect(shotMatchPrimaries(target, target).gain).toBeNull();
  });
});

describe('scopes', () => {
  it('bins a two-tone frame into the waveform, parade, vectorscope and histogram and measures it', () => {
    const width = 8;
    const height = 2;
    const data = new Uint8ClampedArray(width * height * 4);
    for (let x = 0; x < width; x++) {
      for (let y = 0; y < height; y++) {
        const i = (y * width + x) * 4;
        const left = x < 4;
        data[i] = left ? 0 : 255;
        data[i + 1] = left ? 0 : 255;
        data[i + 2] = left ? 0 : 255;
        data[i + 3] = 255;
      }
    }
    const s = computeScopes({ data, width, height }, { columns: 8, levels: 16, vectorSize: 16 });
    expect(s.stats.meanLuma).toBeCloseTo(0.5, 6);
    expect(s.stats.meanR).toBeCloseTo(0.5, 6);
    expect(s.stats.lumaSpread).toBeCloseTo(0.5, 6);
    expect(s.clipped).toEqual({ black: 0.5, white: 0.5 });
    // Left columns at level 0, right columns at the top level.
    expect(s.waveform[0 * 16 + 0]).toBe(2);
    expect(s.waveform[7 * 16 + 15]).toBe(2);
    expect(s.histogram.y[0]).toBe(8);
    expect(s.histogram.y[15]).toBe(8);
    // Neutral greys sit at the vectorscope's centre.
    const centre = 8 * 16 + 8;
    expect(s.vectorscope[centre]).toBe(16);
    expect(luma709(255, 255, 255)).toBeCloseTo(255, 6);
  });
});

describe('preview grade', () => {
  it('folds enabled primaries in order and leaves identity alone', () => {
    expect(previewGradeOf([])).toBe(IDENTITY_PREVIEW_GRADE);
    expect(previewGradeOf([{ enabled: false, nodes: [{ id: 'a', primaries: { exposure: 1 } }] }])).toBe(IDENTITY_PREVIEW_GRADE);
    const g = previewGradeOf([stack({ id: 'a', primaries: { exposure: 1, lift: { r: 0.1 }, gain: { master: 0.5 } } }), stack({ id: 'b', primaries: { saturation: 0.5, contrast: 1.2, pivot: 0.4 } })]);
    expect(isIdentityPreviewGrade(g)).toBe(false);
    expect(g.exposureGain).toBe(2);
    expect(g.lift).toEqual([0.1, 0, 0]);
    expect(g.gain).toEqual([1.5, 1.5, 1.5]);
    expect(g.saturation).toBe(0.5);
    expect(g.contrast).toBe(1.2);
    expect(g.pivot).toBe(0.4);
  });

  it('applies like the shader: exposure doubles, saturation zero greys, hue rotates, clamps', () => {
    expect(applyPreviewGrade(IDENTITY_PREVIEW_GRADE, [0.2, 0.4, 0.6])).toEqual([0.2, 0.4, 0.6]);
    const exposure = previewGradeOf([stack({ id: 'a', primaries: { exposure: 1 } })]);
    expect(applyPreviewGrade(exposure, [0.2, 0.3, 0.6]).map((v) => Math.round(v * 100) / 100)).toEqual([0.4, 0.6, 1]);
    const grey = applyPreviewGrade(previewGradeOf([stack({ id: 'a', primaries: { saturation: 0 } })]), [1, 0, 0]);
    expect(grey[0]).toBeCloseTo(grey[1], 6);
    expect(grey[1]).toBeCloseTo(grey[2], 6);
    expect(grey[0]).toBeCloseTo(0.2126, 4);
    const rotated = applyPreviewGrade(previewGradeOf([stack({ id: 'a', primaries: { hue: 120 } })]), [1, 0, 0]);
    expect(rotated[1]).toBeGreaterThan(rotated[0]);
    const warm = applyPreviewGrade(previewGradeOf([stack({ id: 'a', primaries: { temperature: 1 } })]), [0.5, 0.5, 0.5]);
    expect(warm[0]).toBeGreaterThan(warm[2]);
  });
});

describe('parity (P7)', () => {
  it('grades are approximate, curves/LUTs approximate, compose features render-required, adjustment clips render-required', () => {
    const base = media('c', 0, 60, 0);
    // Primaries are the export's maths per node (preview-grade.ts), so they are faithful up to the shader's node bound.
    expect(classifyItem({ ...base, grade: stack({ id: 'a', primaries: { saturation: 1.3 } }) }).parity).toBe('faithful');
    expect(classifyItem({ ...base, grade: stack(...Array.from({ length: 17 }, (_, i) => ({ id: `p${i}`, primaries: { saturation: 1.1 } }))) }).parity).toBe('approximate');
    expect(classifyItem({ ...base, grade: stack({ id: 'a', lut: { file: { kind: 'projectFile', projectFileId: 'x' } } }) }).parity).toBe('faithful'); // the grade pass applies LUTs; the loading note comes from gradeLutStatus
    expect(classifyItem({ ...base, grade: { enabled: false, nodes: [{ id: 'a', primaries: { saturation: 2 } }] } }).parity).toBe('faithful');
    for (const patch of [{ stabilize: true }, { effects: [{ type: 'sparkle' as never }] }]) {
      expect(classifyItem({ ...base, ...patch }).parity).toBe('renderRequired');
    }
    expect(classifyItem({ ...base, effects: [{ type: 'grain', enabled: false }] }).parity).toBe('faithful');
    const adjustment: AdjustmentClip = { id: 'adj', type: 'adjustmentClip', name: 'adj', recordIn: 0, recordOut: 10 };
    expect(classifyItem(adjustment).parity).toBe('faithful');
    expect(classifyItem({ ...adjustment, effects: [{ type: 'sparkle' as never }] }).parity).toBe('renderRequired');
  });
});

describe('engine (P7)', () => {
  it('setColorGroup adds, replaces and removes a group with exact inverses, and refuses bad members', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0), media('c2', 60, 120, 0)])]);
    const group: ColorGroup = { id: 'g1', name: 'G', clipIds: ['c1'], grade: null };
    const added = apply(d, { type: 'setColorGroup', groupId: 'g1', group });
    expect(added.doc.colorGroups).toEqual([group]);
    expect(added.inverse).toEqual([{ type: 'setColorGroup', groupId: 'g1', group: undefined }]);
    const replaced = apply(added.doc, { type: 'setColorGroup', groupId: 'g1', group: { ...group, clipIds: ['c1', 'c2'] } });
    expect(replaced.inverse).toEqual([{ type: 'setColorGroup', groupId: 'g1', group }]);
    const removed = apply(replaced.doc, { type: 'setColorGroup', groupId: 'g1', group: undefined });
    expect(removed.doc.colorGroups).toBeUndefined();
    expect(removed.inverse[0]).toMatchObject({ type: 'setColorGroup', groupId: 'g1', group: { clipIds: ['c1', 'c2'] } });
    expect(() => apply(d, { type: 'setColorGroup', groupId: 'g1', group: { ...group, clipIds: ['nope'] } })).toThrow(EditEngineError);
    expect(() => apply(d, { type: 'setColorGroup', groupId: 'g1', group: { ...group, id: 'g2' } })).toThrow(/groupId/);
    expect(() => apply(added.doc, { type: 'setColorGroup', groupId: 'g2', group: { id: 'g2', name: 'H', clipIds: ['c1'] } })).toThrow(/more than one/);
    expect(() => apply(d, { type: 'setColorGroup', groupId: 'zz', group: undefined })).toThrow(/no colour group/);
    // Lifting a member keeps the document editable: the dangling id is tolerated.
    const lifted = apply(added.doc, { type: 'lift', itemId: 'c1' });
    expect(lifted.doc.colorGroups?.[0].clipIds).toEqual(['c1']);
  });

  it('setProperty accepts the P7 shapes and refuses hostile ones', () => {
    const d = doc([track(TRACKS.v1, 'video', [media('c1', 0, 60, 0), { id: 't1', type: 'titleClip', name: 't', recordIn: 60, recordOut: 90, text: 'Hi', enabled: true, provenance: null }, { id: 'a1', type: 'adjustmentClip', name: 'a', recordIn: 90, recordOut: 120, provenance: null } as AdjustmentClip])]);
    const ok = apply(d, { type: 'setProperty', itemId: 'c1', key: 'grade', value: stack({ id: 'n', primaries: { contrast: 1.5 } }) });
    expect((ok.doc.tracks[0].items[0] as MediaClip).grade?.nodes).toHaveLength(1);
    expect(ok.inverse).toEqual([{ type: 'setProperty', itemId: 'c1', key: 'grade', value: undefined }]);
    apply(d, { type: 'setProperty', itemId: 'c1', key: 'mask', value: { kind: 'ellipse', x: 0.5, y: 0.5, w: 0.4, h: 0.4, feather: 0.1 } });
    apply(d, { type: 'setProperty', itemId: 'c1', key: 'effects', value: [{ type: 'blur', amount: 0.3 }] });
    apply(d, { type: 'setProperty', itemId: 't1', key: 'template', value: 'lowerThird' });
    apply(d, { type: 'setProperty', itemId: 'a1', key: 'grade', value: stack({ id: 'n', primaries: { saturation: 0 } }) });
    apply(d, { type: 'setProperty', itemId: 'a1', key: 'enabled', value: false });
    expect(() => apply(d, { type: 'setProperty', itemId: 'c1', key: 'grade', value: stack({ id: 'n', primaries: { contrast: 9 } }) })).toThrow(/grade stack/);
    expect(() => apply(d, { type: 'setProperty', itemId: 'c1', key: 'effects', value: [{ type: 'sparkle' } as never] })).toThrow(/catalogue/);
    expect(() => apply(d, { type: 'setProperty', itemId: 'c1', key: 'keyer', value: { color: 'green' } })).toThrow(/RRGGBB/);
    expect(() => apply(d, { type: 'setProperty', itemId: 't1', key: 'template', value: 'neon' })).toThrow(/template/);
    expect(() => apply(d, { type: 'setProperty', itemId: 'c1', key: 'retimeQuality', value: 'magic' })).toThrow(/opticalFlow/);
    expect(() => apply(d, { type: 'setProperty', itemId: 't1', key: 'grade', value: null })).toThrow(/not settable/);
  });
});
