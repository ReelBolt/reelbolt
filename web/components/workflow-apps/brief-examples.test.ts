import { describe, expect, it } from 'vitest';
import { createTranslator } from 'use-intl/core';
import { loadMessages } from '@/i18n/messages';
import { PERSONAS } from '@/lib/attribution/persona';
import type { Msg } from '@/i18n/msg';
import { EXAMPLES_PER_FAMILY, briefExamples, briefFamilyOf, briefPersona, briefPrompt, insertExample, type BriefFamily } from './brief-examples';

const translator = (locale: string) => {
  const t = createTranslator({ locale, messages: loadMessages(locale) }) as unknown as (k: string, v?: Record<string, unknown>) => string;
  return (m: Msg): string => t(m.$msg, m.values as Record<string, unknown>);
};
const FAMILIES: BriefFamily[] = ['footage', 'promo'];

describe('brief examples', () => {
  it('are written for the footage and promo families only', () => {
    expect(briefFamilyOf('footage')).toBe('footage');
    expect(briefFamilyOf('promo')).toBe('promo');
    for (const other of ['brand', 'utility', '', null, undefined]) expect(briefFamilyOf(other)).toBeNull();
  });

  it('speak to the chosen audience, else the suggested one, else nobody in particular', () => {
    expect(briefPersona({ persona: 'educator', suggestedPersona: 'creator' })).toBe('educator');
    expect(briefPersona({ persona: null, suggestedPersona: 'nonprofit' })).toBe('nonprofit');
    expect(briefPersona({ persona: null, suggestedPersona: null })).toBe('other');
    expect(briefPersona(null)).toBe('other');
    expect(briefPersona({ persona: 'wizard' as never, suggestedPersona: null })).toBe('other');
  });

  for (const locale of ['en', 'it']) {
    it(`has three different, real examples and a prompt for every audience and family (${locale})`, () => {
      const say = translator(locale);
      const seen = new Map<string, string>();
      for (const family of FAMILIES) {
        const prompts = new Set<string>();
        for (const persona of PERSONAS) {
          const examples = briefExamples(family, persona).map(say);
          expect(examples).toHaveLength(EXAMPLES_PER_FAMILY);
          expect(new Set(examples).size).toBe(EXAMPLES_PER_FAMILY);
          for (const example of examples) {
            // A sentence a person could type: no placeholder, no braces left from the catalogue.
            expect(example.length).toBeGreaterThan(30);
            expect(example).not.toMatch(/[{}]|\bTODO\b|lorem/i);
            // Every audience has its own words: no example is shared by two audiences of a family.
            const key = `${family}:${example}`;
            expect(seen.get(key), `"${example}" is used twice`).toBeUndefined();
            seen.set(key, persona);
          }
          const prompt = say(briefPrompt(family, persona));
          expect(prompt.length).toBeGreaterThan(20);
          prompts.add(prompt);
        }
        // The prompt changes with the audience too (two audiences may share one, not all).
        expect(prompts.size).toBeGreaterThan(3);
      }
    });
  }

  it('makes no promise in an example: no price, no number of minutes saved, no hype', () => {
    const say = translator('en');
    for (const family of FAMILIES) {
      for (const persona of PERSONAS) {
        for (const example of briefExamples(family, persona).map(say)) {
          expect(example).not.toMatch(/\$|€|\bcredits?\b|\bfaster\b|\bsaves? (?:you|time|hours)|\d+ ?x\b|guarantee|viral|!/i);
        }
      }
    }
  });

  it('puts an example in the box without ever replacing what the person typed', () => {
    const examples = ['Example one.', 'Example two.', 'Example three.'];
    expect(insertExample('', 'Example one.', examples)).toBe('Example one.');
    expect(insertExample('   ', 'Example one.', examples)).toBe('Example one.');
    // An untouched example is swapped for the next one chosen.
    expect(insertExample('Example one.', 'Example two.', examples)).toBe('Example two.');
    // The person's own words stay; the example goes on a new line.
    expect(insertExample('For my mum.', 'Example two.', examples)).toBe('For my mum.\nExample two.');
    expect(insertExample('For my mum.\n', 'Example two.', examples)).toBe('For my mum.\nExample two.');
    // An edited example is the person's own text now.
    expect(insertExample('Example one, shorter.', 'Example two.', examples)).toBe('Example one, shorter.\nExample two.');
    // Choosing the same example twice adds nothing.
    expect(insertExample('For my mum.\nExample two.', 'Example two.', examples)).toBe('For my mum.\nExample two.');
  });
});
