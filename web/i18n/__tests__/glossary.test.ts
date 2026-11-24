import { describe, expect, it } from 'vitest';
import glossary from '../glossary.json';
import { loadFlat } from '../catalog-utils';

describe('Italian glossary', () => {
  it('has no banned variant in any Italian string', () => {
    const banned = (glossary.it.banned as [string, string, string?][]).map(([pattern, why, flags]) => ({ re: new RegExp(pattern, flags ?? 'i'), why }));
    const hits: string[] = [];
    for (const [key, value] of Object.entries(loadFlat('it'))) {
      if (typeof value !== 'string') continue;
      for (const { re, why } of banned) if (re.test(value)) hits.push(`${key}: "${value}" (${why})`);
    }
    expect(hits).toEqual([]);
  });
});
