/** @vitest-environment node */
/** The Simple preview's SVG grade filter evaluates to the same pixels as applyPreviewGrade. */

import { describe, expect, it } from 'vitest';
import { applyPreviewGrade, evalSvgGradeFilter, svgGradeFilterOf, TABLE_SAMPLES } from '../svg-grade';
import { IDENTITY_PREVIEW_GRADE, previewGradeOf, type PreviewGrade } from '../preview-grade';

const grade = (patch: Partial<PreviewGrade>): PreviewGrade => ({ ...IDENTITY_PREVIEW_GRADE, offset: [0, 0, 0], lift: [0, 0, 0], gain: [1, 1, 1], gamma: [1, 1, 1], ...patch });

// 8-bit values: the table is exact at them (it is sampled at k/255).
const LEVELS = [0, 17, 51, 85, 102, 128, 153, 187, 221, 238, 255].map((v) => v / 255);
const GRID: [number, number, number][] = [];
for (const r of LEVELS) for (const g of LEVELS) for (const b of [0, 64 / 255, 200 / 255, 1]) GRID.push([r, g, b]);

const CASES: [string, PreviewGrade][] = [
  ['exposure + gain (no overshoot matrix)', grade({ exposureGain: 1.4, gain: [1.05, 1, 0.95] })],
  ['lift + gamma', grade({ lift: [0.04, 0.02, 0], gamma: [0.9, 1, 1.1] })],
  ['offset + contrast about 0.4', grade({ offset: [0.02, -0.02, 0], contrast: 1.15, pivot: 0.4 })],
  ['temperature + tint', grade({ temperature: 0.5, tint: -0.3 })],
  ['saturation', grade({ saturation: 0.6 })],
  ['hue', grade({ hue: 35 })],
  ['saturation + hue + warm', grade({ saturation: 1.2, hue: -20, temperature: 0.3 })],
];

describe('svgGradeFilterOf', () => {
  it('is null at identity (no filter)', () => {
    expect(svgGradeFilterOf(IDENTITY_PREVIEW_GRADE)).toBeNull();
    expect(svgGradeFilterOf(previewGradeOf([null, undefined]))).toBeNull();
  });

  it('builds >= 64-sample tables per channel and a 4x5 matrix', () => {
    const f = svgGradeFilterOf(grade({ exposureGain: 1.2 }))!;
    expect(TABLE_SAMPLES).toBeGreaterThanOrEqual(64);
    for (const t of f.tables) expect(t).toHaveLength(TABLE_SAMPLES);
    expect(f.matrix).toHaveLength(20);
    expect(f.id).toMatch(/^rf-grade-/);
  });

  it('the signature and id follow the grade, and only the grade', () => {
    const a = svgGradeFilterOf(grade({ hue: 10 }))!;
    const b = svgGradeFilterOf(grade({ hue: 10 }))!;
    const c = svgGradeFilterOf(grade({ hue: 11 }))!;
    expect(a.signature).toBe(b.signature);
    expect(a.id).toBe(b.id);
    expect(a.id).not.toBe(c.id);
  });

  it.each(CASES)('%s matches applyPreviewGrade within 1/255', (_name, g) => {
    const f = svgGradeFilterOf(g)!;
    let worst = 0;
    for (const rgb of GRID) {
      const want = applyPreviewGrade(g, rgb);
      const got = evalSvgGradeFilter(f, rgb);
      for (let i = 0; i < 3; i++) worst = Math.max(worst, Math.abs(want[i] - got[i]));
    }
    expect(worst).toBeLessThanOrEqual(1 / 255 + 1e-9);
  });

  it('documented difference: a channel that overshoots 1 is clamped before the matrix mixes it', () => {
    // Exposure x2 pushes red to 1.6 (applyPreviewGrade keeps it into the desaturation, the SVG table clamps it at 1).
    const g = grade({ exposureGain: 2, saturation: 0.5 });
    const rgb: [number, number, number] = [0.8, 0.2, 0.2];
    const want = applyPreviewGrade(g, rgb);
    const got = evalSvgGradeFilter(svgGradeFilterOf(g)!, rgb);
    expect(Math.abs(want[1] - got[1])).toBeGreaterThan(1 / 255);
    // Without the matrix change the same overshoot is identical (both clamp the same value).
    const plain = grade({ exposureGain: 2 });
    const a = applyPreviewGrade(plain, rgb);
    const b = evalSvgGradeFilter(svgGradeFilterOf(plain)!, rgb);
    for (let i = 0; i < 3; i++) expect(Math.abs(a[i] - b[i])).toBeLessThanOrEqual(1 / 255);
  });
});
