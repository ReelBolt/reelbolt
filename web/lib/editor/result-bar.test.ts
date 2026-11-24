import { describe, expect, it } from 'vitest';
import { changedWords, chargedWords, optionLabel } from './result-bar';

describe('result bar wording', () => {
  it('says what changed in plain words', () => {
    expect(changedWords({ title: 'Erase object', layerNames: ['Erase object'] }, false)).toBe('Erase object added a new layer, "Erase object". Your original is untouched.');
    expect(changedWords({ title: 'Cut out subject', layerNames: ['a', 'b', 'c'] }, true)).toBe('Cut out subject found 3 options. Showing option 1.');
    expect(changedWords({ title: 'Make bigger', layerNames: ['a', 'b'] }, false)).toBe('Make bigger added 2 new layers. Your originals are untouched.');
    expect(changedWords({ title: 'X', layerNames: [] }, false)).toContain('nothing new');
  });
  it('shows the cost, or says nothing was charged', () => {
    expect(chargedWords(4.5)).toMatch(/It cost .*4\.5/);
    expect(chargedWords(null)).toBe('No credits were charged.');
    expect(chargedWords(0)).toBe('No credits were charged.');
  });
  it('labels the option', () => {
    expect(optionLabel(1, 3)).toBe('Option 2 of 3');
  });
});
