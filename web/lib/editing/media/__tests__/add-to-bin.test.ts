import { describe, expect, it } from 'vitest';
import { isSmartBin, planAddToBin, smartBinRefusal } from '../bins';
import { BINS, POOL } from '../fixtures';
import type { MediaBin } from '../types';

const smart: MediaBin = { id: 'smart-1', name: 'Long takes', parentBinId: null, sortOrder: 9, itemCount: 0, isSmart: true, smartFilter: { nameContains: 'take' } };

describe('planAddToBin (the tile menu’s "Add to bin", no drag needed)', () => {
  it('adds a clip to an ordinary bin it is not in', () => {
    const target = BINS.find((b) => !POOL[0].bins.some((pin) => pin.binId === b.id))!;
    expect(planAddToBin(POOL[0].mediaRef, target.id, BINS, POOL[0].bins)).toEqual({ kind: 'addItem', binId: target.id, mediaRef: POOL[0].mediaRef });
  });

  it('refuses a smart bin with the same stated reason a drop gets', () => {
    const plan = planAddToBin(POOL[0].mediaRef, smart.id, [...BINS, smart], POOL[0].bins);
    expect(plan).toEqual({ kind: 'none', reason: smartBinRefusal(smart) });
    expect(smartBinRefusal(smart)).toBe('"Long takes" is a smart bin: it shows every source matching its filter. Tag the clip instead, or add it to an ordinary bin.');
  });

  it('refuses a bin the clip is already in, and a bin that is gone', () => {
    const pinned = POOL[0].bins[0];
    expect(planAddToBin(POOL[0].mediaRef, pinned.binId, BINS, POOL[0].bins)).toEqual({ kind: 'none', reason: 'That clip is already in this bin.' });
    expect(planAddToBin(POOL[0].mediaRef, 'gone', BINS, POOL[0].bins)).toEqual({ kind: 'none', reason: 'That bin no longer exists.' });
  });

  it('knows a smart bin by its flag or by its filter', () => {
    expect(isSmartBin(smart)).toBe(true);
    expect(isSmartBin({ isSmart: false, smartFilter: null })).toBe(false);
    expect(isSmartBin({ smartFilter: { favourite: true } })).toBe(true);
  });
});
