import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';
import { DARK_THEME, LIGHT_THEME } from '../paint';

const rgb = (hex: string): number[] => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16));
const luminance = (c: readonly number[]) => {
  const [r, g, b] = c.map((v) => (v / 255 <= 0.03928 ? v / 255 / 12.92 : ((v / 255 + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};
const contrast = (a: readonly number[], b: readonly number[]) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

const css = readFileSync(resolve(__dirname, '../../../../app/global.css'), 'utf8');
const [light, dark] = [...css.matchAll(/--rb-ed-fg-muted:\s*(#[0-9a-f]{6})/g)].map((m) => rgb(m[1]));

describe('toolbar and snap label contrast', () => {
  it('the on bar of a trim mode reaches 3:1 against the on tint and the off chip in both themes', () => {
    const stylesheet = readFileSync(resolve(__dirname, '../TimelineMenus.module.css'), 'utf8');
    expect(stylesheet).toMatch(/\.stateOn\s*{[^}]*box-shadow:[^;]*var\(--rb-ed-fg-muted\)/);
    expect(contrast(light, [253, 240, 207])).toBeGreaterThanOrEqual(3);
    expect(contrast(light, [255, 255, 255])).toBeGreaterThanOrEqual(3);
    expect(contrast(dark, rgb('#12141a'))).toBeGreaterThanOrEqual(3);
  });

  it('the snap label text reaches 4.5:1; the light guide line keeps its colour', () => {
    expect(contrast(rgb(LIGHT_THEME.snapLabelText), rgb(LIGHT_THEME.background))).toBeGreaterThanOrEqual(4.5);
    expect(contrast(rgb(DARK_THEME.snapLabelText), rgb(DARK_THEME.background))).toBeGreaterThanOrEqual(4.5);
    expect(LIGHT_THEME.snapGuide).toBe('#0b9fb5');
  });

  it('the disabled reason uses the muted token, 4.5:1 on a white menu', () => {
    const menu = readFileSync(resolve(__dirname, '../ClipContextMenu.tsx'), 'utf8');
    expect(menu).toMatch(/color: 'var\(--rb-ed-fg-muted\)'.*-reason/);
    expect(contrast(light, [255, 255, 255])).toBeGreaterThanOrEqual(4.5);
  });
});
