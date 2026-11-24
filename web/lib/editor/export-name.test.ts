import { describe, expect, it } from 'vitest';
import { DEFAULT_EXPORT_NAME, exportFileName } from './export-name';

describe('exportFileName', () => {
  it('adds .png and keeps a good name', () => {
    expect(exportFileName('My cover')).toBe('My cover.png');
    expect(exportFileName('cover.PNG')).toBe('cover.png');
  });

  it('falls back to the default for an empty or dots-only name', () => {
    expect(exportFileName('')).toBe(DEFAULT_EXPORT_NAME);
    expect(exportFileName('   ')).toBe(DEFAULT_EXPORT_NAME);
    expect(exportFileName('...')).toBe(DEFAULT_EXPORT_NAME);
    expect(exportFileName('.png')).toBe(DEFAULT_EXPORT_NAME);
  });

  it('strips path separators and characters storage dislikes', () => {
    expect(exportFileName('../../etc/passwd')).toBe('etc passwd.png');
    expect(exportFileName('a:b*c?d"e<f>g|h')).toBe('a b c d e f g h.png');
    expect(exportFileName('line\nbreak')).toBe('line break.png');
  });

  it('limits the length', () => {
    expect(exportFileName('x'.repeat(500)).length).toBeLessThanOrEqual(104);
  });
});
