import { describe, expect, it } from 'vitest';
import { DEFAULT_LOCALE, LOCALES } from '../config';
import { loadFlat } from '../catalog-utils';

/**
 * A translator's note must never ship in a catalogue value (users would read it). Notes belong in
 * messages/<locale>/_flags/<area>.json. ICU select/plural syntax uses braces, so a '|' in a value is never
 * legitimate; the other markers are leftovers from drafting. Applies to every locale except the source.
 */
const MARKERS: [RegExp, string][] = [
  [/\|/, "contains '|' (a translator note after a separator?)"],
  [/\[TODO/i, "contains '[TODO'"],
  [/\bNOTE:/, "contains 'NOTE:'"],
  [/\bXXX\b/, "contains 'XXX'"],
];

describe('catalogues carry no translator notes', () => {
  const locales = LOCALES.filter((l: string) => l !== DEFAULT_LOCALE);
  for (const locale of locales) {
    it(`${locale} values contain no note markers`, () => {
      const hits: string[] = [];
      for (const [key, value] of Object.entries(loadFlat(locale))) {
        if (typeof value !== 'string') continue;
        for (const [re, why] of MARKERS) if (re.test(value)) hits.push(`${locale} ${key}: ${why}`);
      }
      expect(hits).toEqual([]);
    });
  }
});
