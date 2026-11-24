import { describe, expect, it } from 'vitest';
import { pollInterval } from './poll-interval';

describe('pollInterval', () => {
  it('does not poll when nothing can change', () => {
    expect(pollInterval({ active: false, unchangedForMs: 0 })).toBe(0);
  });
  it('polls fast at first and backs off after a minute without a change', () => {
    expect(pollInterval({ active: true, unchangedForMs: 0 })).toBe(4000);
    expect(pollInterval({ active: true, unchangedForMs: 59_999 })).toBe(4000);
    expect(pollInterval({ active: true, unchangedForMs: 60_000 })).toBe(8000);
  });
});
