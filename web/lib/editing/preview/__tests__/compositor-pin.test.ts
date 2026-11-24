import { describe, expect, it } from 'vitest';
import { FRAGMENT_SHADER, PIN_FRAGMENT_SHADER, VERTEX_SHADER, createGlCompositor } from '../gl-compositor';
import { pinInverseColumnMajor, type Quad } from '../live-geometry';
import { insertAsMedia } from '../live-session';
import type { InsertClip } from '../../types';

/**
 * A recording fake of the WebGL2 calls: uniform values by name, and every draw with the program that ran it
 * (programs are numbered in creation order: 0 = layer, 1 = effects, 2 = region, 3 = pin) and the framebuffer clear colour.
 */
function recordingGl() {
  const uniforms: Record<string, unknown[][]> = {};
  const draws: { program: number; clear: number[]; viewport: number[] }[] = [];
  const programs: object[] = [];
  let current = -1;
  let clear: number[] = [];
  let viewport: number[] = [];
  const target: Record<string, unknown> = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, TEXTURE_2D: 13, TRIANGLES: 11 };
  const gl = new Proxy(target, {
    get(t, name: string) {
      if (name in t) return t[name];
      return (...args: unknown[]) => {
        if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
        if (name === 'getUniformLocation') return String(args[1]);
        if (name === 'createProgram') {
          const p = {};
          programs.push(p);
          return p;
        }
        if (name === 'useProgram') current = programs.indexOf(args[0] as object);
        if (name === 'viewport') viewport = args.map(Number);
        if (name === 'clearColor') clear = args.map(Number);
        if (name.startsWith('uniform') && typeof args[0] === 'string') (uniforms[args[0]] ??= []).push(args.slice(1));
        if (name === 'drawArrays') draws.push({ program: current, clear: [...clear], viewport: [...viewport] });
        if (name.startsWith('create')) return {};
        return null;
      };
    },
  }) as unknown as WebGL2RenderingContext;
  return { gl, uniforms, draws };
}

function setup(canvasW: number, canvasH: number) {
  const rec = recordingGl();
  const canvas = { width: canvasW, height: canvasH, getContext: () => rec.gl, addEventListener: () => {}, removeEventListener: () => {} } as unknown as HTMLCanvasElement;
  const compositor = createGlCompositor(canvas)!;
  compositor.resize(canvasW, canvasH);
  return { ...rec, compositor };
}
const widescreen = { width: 1920, height: 1080 } as unknown as CanvasImageSource;
const crop0 = { left: 0, right: 0, top: 0, bottom: 0 };
const last = (rec: Record<string, unknown[][]>, name: string) => rec[name].at(-1)!;
const quad: Quad = [[0.1, 0.2], [0.9, 0.1], [0.8, 0.9], [0.2, 0.8]];

describe('shader sources carry the render maths', () => {
  it('the layer shaders know the shape mask and its local frame; the pin shader divides per fragment', () => {
    expect(VERTEX_SHADER).toContain('vLocal');
    expect(FRAGMENT_SHADER).toContain('float shapeMask()');
    expect(FRAGMENT_SHADER).toContain('max(0.0005, uShapeP.x)'); // the render's feather floor
    expect(FRAGMENT_SHADER).toContain('max(vec2(0.001), uShape.zw * 0.5)'); // the ellipse radius floor
    expect(PIN_FRAGMENT_SHADER).toContain('h.xy / h.z'); // projective divide, not an affine UV interpolation
    expect(PIN_FRAGMENT_SHADER).toContain('uniform mat3 uInv');
  });
});

describe('reframe in the vertex path', () => {
  it('an untransformed layer samples the render crop window, and the fit sees the reframed picture', () => {
    const { compositor, uniforms } = setup(540, 960);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, aspectPolicy: 'fit', reframe: { x: 0.5, y: 0.5 } }]);
    expect(last(uniforms, 'uUvOrigin')[0]).toBeCloseTo(656.25 / 1920, 6);
    expect(last(uniforms, 'uUvOrigin')[1]).toBe(0);
    expect(last(uniforms, 'uUvSize')[0]).toBeCloseTo(607.5 / 1920, 6);
    expect(last(uniforms, 'uUvSize')[1]).toBe(1);
    // 607.5 x 1080 is the canvas's 9:16: no letterbox. Without the reframe the whole 16:9 picture would fit at 540 x 304.
    expect(last(uniforms, 'uScale')[0]).toBeCloseTo(1, 6);
    expect(last(uniforms, 'uScale')[1]).toBeCloseTo(1, 6);
  });

  it('no reframe keeps the whole picture; the clip crop becomes a window INSIDE the reframe window', () => {
    const plain = setup(540, 960);
    plain.compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, aspectPolicy: 'fit' }]);
    expect(last(plain.uniforms, 'uUvSize')).toEqual([1, 1]);
    const { compositor, uniforms } = setup(540, 960);
    const t = { x: 0, y: 0, scale: 1, rotation: 0, opacity: 1, crop: { ...crop0, left: 0.5 } };
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, transform: t, reframe: { x: 0, y: 0 } }]);
    // Reframe window at x = 0, w = 607.5 / 1920; crop left 0.5 of THAT window.
    expect(last(uniforms, 'uUvOrigin')[0]).toBeCloseTo((0.5 * 607.5) / 1920, 6);
    expect(last(uniforms, 'uUvSize')[0]).toBeCloseTo((0.5 * 607.5) / 1920, 6);
    expect(last(uniforms, 'uUvSize')[1]).toBe(1);
  });
});

