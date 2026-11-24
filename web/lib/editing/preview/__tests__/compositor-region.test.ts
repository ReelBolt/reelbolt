import { describe, expect, it } from 'vitest';
import { REGION_FRAGMENT_SHADER, createGlCompositor } from '../gl-compositor';
import { pixelBlockPx, type PreviewRegionEffect } from '../region-effects';

/** Fake WebGL2 recording uniforms by name and each draw's program (0 layer, 1 effects, 2 region, 3 pin). */
function setup(w: number, h: number) {
  const uniforms: Record<string, unknown[][]> = {};
  const draws: { program: number; framebuffer: number }[] = [];
  const programs: object[] = [];
  const fbos: object[] = [];
  let program = -1;
  let fbo = -1;
  const target: Record<string, unknown> = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, TEXTURE_2D: 13, TRIANGLES: 11, FRAMEBUFFER: 36160 };
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
        if (name === 'createFramebuffer') {
          const f = {};
          fbos.push(f);
          return f;
        }
        if (name === 'useProgram') program = programs.indexOf(args[0] as object);
        if (name === 'bindFramebuffer') fbo = args[1] === null ? -1 : fbos.indexOf(args[1] as object);
        if (name.startsWith('uniform') && typeof args[0] === 'string') (uniforms[args[0]] ??= []).push(args.slice(1));
        if (name === 'drawArrays') draws.push({ program, framebuffer: fbo });
        if (name.startsWith('create')) return {};
        return null;
      };
    },
  }) as unknown as WebGL2RenderingContext;
  const canvas = { width: w, height: h, getContext: () => gl, addEventListener: () => {}, removeEventListener: () => {} } as unknown as HTMLCanvasElement;
  const compositor = createGlCompositor(canvas)!;
  compositor.resize(w, h);
  return { compositor, uniforms, draws };
}
const image = { width: 1920, height: 1080 } as unknown as CanvasImageSource;
const boxes = (over: Partial<{ invert: boolean; featherPx: number }> = {}): PreviewRegionEffect['matte'] => ({ kind: 'boxes', matte: { boxes: [{ x: 0.1, y: 0.2, w: 0.3, h: 0.4 }], invert: false, featherPx: 8, ...over } });
const fx = (effect: PreviewRegionEffect['effect'], strength: number, matte = boxes()): PreviewRegionEffect => ({ effect, strength, matte, key: 'b0' });
const last = (u: Record<string, unknown[][]>, n: string) => u[n].at(-1)!;

describe('region effect passes', () => {
  it('the region shader holds the render looks', () => {
    expect(REGION_FRAGMENT_SHADER).toContain('5 outline'); // outline: the final else branch
    expect(REGION_FRAGMENT_SHADER).toContain('1.0 - cov'); // outside dimming / desaturation
    expect(REGION_FRAGMENT_SHADER).toContain('uMask3');
  });

  it('a clip with no region effect never touches the region program', () => {
    const { compositor, draws } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image, opacity: 1 }]);
    expect(draws.map((d) => d.program)).toEqual([0]);
  });

  it('blur: two blur passes on the small copy, the matte composite, then the layer draws from the result', () => {
    const { compositor, uniforms, draws } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image, opacity: 1, regionEffects: [fx('blur', 1)] }]);
    expect(draws.map((d) => d.program)).toEqual([1, 1, 2, 0]);
    expect(last(uniforms, 'uMode')[0]).toBe(0);
    expect(last(uniforms, 'uSrc')).toEqual([0]);
    expect(last(uniforms, 'uBoxN')).toEqual([1]);
    expect(last(uniforms, 'uInvert')).toEqual([0]);
  });

  it('pixelate uses the render block size on the working picture; the box feather is the 3.2 sigma ramp', () => {
    const { compositor, uniforms, draws } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image, opacity: 1, regionEffects: [fx('pixelate', 0.5)] }]);
    expect(draws.map((d) => d.program)).toEqual([2, 0]);
    // 1920x1080 is capped at a 1280 long side: 1280x720, short side 720.
    expect(last(uniforms, 'uP')[0]).toBe(pixelBlockPx(720, 0.5));
    expect(last(uniforms, 'uSize')).toEqual([1280, 720]);
    expect(last(uniforms, 'uFeather')[0]).toBeCloseTo(3.2 * 8, 9);
  });

  it('highlight, spotlight, colour pop and outline carry their parameters; effects chain in order (ping-pong targets)', () => {
    const { compositor, uniforms, draws } = setup(960, 540);
    compositor.draw([{ layerId: 'a', image, opacity: 1, regionEffects: [fx('highlight', 1), fx('spotlight', 1), fx('colorpop', 1), fx('outline', 1)] }]);
    expect(draws.map((d) => d.program)).toEqual([2, 2, 2, 2, 0]);
    expect(uniforms.uMode.map((m) => m[0])).toEqual([2, 3, 4, 5]);
    expect(uniforms.uP[0]).toEqual([0.1, 1.1, 0.15, 0]);
    expect(uniforms.uP[1][0]).toBeCloseTo(0.6, 6);
    expect(uniforms.uP[3][0]).toBe(3); // outline band on a 720 short side: round(2.88)
    // Each effect reads the previous one's target: the framebuffers alternate.
    const targets = draws.slice(0, 4).map((d) => d.framebuffer);
    expect(targets[0]).not.toBe(targets[1]);
    expect(targets[0]).toBe(targets[2]);
    expect(targets[1]).toBe(targets[3]);
  });

  it('a mask video binds one frame per track and switches the shader to the decoded matte', () => {
    const { compositor, uniforms } = setup(960, 540);
    const frame = { image: { width: 640, height: 360 } as unknown as CanvasImageSource };
    const matte: PreviewRegionEffect['matte'] = { kind: 'maskVideo', frames: [frame, { image: { width: 640, height: 360 } as unknown as CanvasImageSource }], invert: true, featherPx: 4 };
    compositor.draw([{ layerId: 'a', image, opacity: 1, regionEffects: [{ effect: 'blur', strength: 1, matte, key: 'v0' }] }]);
    expect(last(uniforms, 'uSrc')).toEqual([1]);
    expect(last(uniforms, 'uMaskN')).toEqual([2]);
    expect(last(uniforms, 'uInvert')).toEqual([1]);
    expect(last(uniforms, 'uBoxN')).toEqual([0]);
  });
});

describe('object mask as a cutout (clip.mask)', () => {
  it('runs the region pass in cutout mode (alpha = the decoded matte) and drops the box matte from the layer draw', () => {
    const { compositor, uniforms, draws } = setup(960, 540);
    const frame = { image: { width: 640, height: 360 } as unknown as CanvasImageSource };
    compositor.draw([{ layerId: 'a', image, opacity: 1, regionEffects: [{ effect: 'cutout', strength: 1, key: 'cut', matte: { kind: 'maskVideo', frames: [frame], invert: false, featherPx: 0 } }] }]);
    expect(draws.map((d) => d.program)).toEqual([2, 0]);
    expect(last(uniforms, 'uMode')[0]).toBe(6);
    expect(last(uniforms, 'uSrc')).toEqual([1]);
    expect(REGION_FRAGMENT_SHADER).toContain('base.a * cov');
  });
});
