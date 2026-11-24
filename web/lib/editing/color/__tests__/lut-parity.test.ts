import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { parseCubeLut, sampleCubeLut } from '../cube-lut';

interface Fixture {
  patches: number[][];
  cases: { name: string; cube: string; intensity: number; chain: string; out: number[][] }[];
}

const file = path.join(__dirname, '..', '__fixtures__', 'lut-parity.json');

describe.skipIf(!existsSync(file))('LUT parity with ffmpeg', () => {
  const fixture = (existsSync(file) ? JSON.parse(readFileSync(file, 'utf8')) : { patches: [], cases: [] }) as Fixture;
  it.each(fixture.cases.map((c) => [c.name, c] as const))('%s', (_name, c) => {
    const lut = parseCubeLut(c.cube);
    fixture.patches.forEach((patch, i) => {
      const got = sampleCubeLut(lut, [(patch[0] as number) / 255, (patch[1] as number) / 255, (patch[2] as number) / 255], c.intensity);
      for (let ch = 0; ch < 3; ch += 1) {
        expect(Math.abs((got[ch] as number) * 255 - ((c.out[i] as number[])[ch] as number))).toBeLessThanOrEqual(2);
      }
    });
  });
});
