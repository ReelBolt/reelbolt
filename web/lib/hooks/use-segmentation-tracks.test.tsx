import React from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, renderHook } from '@testing-library/react';
import { SWRConfig } from 'swr';

vi.mock('../api/client', () => ({
  fetcher: vi.fn(async () => [{ trackId: 'g1', label: 'cat', fps: 30, firstFrame: 0, lastFrame: 30, coverageEndSec: 1, boxKeyframes: [], maskPreview: { state: 'pending' } }]),
}));

import { MASK_PREVIEW_MAX_TRIES } from '../editing/preview/segmentation-tracks';
import { useSegmentationTracks } from './use-segmentation-tracks';

const wrapper = ({ children }: { children: React.ReactNode }) => <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>;

describe('mask preview polling keeps going while the preview stays pending', () => {
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('asks again every 2 s while the server answers 202 (the first 202 must not end the polling)', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const fetchMock = vi.fn(async () => new Response(JSON.stringify({ state: 'pending' }), { status: 202 }));
    vi.stubGlobal('fetch', fetchMock);
    renderHook(() => useSegmentationTracks('p1', ['a1']), { wrapper });
    for (let i = 0; i < 6; i++) {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(2100);
      });
    }
    expect(fetchMock.mock.calls.length).toBeGreaterThanOrEqual(3);
    expect(fetchMock.mock.calls.length).toBeLessThanOrEqual(MASK_PREVIEW_MAX_TRIES);
  });

  it('stops for good on a 404 (none)', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    const fetchMock = vi.fn(async () => new Response('', { status: 404 }));
    vi.stubGlobal('fetch', fetchMock);
    renderHook(() => useSegmentationTracks('p1', ['a1']), { wrapper });
    for (let i = 0; i < 5; i++) {
      await act(async () => {
        await vi.advanceTimersByTimeAsync(2100);
      });
    }
    expect(fetchMock.mock.calls.length).toBe(1);
  });
});
