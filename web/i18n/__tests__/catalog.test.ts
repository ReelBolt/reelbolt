import { describe, expect, it } from 'vitest';
import { DEFAULT_LOCALE, LOCALES } from '../config';
import { loadFlat, namespaceFiles, shapeOf } from '../catalog-utils';

const english = loadFlat(DEFAULT_LOCALE);
const englishFiles = namespaceFiles(DEFAULT_LOCALE);

describe('the language registry', () => {
  it('has English as the default', () => {
    expect(LOCALES).toContain(DEFAULT_LOCALE);
  });
});

describe.each(LOCALES)('catalogue %s', (locale) => {
  const flat = loadFlat(locale);

  it('has the same namespace files as English', () => {
    expect(namespaceFiles(locale)).toEqual(englishFiles);
  });

  it('has exactly the English key set (missing and extra are named)', () => {
    const missing = Object.keys(english).filter((k) => !(k in flat));
    const extra = Object.keys(flat).filter((k) => !(k in english));
    expect({ missing, extra }).toEqual({ missing: [], extra: [] });
  });

  it('has only non-empty strings, no TODO/FIXME, valid ICU', () => {
    const problems: string[] = [];
    for (const [key, value] of Object.entries(flat)) {
      if (typeof value !== 'string') { problems.push(`${key}: not a string`); continue; }
      if (value.trim() === '') problems.push(`${key}: empty`);
      if (/\b(TODO|FIXME)\b/.test(value)) problems.push(`${key}: leftover TODO/FIXME`);
      try {
        const shape = shapeOf(value);
        for (const [arg, ok] of Object.entries(shape.hasOther)) if (!ok) problems.push(`${key}: plural/select "${arg}" has no "other"`);
      } catch (e) {
        problems.push(`${key}: invalid ICU (${(e as Error).message})`);
      }
    }
    expect(problems).toEqual([]);
  });

  if (locale !== DEFAULT_LOCALE) {
    it('uses the same ICU arguments and plural/select shape as English, key by key', () => {
      const problems: string[] = [];
      for (const [key, en] of Object.entries(english)) {
        const other = flat[key];
        if (typeof en !== 'string' || typeof other !== 'string') continue;
        try {
          const a = shapeOf(en).args;
          const b = shapeOf(other).args;
          if (JSON.stringify(Object.entries(a).sort()) !== JSON.stringify(Object.entries(b).sort())) {
            problems.push(`${key}: en ${JSON.stringify(a)} vs ${locale} ${JSON.stringify(b)}`);
          }
        } catch {
          // reported by the ICU-validity test
        }
      }
      expect(problems).toEqual([]);
    });
  }
});
