import { describe, expect, it } from 'vitest';
import { isNeedsAttention, runStatusDisplay } from './run-status';

const na = { reason: 'r', stepOrder: 1, score: 6, minScore: 8, iterations: 3, message: 'm' };

describe('run status', () => {
  it('flags only a Passed run with needsAttention', () => {
    expect(isNeedsAttention({ status: 'Passed', needsAttention: na })).toBe(true);
    expect(isNeedsAttention({ status: 'Passed', needsAttention: null })).toBe(false);
    expect(isNeedsAttention({ status: 'Failed', needsAttention: na })).toBe(false);
  });
  it('derives amber label or the plain status', () => {
    expect(runStatusDisplay({ status: 'Passed', needsAttention: na })).toEqual({ label: 'Needs attention', color: 'yellow' });
    expect(runStatusDisplay({ status: 'Passed' })).toEqual({ label: 'Passed', color: 'green' });
  });
});
