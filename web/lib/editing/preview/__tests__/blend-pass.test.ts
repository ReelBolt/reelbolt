/** @vitest-environment node */
/**
 * T-012: the viewer's blend maths against the C# table (`BlendModes.cs`, 8-bit gamma-encoded RGB, A =
 * backdrop, B = layer, integer division truncating), and the generated GLSL against the same rows.
 */
import { describe, expect, it } from 'vitest';
import { BLEND_TABLE, BLEND_VERTEX_SHADER, blendChannel, blendFragmentSource, blendModeId, blendPixel, isBlendingWord } from '../blend-pass';
import { BLEND_MODES } from '../../engine/ops';

// An independent statement of the C# formulas (the docs table), used over a coarse grid of (A, B).
const MULTIPLY = (x: number, a: number, b: number) => x * Math.floor((a * b) / 255);
const SCREEN = (x: number, a: number, b: number) => 255 - x * Math.floor(((255 - a) * (255 - b)) / 255);
const REFERENCE: Record<string, (a: number, b: number) => number> = {
  multiply: (a, b) => MULTIPLY(1, a, b),
  screen: (a, b) => SCREEN(1, a, b),
  overlay: (a, b) => (a < 128 ? MULTIPLY(2, a, b) : SCREEN(2, a, b)),
  hardLight: (a, b) => (b < 128 ? MULTIPLY(2, a, b) : SCREEN(2, a, b)),
  softLight: (a, b) => Math.max(0, Math.min(255, Math.floor((a * a) / 255 + (2 * b * a * (255 - a)) / 65025))),
  darken: (a, b) => Math.min(a, b),
  lighten: (a, b) => Math.max(a, b),
  add: (a, b) => Math.min(255, a + b),
  difference: (a, b) => Math.abs(a - b),
};

describe('blendChannel', () => {
  it('matches the C# table on a grid of (A, B), every mode, always within 0..255', () => {
    for (const [word, reference] of Object.entries(REFERENCE)) {
      for (let a = 0; a <= 255; a += 5) {
        for (let b = 0; b <= 255; b += 5) {
          const got = blendChannel(word, a, b);
          expect(got, `${word}(${a},${b})`).toBe(reference(a, b));
          expect(got).toBeGreaterThanOrEqual(0);
          expect(got).toBeLessThanOrEqual(255);
        }
      }
      for (const edge of [0, 1, 127, 128, 254, 255]) {
        for (const other of [0, 1, 127, 128, 254, 255]) expect(blendChannel(word, edge, other), `${word}(${edge},${other})`).toBe(reference(edge, other));
      }
    }
  });

  it('pins hand-computed values (truncating integer division where the C# says so)', () => {
    expect(blendChannel('multiply', 200, 100)).toBe(78); // 20000 / 255 = 78.4
    expect(blendChannel('screen', 200, 100)).toBe(222); // 255 - 8525 / 255 (33.4 -> 33)
    expect(blendChannel('overlay', 100, 200)).toBe(156); // A < 128: 2 * (20000 / 255)
    expect(blendChannel('overlay', 200, 100)).toBe(189); // A >= 128: 255 - 2 * 33
    expect(blendChannel('hardLight', 100, 200)).toBe(189); // decided by the layer, not the backdrop
    expect(blendChannel('hardLight', 200, 100)).toBe(156);
    expect(blendChannel('softLight', 128, 64)).toBe(96); // 64.25 + 31.99 = 96.2 -> 96
    expect(blendChannel('softLight', 255, 7)).toBe(255);
    expect(blendChannel('softLight', 0, 200)).toBe(0);
    expect(blendChannel('darken', 10, 20)).toBe(10);
    expect(blendChannel('lighten', 10, 20)).toBe(20);
    expect(blendChannel('add', 200, 100)).toBe(255);
    expect(blendChannel('add', 100, 50)).toBe(150);
    expect(blendChannel('difference', 10, 25)).toBe(15);
  });

  it('normal and unknown words are not blends (the layer shows as plain alpha-over)', () => {
    expect(isBlendingWord('normal')).toBe(false);
    expect(isBlendingWord(undefined)).toBe(false);
    expect(isBlendingWord('sparkle')).toBe(false);
    expect(blendModeId('normal')).toBe(0);
    expect(blendChannel('normal', 10, 99)).toBe(99);
  });
});

describe('blendPixel', () => {
  it('replaces the backdrop in proportion to the layer alpha', () => {
    expect(blendPixel('multiply', [200, 200, 200], [100, 100, 100], 1)).toEqual([78, 78, 78]);
    expect(blendPixel('multiply', [200, 200, 200], [100, 100, 100], 0)).toEqual([200, 200, 200]);
    expect(blendPixel('multiply', [200, 200, 200], [100, 100, 100], 0.5)).toEqual([139, 139, 139]);
  });

  it('over black (the bottom-most picture) multiply is black, add is the layer', () => {
    expect(blendPixel('multiply', [0, 0, 0], [90, 120, 250], 1)).toEqual([0, 0, 0]);
    expect(blendPixel('add', [0, 0, 0], [90, 120, 250], 1)).toEqual([90, 120, 250]);
    expect(blendPixel('screen', [0, 0, 0], [90, 120, 250], 1)).toEqual([90, 120, 250]);
  });
});

describe('the generated shader', () => {
  it('has one branch per catalogue word, in the contract order, ids 1..9', () => {
    expect(BLEND_TABLE.map((r) => r.word)).toEqual(BLEND_MODES.filter((w) => w !== 'normal'));
    const source = blendFragmentSource();
    BLEND_TABLE.forEach((row, i) => {
      expect(source).toContain(`if (mode == ${i + 1}) return ${row.glsl};`);
      expect(blendModeId(row.word)).toBe(i + 1);
    });
    expect(source).toContain('uniform int uMode;');
    expect(source).toContain('if (uMode < 0)');
  });

  it('keeps the integer forms of the C# (no float shortcut for the integer modes)', () => {
    const glsl = Object.fromEntries(BLEND_TABLE.map((r) => [r.word, r.glsl]));
    expect(glsl.multiply).toBe('(a * b) / 255');
    expect(glsl.screen).toBe('255 - ((255 - a) * (255 - b)) / 255');
    expect(glsl.overlay).toContain('a < 128');
    expect(glsl.hardLight).toContain('b < 128');
    expect(glsl.softLight).toContain('65025.0');
    expect(glsl.softLight).toContain('floor(');
  });

  it('needs no vertex attributes', () => {
    expect(BLEND_VERTEX_SHADER).toContain('gl_VertexID');
    expect(BLEND_VERTEX_SHADER).not.toContain('in vec2');
  });
});
