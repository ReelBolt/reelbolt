/** @vitest-environment node */
/**
 * control-shape-restyle: the token contract for the applied shape.
 *
 * The proposal phase had the opposite test ("nothing outside the preview consumes these tokens"),
 * because the values were declared but not applied. Variant B is applied now, so this file asserts
 * the consumption instead: every token is declared in both scheme blocks, every token is read by an
 * app consumer (`app/global.css` or `app/theme.ts`), the size map is real, and the control rules
 * carry no literal colours. The preview only renders the result.
 */

import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

const WEB_ROOT = resolve(__dirname, '..', '..');
const GLOBAL_CSS = readFileSync(resolve(WEB_ROOT, 'app', 'global.css'), 'utf8');
const THEME = readFileSync(resolve(WEB_ROOT, 'app', 'theme.ts'), 'utf8');

const BASE_TOKENS = [
  '--rb-control-slant',
  '--rb-control-track-h',
  '--rb-control-track-radius',
  '--rb-control-thumb-size',
  '--rb-control-thumb-radius',
  '--rb-control-thumb-rim',
  '--rb-control-track-bg',
  '--rb-control-track-bg-disabled',
  '--rb-control-fill-bg',
  '--rb-control-fill-bg-disabled',
  '--rb-control-thumb-bg',
  '--rb-control-thumb-rim-color',
  '--rb-control-glow',
  '--rb-control-focus',
  '--rb-control-focus-halo',
  '--rb-control-hit',
  '--rb-control-switch-w',
  '--rb-control-switch-h',
  '--rb-control-switch-radius',
  '--rb-control-switch-thumb',
  '--rb-control-switch-off-bg',
  '--rb-control-switch-on-bg',
  '--rb-control-switch-thumb-bg',
];

const SIZES = ['xs', 'sm', 'md', 'lg', 'xl'] as const;

const SLIDER_SIZE_TOKENS = SIZES.map((size) => `--rb-control-track-h-${size}`);
const SWITCH_SIZE_TOKENS = SIZES.flatMap((size) => [
  `--rb-control-switch-w-${size}`,
  `--rb-control-switch-h-${size}`,
  `--rb-control-switch-thumb-${size}`,
]);

const ALL_TOKENS = [...BASE_TOKENS, ...SLIDER_SIZE_TOKENS, ...SWITCH_SIZE_TOKENS];

