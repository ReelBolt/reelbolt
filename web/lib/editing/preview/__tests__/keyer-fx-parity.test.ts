import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { FX_FRAGMENT_SHADER, planEffectPasses, previewEffectsOf, unpreviewableEffectsOf } from '../gl-compositor';
import { FX_EXTRA_GLSL, grainStrength, sharpenLumaAmount, sharpenSample } from '../fx-extra';
import { GRADE_PASS_FRAGMENT_SHADER } from '../grade-pass';
import { keyAlpha, keyDespill, keyerParamsOf, KEYER_GLSL } from '../keyer';

interface Fixture {
  pixels: number[][];
  colorkey: { color: string; similarity: number; blend: number; alpha: number[] }[];
  despill: { type: 'green' | 'blue'; mix: number; out: number[][] }[];
  sharpen: { w: number; h: number; in: number[]; cases: { amount: number; out: number[] }[] };
  grain: { strength: number; min: number; max: number; mean: number; std: number }[];
}

// Real ffmpeg output (see the fixture's generation in the T-008a report): colorkey on RGBA, despill, unsharp, noise.
const fx = JSON.parse(readFileSync(path.join(__dirname, '..', '..', 'color', '__fixtures__', 'fx-parity.json'), 'utf8')) as Fixture;

describe('keyer = ffmpeg colorkey', () => {
  it.each(fx.colorkey.map((c) => [`${c.color} sim ${c.similarity} blend ${c.blend}`, c] as const))('%s', (_n, c) => {
    const p = keyerParamsOf({ color: c.color, similarity: c.similarity, blend: c.blend })!;
    fx.pixels.forEach((px, i) => {
      const got = keyAlpha(p, [(px[0] as number) / 255, (px[1] as number) / 255, (px[2] as number) / 255]) * 255;
      expect(Math.abs(got - (c.alpha[i] as number))).toBeLessThanOrEqual(2);
    });
  });
});

describe('keyer = ffmpeg despill', () => {
  it.each(fx.despill.map((d) => [`${d.type} mix ${d.mix}`, d] as const))('%s', (_n, d) => {
    // spill = mix; the key colour decides the type (pure green / pure blue)
    const p = keyerParamsOf({ color: d.type === 'green' ? '#00FF00' : '#0000FF', spill: d.mix })!;
    fx.pixels.forEach((px, i) => {
      const got = d.mix === 0 ? [px[0], px[1], px[2]] : keyDespill(p, [(px[0] as number) / 255, (px[1] as number) / 255, (px[2] as number) / 255]).map((v) => v * 255);
      // mix 0 means "no spill" in the export (spill > 0 gates despill); ffmpeg's mix=0 is a real value, so only compare mix > 0
      if (d.mix === 0) return;
      for (let ch = 0; ch < 3; ch += 1) expect(Math.abs((got[ch] as number) - ((d.out[i] as number[])[ch] as number))).toBeLessThanOrEqual(2);
    });
  });
});

describe('keyerParamsOf', () => {
  it('has the export defaults and clamps', () => {
    expect(keyerParamsOf(null)).toBeNull();
    expect(keyerParamsOf({})).toEqual({ color: [0, 1, 0], similarity: 0.3, blend: 0, spill: 0, spillType: 0 });
    expect(keyerParamsOf({ similarity: 0, blend: 4, color: 'nope' })).toMatchObject({ similarity: 0.01, blend: 1, color: [0, 1, 0] });
  });
  it('despill type follows the dominant channel; a red or grey key has none', () => {
    expect(keyerParamsOf({ color: '#10FF20', spill: 0.5 })?.spillType).toBe(1);
    expect(keyerParamsOf({ color: '#1020FF', spill: 0.5 })?.spillType).toBe(2);
    expect(keyerParamsOf({ color: '#FF2010', spill: 0.5 })?.spillType).toBe(0);
    expect(keyerParamsOf({ color: '#00FF00', spill: 0 })?.spillType).toBe(0);
  });
  it('the grade pass shader carries the keyer before the grade, alpha multiplied at the end', () => {
    expect(GRADE_PASS_FRAGMENT_SHADER).toContain('keyed = keyAlpha(original);');
    expect(GRADE_PASS_FRAGMENT_SHADER).toContain('outColor = vec4(x, c.a * keyed);');
    expect(KEYER_GLSL).toContain('uniform vec4 uKeyA;');
  });
});

