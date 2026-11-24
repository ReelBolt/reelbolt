import { describe, expect, it } from 'vitest';
import { FX_FRAGMENT_SHADER, createGlCompositor, planEffectPasses, previewEffectsOf, rendersPadded } from '../gl-compositor';
import { classifyItem } from '../parity';
import type { Item } from '../../types';

const clip = (effects: unknown): Item => ({ id: 'c', type: 'mediaClip', name: 'c', effects } as unknown as Item);
const parityOf = (effects: unknown) => classifyItem(clip(effects));

describe('parity of clip effects', () => {
  it.each(['blur', 'pixelate', 'mono', 'vignette'])('%s previews approximately with a plain reason', (type) => {
    const p = parityOf([{ type, amount: 0.5 }]);
    expect(p.parity).toBe('approximate');
    expect(p.reason).not.toMatch(/render-only/);
  });
  it.each(['grain', 'sharpen'])('%s previews approximately', (type) => {
    const p = parityOf([{ type, amount: 0.5 }]);
    expect(p.parity).toBe('approximate');
    expect(p.reason).not.toMatch(/render-only/);
  });
  it('a mix with one unknown effect is renderRequired for that effect only; the others still preview', () => {
    const p = parityOf([{ type: 'blur', amount: 0.5 }, { type: 'grain', amount: 0.5 }, { type: 'wobble', amount: 0.5 }]);
    expect(p.parity).toBe('renderRequired');
    expect(p.findings.map((f) => f.reason).some((r) => /Blur, pixelate/.test(r))).toBe(true);
    expect(parityOf([{ type: 'blur', amount: 0.5 }, { type: 'grain', amount: 0.5 }]).parity).toBe('approximate');
    expect(parityOf([{ type: 'grain', amount: 0.5, enabled: false }, { type: 'sharpen', amount: 0 }]).parity).toBe('faithful');
  });
});

describe('previewEffectsOf', () => {
  it('keeps the renderer order and clamps; an unpreviewable effect is skipped alone', () => {
    expect(previewEffectsOf([{ type: 'mono', amount: 2 }, { type: 'blur' }, { type: 'vignette', amount: 0, enabled: true }])).toEqual([{ type: 'mono', amount: 1 }, { type: 'blur', amount: 0.5 }]);
    expect(previewEffectsOf([{ type: 'grain', amount: 0.3 }, { type: 'wobble', amount: 0.3 }])).toEqual([{ type: 'grain', amount: 0.3 }]);
  });
});

describe('blur amount range (T-102)', () => {
  it('lets a blur reach 2 (sigma 40 on the 1920 reference) and keeps every other effect at 1', () => {
    expect(previewEffectsOf([{ type: 'blur', amount: 2 }, { type: 'blur', amount: 3 }, { type: 'pixelate', amount: 1.5 }])).toEqual([
      { type: 'blur', amount: 2 },
      { type: 'blur', amount: 2 },
      { type: 'pixelate', amount: 1 },
    ]);
  });

  it('keeps a sigma-40 blur as two small separable passes at a reduced working size', () => {
    const plan = planEffectPasses([{ type: 'blur', amount: 2 }], 1920, 1080);
    expect(plan.passes.map((p) => p.kind)).toEqual(['blurH', 'blurV']);
    expect(plan.width).toBeLessThanOrEqual(256);
    const sigma = plan.passes[0].value;
    expect(sigma).toBeLessThanOrEqual(4.5);
    expect(sigma).toBeCloseTo((40 * plan.width) / 1920, 1);
  });

  it('leaves an amount at the old maximum meaning exactly what it did', () => {
    const plan = planEffectPasses([{ type: 'blur', amount: 1 }], 1920, 1080);
    expect(plan.passes[0].value).toBeCloseTo((20 * plan.width) / 1920, 5);
  });
});

describe('planEffectPasses', () => {
  it('matches the renderer blur strength: sigma = amount*20 at the 1920 reference', () => {
    // 1080p source, amount 0.2 -> sigma 4 at ref; working long side 960 -> 2 px; ratio holds at any working size.
    const plan = planEffectPasses([{ type: 'blur', amount: 0.2 }], 1920, 1080);
    const sigma = plan.passes[0].value;
    expect((sigma * 1920) / Math.max(plan.width, plan.height)).toBeCloseTo(4, 5);
    expect(plan.passes.map((p) => p.kind)).toEqual(['blurH', 'blurV']);
  });
  it('keeps the kernel small: sigma <= 4 working px, work size <= 960', () => {
    const plan = planEffectPasses([{ type: 'blur', amount: 1 }], 1920, 1080);
    expect(plan.passes[0].value).toBeLessThanOrEqual(4.0001);
    expect(Math.max(plan.width, plan.height)).toBeLessThanOrEqual(960);
    expect((plan.passes[0].value * 1920) / Math.max(plan.width, plan.height)).toBeCloseTo(20, 1);
  });
  it('frame budget: the worst chain stays under 40 M texture fetches', () => {
    const worst = planEffectPasses([{ type: 'blur', amount: 1 }, { type: 'pixelate', amount: 1 }, { type: 'mono', amount: 1 }, { type: 'vignette', amount: 1 }], 3840, 2160);
    expect(worst.taps).toBeLessThan(40e6);
    // A 1080p unblurred composite alone is ~2 M fetches per layer; blur at 1.0 adds under 3x that.
    expect(planEffectPasses([{ type: 'blur', amount: 1 }], 1920, 1080).taps).toBeLessThan(6e6);
  });
  it('pixelate block and vignette angle follow the renderer', () => {
    expect(planEffectPasses([{ type: 'pixelate', amount: 0.01 }], 1920, 1080).passes[0].value).toBe(2);
    expect(planEffectPasses([{ type: 'pixelate', amount: 0.5 }], 1920, 1080).passes[0].value).toBe(32);
    expect(planEffectPasses([{ type: 'vignette', amount: 1 }], 1920, 1080).passes[0].value).toBeCloseTo(Math.PI / 2.5, 6);
  });
});

