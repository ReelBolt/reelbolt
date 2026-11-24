import { describe, expect, it } from 'vitest';
import {
  blurSigmaPx,
  dimmingOf,
  matteSourceKindFor,
  outlineBandPx,
  pendingPreviewTracks,
  pixelBlockPx,
  planRegionBlur,
  previewRegionEffectsOf,
  regionStrength,
  roundHalfEven,
} from '../region-effects';
import { maskPreviewUrl, segmentationAnalysesOf, type SegmentationTrack } from '../segmentation-tracks';
import type { EditTimeline, Item, RegionClipEffect, SegmentationMask } from '../../types';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';

const box = { t: 0, x: 0.1, y: 0.2, w: 0.3, h: 0.4 };
const trk = (trackId: string, state?: 'ready' | 'pending' | 'none'): SegmentationTrack => ({
  trackId, label: trackId, firstFrame: 0, lastFrame: 100, fps: 25, boxKeyframes: [box], ...(state ? { maskPreview: { state } } : {}),
});
const mask = (over: Partial<SegmentationMask> = {}): SegmentationMask => ({ kind: 'segmentation', analysisStepResultId: 'a1', trackIds: ['g1'], invert: false, featherPx: 8, ...over });
const entry = (over: Partial<RegionClipEffect> = {}): RegionClipEffect => ({ effect: 'blur', strength: 0.5, mask: mask(), enabled: true, ...over });
const lookup = (tracks: SegmentationTrack[]) => (id: string) => (id === 'a1' ? tracks : undefined);

describe('render parameters (RegionEffectFilterBuilder.cs)', () => {
  it('rounds halves to even like .NET Math.Round', () => {
    expect([roundHalfEven(0.5), roundHalfEven(1.5), roundHalfEven(2.5), roundHalfEven(3.5), roundHalfEven(2.4), roundHalfEven(2.6)]).toEqual([0, 2, 2, 4, 2, 3]);
  });
  it('clamps the strength to 0..2 and reads a non-finite one as 1', () => {
    expect([regionStrength(5), regionStrength(-1), regionStrength(0.3), regionStrength(Number.NaN)]).toEqual([2, 0, 0.3, 1]);
  });
  it('blur sigma: 1.5% of the short side x strength, floor 0.1, two decimals', () => {
    expect(blurSigmaPx(1080, 1)).toBe(16.2);
    expect(blurSigmaPx(1080, 0.5)).toBe(8.1);
    expect(blurSigmaPx(720, 1)).toBe(10.8);
    expect(blurSigmaPx(100, 0.01)).toBe(0.1);
  });
  it('pixel block: 2% of the short side x strength, banker rounding, clamped to [2, short / 2]', () => {
    expect(pixelBlockPx(1080, 1)).toBe(22); // 21.6
    expect(pixelBlockPx(1080, 0.5)).toBe(11); // 10.8
    expect(pixelBlockPx(100, 0.25)).toBe(2); // 0.5 -> 0 -> floor at 2
    expect(pixelBlockPx(100, 2)).toBe(4);
    expect(pixelBlockPx(10, 2)).toBe(2); // short / 2 = 5 but 0.4 -> 0 -> 2
  });
  it('outline band: 0.4% of the short side x strength, 1..16', () => {
    expect(outlineBandPx(1080, 1)).toBe(4); // 4.32
    expect(outlineBandPx(1080, 2)).toBe(9); // 8.64
    expect(outlineBandPx(4000, 2)).toBe(16); // 32 capped
    expect(outlineBandPx(100, 1)).toBe(1); // 0.4 -> 0 -> floor at 1
  });
  it('highlight lifts inside and dims outside; spotlight dims outside by 0.6 x strength', () => {
    expect(dimmingOf('highlight', 1)).toEqual({ gain: 0.1, contrast: 1.1, dim: 0.15 });
    expect(dimmingOf('highlight', 0.5)).toEqual({ gain: 0.05, contrast: 1.05, dim: 0.075 });
    expect(dimmingOf('spotlight', 1).dim).toBe(0.6);
    expect(dimmingOf('spotlight', 2).dim).toBe(1.2);
    expect(dimmingOf('blur', 1)).toEqual({ gain: 0, contrast: 1, dim: 0 });
  });
  it('plans the blur on a smaller copy so its sigma stays <= 4 working pixels', () => {
    const big = planRegionBlur(1920, 1080, 1); // wanted short side round(4 / 0.015) = 267
    expect([big.width, big.height]).toEqual([475, 267]);
    expect(big.sigma).toBeCloseTo(267 * 0.015, 6);
    const soft = planRegionBlur(1920, 1080, 0.1); // sigma 1.62 at full size: no reduction
    expect([soft.width, soft.height]).toEqual([1920, 1080]);
    expect(soft.sigma).toBeCloseTo(1.62, 6);
    expect(planRegionBlur(100, 50, 1).sigma).toBeCloseTo(0.75, 6);
  });
});

