import { describe, expect, it } from 'vitest';
import { createGlCompositor } from '../gl-compositor';
import { sourceTimeSec } from '../scheduler';
import { classifyItem } from '../parity';
import { boxAt, hasBoxData, maskBindsClip, matteFor, segmentationAnalysesOf, type SegmentationTrack } from '../segmentation-tracks';
import type { EditTimeline, Item, SegmentationMask } from '../../types';
import { doc, media, track, TRACKS } from '../../__tests__/fixtures';

const track1: SegmentationTrack = {
  trackId: 'g1', label: 'person', firstFrame: 0, lastFrame: 100, fps: 25,
  boxKeyframes: [{ t: 0, x: 0.1, y: 0.2, w: 0.2, h: 0.4 }, { t: 2, x: 0.5, y: 0.2, w: 0.2, h: 0.4 }, { t: 4, x: 0.5, y: 0.6, w: 0.4, h: 0.2 }],
};
const track2: SegmentationTrack = { trackId: 'g2', label: 'car', firstFrame: 50, lastFrame: 100, fps: 25, boxKeyframes: [{ t: 2, x: 0, y: 0, w: 0.1, h: 0.1 }] };
const mask = (over: Partial<SegmentationMask> = {}): SegmentationMask => ({ kind: 'segmentation', analysisStepResultId: 'a1', trackIds: ['g1'], invert: false, featherPx: 8, ...over });
const lookup = (id: string) => (id === 'a1' ? [track1, track2] : undefined);

describe('boxAt', () => {
  it('interpolates linearly in source seconds and holds the ends', () => {
    expect(boxAt(track1, 1)!.x).toBeCloseTo(0.3, 9);
    expect(boxAt(track1, 1)!.h).toBeCloseTo(0.4, 9);
    const b = boxAt(track1, 3)!;
    expect([b.x, b.y, b.w, b.h].map((v) => Math.round(v * 1000) / 1000)).toEqual([0.5, 0.4, 0.3, 0.3]);
    expect(boxAt(track1, 0)).toEqual({ x: 0.1, y: 0.2, w: 0.2, h: 0.4 });
  });
  it('is null outside the track frame coverage and with no keyframes', () => {
    expect(boxAt({ ...track1, firstFrame: 25, lastFrame: 75 }, 0.5)).toBeNull(); // before frame 25 (1 s)
    expect(boxAt({ ...track1, firstFrame: 25, lastFrame: 75 }, 3.5)).toBeNull(); // after frame 75 (3 s)
    expect(boxAt({ ...track1, boxKeyframes: [] }, 1)).toBeNull();
  });
});

describe('matteFor', () => {
  it('unions the named tracks only, honours invert and clamps the feather', () => {
    const m = matteFor(mask({ trackIds: ['g1', 'g2'], featherPx: 200 }), lookup, 2)!;
    expect(m.boxes).toHaveLength(2);
    expect(m.featherPx).toBe(64);
    expect(matteFor(mask({ trackIds: ['g1'] }), lookup, 2)!.boxes).toHaveLength(1);
    expect(matteFor(mask({ invert: true }), lookup, 2)!.invert).toBe(true);
  });
  it('is EMPTY (zero boxes) where the object is absent, and the invert flag still applies', () => {
    const away = matteFor(mask({ trackIds: ['g2'] }), lookup, 0)!; // g2 starts at frame 50 (2 s)
    expect(away).toEqual({ boxes: [], invert: false, featherPx: 8 });
    expect(matteFor(mask({ trackIds: ['g2'], invert: true }), lookup, 0)).toEqual({ boxes: [], invert: true, featherPx: 8 });
    expect(matteFor(mask(), lookup, 9)!.boxes).toEqual([]); // after g1's last frame
  });
  it('is null (unmatted) when the tracks are not loaded or carry no box data at all', () => {
    expect(matteFor(mask(), () => undefined, 1)).toBeNull();
    expect(matteFor(mask(), undefined, 1)).toBeNull();
    expect(matteFor(mask(), () => [{ ...track1, boxKeyframes: [] }], 1)).toBeNull();
  });
  it('follows the decoded picture at a playback rate other than 1, reversed and held', () => {
    const rate = { num: 25, den: 1 };
    const at = (clip: object, frame: number) => matteFor(mask(), lookup, sourceTimeSec({ sourceIn: 0, recordIn: 0, recordOut: 100, ...clip } as never, frame, rate))!.boxes[0];
    // track1 keys: t=0 x=.1, t=2 x=.5, t=4 x=.5 (y .2 -> .6).
    expect(at({ playbackRate: 2 }, 25)!.x).toBeCloseTo(0.5, 9); // frame 25 x2 = source frame 50 = 2 s
    expect(at({ reverse: true }, 75)!.x).toBeCloseTo(0.3, 9); // (100 - 75) = 25 source frames = 1 s from the clip's end
    expect(at({ hold: true, sourceIn: 25 }, 90)!.x).toBeCloseTo(0.3, 9); // a hold reads sourceIn (1 s) at every frame
  });
});