const TOKENS_ESCAPED = ALL_TOKENS.map((token) => token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'));

/**
 * The declarations of one scheme block. `--rb-control-*` is only allowed in these blocks and on the
 * Mantine roots that re-derive the size-dependent values, so the default block is the one the test
 * treats as the declared palette.
 */
function declaredInTheme(scheme: 'light' | 'dark'): Set<string> {
  const block = new RegExp(
    `:root\\[data-mantine-color-scheme='${scheme}'\\]\\s*\\{([\\s\\S]*?)\\n\\}`,
    'g',
  );
  const declared = new Set<string>();
  for (const match of GLOBAL_CSS.matchAll(block)) {
    for (const decl of match[1].matchAll(/(--[\w-]+)\s*:\s*[^;]+;/g)) {
      declared.add(decl[1]);
    }
  }
  return declared;
}

/** A token is consumed when an app consumer reads it through var(), never when it is only declared.
 * The lookahead stops `--rb-control-switch-w` from being satisfied by `var(--rb-control-switch-w-xs)`. */
function consumersOf(token: string): string[] {
  const pattern = new RegExp(`var\\(\\s*${token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}(?![\\w-])`);
  return [GLOBAL_CSS, THEME].filter((source) => pattern.test(source));
}

function sizeValue(token: string): number {
  const match = GLOBAL_CSS.match(
    new RegExp(`${token.replace(/[.*+?^${}()|[\]\\]/g, '\\$&')}:\\s*(\\d+)px`),
  );
  expect(match, `${token} must be declared with a px value`).not.toBeNull();
  return Number(match![1]);
}

describe('--rb-control-* tokens (applied shape)', () => {
  it.each(['light', 'dark'] as const)('declares every token in the %s theme block', (scheme) => {
    const declared = declaredInTheme(scheme);
    expect(ALL_TOKENS.filter((token) => !declared.has(token))).toEqual([]);
  });

  it('is consumed by an app consumer for every token (the opposite of the proposal guard)', () => {
    const unconsumed = TOKENS_ESCAPED.filter((token) => consumersOf(token).length === 0);
    expect(unconsumed).toEqual([]);
  });

  it('is consumed by both the global rules and the Mantine theme where the shape needs each', () => {
    // The geometry that cannot live in Mantine's theme API must be in global.css; the radius and
    // thumb size that Mantine exposes as CSS variables must reach it through theme.ts too.
    for (const token of [
      '--rb-control-slant',
      '--rb-control-thumb-rim',
      '--rb-control-glow',
      '--rb-control-hit',
    ]) {
      expect(consumersOf(token)).toContain(GLOBAL_CSS);
    }
    for (const token of [
      '--rb-control-thumb-size',
      '--rb-control-thumb-radius',
      '--rb-control-switch-radius',
    ]) {
      expect(consumersOf(token)).toContain(THEME);
    }
  });

  it('maps the track height to the Mantine size scale, strictly increasing', () => {
    for (const size of SIZES) {
      expect(GLOBAL_CSS).toMatch(
        new RegExp(`--slider-size-${size}:\\s*var\\(--rb-control-track-h-${size}\\)`),
      );
    }
    const heights = SIZES.map((size) => sizeValue(`--rb-control-track-h-${size}`));
    expect(heights).toEqual([12, 14, 18, 22, 26]);
    expect(heights.every((height, index) => index === 0 || height > heights[index - 1])).toBe(true);
    // Mantine's invariant: the thumb is exactly 2 x the track, so the fill ends at the knob.
    expect(GLOBAL_CSS).toMatch(/--rb-control-thumb-size:\s*calc\(var\(--slider-size\)\s*\*\s*2\)/);
  });

  it('maps every switch size, at B proportions (4 px inset per side, 3 px at xs)', () => {
    // B is 26/18: a 4 px inset each side. xs keeps a 14 px knob on a 20 px track (3 px a side), so
    // the knob keeps its presence at the smallest size.
    const inset: Record<(typeof SIZES)[number], number> = { xs: 6, sm: 8, md: 8, lg: 8, xl: 8 };
    for (const size of SIZES) {
      expect(GLOBAL_CSS).toMatch(
        new RegExp(`--switch-height-${size}:\\s*var\\(--rb-control-switch-h-${size}\\)`),
      );
      expect(GLOBAL_CSS).toMatch(
        new RegExp(`--switch-width-${size}:\\s*var\\(--rb-control-switch-w-${size}\\)`),
      );
      expect(GLOBAL_CSS).toMatch(
        new RegExp(`--switch-thumb-size-${size}:\\s*var\\(--rb-control-switch-thumb-${size}\\)`),
      );
      expect(sizeValue(`--rb-control-switch-h-${size}`) - sizeValue(`--rb-control-switch-thumb-${size}`)).toBe(inset[size]);
    }
  });

  it('ships variant B geometry and the recorded palette, including the muted disabled pairs', () => {
    expect(GLOBAL_CSS).toMatch(/--rb-control-slant:\s*15deg/);
    expect(GLOBAL_CSS).toMatch(/--rb-control-thumb-rim:\s*1px/);
    expect(GLOBAL_CSS).toMatch(/--rb-control-track-h-md:\s*18px/);
    expect(GLOBAL_CSS).not.toMatch(/--rb-control-glow:\s*none/);
    // Light then dark: the disabled pairs stay below 3:1 on purpose (WCAG 1.4.11 inactive exemption).
    expect(GLOBAL_CSS).toMatch(/--rb-control-track-bg-disabled:\s*#d6d2e1/);
    expect(GLOBAL_CSS).toMatch(/--rb-control-track-bg-disabled:\s*#2e2a3a/);
    expect(GLOBAL_CSS).toMatch(/--rb-control-fill-bg-disabled:\s*#b9b3c8/);
    expect(GLOBAL_CSS).toMatch(/--rb-control-fill-bg-disabled:\s*#4f4a5d/);
  });

  it('has no literal colour in any rule that consumes a control token', () => {
    const colourLiteral = /#[0-9a-f]{3,8}\b|\b(?:rgb|hsl|oklch|color)\(/i;
    const offenders = GLOBAL_CSS.split('\n')
      .map((line, index) => ({ line: line.trim(), index: index + 1 }))
      .filter(({ line }) => {
        if (!line.includes('var(--rb-control-')) return false;
        // The token declarations are the palette itself; only rules must be literal-free.
        if (/^--rb-control-[\w-]+\s*:/.test(line)) return false;
        if (line.startsWith('/*') || line.startsWith('*')) return false;
        return colourLiteral.test(line);
      })
      .map(({ line, index }) => `global.css:${index}: ${line}`);
    expect(offenders).toEqual([]);
  });
});
