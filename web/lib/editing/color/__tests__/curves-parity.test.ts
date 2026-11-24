import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import type { GradeCurves } from '../../types';
import { bakeCurves, CURVE_TABLE_SIZE } from '../grade-curves';

interface Fixture {
  cases: { name: string; curves: GradeCurves; r: number[]; g: number[]; b: number[] }[];
}

// Real ffmpeg `curves` on a 256-step grey ramp (see the fixture's invocation). 8-bit output: within 3 levels
// (the second pass of a master curve reads an 8-bit intermediate).
const fixture = JSON.parse(readFileSync(path.join(__dirname, '..', '__fixtures__', 'curves-parity.json'), 'utf8')) as Fixture;

describe('curves = ffmpeg curves (natural cubic spline, master after channel)', () => {
  it.each(fixture.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    const bake = bakeCurves(c.curves)!;
    for (let i = 0; i < CURVE_TABLE_SIZE; i += 1) {
      expect(Math.abs((bake.table[i * 3] as number) * 255 - (c.r[i] as number))).toBeLessThanOrEqual(3);
      expect(Math.abs((bake.table[i * 3 + 1] as number) * 255 - (c.g[i] as number))).toBeLessThanOrEqual(3);
      expect(Math.abs((bake.table[i * 3 + 2] as number) * 255 - (c.b[i] as number))).toBeLessThanOrEqual(3);
    }
  });
});
