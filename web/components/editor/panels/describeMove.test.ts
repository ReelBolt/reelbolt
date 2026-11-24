import { describe, expect, it } from 'vitest';
import { describeMove } from './describeMove';
import { syncConfidenceWord } from './syncConfidence';

const rate = { num: 30, den: 1 };
describe('plain-language panel text', () => {
  it('describes a move as a timecode sentence', () => {
    expect(describeMove({ recordIn: 60, recordOut: 120 }, { recordIn: 150, recordOut: 210 }, rate)).toMatch(/^Moved from .*00:02.* to .*00:05/);
  });
  it('maps correlation to good, fair or low', () => {
    const r = (correlation: number, margin: number) => ({ offsetSeconds: 1, correlation, margin }) as never;
    expect(syncConfidenceWord(r(0.8, 0.5))).toBe('good');
    expect(syncConfidenceWord(r(0.35, 0))).toBe('fair');
    expect(syncConfidenceWord(r(0.1, 0))).toBe('low');
  });
});