describe('segmentation parity', () => {
  const clip = (m: unknown, extra: object = {}) => ({ ...media('c1', 0, 100), mask: m, ...extra }) as unknown as Item;
  it('approximate with the agreed reason when box data is there', () => {
    const p = classifyItem(clip(mask()), undefined, { tracksFor: lookup });
    expect(p).toMatchObject({ parity: 'approximate', reason: 'The preview follows a box around the object; the render follows its exact outline.' }); // no maskPreview = none: never "preparing"
  });
  it('renderRequired with a plain reason when there are no boxes (not loaded, refused, empty)', () => {
    for (const l of [undefined, () => undefined, () => [{ ...track1, boxKeyframes: [] }]]) {
      const p = classifyItem(clip(mask()), undefined, { tracksFor: l });
      expect(p.parity).toBe('renderRequired');
      expect(p.reason).not.toMatch(/shape mask/);
    }
  });
  it('approximate (a small copy of the mask) once every named track has its mask preview ready; pending or none stays a box', () => {
    const ready = (state: 'ready' | 'pending' | 'none') => (id: string) => (id === 'a1' ? [{ ...track1, maskPreview: { state } }, track2] : undefined);
    expect(classifyItem(clip(mask()), undefined, { tracksFor: ready('ready') })).toMatchObject({ parity: 'approximate', reason: expect.stringMatching(/small copy of the mask/) });
    expect(classifyItem(clip(mask()), undefined, { tracksFor: ready('pending') }).reason).toBe('Outline preview still preparing, showing a box.');
    expect(classifyItem(clip(mask()), undefined, { tracksFor: ready('none') }).reason).toBe('The preview follows a box around the object; the render follows its exact outline.');
    // A ready mask video needs no box data.
    const noBoxes = (id: string) => (id === 'a1' ? [{ ...track1, boxKeyframes: [], maskPreview: { state: 'ready' as const } }] : undefined);
    expect(classifyItem(clip(mask()), undefined, { tracksFor: noBoxes })).toMatchObject({ parity: 'approximate', reason: expect.stringMatching(/small copy of the mask/) });
    // Every named track must be ready.
    expect(classifyItem(clip(mask({ trackIds: ['g1', 'g2'] })), undefined, { tracksFor: (id) => (id === 'a1' ? [{ ...track1, maskPreview: { state: 'ready' as const } }, { ...track2, maskPreview: { state: 'pending' as const } }] : undefined) }).reason).toBe('Outline preview still preparing, showing a box.');
  });
  it('the no-box text says the unmasked picture is shown dimmed', () => {
    expect(classifyItem(clip(mask()), undefined, { tracksFor: () => undefined }).reason).toMatch(/unmasked picture dimmed/);
  });
  it('a shape mask is approximate with the shape reason; the mask reason leads over effects', () => {
    const shape = classifyItem(clip({ kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5 }));
    expect(shape.parity).toBe('approximate');
    expect(shape.reason).toMatch(/shape mask/);
    const p = classifyItem(clip(mask(), { effects: [{ type: 'blur', amount: 0.5 }] }), undefined, { tracksFor: lookup });
    expect(p.reason).toMatch(/box around the object/);
  });
  it('hasBoxData needs a named track with keyframes', () => {
    expect(hasBoxData(mask(), lookup)).toBe(true);
    expect(hasBoxData(mask({ trackIds: ['g3'] }), lookup)).toBe(false);
  });
});

