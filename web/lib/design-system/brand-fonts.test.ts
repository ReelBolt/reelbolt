import { describe, expect, it } from 'vitest';
import { brandCssFamily, brandFontFamilies, parseFontStack, resolveBrandFont } from './brand-fonts';

describe('resolveBrandFont', () => {
  it('finds every pinned family, case-insensitively', () => {
    expect(brandFontFamilies()).toHaveLength(11);
    expect(resolveBrandFont('inter')).toEqual({ kind: 'available', family: 'Inter', cssFamily: 'ReelBolt Brand Font 1' });
    expect(resolveBrandFont('JETBRAINS MONO').kind).toBe('available');
  });

  it('resolves stacks and quoted names', () => {
    expect(resolveBrandFont('Inter, sans-serif')).toMatchObject({ kind: 'available', family: 'Inter' });
    expect(resolveBrandFont(`'Chakra Petch', "Helvetica Neue", sans-serif`)).toMatchObject({ family: 'Chakra Petch' });
    expect(resolveBrandFont('Helvetica, "Space Grotesk", sans-serif')).toMatchObject({ family: 'Space Grotesk' });
  });

  it('reports generic keywords', () => {
    expect(resolveBrandFont('sans-serif')).toEqual({ kind: 'generic', keyword: 'sans-serif' });
    expect(resolveBrandFont('Monospace, serif')).toEqual({ kind: 'generic', keyword: 'monospace' });
  });

  it('reports bundled-but-not-pinned and unknown families as unavailable', () => {
    for (const name of ['DejaVu Sans', 'Oswald', 'Roboto Condensed', 'Archivo Black', 'Permanent Marker', 'Bangers', 'Comic Neue']) {
      expect(resolveBrandFont(name)).toEqual({ kind: 'unavailable', name });
    }
    expect(resolveBrandFont('Foo, sans-serif')).toEqual({ kind: 'unavailable', name: 'Foo' });
  });
});

describe('parseFontStack', () => {
  it('keeps commas inside quotes', () => {
    expect(parseFontStack(`"A, B", 'C'  d,e`)).toEqual(['A, B', 'C d', 'e']);
  });

  it('declares every family under a distinct neutral alias that never carries the family name', () => {
    const aliases = brandFontFamilies().map((family) => brandCssFamily(family));
    expect(brandCssFamily('Comic Neue')).toBeNull();
    expect(new Set(aliases).size).toBe(brandFontFamilies().length);
    brandFontFamilies().forEach((family, index) => {
      expect(brandCssFamily(family)).toBe(`ReelBolt Brand Font ${index + 1}`);
      expect(brandCssFamily(family)?.toLowerCase()).not.toContain(family.toLowerCase());
    });
  });
});