describe('shape mask uniforms', () => {
  it('an untransformed layer masks in canvas fractions; a transformed one in its own frame; none turns it off', () => {
    const { compositor, uniforms } = setup(960, 540);
    const shape = { kind: 'ellipse' as const, x: 0.4, y: 0.6, w: 0.3, h: 0.2, feather: 0.05, invert: true };
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, shape }]);
    expect(last(uniforms, 'uShapeOn')).toEqual([1]);
    expect(last(uniforms, 'uShape').map(Number)).toEqual([0.4, 0.6, 0.3, 0.2]);
    expect(last(uniforms, 'uShapeP').map((v) => Math.round(Number(v) * 1e6) / 1e6)).toEqual([0.05, 1, 1, 0]);
    expect(last(uniforms, 'uLocalMode')).toEqual([1]);
    const t = { x: 0, y: 0, scale: 0.5, rotation: 0, opacity: 1, crop: crop0 };
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, transform: t, shape: { ...shape, kind: 'rect', invert: false } }]);
    expect(last(uniforms, 'uLocalMode')).toEqual([0]);
    expect(last(uniforms, 'uShapeP').map((v) => Math.round(Number(v) * 1e6) / 1e6)).toEqual([0.05, 0, 0, 0]);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1 }]);
    expect(last(uniforms, 'uShapeOn')).toEqual([0]);
  });

  it('with effects the mask is on for the picture pass and off for the final full-canvas composite', () => {
    const { compositor, uniforms } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, effects: [{ type: 'mono', amount: 1 }], shape: { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.1, invert: false } }]);
    expect(uniforms.uShapeOn.map((v) => v[0])).toEqual([1, 0]);
  });

  it('a masked layer clears its padding to transparent (the render swaps the opaque bars for transparent ones)', () => {
    const { compositor, draws } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image: { width: 1080, height: 1920 } as unknown as CanvasImageSource, opacity: 1, aspectPolicy: 'fit', effects: [{ type: 'mono', amount: 1 }], shape: { kind: 'rect', x: 0.5, y: 0.5, w: 0.5, h: 0.5, feather: 0.1, invert: false } }]);
    expect(draws[0].clear[3]).toBe(0);
  });
});

describe('corner pin / insert quad pass', () => {
  it('renders the layer on a transparent canvas-shaped target, then warps it with the INVERSE homography of the quad', () => {
    const { compositor, uniforms, draws } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 0.5, aspectPolicy: 'fit', pin: quad }]);
    expect(draws.map((d) => d.program)).toEqual([0, 3]); // the picture on the layer program, then the pin program
    expect(draws[0].clear[3]).toBe(0);
    expect(draws[0].viewport.slice(2)).toEqual([960, 540]);
    const sent = last(uniforms, 'uInv')[1] as Float32Array;
    const expected = pinInverseColumnMajor(quad)!;
    expect(Array.from(sent)).toEqual(Array.from(expected));
    expect(last(uniforms, 'uOpacity')).toEqual([0.5]);
    expect(last(uniforms, 'uPx')[0]).toBeCloseTo(1 / 960, 9);
    expect(last(uniforms, 'uPx')[1]).toBeCloseTo(1 / 540, 9);
  });

  it('keeps the effects chain in front of the pin: picture, effect passes, then the pin (no flipped composite)', () => {
    const { compositor, draws } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, effects: [{ type: 'mono', amount: 1 }], pin: quad }]);
    expect(draws.map((d) => d.program)).toEqual([0, 1, 3]);
  });

  it('a pin the render cannot build (degenerate quad, null) draws nothing for that layer', () => {
    const { compositor, draws } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, pin: null }]);
    compositor.draw([{ layerId: 'b', image: widescreen, opacity: 1, pin: [[0.2, 0.2], [0.2, 0.2], [0.2, 0.2], [0.2, 0.2]] }]);
    expect(draws).toHaveLength(0);
  });

  it('an unpinned layer is unchanged: one draw on the layer program', () => {
    const { compositor, draws } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1 }]);
    expect(draws.map((d) => d.program)).toEqual([0]);
  });
});

describe('insert clips in the live session', () => {
  const insert = (over: Partial<InsertClip> = {}): InsertClip => ({
    id: 'i1', name: 'Screen', type: 'insertClip', recordIn: 10, recordOut: 40,
    assetRef: { kind: 'projectFile', projectFileId: 'f1' }, enabled: true,
    quad: { value: { topLeft: { x: 0.1, y: 0.1 }, topRight: { x: 0.9, y: 0.1 }, bottomRight: { x: 0.9, y: 0.9 }, bottomLeft: { x: 0.1, y: 0.9 } } },
    ...over,
  });

  it('decodes as a media clip that plays from the start of its asset', () => {
    const clip = insertAsMedia(insert());
    expect(clip.type).toBe('mediaClip');
    expect(clip.sourceIn).toBe(0);
    expect(clip.assetRef).toEqual({ kind: 'projectFile', projectFileId: 'f1' });
    expect([clip.recordIn, clip.recordOut]).toEqual([10, 40]);
  });
});