describe('coverage: the binding rule (SegmentationMaskBinding.cs:73-82)', () => {
  // 25 fps timeline; tracks hold 4 s (coverageEndSec) from source 0.
  const rate = { num: 25, den: 1 };
  const withCoverage = (id: string, coverageEndSec: number): SegmentationTrack => ({ ...track1, trackId: id, coverageEndSec });
  const cov = (...tracks: SegmentationTrack[]) => (id: string) => (id === 'a1' ? tracks : undefined);
  const clip = (over: object) => ({ sourceIn: 0, recordIn: 0, recordOut: 100, ...over }) as Parameters<typeof maskBindsClip>[0];
  const d = (): EditTimeline => ({ ...doc([track(TRACKS.v1, 'video', [])]), settings: { ...doc([]).settings, rate } });

  it('end = sourceIn/fps + span*rate/fps must be <= coverage + 1 frame (the C# arithmetic, line by line)', () => {
    const l = cov(withCoverage('g1', 4));
    expect(maskBindsClip(clip({}), mask(), l, rate)).toBe(true); // end 4.0 == coverage
    expect(maskBindsClip(clip({ recordOut: 101 }), mask(), l, rate)).toBe(true); // end 4.04 == coverage + 1 frame: tolerated
    expect(maskBindsClip(clip({ recordOut: 102 }), mask(), l, rate)).toBe(false); // end 4.08 > 4.04
    expect(maskBindsClip(clip({ sourceIn: 25 }), mask(), l, rate)).toBe(false); // starts at 1 s: end 5.0
    expect(maskBindsClip(clip({ playbackRate: 2, recordOut: 50 }), mask(), l, rate)).toBe(true); // 50 frames x2 = 4 s
    expect(maskBindsClip(clip({ playbackRate: 2, recordOut: 60 }), mask(), l, rate)).toBe(false); // 4.8 s
    expect(maskBindsClip(clip({ playbackRate: 0, recordOut: 100 }), mask(), l, rate)).toBe(true); // non-positive rate counts as 1
    expect(maskBindsClip(clip({ hold: true, sourceIn: 99, recordOut: 1000 }), mask(), l, rate)).toBe(true); // a hold is one frame: 3.96 + 0.04
  });
  it('EVERY named track must cover; coverage is from source 0, not the first non-empty frame', () => {
    const l = cov(withCoverage('g1', 4), { ...withCoverage('g2', 2), firstFrame: 0 });
    expect(maskBindsClip(clip({}), mask({ trackIds: ['g1'] }), l, rate)).toBe(true);
    expect(maskBindsClip(clip({}), mask({ trackIds: ['g1', 'g2'] }), l, rate)).toBe(false);
    // g2 starts late (firstFrame 50) but its video still holds frames from source 0 to 4 s: covered.
    expect(maskBindsClip(clip({}), mask({ trackIds: ['g2'] }), cov({ ...withCoverage('g2', 4), firstFrame: 50 }), rate)).toBe(true);
  });
  it('is unknown while tracks, a named track or its coverage are not available', () => {
    expect(maskBindsClip(clip({}), mask(), () => undefined, rate)).toBeUndefined();
    expect(maskBindsClip(clip({}), mask({ trackIds: ['g9'] }), cov(withCoverage('g1', 4)), rate)).toBeUndefined();
    expect(maskBindsClip(clip({}), mask(), cov(track1), rate)).toBeUndefined();
  });
  it('parity is renderRequired with the coverage reason for a refused clip, approximate for a bound one', () => {
    const l = cov(withCoverage('g1', 4));
    const c = (recordOut: number) => ({ ...media('c1', 0, recordOut), mask: mask() }) as unknown as Item;
    expect(classifyItem(c(100), d(), { tracksFor: l }).parity).toBe('approximate');
    const past = classifyItem(c(200), d(), { tracksFor: l });
    expect(past.parity).toBe('renderRequired');
    expect(past.reason).toMatch(/does not cover this part of the clip/);
  });
});

describe('segmentationAnalysesOf', () => {
  it('lists the distinct analyses of segmentation masks only', () => {
    const d: EditTimeline = doc([track(TRACKS.v1, 'video', [
      { ...media('a', 0, 10), mask: mask() } as unknown as Item,
      { ...media('b', 10, 20), mask: mask() } as unknown as Item,
      { ...media('c', 20, 30), mask: { kind: 'rect' } } as unknown as Item,
    ])]);
    expect(segmentationAnalysesOf(d)).toEqual(['a1']);
  });
});

