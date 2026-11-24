import { describe, expect, it, vi } from 'vitest';
import { parentsFirst, restoreDeletedBins, type DeletedBinSnapshot } from './bin-restore';
import type { MediaBin } from './types';

const bin = (id: string, parentBinId: string | null, sortOrder = 0, extra: Partial<MediaBin> = {}): MediaBin => ({
  id, name: id, parentBinId, sortOrder, itemCount: 0, ...extra,
});

describe('R05.3 bin restore', () => {
  it('orders parents before children whatever the input order', () => {
    const ordered = parentsFirst([
      { bin: bin('child', 'root'), mediaRefs: [] },
      { bin: bin('grand', 'child'), mediaRefs: [] },
      { bin: bin('root', null), mediaRefs: [] },
    ]);
    expect(ordered.map((s) => s.bin.id)).toEqual(['root', 'child', 'grand']);
  });

  it('re-creates the subtree under new ids, keeps the outer parent, and re-pins every clip', async () => {
    let n = 0;
    const createBin = vi.fn(async (request: { name: string; parentBinId?: string | null }) => ({
      id: `n${(n += 1)}`, name: request.name, parentBinId: request.parentBinId ?? null, sortOrder: 0, itemCount: 0,
    }));
    const addBinItem = vi.fn(async () => ({ id: `pin${(n += 1)}` }));
    const snapshots: DeletedBinSnapshot[] = [
      { bin: bin('sub', 'root'), mediaRefs: ['file:b'] },
      { bin: bin('root', 'outer'), mediaRefs: ['file:a', 'file:c'] },
    ];
    const result = await restoreDeletedBins(snapshots, { createBin, addBinItem });
    expect(createBin.mock.calls.map((c) => c[0])).toEqual([
      { name: 'root', parentBinId: 'outer' },
      { name: 'sub', parentBinId: 'n1' },
    ]);
    expect(result.bins.map((b) => b.itemCount)).toEqual([2, 1]);
    expect(result.memberships).toHaveLength(3);
    expect(result.skippedPins).toBe(0);
  });

  it('keeps a smart bin rule and counts a pin whose source is gone as skipped', async () => {
    const createBin = vi.fn(async (request: { name: string }) => ({ id: 'x', name: request.name, parentBinId: null, sortOrder: 0, itemCount: 0 }));
    const addBinItem = vi.fn(async () => { throw new Error('gone'); });
    const rule = { kinds: ['video'] } as NonNullable<MediaBin['smartFilter']>;
    const result = await restoreDeletedBins(
      [{ bin: bin('smart', null, 0, { smartFilter: rule, isSmart: true }), mediaRefs: [] }, { bin: bin('plain', null, 1), mediaRefs: ['file:z'] }],
      { createBin, addBinItem },
    );
    expect(createBin.mock.calls[0][0]).toEqual({ name: 'smart', smartFilter: rule });
    expect(result.skippedPins).toBe(1);
    expect(result.bins[1].itemCount).toBe(0);
  });
});
