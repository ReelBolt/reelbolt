import { describe, expect, it } from 'vitest';
import { createRecentDrops } from '../recent-drops';

describe('frames skipped in the last few seconds', () => {
  it('counts drops inside the window and forgets them after it', () => {
    const recent = createRecentDrops(3000);
    expect(recent.push({ at: 0, dropped: 0 })).toBe(0);
    expect(recent.push({ at: 500, dropped: 2 })).toBe(2);
    expect(recent.push({ at: 1000, dropped: 5 })).toBe(5);
    // Smooth from here on: the count holds while those drops are inside the window, then falls to zero.
    expect(recent.push({ at: 3000, dropped: 5 })).toBe(5);
    expect(recent.push({ at: 3600, dropped: 5 })).toBe(3);
    expect(recent.push({ at: 4100, dropped: 5 })).toBe(0);
  });

  it('starts over after a reset or when a new session counts from zero', () => {
    const recent = createRecentDrops(3000);
    recent.push({ at: 0, dropped: 10 });
    expect(recent.push({ at: 100, dropped: 12 })).toBe(2);
    recent.reset();
    expect(recent.push({ at: 200, dropped: 12 })).toBe(0);
    expect(recent.push({ at: 300, dropped: 1 })).toBe(0);
    expect(recent.push({ at: 400, dropped: 3 })).toBe(2);
  });
});