describe('the matte in the compositor', () => {
  it('sets the matte uniforms for a masked layer and turns them off for others', () => {
    const calls: [string, unknown[]][] = [];
    const target: Record<string, unknown> = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, TEXTURE_2D: 13, TRIANGLES: 11 };
    const gl = new Proxy(target, {
      get(t, name: string) {
        if (name in t) return t[name];
        return (...args: unknown[]) => {
          if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
          if (name === 'getUniformLocation') return String(args[1]);
          if (name.startsWith('uniform')) calls.push([String(args[0]), args.slice(1)]);
          if (name.startsWith('create')) return {};
          return null;
        };
      },
    }) as unknown as WebGL2RenderingContext;
    const canvas = { width: 320, height: 180, getContext: () => gl, addEventListener: () => {}, removeEventListener: () => {} } as unknown as HTMLCanvasElement;
    const compositor = createGlCompositor(canvas)!;
    compositor.resize(320, 180);
    const image = { width: 1920, height: 1080 } as unknown as CanvasImageSource;
    compositor.draw([{ layerId: 'a', image, opacity: 1, matte: { boxes: [{ x: 0.1, y: 0.2, w: 0.3, h: 0.4 }], invert: true, featherPx: 12 } }]);
    const last = (n: string) => calls.filter(([k]) => k === n).at(-1)![1];
    expect(last('uMatteOn')).toEqual([1]);
    expect(last('uMatteN')).toEqual([1]);
    expect(last('uMatteInvert')).toEqual([1]);
    expect(last('uMatteFeather')[0]).toBeCloseTo(3.2 * 12, 9); // 3.2 sigma, no source width known = no proxy scaling
    expect(last('uSrcSize')).toEqual([1920, 1080]);
    expect(Array.from(last('uMatteBoxes')[0] as Float32Array).slice(0, 4).map((v) => Math.round(v * 100) / 100)).toEqual([0.1, 0.2, 0.3, 0.4]);
    compositor.draw([{ layerId: 'a', image, opacity: 1 }]);
    expect(last('uMatteOn')).toEqual([0]);
  });

  it('applies the matte in source geometry, before crop and transform (pass 0 of the effects target)', () => {
    const calls: string[] = [];
    const target: Record<string, unknown> = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, TEXTURE_2D: 13, TRIANGLES: 11 };
    const gl = new Proxy(target, {
      get(t, name: string) {
        if (name in t) return t[name];
        return (...args: unknown[]) => {
          if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
          if (name === 'getUniformLocation') return String(args[1]);
          if (name === 'uniform1f' && args[0] === 'uMatteOn') calls.push(`matte${args[1]}`);
          if (name === 'drawArrays') calls.push('draw');
          if (name.startsWith('create')) return {};
          return null;
        };
      },
    }) as unknown as WebGL2RenderingContext;
    const canvas = { width: 320, height: 180, getContext: () => gl, addEventListener: () => {}, removeEventListener: () => {} } as unknown as HTMLCanvasElement;
    const compositor = createGlCompositor(canvas)!;
    compositor.resize(320, 180);
    const t = { x: 0, y: 0, scale: 2, rotation: 0, opacity: 1, crop: { left: 0.1, right: 0, top: 0, bottom: 0 } };
    compositor.draw([{ layerId: 'a', image: { width: 640, height: 360 } as unknown as CanvasImageSource, opacity: 1, transform: t, effects: [{ type: 'mono', amount: 1 }], matte: { boxes: [{ x: 0, y: 0, w: 1, h: 1 }], invert: false, featherPx: 0 } }]);
    // The matte is on for the source pass, off for the final full-canvas composite.
    expect(calls.filter((c) => c.startsWith('matte'))).toEqual(['matte1', 'matte0']);
  });
});