describe('sharpen = ffmpeg unsharp (la = amount * 3, 5x5 binomial)', () => {
  it.each([0, 1])('case %i matches the interior pixels', (k) => {
    const { w, h, cases } = fx.sharpen;
    const c = cases[k]!;
    const at = (x: number, y: number) => fx.sharpen.in[Math.min(h - 1, Math.max(0, y)) * w + Math.min(w - 1, Math.max(0, x))] as number;
    for (let y = 3; y < h - 3; y += 1) {
      for (let x = 3; x < w - 3; x += 1) {
        const hood: number[] = [];
        for (let dy = -2; dy <= 2; dy += 1) for (let dx = -2; dx <= 2; dx += 1) hood.push(at(x + dx, y + dy));
        const want = Math.min(255, Math.max(0, sharpenSample(hood, c.amount)));
        expect(Math.abs(want - (c.out[y * w + x] as number))).toBeLessThanOrEqual(1.5);
      }
    }
  });
  it('maps the effect amount to unsharp\'s la', () => {
    expect(sharpenLumaAmount(0.5)).toBe(1.5);
    expect(sharpenLumaAmount(1)).toBe(3);
  });
});

describe('grain = ffmpeg noise (uniform, width N, N = round(amount * 40))', () => {
  it('maps the effect amount to noise\'s strength', () => {
    expect(grainStrength(0.5)).toBe(20);
    expect(grainStrength(1)).toBe(40);
  });
  it('ffmpeg\'s noise at strength N has std N / sqrt(12) and spans about +-N/2 (what the shader\'s hash is built to match)', () => {
    for (const g of fx.grain) {
      expect(g.std).toBeCloseTo(g.strength / Math.sqrt(12), 0);
      expect(g.max - g.min).toBeLessThanOrEqual(g.strength + 1);
      expect(g.mean).toBeGreaterThan(126);
      expect(g.mean).toBeLessThan(129);
    }
  });
  it('the shader adds (n - 0.5) * N / 255 per channel with a per-frame seed', () => {
    expect(FX_EXTRA_GLSL).toContain('(n - 0.5) * uAmount');
    expect(FX_EXTRA_GLSL).toContain('uint f = uint(max(uSeed, 0.0));');
    expect(FX_FRAGMENT_SHADER).toContain('uMode == 5');
  });
});

describe('partial effect chains', () => {
  it('an unknown effect no longer drops the chain: the known ones run in order, the rest are named', () => {
    const chain = [{ type: 'blur', amount: 0.2 }, { type: 'wobble', amount: 0.5 }, { type: 'grain', amount: 0.5 }, { type: 'sharpen', amount: 0.4 }, { type: 'glow', amount: 0, enabled: true }, { type: 'mono', amount: 1, enabled: false }];
    expect(previewEffectsOf(chain)).toEqual([{ type: 'blur', amount: 0.2 }, { type: 'grain', amount: 0.5 }, { type: 'sharpen', amount: 0.4 }]);
    expect(unpreviewableEffectsOf(chain)).toEqual(['wobble']);
  });
  it('plans a sharpen and a grain pass with the filters\' own numbers', () => {
    const plan = planEffectPasses([{ type: 'sharpen', amount: 0.5 }, { type: 'grain', amount: 0.5 }], 1920, 1080);
    expect(plan.passes).toEqual([{ kind: 'sharpen', value: 1.5 }, { kind: 'grain', value: 20 }]);
    expect(plan.taps).toBe(plan.width * plan.height * 26);
  });
});