describe('matte source: boxes | maskVideo, with the box fallback', () => {
  it('uses the mask video only when EVERY named track is ready, the host has a URL, and there are at most 4 tracks', () => {
    const ready = lookup([trk('g1', 'ready'), trk('g2', 'ready')]);
    expect(matteSourceKindFor(mask({ trackIds: ['g1', 'g2'] }), ready, true)).toBe('maskVideo');
    expect(matteSourceKindFor(mask({ trackIds: ['g1', 'g2'] }), ready, false)).toBe('boxes');
    expect(matteSourceKindFor(mask({ trackIds: ['g1', 'g2'] }), lookup([trk('g1', 'ready'), trk('g2', 'pending')]), true)).toBe('boxes');
    expect(matteSourceKindFor(mask(), lookup([trk('g1', 'none')]), true)).toBe('boxes');
    expect(matteSourceKindFor(mask(), lookup([trk('g1')]), true)).toBe('boxes'); // an older API: no maskPreview
    expect(matteSourceKindFor(mask(), undefined, true)).toBe('boxes');
    const five = ['g1', 'g2', 'g3', 'g4', 'g5'];
    expect(matteSourceKindFor(mask({ trackIds: five }), lookup(five.map((id) => trk(id, 'ready'))), true)).toBe('boxes');
  });

  it('lists the tracks still being made', () => {
    expect(pendingPreviewTracks(mask({ trackIds: ['g1', 'g2'] }), lookup([trk('g1', 'ready'), trk('g2', 'pending')]))).toEqual(['g2']);
  });

  it('an entry draws a mask video when it has decoded frames, otherwise its boxes, otherwise it waits (unready)', () => {
    const l = lookup([trk('g1', 'ready')]);
    const frames = [{ image: {} as CanvasImageSource }];
    const withVideo = previewRegionEffectsOf([entry()], l, 1, () => frames);
    expect(withVideo.drawn).toHaveLength(1);
    expect(withVideo.drawn[0].matte.kind).toBe('maskVideo');
    const fallback = previewRegionEffectsOf([entry()], l, 1, () => null);
    expect(fallback.drawn[0].matte.kind).toBe('boxes');
    expect(fallback.drawn[0].strength).toBe(0.5);
    const notLoaded = previewRegionEffectsOf([entry()], () => undefined, 1);
    expect(notLoaded).toEqual({ drawn: [], unready: 1 });
  });

  it('skips disabled entries, keeps document order, and draws at most 4 (MaxRegionEffects)', () => {
    const l = lookup([trk('g1')]);
    const list = [entry({ effect: 'pixelate' }), entry({ enabled: false }), entry({ effect: 'spotlight' }), entry({ effect: 'colorpop' }), entry({ effect: 'outline' }), entry({ effect: 'highlight' })];
    const r = previewRegionEffectsOf(list, l, 1);
    expect(r.drawn.map((d) => d.effect)).toEqual(['pixelate', 'spotlight', 'colorpop', 'outline']);
    expect(previewRegionEffectsOf(null, l, 1)).toEqual({ drawn: [], unready: 0 });
  });

  it('carries invert and the clamped feather onto a mask-video matte', () => {
    const r = previewRegionEffectsOf([entry({ mask: mask({ invert: true, featherPx: 200 }) })], lookup([trk('g1', 'ready')]), 1, () => [{ image: {} as CanvasImageSource }]);
    expect(r.drawn[0].matte).toMatchObject({ kind: 'maskVideo', invert: true, featherPx: 64 });
  });
});

describe('wire shape (T-006) and analyses to load', () => {
  it('addresses the mask preview of one track', () => {
    expect(maskPreviewUrl('p1', 'r1', 'g2')).toBe('/api/v1/projects/p1/step-results/r1/segmentation-tracks/g2/mask-preview');
  });
  it('region effects name analyses too, so their boxes load without a segmentation mask on the clip', () => {
    const d: EditTimeline = doc([track(TRACKS.v1, 'video', [
      { ...media('a', 0, 10), regionEffects: [entry({ mask: mask({ analysisStepResultId: 'z9' }) })] } as unknown as Item,
      { ...media('b', 10, 20), mask: mask() } as unknown as Item,
    ])]);
    expect(segmentationAnalysesOf(d)).toEqual(['a1', 'z9']);
  });
});

describe('cutout for the clip object mask', () => {
  it('carries the mask invert and clamped feather through the same maskVideo source', async () => {
    const { cutoutEffectOf } = await import('../region-effects');
    const cut = cutoutEffectOf(mask({ invert: true, featherPx: 99 }), [{ image: {} as CanvasImageSource }]);
    expect(cut.effect).toBe('cutout');
    expect(cut.matte).toMatchObject({ kind: 'maskVideo', invert: true, featherPx: 64 });
  });
});