describe('matte edge cases in the compositor', () => {
  const harness = () => {
    const calls: [string, unknown[]][] = [];
    const clears: number[][] = [];
    const target: Record<string, unknown> = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, TEXTURE_2D: 13, TRIANGLES: 11 };
    const gl = new Proxy(target, {
      get(t, name: string) {
        if (name in t) return t[name];
        return (...args: unknown[]) => {
          if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
          if (name === 'getUniformLocation') return String(args[1]);
          if (name.startsWith('uniform')) calls.push([String(args[0]), args.slice(1)]);
          if (name === 'clearColor') clears.push(args.map(Number));
          if (name.startsWith('create')) return {};
          return null;
        };
      },
    }) as unknown as WebGL2RenderingContext;
    const canvas = { width: 540, height: 960, getContext: () => gl, addEventListener: () => {}, removeEventListener: () => {} } as unknown as HTMLCanvasElement;
    const compositor = createGlCompositor(canvas)!;
    compositor.resize(540, 960);
    const last = (n: string) => calls.filter(([k]) => k === n).at(-1)![1];
    return { compositor, last, clears };
  };
  const image = { width: 960, height: 540 } as unknown as CanvasImageSource;

  it('an empty matte stays on: coverage 0, or the whole picture when inverted', () => {
    for (const invert of [false, true]) {
      const { compositor, last } = harness();
      compositor.draw([{ layerId: 'a', image, opacity: 1, matte: { boxes: [], invert, featherPx: 4 } }]);
      expect(last('uMatteOn')).toEqual([1]);
      expect(last('uMatteN')).toEqual([0]);
      expect(last('uMatteInvert')).toEqual([invert ? 1 : 0]);
    }
  });

  it('a masked, letterboxed clip with an effect clears its pad to transparent; an unmasked one to opaque black', () => {
    const effects = [{ type: 'blur' as const, amount: 0.5 }];
    const masked = harness();
    masked.compositor.draw([{ layerId: 'a', image, opacity: 1, padded: true, effects, matte: { boxes: [], invert: true, featherPx: 0 } }]);
    // clears[0] is the frame clear; clears[1] is the effects target's pad.
    expect(masked.clears[1][3]).toBe(0);
    const plain = harness();
    plain.compositor.draw([{ layerId: 'a', image, opacity: 1, padded: true, effects }]);
    expect(plain.clears[1][3]).toBe(1);
  });

  it('the feather ramp is 3.2 sigma wide, scaled from source to proxy pixels (10-90% = 2.56 sigma)', () => {
    const { compositor, last } = harness();
    compositor.draw([{ layerId: 'a', image, opacity: 1, matte: { boxes: [{ x: 0, y: 0, w: 1, h: 1 }], invert: false, featherPx: 10, sourceWidth: 1920 } }]);
    // proxy 960 wide, source 1920: sigma 10 source px = 5 proxy px -> ramp 16.
    expect(last('uMatteFeather')).toEqual([16]);
  });
});

describe('a rotated clip turns its mask boxes with the picture', () => {
  it('a quarter turn clockwise sends (u, v) to (1 - v, u)', async () => {
    const { rotateMatteBox, rotateMatte } = await import('../segmentation-tracks');
    const box = { x: 0.1, y: 0.2, w: 0.3, h: 0.4 };
    const near = (a: { x: number; y: number; w: number; h: number }, b: typeof a) => {
      for (const k of ['x', 'y', 'w', 'h'] as const) expect(a[k]).toBeCloseTo(b[k], 10);
    };
    near(rotateMatteBox(box, 0), box);
    near(rotateMatteBox(box, 90), { x: 1 - (0.2 + 0.4), y: 0.1, w: 0.4, h: 0.3 });
    near(rotateMatteBox(box, 180), { x: 1 - (0.1 + 0.3), y: 1 - (0.2 + 0.4), w: 0.3, h: 0.4 });
    near(rotateMatteBox(box, 270), { x: 0.2, y: 1 - (0.1 + 0.3), w: 0.4, h: 0.3 });
    // Four quarter turns are the identity.
    let turned = box;
    for (let i = 0; i < 4; i++) turned = rotateMatteBox(turned, 90);
    near(turned, box);
    const matte = { boxes: [box], invert: true, featherPx: 3 };
    expect(rotateMatte(matte, 0)).toBe(matte);
    expect(rotateMatte(matte, null)).toBe(matte);
    const rotated = rotateMatte(matte, 90);
    expect(rotated).toMatchObject({ invert: true, featherPx: 3 });
    near(rotated.boxes[0], rotateMatteBox(box, 90));
  });
});
