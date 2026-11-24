import { describe, expect, it } from 'vitest';
import { resyncDelay } from './resync-delay';

describe('resyncDelay', () => {
  it('waits the debounce for a fresh trigger', () => {
    expect(resyncDelay(1000, 1000)).toBe(1500);
  });
  it('shortens the wait so a steady stream still reads within 5 s of the first trigger', () => {
    expect(resyncDelay(4000, 0)).toBe(1000);
    expect(resyncDelay(4900, 0)).toBe(100);
  });
  it('reads at once when 5 s have already passed', () => {
    expect(resyncDelay(6000, 0)).toBe(0);
  });
});
