import { describe, expect, it } from 'vitest';
import { createGlCompositor, fitFor } from '../gl-compositor';

/**
 * Pixel geometry of crop then fit then transform, the render's order (TransformFilterBuilder.cs:91 crop, :95 fit with
 * force_original_aspect_ratio=decrease, :101 transform scale). The "render" reference for the seeded fill layer was run
 * through the real ffmpeg with those exact filters on a 1920x1080 ramp: output 1079x1920 for a 1080x1920 canvas (the 1 px
 * is crop rounding), source columns 0.3412..0.6549 at the left/right edges, source rows 0..1 top to bottom - the cropped
 * picture covers the whole canvas, no bands.
 */
const crop = (left: number, right: number, top = 0, bottom = 0) => ({ left, right, top, bottom });
const transform = (c: ReturnType<typeof crop>, scale = 1, x = 0, y = 0) => ({ x, y, scale, rotation: 0, opacity: 1, crop: c });

function drawn(canvasW: number, canvasH: number, srcW: number, srcH: number, t: ReturnType<typeof transform>) {
  const uniforms: Record<string, number[]> = {};
  const target: Record<string, unknown> = { VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, TEXTURE_2D: 13, TRIANGLES: 11 };
  const gl = new Proxy(target, {
    get(tg, name: string) {
      if (name in tg) return tg[name];
      return (...args: unknown[]) => {
        if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
        if (name === 'getUniformLocation') return String(args[1]);
        if (name.startsWith('uniform') && typeof args[0] === 'string') uniforms[args[0]] = args.slice(1).map(Number);
        if (name.startsWith('create')) return {};
        return null;
      };
    },
  }) as unknown as WebGL2RenderingContext;
  const canvas = { width: canvasW, height: canvasH, getContext: () => gl, addEventListener: () => {}, removeEventListener: () => {} } as unknown as HTMLCanvasElement;
  const compositor = createGlCompositor(canvas)!;
  compositor.resize(canvasW, canvasH);
  compositor.draw([{ layerId: 'a', image: { width: srcW, height: srcH } as unknown as CanvasImageSource, opacity: 1, transform: t, aspectPolicy: 'fit' }]);
  return uniforms;
}

describe('crop, then fit, then transform', () => {
  it('the seeded fill layer (crop L/R 0.341797, scale 1) covers the 9:16 canvas, showing source x 0.3418..0.6582', () => {
    const u = drawn(540, 960, 1920, 1080, transform(crop(0.341797, 0.341797)));
    // The cropped picture is 607.5x1080 = 9:16: the quad spans the whole canvas (clip scale 1,1), no bands.
    expect(u.uScale[0]).toBeCloseTo(1, 3);
    expect(u.uScale[1]).toBeCloseTo(1, 3);
    // The window of the source it shows, matching the render's edge columns.
    expect(u.uUvOrigin[0]).toBeCloseTo(0.341797, 6);
    expect(u.uUvSize[0]).toBeCloseTo(1 - 2 * 0.341797, 6);
    expect(u.uUvOrigin[1]).toBe(0);
    expect(u.uUvSize[1]).toBe(1);
    // On-canvas rectangle in pixels: x 0..540, y 0..960.
    expect(540 * u.uScale[0]).toBeCloseTo(540, 0);
    expect(960 * u.uScale[1]).toBeCloseTo(960, 0);
  });

  it('a plain crop is fitted after the crop: 1920x1080 cropped left 0.25 (4:3) on a 16:9 canvas spans 75% of the width', () => {
    const u = drawn(960, 540, 1920, 1080, transform(crop(0.25, 0)));
    expect(u.uScale[0]).toBeCloseTo(0.75, 6); // (1440/1080) / (16/9)
    expect(u.uScale[1]).toBeCloseTo(1, 6);
    expect(u.uUvOrigin[0]).toBe(0.25);
    expect(u.uUvSize[0]).toBe(0.75);
  });

  it('a transform scale multiplies the fitted cropped size, and the crop does not shrink it twice', () => {
    const u = drawn(960, 540, 1920, 1080, transform(crop(0.25, 0), 0.5));
    expect(u.uScale[0]).toBeCloseTo(0.375, 6);
    expect(u.uScale[1]).toBeCloseTo(0.5, 6);
  });

  it('fitFor: an uncropped transformed layer keeps the plain fit; an untransformed one keeps its aspect policy', () => {
    expect(fitFor({ transform: transform(crop(0, 0)) }, 1920, 1080, 540, 960)).toEqual([1, 540 / 960 / (1920 / 1080)]);
    expect(fitFor({ aspectPolicy: 'stretch' }, 1920, 1080, 540, 960)).toEqual([1, 1]);
  });
});