describe('effect program in the compositor', () => {
  it('compiles and links alongside the main program, and an effect layer draws offscreen passes', () => {
    const calls: string[] = [];
    const target: Record<string, unknown> = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, TEXTURE_2D: 13, TRIANGLES: 11 };
    const shaders: string[] = [];
    const gl = new Proxy(target, {
      get(t, name: string) {
        if (name in t) return t[name];
        return (...args: unknown[]) => {
          calls.push(name);
          if (name === 'shaderSource') shaders.push(String(args[1]));
          if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
          if (name.startsWith('create')) return {};
          return null;
        };
      },
    }) as unknown as WebGL2RenderingContext;
    const canvas = { width: 320, height: 180, getContext: () => gl, addEventListener: () => {}, removeEventListener: () => {} } as unknown as HTMLCanvasElement;
    const compositor = createGlCompositor(canvas)!;
    expect(calls.filter((c) => c === 'createProgram').length).toBe(4); // clip, effect, region and pin programs
    expect(shaders).toContain(FX_FRAGMENT_SHADER);
    calls.length = 0;
    const image = { width: 1920, height: 1080 } as unknown as CanvasImageSource;
    compositor.draw([{ layerId: 'a', image, opacity: 1, effects: [{ type: 'blur', amount: 1 }] }]);
    // the picture pass, blur H + V, then the final composite.
    expect(calls.filter((c) => c === 'drawArrays').length).toBe(4);
    expect(calls.filter((c) => c === 'bindFramebuffer').length).toBeGreaterThan(0);
    calls.length = 0;
    compositor.draw([{ layerId: 'a', image, opacity: 1 }]);
    expect(calls.filter((c) => c === 'drawArrays').length).toBe(1);
  });
});

/** A fake GL that records uniform values by name and the clear colour / viewport of each draw. */
function recordingGl() {
  const uniforms: Record<string, number[][]> = {};
  const draws: { viewport: number[]; clear: number[] }[] = [];
  let viewport: number[] = [];
  let clear: number[] = [];
  const target: Record<string, unknown> = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, TEXTURE_2D: 13, TRIANGLES: 11 };
  const gl = new Proxy(target, {
    get(t, name: string) {
      if (name in t) return t[name];
      return (...args: unknown[]) => {
        if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
        if (name === 'getUniformLocation') return String(args[1]);
        if (name === 'viewport') viewport = args.map(Number);
        if (name === 'clearColor') clear = args.map(Number);
        if (name.startsWith('uniform') && typeof args[0] === 'string') (uniforms[args[0]] ??= []).push(args.slice(1).map(Number));
        if (name === 'drawArrays') draws.push({ viewport: [...viewport], clear: [...clear] });
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

describe('effects run in the render geometry (output pixels, after fit and transform)', () => {
  it('a letterboxed clip is padded with opaque black and vignetted on the whole canvas', () => {
    const { compositor, uniforms, draws } = setup(540, 960);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, aspectPolicy: 'fit', effects: [{ type: 'vignette', amount: 1 }] }]);
    expect(uniforms.uVig).toEqual([[0.5, 0.5, 0.5, 0.5]]);
    // The picture pass clears to opaque black (the bars), at the canvas's 9:16 shape.
    expect(draws[0].clear[3]).toBe(1);
    expect(draws[0].viewport[2] / draws[0].viewport[3]).toBeCloseTo(540 / 960, 2);
    // Pass 0 is the picture, then the vignette, then the full-canvas composite.
    expect(draws).toHaveLength(3);
  });

  it('pixelate blocks are output pixels on the canvas, not on the source', () => {
    const { compositor, uniforms } = setup(540, 960);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, effects: [{ type: 'pixelate', amount: 0.5 }] }]);
    // block 32 reference px; reference frame is 1080x1920 for a 9:16 canvas.
    expect(uniforms.uCells[0][0]).toBeCloseTo(1080 / 32, 5);
    expect(uniforms.uCells[0][1]).toBeCloseTo(1920 / 32, 5);
  });

  it('blur sigma does not change with the clip scale or the source size', () => {
    const a = setup(960, 540);
    a.compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, effects: [{ type: 'blur', amount: 0.5 }] }]);
    const b = setup(960, 540);
    const t = { x: 0, y: 0, scale: 2, rotation: 0, opacity: 1, crop: crop0 };
    b.compositor.draw([{ layerId: 'a', image: { width: 640, height: 360 } as unknown as CanvasImageSource, opacity: 1, transform: t, effects: [{ type: 'blur', amount: 0.5 }] }]);
    expect(b.uniforms.uSigma).toEqual(a.uniforms.uSigma);
    // sigma 10 reference px at amount 0.5 -> workLong * 10 / 1920 working px.
    const plan = planEffectPasses([{ type: 'blur', amount: 0.5 }], 960, 540);
    expect(a.uniforms.uSigma[0][0]).toBeCloseTo(plan.passes[0].value, 6);
    expect((plan.passes[0].value * 1920) / Math.max(plan.width, plan.height)).toBeCloseTo(10, 4);
  });

  it('a scaled, offset clip vignettes on its own placed frame, over transparency', () => {
    const { compositor, uniforms, draws } = setup(960, 540);
    const t = { x: 0.1, y: -0.2, scale: 2, rotation: 0, opacity: 1, crop: { ...crop0, left: 0.25 } };
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, transform: t, effects: [{ type: 'vignette', amount: 1 }] }]);
    const [cx, cy, hx, hy] = uniforms.uVig[0];
    expect(cx).toBeCloseTo(0.6, 6);
    expect(cy).toBeCloseTo(0.7, 6);
    expect(hx).toBeCloseTo(0.5 * 2 * 0.75, 6);
    expect(hy).toBeCloseTo(0.5 * 2, 6);
    expect(draws[0].clear[3]).toBe(0);
  });

  it('working targets are bucketed so a changing amount does not churn allocations', () => {
    const sizes = new Set<number>();
    for (let a = 0.3; a <= 1; a += 0.01) sizes.add(planEffectPasses([{ type: 'blur', amount: a }], 1920, 1080).width);
    expect(sizes.size).toBeLessThanOrEqual(6);
  });
});

