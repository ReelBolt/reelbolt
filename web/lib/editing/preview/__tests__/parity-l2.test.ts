import { describe, expect, it } from 'vitest';
import { classifyItem } from '../parity';
import type { Item, RegionClipEffect, SegmentationMask } from '../../types';
import type { SegmentationTrack } from '../segmentation-tracks';
import { media } from '../../__tests__/fixtures';

const clip = (extra: object) => ({ ...media('c1', 0, 100), ...extra }) as unknown as Item;
const quad = { topLeft: { x: 0, y: 0 }, topRight: { x: 1, y: 0 }, bottomRight: { x: 1, y: 1 }, bottomLeft: { x: 0, y: 1 } };
const mask: SegmentationMask = { kind: 'segmentation', analysisStepResultId: 'a1', trackIds: ['g1'], invert: false, featherPx: 4 };
const region = (over: Partial<RegionClipEffect> = {}): RegionClipEffect => ({ effect: 'blur', strength: 0.5, mask, enabled: true, ...over });
const track = (state?: 'ready' | 'pending' | 'none'): SegmentationTrack => ({ trackId: 'g1', label: 'p', firstFrame: 0, lastFrame: 100, fps: 25, boxKeyframes: [{ t: 0, x: 0, y: 0, w: 1, h: 1 }], ...(state ? { maskPreview: { state } } : {}) });
const lookup = (state?: 'ready' | 'pending' | 'none') => (id: string) => (id === 'a1' ? [track(state)] : undefined);

describe('L2 parity classes', () => {
  it('reframe, shape mask and corner pin are approximate with the new reasons', () => {
    expect(classifyItem(clip({ reframe: { x: 0.5, y: 0.5 } }))).toMatchObject({ parity: 'approximate', reason: expect.stringMatching(/cropped around the subject/) });
    expect(classifyItem(clip({ cornerPin: quad }))).toMatchObject({ parity: 'approximate', reason: expect.stringMatching(/true perspective warp/) });
    expect(classifyItem(clip({ mask: { kind: 'ellipse' } }))).toMatchObject({ parity: 'approximate', reason: expect.stringMatching(/soft edge/) });
  });

  it('region effects: never faithful - close when ready, a box while it prepares, render-only without box data', () => {
    const c = clip({ regionEffects: [region()] });
    expect(classifyItem(c, undefined, { tracksFor: lookup('ready') })).toMatchObject({ parity: 'approximate', reason: expect.stringMatching(/small copy of the mask/) });
    expect(classifyItem(c, undefined, { tracksFor: lookup('pending') })).toMatchObject({ parity: 'approximate', reason: 'Outline preview still preparing, showing a box.' });
    expect(classifyItem(c, undefined, { tracksFor: lookup() }).reason).toBe('The preview follows a box around the object; the render follows its exact outline.');
    expect(classifyItem(c, undefined, { tracksFor: lookup('none') }).parity).toBe('approximate');
    // Ready mask video without any box data: faithful (boxes are only the fallback).
    const noBoxes = (id: string) => (id === 'a1' ? [{ ...track('ready'), boxKeyframes: [] }] : undefined);
    expect(classifyItem(c, undefined, { tracksFor: noBoxes }).reason).toMatch(/small copy of the mask/);
    expect(classifyItem(c, undefined, { tracksFor: () => undefined }).parity).toBe('renderRequired');
    // A disabled entry does not count.
    expect(classifyItem(clip({ regionEffects: [region({ enabled: false })] }), undefined, { tracksFor: () => undefined }).parity).toBe('faithful');
  });

  it('inserts are approximate: a perspective warp, with or without a matte', () => {
    const insert = { id: 'i', type: 'insertClip', name: 'i', recordIn: 0, recordOut: 10, quad: { value: quad } } as unknown as Item;
    expect(classifyItem(insert)).toMatchObject({ parity: 'approximate', reason: 'The insert is previewed as a perspective warp; edges may differ by a pixel.' });
    expect(classifyItem({ ...insert, matte: { color: '#00ff00' } } as unknown as Item).reason).toBe('The insert is previewed as a perspective warp; edges may differ by a pixel.');
  });
});
