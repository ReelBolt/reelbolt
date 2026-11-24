// @vitest-environment jsdom

import { describe, it, expect, beforeEach, afterEach, vi } from 'vitest';
import type { ReactNode } from 'react';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import { SWRConfig } from 'swr';

const notify = vi.hoisted(() => vi.fn());
vi.mock('@mantine/notifications', () => ({ notifications: { show: notify } }));

import { useAdoptAssistantMode, useAssistantPreferences } from './use-assistant-preferences';

interface Call {
  method: string;
  body: unknown;
}

let calls: Call[];
let stored: string;
/** Resolves the next PUT; absent = answer at once. */
let holdPut: Promise<void> | null;
let putStatus: number;

function installFetch() {
  vi.stubGlobal(
    'fetch',
    vi.fn<typeof fetch>(async (_input, init) => {
      const method = init?.method ?? 'GET';
      calls.push({ method, body: typeof init?.body === 'string' ? JSON.parse(init.body) : undefined });
      if (method === 'PUT') {
        if (holdPut) await holdPut;
        if (putStatus !== 200) return new Response(JSON.stringify({ message: 'no' }), { status: putStatus, headers: { 'content-type': 'application/json' } });
        stored = (calls[calls.length - 1].body as { approvalMode: string }).approvalMode;
      }
      return new Response(JSON.stringify({ approvalMode: stored }), { status: 200, headers: { 'content-type': 'application/json' } });
    }),
  );
}

const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>
);

beforeEach(() => {
  calls = [];
  stored = 'ask';
  holdPut = null;
  putStatus = 200;
  notify.mockReset();
  installFetch();
});

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

describe('useAssistantPreferences', () => {
  it('reads ask until the server has answered, and says it has not', async () => {
    const { result } = renderHook(() => useAssistantPreferences(), { wrapper });

    expect(result.current.mode).toBe('ask');
    expect(result.current.loaded).toBe(false);

    await waitFor(() => expect(result.current.loaded).toBe(true));
    expect(result.current.mode).toBe('ask');
    expect(calls[0]).toEqual({ method: 'GET', body: undefined });
  });

  it('reads the mode the server stored', async () => {
    stored = 'auto';
    const { result } = renderHook(() => useAssistantPreferences(), { wrapper });

    await waitFor(() => expect(result.current.mode).toBe('auto'));
  });

  it('still reads ask, and offers the switch, when the preference cannot be read at all', async () => {
    vi.stubGlobal('fetch', vi.fn(() => Promise.reject(new Error('offline'))));
    const { result } = renderHook(() => useAssistantPreferences(), { wrapper });

    await waitFor(() => expect(result.current.error).toBeDefined());
    expect(result.current.mode).toBe('ask');
    expect(result.current.loaded).toBe(false);
  });

  it('switches at once, before the server has answered, and keeps what the server stored', async () => {
    let release: () => void = () => {};
    holdPut = new Promise<void>((resolve) => { release = resolve; });
    const { result } = renderHook(() => useAssistantPreferences(), { wrapper });
    await waitFor(() => expect(result.current.loaded).toBe(true));

    let outcome: Promise<boolean> = Promise.resolve(false);
    act(() => {
      outcome = result.current.setMode('auto');
    });

    await waitFor(() => expect(result.current.mode).toBe('auto'));
    expect(calls.at(-1)).toEqual({ method: 'PUT', body: { approvalMode: 'auto' } });

    await act(async () => {
      release();
      await expect(outcome).resolves.toBe(true);
    });
    expect(result.current.mode).toBe('auto');
    expect(notify).not.toHaveBeenCalled();
  });

  it('goes back to what it was and says so calmly when the server refuses', async () => {
    putStatus = 500;
    const { result } = renderHook(() => useAssistantPreferences(), { wrapper });
    await waitFor(() => expect(result.current.loaded).toBe(true));

    let ok = true;
    await act(async () => {
      ok = await result.current.setMode('auto');
    });

    expect(ok).toBe(false);
    expect(result.current.mode).toBe('ask');
    expect(notify).toHaveBeenCalledTimes(1);
    const shown = notify.mock.calls[0][0] as { title: string; message: string; color: string };
    expect(shown.title).toBe("Couldn't change that setting");
    expect(shown.message).toMatch(/Nothing was changed/);
    expect(shown.color).not.toBe('red');
  });

  it('can switch back to asking', async () => {
    stored = 'auto';
    const { result } = renderHook(() => useAssistantPreferences(), { wrapper });
    await waitFor(() => expect(result.current.mode).toBe('auto'));

    await act(async () => {
      await result.current.setMode('ask');
    });

    expect(result.current.mode).toBe('ask');
    expect(calls.at(-1)).toEqual({ method: 'PUT', body: { approvalMode: 'ask' } });
  });
});

describe('useAdoptAssistantMode', () => {
  it('puts the mode the server just switched to on screen, then confirms it with the server', async () => {
    const { result } = renderHook(() => ({ prefs: useAssistantPreferences(), adopt: useAdoptAssistantMode() }), { wrapper });
    await waitFor(() => expect(result.current.prefs.loaded).toBe(true));
    stored = 'auto';

    act(() => result.current.adopt('auto'));

    await waitFor(() => expect(result.current.prefs.mode).toBe('auto'));
    // It confirmed with the server rather than trusting its own write.
    await waitFor(() => expect(calls.filter((c) => c.method === 'GET').length).toBeGreaterThanOrEqual(2));
  });
});
