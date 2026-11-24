import { describe, expect, it } from 'vitest';
import { createGlCompositor, readCleanPicture } from '../gl-compositor';
import { viewingLutFromProfile } from '../../color/viewer-color';
import { CAMERA_PROFILE_IDS } from '../../color/camera-profiles';

const TEXTURE0 = 12;
const TEXTURE_3D = 15;
const RGBA = 19;

function setup() {
  const calls: Array<[string, unknown[]]> = [];
  const target: Record<string, unknown> = {
    VERTEX_SHADER: 1, FRAGMENT_SHADER: 2, LINK_STATUS: 3, COMPILE_STATUS: 4, ARRAY_BUFFER: 5, STATIC_DRAW: 6, FLOAT: 7,
    BLEND: 8, SRC_ALPHA: 9, ONE_MINUS_SRC_ALPHA: 10, TRIANGLES: 11, TEXTURE0, TEXTURE_2D: 13, COLOR_BUFFER_BIT: 14,
    TEXTURE_3D, RGBA, UNSIGNED_BYTE: 20, NEAREST: 21, LINEAR: 22, CLAMP_TO_EDGE: 23, HALF_FLOAT: 24, RGBA16F: 25,
  };
  const gl = new Proxy(target, {
    get(t, name: string) {
      if (name in t) return t[name];
      return (...args: unknown[]) => {
        calls.push([name, args]);
        if (name === 'getProgramParameter' || name === 'getShaderParameter') return true;
        // Record uniform locations by name so `uniform1i` calls can be attributed to their sampler.
        if (name === 'getUniformLocation') return String(args[1]);
        if (name.startsWith('create')) return {};
        return null;
      };
    },
  }) as unknown as WebGL2RenderingContext;
  const canvas = { width: 320, height: 180, getContext: () => gl, addEventListener: () => undefined, removeEventListener: () => undefined } as unknown as HTMLCanvasElement;
  return { canvas, calls };
}

const unitOf = (calls: Array<[string, unknown[]]>, uniform: string) => calls.filter(([n, a]) => n === 'uniform1i' && a[0] === uniform).map(([, a]) => a[1]);
const activeUnits = (calls: Array<[string, unknown[]]>) => calls.filter(([n]) => n === 'activeTexture').map(([, a]) => Number(a[0]) - TEXTURE0);
const image = { width: 4, height: 4 } as unknown as CanvasImageSource;
const layer = { layerId: 'a', contentKey: 'k', image, opacity: 1, inputProfile: CAMERA_PROFILE_IDS[0] };

describe('compositor viewing stage', () => {
  it('an export-like draw (applyViewingLut false) never builds or runs the viewing pass', () => {
    const { canvas, calls } = setup();
    const c = createGlCompositor(canvas)!;
    c.setViewingLut(viewingLutFromProfile(CAMERA_PROFILE_IDS[0]));
    const before = calls.filter(([n]) => n === 'createProgram').length;
    c.draw([layer], { applyViewingLut: false });
    expect(calls.filter(([n]) => n === 'createProgram').length).toBe(before);
    expect(c.withCleanPicture(() => 7)).toBe(7);
  });
  it('the live draw runs the viewing pass last and a clean read swaps it off and back on', () => {
    const { canvas, calls } = setup();
    const c = createGlCompositor(canvas)!;
    c.setViewingLut(viewingLutFromProfile(CAMERA_PROFILE_IDS[0]));
    c.draw([layer]);
    const uOn = () => calls.filter(([n, a]) => n === 'uniform1f' && a[1] === 0 || n === 'uniform1f' && a[1] === 1).length;
    const n0 = uOn();
    let sawInside = 0;
    c.withCleanPicture(() => {
      sawInside = uOn();
    });
    expect(sawInside).toBeGreaterThan(n0);
    expect(uOn()).toBeGreaterThan(sawInside);
    expect(readCleanPicture(canvas, () => 'x')).toBe('x');
  });
});

describe('sampler texture units (real-GL finding)', () => {
  it('sets each sampler uniform to its own unit once after link', () => {
    const { canvas, calls } = setup();
    createGlCompositor(canvas)!;
    expect(unitOf(calls, 'uTex')).toEqual([0, 0, 0, 0]); // clip, effect, region and pin programs, all on the picture unit
    expect(unitOf(calls, 'uInputLut')).toEqual([4]);
  });

  it('a stage-off draw binds a 1x1x1 dummy 3D texture on the input LUT unit, never unit 0', () => {
    const { canvas, calls } = setup();
    const c = createGlCompositor(canvas)!;
    c.draw([{ layerId: 'a', contentKey: 'k', image, opacity: 1 }]);
    const dummy = calls.filter(([n, a]) => n === 'texImage3D' && a[3] === 1 && a[4] === 1 && a[5] === 1);
    expect(dummy).toHaveLength(1);
    expect(dummy[0]![1][2]).toBe(RGBA);
    expect(calls.some(([n, a]) => n === 'bindTexture' && a[0] === TEXTURE_3D)).toBe(true);
    const units = activeUnits(calls);
    expect(units).toContain(4);
    expect(units[units.length - 1]).toBe(0);
  });

  it('the viewing program puts its LUT sampler on unit 5, distinct from the picture and the clip table', () => {
    const { canvas, calls } = setup();
    const c = createGlCompositor(canvas)!;
    c.setViewingLut(viewingLutFromProfile(CAMERA_PROFILE_IDS[0]));
    c.draw([{ layerId: 'a', contentKey: 'k', image, opacity: 1 }]);
    expect(unitOf(calls, 'uLut')).toEqual([5]);
    expect(unitOf(calls, 'uInputLut')).toEqual([4]);
    expect(unitOf(calls, 'uTex')).toEqual([0, 0, 0, 0, 0]); // clip, effect, region, pin and viewing programs
  });
});
