import { describe, expect, it } from 'vitest';
import { theme } from './theme';

/**
 * WCAG AA for every colour pair the dashboard look introduces, in both schemes. Pure computation: Mantine palettes come
 * from theme.ts; the --rb-* values are the literals in global.css (mirrored below, keep in sync). Mantine's variant
 * variables follow @mantine/core 9.7 get-css-color-variables:
 *   light scheme: filled = c6 (white text); light = solid c1 with c9 text; outline/subtle/text = c6.
 *   dark scheme:  filled = c6 (white text); light = c9 darkened 50% with c0 text; outline = c2; text/link = c4.
 */
type RGB = [number, number, number];
const rgb = (hex: string): RGB => [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16)) as RGB;
const lin = (c: number) => { const x = c / 255; return x <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4; };
const lum = (c: RGB) => 0.2126 * lin(c[0]) + 0.7152 * lin(c[1]) + 0.0722 * lin(c[2]);
const ratio = (a: RGB | string, b: RGB | string) => {
  const x = lum(typeof a === 'string' ? rgb(a) : a); const y = lum(typeof b === 'string' ? rgb(b) : b);
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05);
};
const darken = (hex: string, amount: number): RGB => rgb(hex).map((v) => Math.round(v * (1 - amount))) as RGB;
const colors = theme.colors as Record<string, readonly string[]>;
const HUES = Object.keys(colors).filter((h) => h !== 'dark');

const S = {
  light: {
    surfaces: ['#FAF9FC', '#FFFFFF', '#F1EFF6'], text: '#000000', dimmed: colors.gray[6], link: colors.violet[6],
    placeholder: colors.gray[6], focus: '#6D28D9', border: '#6F6A7D', error: colors.red[6], success: colors.green[6],
    flow: { surface: '#FFFFFF', running: '#6d28d9', done: '#2b7a43', failed: '#c92a2a', skipped: '#8a5a00', pending: '#5c6068', onFill: '#ffffff' },
  },
  dark: {
    surfaces: ['#0B0B12', '#15131D', '#1D1927'], text: colors.dark[0], dimmed: colors.dark[2], link: colors.violet[4],
    placeholder: colors.dark[3], focus: '#C4B5FD', border: '#6F6A7D', error: colors.red[4], success: colors.green[4],
    flow: { surface: '#0B0B12', running: '#a78bfa', done: '#6fcf8a', failed: '#ff8787', skipped: '#f5b942', pending: '#9a9fab', onFill: '#14161a' },
  },
} as const;

describe.each(Object.entries(S))('theme contrast (%s scheme)', (name, s) => {
  const light = name === 'light';

  it.each(HUES)('%s: filled, light, outline and subtle variants', (h) => {
    const pal = colors[h];
    expect(ratio('#FFFFFF', pal[6]), `${h} filled`).toBeGreaterThanOrEqual(3); // large/bold button labels; brand hues are held to 4.5 below
    const lightBg = light ? rgb(pal[1]) : darken(pal[9], 0.5);
    const lightText = light ? pal[9] : pal[0];
    expect(ratio(lightText, lightBg), `${h} light variant`).toBeGreaterThanOrEqual(4.5);
    const outline = light ? pal[6] : pal[2];
    for (const surf of s.surfaces) {
      expect(ratio(outline, surf), `${h} outline on ${surf}`).toBeGreaterThanOrEqual(4.5);
      expect(ratio(lightText, surf), `${h} subtle on ${surf}`).toBeGreaterThanOrEqual(4.5);
    }
  });

  it('brand hues (violet, green, red, gray) keep white filled text at AA', () => {
    for (const h of ['violet', 'green', 'red', 'gray']) expect(ratio('#FFFFFF', colors[h][6]), h).toBeGreaterThanOrEqual(4.5);
  });

  it('body, dimmed, link, placeholder, error and success text on every surface', () => {
    for (const surf of s.surfaces) {
      expect(ratio(s.text, surf), `text on ${surf}`).toBeGreaterThanOrEqual(4.5);
      expect(ratio(s.dimmed, surf), `dimmed on ${surf}`).toBeGreaterThanOrEqual(4.5);
      expect(ratio(s.link, surf), `link on ${surf}`).toBeGreaterThanOrEqual(4.5);
      expect(ratio(s.placeholder, surf), `placeholder on ${surf}`).toBeGreaterThanOrEqual(light ? 4.5 : 3);
      expect(ratio(s.error, surf), `error on ${surf}`).toBeGreaterThanOrEqual(4.5);
      expect(ratio(s.success, surf), `success on ${surf}`).toBeGreaterThanOrEqual(4.5);
    }
  });

  it('rendered focus ring and resting input/checkbox border reach 3:1 on every surface', () => {
    for (const surf of s.surfaces) {
      expect(ratio(s.focus, surf), `focus ring on ${surf}`).toBeGreaterThanOrEqual(3);
      expect(ratio(s.border, surf), `input border on ${surf}`).toBeGreaterThanOrEqual(3);
    }
  });

  it('status tokens (--rb-status-*) pair text with its tint', () => {
    const pairs = light
      ? [['#5B21B6', '#EBE3FE'], ['#265839', '#DCEEE1'], ['#A61F37', '#FFE0E3'], ['#7A4A00', '#FDF0CF']]
      : [['#C4B5FD', '#2A1A4D'], ['#92C7A2', '#14261B'], ['#FA9AA6', '#3A1220'], ['#F2C45C', '#33270A']];
    for (const [fg, bg] of pairs) expect(ratio(fg, bg), `${fg} on ${bg}`).toBeGreaterThanOrEqual(4.5);
  });

  it('nav section labels and disabled buttons', () => {
    const label = light ? colors.gray[7] : colors.gray[4];
    for (const surf of s.surfaces) expect(ratio(label, surf), `section label on ${surf}`).toBeGreaterThanOrEqual(4.5);
    // Disabled: transparent fill, so the label sits on the surface.
    const dis = light ? colors.gray[6] : colors.dark[2];
    for (const surf of s.surfaces) expect(ratio(dis, surf), `disabled label on ${surf}`).toBeGreaterThanOrEqual(4.5);
  });

  it('active nav tab: white label and the inset bar', () => {
    expect(ratio('#FFFFFF', colors.violet[6])).toBeGreaterThanOrEqual(4.5);
    expect(ratio('#F5F3F8', colors.violet[6]), 'bar vs tab').toBeGreaterThanOrEqual(3);
  });

  it('run graph: text on status fills, and node borders on the surface', () => {
    const f = s.flow;
    for (const k of ['running', 'done', 'failed', 'skipped', 'pending'] as const) {
      expect(ratio(f.onFill, f[k]), `on-fill ${k}`).toBeGreaterThanOrEqual(4.5);
      expect(ratio(f[k], f.surface), `${k} border on surface`).toBeGreaterThanOrEqual(3);
    }
  });
});

describe('ink header bar (same in both schemes)', () => {
  it('controls and text on the ink bar', () => {
    expect(ratio('#F5F3F8', '#0B0B12')).toBeGreaterThanOrEqual(4.5);
    expect(ratio('#A79FB4', '#0B0B12')).toBeGreaterThanOrEqual(4.5);
    expect(ratio('#C4B5FD', '#0B0B12')).toBeGreaterThanOrEqual(4.5);
    expect(ratio('#F5F3F8', '#15131D')).toBeGreaterThanOrEqual(4.5);
    expect(ratio('#A855F7', '#0B0B12'), 'accent underline').toBeGreaterThanOrEqual(3);
  });
});
