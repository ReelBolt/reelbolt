import { describe, expect, it } from 'vitest';
import { fontStackText, isFontFamilyPath } from './font-diff';

describe('isFontFamilyPath', () => {
  it.each([
    ['typography.family.heading', true],
    ['typography.heading.fontFamily', true],
    ['fontFamily', true],
    ['color.family-tint', false],
    ['typography.familyName.heading', false],
    ['color.brand.primary', false],
  ])('%s -> %s', (path, expected) => {
    expect(isFontFamilyPath(path)).toBe(expected);
  });
});

describe('fontStackText', () => {
  it('joins an array of family names and keeps a string verbatim', () => {
    expect(fontStackText(['Inter', 'sans-serif'])).toBe('Inter, sans-serif');
    expect(fontStackText('Chakra Petch')).toBe('Chakra Petch');
  });
  it('is null for anything else', () => {
    expect(fontStackText(null)).toBeNull();
    expect(fontStackText({ hex: '#fff' })).toBeNull();
    expect(fontStackText([])).toBeNull();
    expect(fontStackText('  ')).toBeNull();
  });
});
