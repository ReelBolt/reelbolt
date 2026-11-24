// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook } from '@testing-library/react';
import { doc, media, track } from '@/lib/editing/__tests__/fixtures';
import type { EditTimeline } from '@/lib/editing/types';
import type { EditProposalResponse } from '@/lib/editing/sync/api';
import { expansionStats } from '@/lib/editing/proposals/apply-proposal';
import { GHOST_SETTLE_MS, useGhostDiff } from '../useProposalFlow';

const V = '11111111-0000-4000-8000-000000000001';
const gen = { origin: 'generated' as const, editedByUser: false };
const make = (end: number): EditTimeline => doc([track('55555555-0000-4000-8000-000000000001', 'video', [media(V, 0, end, 0, { provenance: gen })])]);
const cuts = Array.from({ length: 150 }, (_, i) => ({ itemId: V, startSec: i * 2 + 0.5, endSec: i * 2 + 1 }));
const proposal = { groups: [{ label: 'cuts', ops: [], touchedItemIds: [V], touchesUserOwned: false, reason: null, cuts }] } as unknown as EditProposalResponse;
const sel = new Set([0]);

beforeEach(() => vi.useFakeTimers());
afterEach(() => { cleanup(); vi.useRealTimers(); });

describe('ghost diff while a content-cut proposal is open', () => {
  it('re-expands once after a burst of document edits, not once per edit', () => {
    const { rerender, result } = renderHook(({ d }) => useGhostDiff(d, proposal, sel), { initialProps: { d: make(400 * 30) } });
    const first = expansionStats.computed;
    expect(result.current?.failed).toEqual([]);
    for (let n = 1; n <= 10; n++) rerender({ d: make(400 * 30 + n) });
    expect(expansionStats.computed).toBe(first);
    act(() => { vi.advanceTimersByTime(GHOST_SETTLE_MS + 1); });
    expect(expansionStats.computed).toBe(first + 1);
  });

  it('a proposal without cuts still follows the document at once', () => {
    const plain = { groups: [{ label: 'x', ops: [], touchedItemIds: [], touchesUserOwned: false, reason: null }] } as unknown as EditProposalResponse;
    const { rerender, result } = renderHook(({ d }) => useGhostDiff(d, plain, sel), { initialProps: { d: make(300) } });
    const before = result.current;
    rerender({ d: make(301) });
    expect(result.current).not.toBe(before);
  });
});
