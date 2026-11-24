import { describe, expect, it } from 'vitest';

import { apply, assertInvariants, EditEngineError, SET_PROPERTY_ALLOWLIST, MAX_REGION_EFFECTS, REGION_EFFECT_TYPES } from '../index';
import { classifyItem } from '../../preview/parity';
import type { EditTimeline, MediaClip, RegionClipEffect, SegmentationMask } from '../../types';

const mask: SegmentationMask = { kind: 'segmentation', analysisStepResultId: '0e4d2c1b-7a6f-4e3d-8c2b-1a0f9e8d7c60', trackIds: ['g1'], invert: false, featherPx: 2 };
const region = (over: Partial<RegionClipEffect> = {}): RegionClipEffect => ({ effect: 'blur', strength: 0.5, mask, enabled: true, ...over });

const clip: MediaClip = {
  id: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeee01', type: 'mediaClip', name: 'Clip', recordIn: 0, recordOut: 100,
  assetRef: { kind: 'projectFile', projectFileId: '7d3c2b1a-0f9e-4d8c-b7a6-5f4e3d2c1b01' }, sourceIn: 0, playbackRate: 1, enabled: true,
} as MediaClip;
const doc = (): EditTimeline => ({
  schemaVersion: 2, id: '0e4d2c1b-7a6f-4e3d-8c2b-1a0f9e8d7c61', projectId: '5b0f6c1e-2a3d-4c8e-9f10-1a2b3c4d5e60', name: 'T', markers: [],
  settings: { width: 1920, height: 1080, rate: { num: 30, den: 1 }, sampleRate: 48000, pixelAspect: { num: 1, den: 1 }, aspectPolicy: 'fit', startTimecodeFrames: 0 },
  tracks: [{ id: '11111111-2222-4333-8444-555555555501', kind: 'video', name: 'V1', color: null, height: 64, locked: false, enabled: true, mute: false, solo: false, busId: null, items: [structuredClone(clip)] }],
} as unknown as EditTimeline);
const set = (value: unknown) => ({ type: 'setProperty', itemId: clip.id, key: 'regionEffects', value }) as never;
const clipOf = (d: EditTimeline): MediaClip => d.tracks[0].items[0] as MediaClip;

describe('regionEffects engine contract', () => {
  it('is allowlisted on mediaClip only and constants match the contract', () => {
    expect(SET_PROPERTY_ALLOWLIST.mediaClip).toContain('regionEffects');
    expect(SET_PROPERTY_ALLOWLIST.titleClip).not.toContain('regionEffects');
    expect(MAX_REGION_EFFECTS).toBe(4);
    expect(REGION_EFFECT_TYPES).toEqual(['blur', 'pixelate', 'highlight', 'spotlight', 'colorpop', 'outline']);
  });

  it('sets, round-trips through the inverse, and clears with undefined', () => {
    const start = doc();
    const set1 = apply(start, set([region(), region({ effect: 'colorpop', strength: 1 })]));
    expect(clipOf(set1.doc).regionEffects).toHaveLength(2);
    assertInvariants(set1.doc, { ids: 'guid' });
    const undone = set1.inverse.reduce((d, op) => apply(d, op, { inverse: true }).doc, set1.doc);
    expect(undone).toEqual(start);
    const cleared = apply(set1.doc, set(undefined));
    expect(clipOf(cleared.doc).regionEffects).toBeUndefined();
    expect(cleared.inverse.reduce((d, op) => apply(d, op, { inverse: true }).doc, cleared.doc)).toEqual(set1.doc);
  });

  it.each([
    ['five effects', Array.from({ length: 5 }, () => region())],
    ['unknown word', [region({ effect: 'sparkle' as never })]],
    ['negative strength', [region({ strength: -0.1 })]],
    ['strength over 1', [region({ strength: 1.5 })]],
    ['NaN strength', [region({ strength: Number.NaN })]],
    ['shape mask', [region({ mask: { kind: 'rect', x: 0, y: 0, w: 1, h: 1 } as never })]],
    ['mask missing feather', [region({ mask: { ...mask, featherPx: undefined } as never })]],
    ['missing enabled', [{ effect: 'blur', strength: 0.5, mask }]],
    ['not a list', { effect: 'blur' }],
  ])('refuses %s', (_name, value) => {
    expect(() => apply(doc(), set(value))).toThrow(EditEngineError);
  });

  it('refuses regionEffects together with stabilize, both orders', () => {
    const withRegion = apply(doc(), set([region()])).doc;
    expect(() => apply(withRegion, { type: 'setProperty', itemId: clip.id, key: 'stabilize', value: true } as never)).toThrow(EditEngineError);
    const stabilised = apply(doc(), { type: 'setProperty', itemId: clip.id, key: 'stabilize', value: true } as never).doc;
    expect(() => apply(stabilised, set([region()]))).toThrow(EditEngineError);
  });

  it('refuses a mask that names a foreign analysis when tracks are known', () => {
    const bad = { ...doc() };
    clipOf(bad).regionEffects = [region()];
    expect(() => assertInvariants(bad, { ids: 'guid', segmentationTracks: { other: ['g1'] } })).toThrow(EditEngineError);
    expect(() => assertInvariants(bad, { ids: 'guid', segmentationTracks: { [mask.analysisStepResultId]: ['g1'] } })).not.toThrow();
  });

  it('keeps regionEffects on both halves of a blade', () => {
    const withRegion = apply(doc(), set([region()])).doc;
    const cut = apply(withRegion, { type: 'blade', itemId: clip.id, atFrame: 40, newItemId: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeee02' } as never).doc;
    const [left, right] = cut.tracks[0].items as MediaClip[];
    expect(left.regionEffects).toEqual([region()]);
    expect(right.regionEffects).toEqual([region()]);
  });
});

describe('regionEffects parity', () => {
  it('is renderRequired when any is enabled, otherwise unaffected', () => {
    const on = classifyItem({ ...clip, regionEffects: [region()] });
    expect(on.parity).toBe('renderRequired');
    expect(on.reason).toMatch(/not loaded yet/);
    expect(classifyItem({ ...clip, regionEffects: [region({ enabled: false })] }).parity).not.toBe('renderRequired');
  });
});
