// @vitest-environment jsdom
/**
 * useObjectRequest poll paths: a 5xx or network error backs off and is asked again, a 4xx stops at once, `stalled`
 * turns true after repeated errors and clears when a later poll succeeds, and re-renders do not stop the polling.
 */
import React, { useEffect, useState } from 'react';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import { ApiError } from '@/lib/api/client';
import { useObjectRequest } from '@/lib/editing/api/objects';

const get = vi.hoisted(() => ({ fn: vi.fn() }));
vi.mock('@/lib/api/client', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/api/client')>()), fetcher: get.fn }));
afterEach(cleanup);

const queued = { requestId: 'r1', executionId: 'e1', status: 'queued', prompt: 'face' };

function Probe({ tickMs }: { tickMs?: number }) {
  const [, setN] = useState(0);
  useEffect(() => {
    if (!tickMs) return;
    const id = window.setInterval(() => setN((n) => n + 1), tickMs);
    return () => window.clearInterval(id);
  }, [tickMs]);
  const r = useObjectRequest('p', 'file:f', 'r1');
  return (
    <>
      <span data-testid="stalled">{String(r.stalled)}</span>
      <button data-testid="retry" onClick={() => r.retry()} />
    </>
  );
}
const view = (tickMs?: number) => render(<SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}><Probe tickMs={tickMs} /></SWRConfig>);
const wait = (ms: number) => new Promise((r) => setTimeout(r, ms));

describe('useObjectRequest poll paths', () => {
  it('asks again after a 5xx (backoff, not a dead stop)', async () => {
    get.fn.mockReset().mockRejectedValue(new ApiError('boom', 500));
    view();
    await waitFor(() => expect(get.fn).toHaveBeenCalledTimes(1));
    await wait(3600); // backoff after one error is 3 s
    expect(get.fn.mock.calls.length).toBeGreaterThan(1);
  }, 10000);

  it('reports stalled when polling stops after the request was already seen', async () => {
    get.fn.mockReset().mockResolvedValueOnce(queued).mockRejectedValue(new ApiError('gone', 404));
    view();
    await waitFor(() => expect(get.fn.mock.calls.length).toBeGreaterThanOrEqual(2), { timeout: 4000 });
    await wait(200);
    expect(screen.getByTestId('stalled').textContent).toBe('true');
  }, 10000);

  it('keeps polling while the host re-renders often (e.g. the inspector during playback)', async () => {
    get.fn.mockReset().mockResolvedValue(queued);
    view(300);
    await waitFor(() => expect(get.fn).toHaveBeenCalledTimes(1));
    await wait(3500);
    expect(get.fn.mock.calls.length).toBeGreaterThanOrEqual(2);
  }, 10000);

  it('stalls after repeated 5xx errors and recovers when a poll succeeds', async () => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    try {
      get.fn.mockReset().mockRejectedValue(new ApiError('boom', 503));
      view();
      await vi.advanceTimersByTimeAsync(40000);
      expect(get.fn.mock.calls.length).toBe(4);
      await waitFor(() => expect(screen.getByTestId('stalled').textContent).toBe('true'));
      get.fn.mockReset().mockResolvedValue({ ...queued, status: 'succeeded' });
      fireEvent.click(screen.getByTestId('retry'));
      await waitFor(() => expect(screen.getByTestId('stalled').textContent).toBe('false'));
    } finally {
      vi.useRealTimers();
    }
  }, 20000);
});
