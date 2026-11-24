// @vitest-environment jsdom
import { describe, it, expect, vi, beforeEach } from 'vitest';
import { act, renderHook, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';
import type { ReactNode } from 'react';

const api = vi.hoisted(() => ({
  getWorkflows: vi.fn(),
  getWorkflowExecutions: vi.fn(),
}));
vi.mock('../api/workflows', () => api);
vi.mock('../utils/poll-interval', async (orig) => {
  const actual = await orig<typeof import('../utils/poll-interval')>();
  // The real rule with a short fast interval, so the test runs on real timers.
  return { ...actual, pollInterval: (r: Parameters<typeof actual.pollInterval>[0]) => actual.pollInterval({ ...r, fastMs: 50 }) };
});
const fetcherMock = vi.hoisted(() => vi.fn());
vi.mock('../api/client', () => ({ fetcher: fetcherMock }));

import { useExecutions, useProjectExecutions } from './use-executions';

const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>
);

beforeEach(() => {
  api.getWorkflows.mockReset();
  api.getWorkflowExecutions.mockReset();
});

describe('useProjectExecutions', () => {
  it('reads every workflow\'s runs, names each by its workflow and lists the newest first', async () => {
    api.getWorkflows.mockResolvedValue([{ id: 'w1', name: 'Launch film' }, { id: 'w2', name: 'Teaser' }]);
    api.getWorkflowExecutions.mockImplementation(async (_p: string, w: string) =>
      w === 'w1'
        ? [{ id: 'a', status: 'Passed', startedAt: '2026-10-01T10:00:00Z' }, { id: 'b', status: 'Failed', startedAt: '2026-10-03T10:00:00Z' }]
        : [{ id: 'c', status: 'Passed', startedAt: '2026-10-02T10:00:00Z' }],
    );
    const { result } = renderHook(() => useProjectExecutions('p1', true), { wrapper });
    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(result.current.data!.map((r) => [r.id, r.workflowName])).toEqual([['b', 'Launch film'], ['c', 'Teaser'], ['a', 'Launch film']]);
    expect(api.getWorkflows).toHaveBeenCalledWith('p1');
    expect(api.getWorkflowExecutions).toHaveBeenCalledWith('p1', 'w2');
  });

  it('does not fetch anything while disabled or without a project', () => {
    renderHook(() => useProjectExecutions('p1', false), { wrapper });
    renderHook(() => useProjectExecutions(undefined, true), { wrapper });
    expect(api.getWorkflows).not.toHaveBeenCalled();
  });

  it('puts a run that never started last', async () => {
    api.getWorkflows.mockResolvedValue([{ id: 'w1', name: 'Launch film' }]);
    api.getWorkflowExecutions.mockResolvedValue([
      { id: 'queued', status: 'Queued', startedAt: null },
      { id: 'done', status: 'Passed', startedAt: '2026-10-01T10:00:00Z' },
    ]);
    const { result } = renderHook(() => useProjectExecutions('p1', true), { wrapper });
    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(result.current.data!.map((r) => r.id)).toEqual(['done', 'queued']);
  });
});

describe('useExecutions polling', () => {
  it('keeps polling after the tab was hidden and shown again', async () => {
    const setVisibility = (v: 'hidden' | 'visible') => {
      Object.defineProperty(document, 'visibilityState', { configurable: true, get: () => v });
      Object.defineProperty(document, 'hidden', { configurable: true, get: () => v === 'hidden' });
      document.dispatchEvent(new Event('visibilitychange'));
    };
    const sleep = (ms: number) => act(async () => { await new Promise((r) => setTimeout(r, ms)); });
    try {
      setVisibility('visible');
      fetcherMock.mockReset();
      fetcherMock.mockImplementation(async () => [{ id: 'a', status: 'Running' }]);
      const { result } = renderHook(() => useExecutions('p1', 'w1'), { wrapper });
      await waitFor(() => expect(result.current.data).toBeDefined());
      await sleep(400);
      const first = fetcherMock.mock.calls.length;
      expect(first).toBeGreaterThan(1);

      act(() => setVisibility('hidden'));
      await sleep(400);
      act(() => setVisibility('visible'));
      await sleep(100);
      const afterShow = fetcherMock.mock.calls.length;
      await sleep(400);
      expect(fetcherMock.mock.calls.length).toBeGreaterThan(afterShow + 1);
    } finally {
      setVisibility('visible');
    }
  });
});
