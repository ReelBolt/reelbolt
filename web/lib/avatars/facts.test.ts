import { describe, expect, it } from 'vitest';
import { draftOf, factsPatch, fidelityBadge, hasFacts, readSubjectFidelity } from './facts';

const facts = { colour: 'silver', makeModel: null, marks: null, scale: null };

describe('identity facts form', () => {
  it('starts from the known facts with empty boxes for the rest', () => {
    expect(draftOf(facts)).toEqual({ colour: 'silver', makeModel: '', marks: '', scale: '' });
    expect(hasFacts(facts)).toBe(true);
    expect(hasFacts(null)).toBe(false);
  });
  it('sends only what changed, and an emptied box clears the fact', () => {
    expect(factsPatch(draftOf(facts), facts)).toBeNull();
    expect(factsPatch({ ...draftOf(facts), makeModel: ' Citroen C3 ', colour: '' }, facts)).toEqual({ makeModel: 'Citroen C3', colour: '' });
  });
});

describe('take check badge', () => {
  it('reads the verdict of a step output and shows only mismatch and unavailable', () => {
    expect(readSubjectFidelity('{"subjectFidelity":{"status":"mismatch"}}')).toBe('mismatch');
    expect(readSubjectFidelity('not json subjectFidelity')).toBeNull();
    expect(readSubjectFidelity(null)).toBeNull();
    expect(fidelityBadge('mismatch')).toBe('mismatch');
    expect(fidelityBadge('unavailable')).toBe('unavailable');
    expect(fidelityBadge('match')).toBeNull();
  });
});
