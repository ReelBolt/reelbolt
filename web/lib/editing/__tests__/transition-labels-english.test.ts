import { describe, expect, it } from 'vitest';
import en from '../../../messages/en/editor-lib.json';
import { transitionLabel } from '../clip-depth/transitions';

/** The English wording of a transition is unchanged: the catalogue says what the English helper has always said. */
describe('transition labels in the English catalogue', () => {
  const entries = Object.entries(en.transitions).filter(([, v]) => typeof v === 'string' && v !== '{type}') as Array<[string, string]>;
  it.each(entries)('%s', (type, label) => {
    expect(label).toBe(transitionLabel(type));
  });
});
