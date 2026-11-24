import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { describe, expect, it } from 'vitest';
import { PERSONAS, PERSONA_PARAM, sanitizePersona } from './persona';

describe('persona closed set', () => {
  it('is exactly the seven tokens of the design, in its order', () => {
    expect([...PERSONAS]).toEqual(['creator', 'business', 'educator', 'nonprofit', 'software', 'agency', 'other']);
    expect(PERSONA_PARAM).toBe('for');
  });

  // The design of record names the set once (section 4). A token added on one side only would be
  // dropped silently by the other, so the list is read from the document itself.
  it('matches the set written in docs/design/new-user-funnel.md', () => {
    const doc = readFileSync(join(__dirname, '..', '..', '..', 'docs', 'design', 'new-user-funnel.md'), 'utf8');
    const line = doc.split('\n').find((l) => l.startsWith('- **Persona**:'));
    expect(line).toBeTruthy();
    expect([...line!.matchAll(/`([a-z]+)`/g)].map((m) => m[1])).toEqual([...PERSONAS]);
  });

  it('trims and lowercases, then accepts only a member of the set', () => {
    expect(sanitizePersona('  Creator ')).toBe('creator');
    expect(sanitizePersona('other')).toBe('other');
    for (const p of PERSONAS) expect(sanitizePersona(p)).toBe(p);
  });

  it('drops anything else, never throws', () => {
    for (const bad of ['', '   ', 'creators', 'small-business', 'admin', '<script>', 'creator,business', null, undefined]) {
      expect(sanitizePersona(bad)).toBeUndefined();
    }
  });
});
