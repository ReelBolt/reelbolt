import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { afterEach, describe, expect, it } from 'vitest';
import { cleanup, render } from '@testing-library/react';
import { BrandMark } from './BrandMark';

// The dashboard mark is the brand app-icon geometry drawn inline. These assertions pin the two
// things that can silently drift: the geometry (which must match brand/app-icon.svg) and the
// colour source (Mantine's violet ramp, never a hardcoded brand hex).
describe('BrandMark', () => {
  afterEach(cleanup);

  it('draws the app-icon geometry from brand/app-icon.svg on the 120-unit box', () => {
    const { container } = render(<BrandMark />);
    const svg = container.querySelector('svg');
    expect(svg?.getAttribute('viewBox')).toBe('0 0 120 120');
    expect(svg?.getAttribute('width')).toBe('28');
    const paths = [...(svg?.querySelectorAll('path') ?? [])].map((p) => p.getAttribute('d'));
    expect(paths).toContain('M36 96V24H62a21 21 0 0 1 0 42H36');
    expect(paths).toContain('M68 12 L28 68 H54 L46 108 L92 48 H64 Z');
  });

  // The colour source moved from inline SVG attributes to BrandMark.module.css, because the mark
  // has to draw differently on a light header and a dark one and one fixed palette cannot do both.
  // The guard's intent is unchanged — Mantine tokens, never a hardcoded brand hex — so it now
  // checks the two places that can actually break it: the markup must not carry a hex, and the
  // stylesheet must not either.
  it('takes its colour from Mantine tokens rather than hardcoded hexes, so it follows primaryColor', () => {
    const { container } = render(<BrandMark />);
    const html = container.innerHTML;
    // Colours are applied by class from the module, so no colour value should be inline at all.
    expect(html).not.toMatch(/#[0-9a-fA-F]{3,6}/);
    expect(html).not.toContain('var(--mantine-color');

    // Comments are stripped first: the file documents which brand hexes it is replacing, and a
    // hex named in prose is not a hex in use.
    const css = readFileSync(join(__dirname, 'BrandMark.module.css'), 'utf8').replace(
      /\/\*[\s\S]*?\*\//g,
      '',
    );
    expect(css).toContain('var(--mantine-color-violet-6)');
    expect(css).not.toMatch(/#[0-9a-fA-F]{3,6}/);
  });

  it('draws a different square in each colour scheme, so the mark is not a pale block on dark', () => {
    const css = readFileSync(join(__dirname, 'BrandMark.module.css'), 'utf8');
    // The regression this pins: the square was ONE fixed violet-1 — a near-white step — in both
    // schemes, so dark mode drew a large pale block. Every colour that must differ per scheme is
    // therefore a light-dark() pair of two DIFFERENT tokens.
    expect(css).toContain(
      'light-dark(var(--mantine-color-violet-1), var(--mantine-color-violet-9))',
    );
    expect(css).toContain(
      'light-dark(var(--mantine-color-violet-9), var(--mantine-color-violet-2))',
    );
    // ...and the bolt's outline tracks whichever square it sits on.
    expect(css.match(/light-dark\(/g)?.length).toBeGreaterThanOrEqual(3);
  });

  it('is decorative: the adjacent title carries the accessible name', () => {
    const { container } = render(<BrandMark />);
    expect(container.querySelector('svg')?.getAttribute('aria-hidden')).toBe('true');
  });
});