describe('the pad decision follows the render (TransformFilterBuilder.cs:61)', () => {
  it('pads only when transform, crop and both fades are all absent; an identity OBJECT is not absent', () => {
    expect(rendersPadded({})).toBe(true);
    expect(rendersPadded({ transform: null, crop: null, fadeInFrames: 0, fadeOutFrames: 0 })).toBe(true);
    expect(rendersPadded({ fadeInFrames: 5 })).toBe(false);
    expect(rendersPadded({ fadeOutFrames: 1 })).toBe(false);
    expect(rendersPadded({ transform: {} })).toBe(false);
    expect(rendersPadded({ crop: { left: 0, right: 0, top: 0, bottom: 0 } })).toBe(false);
  });

  it('a fade-only 16:9 clip in a 9:16 canvas vignettes its own picture frame, over transparency', () => {
    const { compositor, uniforms, draws } = setup(540, 960);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 0.5, aspectPolicy: 'fit', padded: rendersPadded({ fadeInFrames: 10 }), effects: [{ type: 'vignette', amount: 1 }] }]);
    const [cx, cy, hx, hy] = uniforms.uVig[0];
    expect([cx, cy, hx]).toEqual([0.5, 0.5, 0.5]);
    expect(hy).toBeCloseTo(0.5 * ((540 / 960) / (1920 / 1080)), 6);
    expect(draws[0].clear[3]).toBe(0);
    // At the picture's corner the render's factor is cos(angle)^4 ~ 0.0095 (the ~0.01 in the QA numbers).
    expect(Math.cos((1 * Math.PI) / 2.5) ** 4).toBeCloseTo(0.0095, 3);
  });

  it('an identity transform object follows the unpadded path like the render', () => {
    const { compositor, uniforms, draws } = setup(540, 960);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, aspectPolicy: 'fit', padded: rendersPadded({ transform: {} }), effects: [{ type: 'vignette', amount: 1 }] }]);
    expect(draws[0].clear[3]).toBe(0);
    expect(uniforms.uVig[0][3]).toBeLessThan(0.5);
  });

  it('a padded clip keeps the opaque black pad and a canvas-origin pixel grid', () => {
    const { compositor, uniforms, draws } = setup(540, 960);
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, aspectPolicy: 'fit', padded: true, effects: [{ type: 'pixelate', amount: 0.5 }] }]);
    expect(draws[0].clear[3]).toBe(1);
    expect(uniforms.uOrigin[0]).toEqual([0, 0]);
  });

  it('on a transformed clip the pixel grid starts at the picture top-left', () => {
    const { compositor, uniforms } = setup(960, 540);
    const t = { x: 0.1, y: -0.2, scale: 0.5, rotation: 0, opacity: 1, crop: crop0 };
    compositor.draw([{ layerId: 'a', image: widescreen, opacity: 1, transform: t, effects: [{ type: 'pixelate', amount: 0.5 }] }]);
    expect(uniforms.uOrigin[0][0]).toBeCloseTo(0.6 - 0.25, 6);
    expect(uniforms.uOrigin[0][1]).toBeCloseTo(1 - 0.7 - 0.25, 6);
  });
});
