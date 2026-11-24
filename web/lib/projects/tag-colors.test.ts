import { describe, expect, it } from 'vitest';
import { TAG_COLORS } from '../types/project-organization';
import { TAG_COLOR_TOKENS, tagChipColors } from './tag-colors';

const lum = (hex: string) => {
  const [r, g, b] = [1, 3, 5].map((i) => {
    const c = parseInt(hex.slice(i, i + 2), 16) / 255;
    return c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4;
  });
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};
const contrast = (a: string, b: string) => {
  const [hi, lo] = [lum(a), lum(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

describe('tag colour tokens', () => {
  it('covers every palette word', () => {
    expect(Object.keys(TAG_COLOR_TOKENS).sort()).toEqual([...TAG_COLORS].sort());
  });
  it.each([...TAG_COLORS])('%s is readable (>= 4.5:1) in light and dark', (word) => {
    const t = TAG_COLOR_TOKENS[word];
    expect(contrast(t.light.text, t.light.bg)).toBeGreaterThanOrEqual(4.5);
    expect(contrast(t.dark.text, t.dark.bg)).toBeGreaterThanOrEqual(4.5);
  });
  it('falls back to gray for unknown words', () => {
    expect(tagChipColors('chartreuse').mantine).toBe('gray');
    expect(tagChipColors('teal').mantine).toBe('teal');
  });
});
