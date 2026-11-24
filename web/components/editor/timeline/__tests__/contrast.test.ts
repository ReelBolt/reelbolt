/**
 * VF1: WCAG contrast over every clip token pair, in both schemes, read from the stylesheet that ships
 * (`app/global.css`) and from the fallback table the canvas uses without one. Text on a clip must hold
 * AA (4.5:1) against its fill; the selection ring (a non-text mark) 3:1 against every fill.
 */

import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { CLIP_FALLBACK, CLIP_TOKENS, type ClipColors, type ClipScheme } from '../editorTheme';

const FILLS = ['video', 'audio', 'title', 'adjustment', 'generator', 'compound', 'caption', 'transition', 'insert', 'muted'] as const;

function channel(c: number): number {
  const v = c / 255;
  return v <= 0.03928 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
}
export function luminance(hex: string): number {
  const h = hex.replace('#', '');
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16));
  return 0.2126 * channel(r) + 0.7152 * channel(g) + 0.0722 * channel(b);
}
export function contrast(a: string, b: string): number {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
}

function stylesheetColors(scheme: ClipScheme): ClipColors {
  const css = readFileSync(join(process.cwd(), 'app/global.css'), 'utf8');
  const selector = scheme === 'light' ? ':root {' : "[data-mantine-color-scheme='dark'] {";
  const start = css.indexOf(selector, css.indexOf('--rb-ed-surface') - 40);
  const body = css.slice(start, css.indexOf('\n}', start));
  const out: Partial<Record<(typeof CLIP_TOKENS)[number], string>> = {};
  for (const token of CLIP_TOKENS) {
    const m = new RegExp(`--rb-clip-${token}:\\s*(#[0-9a-fA-F]{6});`).exec(body);
    expect(m, `--rb-clip-${token} (${scheme}) is a 6-digit hex in global.css`).not.toBeNull();
    out[token] = m![1];
  }
  return out as ClipColors;
}

describe('VF1 contrast maths', () => {
  it('matches the WCAG reference ratios', () => {
    expect(contrast('#000000', '#ffffff')).toBeCloseTo(21, 5);
    expect(contrast('#777777', '#ffffff')).toBeCloseTo(4.48, 2);
  });
});

for (const scheme of ['light', 'dark'] as const) {
  for (const [source, colors] of [['global.css', () => stylesheetColors(scheme)], ['fallback table', () => CLIP_FALLBACK[scheme]]] as const) {
    describe(`VF1 clip tokens, ${scheme} scheme, ${source}`, () => {
      const c = colors();
      it.each(FILLS)('text on the %s fill holds AA (4.5:1)', (fill) => {
        expect(contrast(c.text, c[fill])).toBeGreaterThanOrEqual(4.5);
      });
      it.each(FILLS)('the selection ring holds 3:1 against the %s fill', (fill) => {
        expect(contrast(c.selected, c[fill])).toBeGreaterThanOrEqual(3);
      });
    });
  }

  it(`the stylesheet and the fallback table agree (${scheme})`, () => {
    expect(stylesheetColors(scheme)).toEqual(CLIP_FALLBACK[scheme]);
  });
}
