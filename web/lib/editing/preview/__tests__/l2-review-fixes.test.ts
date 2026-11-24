import { describe, expect, it } from 'vitest';
import { classifyItem } from '../parity';
import { matteSourceKindFor } from '../region-effects';
import { insertQuadAt } from '../live-geometry';
import { createGlCompositor } from '../gl-compositor';
import type { Item, RegionClipEffect, SegmentationMask } from '../../types';
import type { SegmentationTrack } from '../segmentation-tracks';
import { media } from '../../__tests__/fixtures';

const ids = ['g1', 'g2', 'g3', 'g4', 'g5'];
const mask = (trackIds: string[]): SegmentationMask => ({ kind: 'segmentation', analysisStepResultId: 'a1', trackIds, invert: false, featherPx: 0 });
const tr = (id: string, state: 'ready' | 'pending' | 'none' = 'ready'): SegmentationTrack => ({ trackId: id, label: 'p', firstFrame: 0, lastFrame: 100, fps: 25, boxKeyframes: [{ t: 0, x: 0, y: 0, w: 1, h: 1 }], maskPreview: { state } });
const lookup = (id: string) => (id === 'a1' ? ids.map((i) => tr(i)) : undefined);
const withRegion = (m: SegmentationMask) => ({ ...media('c1', 0, 100), regionEffects: [{ effect: 'blur', strength: 1, mask: m, enabled: true } as RegionClipEffect] }) as unknown as Item;

describe('QA 3: parity and the draw path share one predicate', () => {
  it('five ready tracks cannot use the mask video, so parity is not "ready"', () => {
    expect(matteSourceKindFor(mask(ids), lookup, true)).toBe('boxes');
    const r = classifyItem(withRegion(mask(ids)), undefined, { tracksFor: lookup });
    expect(r.reason).toBe('The preview follows a box around the object; the render follows its exact outline.');
    const seg = classifyItem({ ...media('c1', 0, 100), mask: mask(ids) } as unknown as Item, undefined, { tracksFor: lookup });
    expect(seg.parity).toBe('approximate');
  });
  it('four ready tracks are drawn from the mask video and the segmentation mask is approximate (close, not identical)', () => {
    const four = ids.slice(0, 4);
    expect(matteSourceKindFor(mask(four), lookup, true)).toBe('maskVideo');
    expect(classifyItem({ ...media('c1', 0, 100), mask: mask(four) } as unknown as Item, undefined, { tracksFor: lookup }).reason).toMatch(/small copy of the mask/);
  });
});

describe('QA 6/8: region effects are never faithful; without box data the text says "not loaded yet"', () => {
  it('ready mask video: approximate with the close-not-identical reason', () => {
    const r = classifyItem(withRegion(mask(['g1'])), undefined, { tracksFor: lookup });
    expect(r.parity).toBe('approximate');
    expect(r.reason).toMatch(/small copy of the mask/);
  });
  it('no data at all: render-only with the not-loaded text', () => {
    const r = classifyItem(withRegion(mask(['g1'])), undefined, { tracksFor: () => undefined });
    expect(r.parity).toBe('renderRequired');
    expect(r.reason).toMatch(/not loaded yet/);
  });
});

describe('QA 2: an insert previews as a perspective warp, its chroma matte included', () => {
  const quad = { topLeft: { x: 0.1, y: 0.1 }, topRight: { x: 0.9, y: 0.1 }, bottomRight: { x: 0.9, y: 0.9 }, bottomLeft: { x: 0.1, y: 0.9 } };
  const insert = (extra: object) => ({ id: 'i', type: 'insertClip', name: 'i', recordIn: 0, recordOut: 10, quad: { value: quad }, ...extra }) as unknown as Item;
  it('keyed insert: the matte is drawn, so it makes no "not previewed" claim', () => {
    const r = classifyItem(insert({ matte: { color: '#00ff00' } }));
    expect(r.parity).toBe('approximate');
    expect(r.reason).toBe('The insert is previewed as a perspective warp; edges may differ by a pixel.');
  });
  it('plain insert makes no claim about a key', () => {
    const r = classifyItem(insert({}));
    expect(r.parity).toBe('approximate');
    expect(r.reason).not.toMatch(/chroma/);
  });
});

describe('QA 5: a keyed insert quad decides its units once across the base value and every key', () => {
  it('a later key that overshoots 1 makes every frame pixels, as in the export', () => {
    const inRange = { topLeft: { x: 0.1, y: 0.1 }, topRight: { x: 0.9, y: 0.1 }, bottomRight: { x: 0.9, y: 0.9 }, bottomLeft: { x: 0.1, y: 0.9 } };
    const over = { topLeft: { x: 0.5, y: 0.1 }, topRight: { x: 1.3, y: 0.1 }, bottomRight: { x: 1.3, y: 0.9 }, bottomLeft: { x: 0.5, y: 0.9 } };
    const q = insertQuadAt({ quad: { value: inRange, keys: [{ frame: 0, value: inRange, interp: 'linear' }, { frame: 30, value: over, interp: 'linear' }] } } as never, 0, 1920, 1080);
    expect(q![1]![0]).toBeCloseTo(0.9 / 1920, 9);
  });
  it('all-fraction keys stay fractions', () => {
    const a = { topLeft: { x: 0.1, y: 0.1 }, topRight: { x: 0.9, y: 0.1 }, bottomRight: { x: 0.9, y: 0.9 }, bottomLeft: { x: 0.1, y: 0.9 } };
    const q = insertQuadAt({ quad: { value: a, keys: [{ frame: 0, value: a, interp: 'linear' }] } } as never, 0, 1920, 1080);
    expect(q![1]![0]).toBeCloseTo(0.9, 9);
  });
});

describe('QA 4: the shape mask frame follows TransformFilterBuilder.Build', () => {
  const setup = () => {
    const uniforms: Record<string, unknown[][]> = {};
    const target: Record<string, unknown> = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, TEXTURE_2D: 13, TRIANGLES: 11 };
    const gl = new Proxy(target, {
      get(t, name: string) {
        if (name in t) return t[name];
        return (...args: unknown[]) => {
          if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
          if (name === 'getUniformLocation') return String(args[1]);
          if (name.startsWith('uniform') && typeof args[0] === 'string') (uniforms[args[0]] ??= []).push(args.slice(1));
          if (name.startsWith('create')) return {};
          return null;
        };
      },
    }) as unknown as WebGL2RenderingContext;
    const canvas = { width: 960, height: 540, getContext: () => gl, addEventListener: () => {}, removeEventListener: () => {} } as unknown as HTMLCanvasElement;
    const compositor = createGlCompositor(canvas)!;
    compositor.resize(960, 540);
    return { compositor, uniforms };
  };
  const image = { width: 1920, height: 1080 } as unknown as CanvasImageSource;
  const shape = { kind: 'rect' as const, x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.1, invert: false };
  it('canvas frame only when padded (no transform, crop or fade); otherwise the picture\'s own frame', () => {
    const a = setup();
    a.compositor.draw([{ layerId: 'a', image, opacity: 1, shape, padded: true }]);
    expect(a.uniforms.uLocalMode.at(-1)).toEqual([1]);
    const b = setup();
    b.compositor.draw([{ layerId: 'a', image, opacity: 1, shape, padded: false }]); // e.g. a fade only: no transform object drawn
    expect(b.uniforms.uLocalMode.at(-1)).toEqual([0]);
  });
});
