// @vitest-environment jsdom
/**
 * The builds list of the Avatars tab: read once, polled with growing pauses only while something is running, and
 * silent on a server without builds.
 */
import { act, cleanup, renderHook } from '@testing-library/react';
import { SWRConfig } from 'swr';
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { useAvatarBuilds } from './use-avatar-builds';

const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0, revalidateOnFocus: false }}>{children}</SWRConfig>
);

let answers: (() => Response)[] = [];
let fetchMock: ReturnType<typeof vi.fn>;
const json = (body: unknown, status = 200) => () => new Response(JSON.stringify(body), { status, headers: { 'content-type': 'application/json' } });
const running = (done: number) => json([{ jobId: 'j1', kind: 'variant', state: 'pictures', name: 'Car · Red', progress: { done, total: 4 } }]);

beforeEach(() => {
  vi.useFakeTimers();
  answers = [];
  fetchMock = vi.fn(async () => (answers.length > 1 ? answers.shift()! : answers[0])());
  vi.stubGlobal('fetch', fetchMock);
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  vi.unstubAllGlobals();
});

const settle = () => act(async () => { await vi.advanceTimersByTimeAsync(0); });
const wait = (ms: number) => act(async () => { await vi.advanceTimersByTimeAsync(ms); });

describe('useAvatarBuilds', () => {
  it('reads the list once and does not poll while nothing is running', async () => {
    answers = [json([{ jobId: 'j0', kind: 'variant', state: 'ready', name: 'Car · Red' }])];
    const { result } = renderHook(() => useAvatarBuilds('p1'), { wrapper });
    await settle();
    expect(result.current.builds.map((b) => b.jobId)).toEqual(['j0']);
    expect(result.current.off).toBe(false);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    expect(fetchMock.mock.calls[0][0]).toBe('/api/v1/projects/p1/avatars/builds');
    await wait(60_000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('polls with growing pauses while a build runs, starts over when it moves, and stops when it ends', async () => {
    answers = [running(1), running(1), running(1), running(2), json([{ jobId: 'j1', kind: 'variant', state: 'ready', name: 'Car · Red' }])];
    const { result } = renderHook(() => useAvatarBuilds('p1'), { wrapper });
    await settle();
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await wait(1_999);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await wait(1);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    // Nothing moved: the next pause is longer.
    await wait(2_999);
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await wait(1);
    expect(fetchMock).toHaveBeenCalledTimes(3);
    await wait(4_500);
    expect(fetchMock).toHaveBeenCalledTimes(4);
    expect(result.current.builds[0].progress?.done).toBe(2);
    // It moved: back to the shortest pause.
    await wait(2_000);
    expect(fetchMock).toHaveBeenCalledTimes(5);
    expect(result.current.builds[0].state).toBe('ready');
    await wait(60_000);
    expect(fetchMock).toHaveBeenCalledTimes(5);
  });

  it('starts polling when a build appears after a refresh', async () => {
    answers = [json([]), running(1)];
    const { result } = renderHook(() => useAvatarBuilds('p1'), { wrapper });
    await settle();
    await wait(30_000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => { await result.current.refresh(); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await wait(2_000);
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('is off, with no builds and no retry, on a server that answers 404', async () => {
    answers = [json({ message: 'Not Found' }, 404)];
    const { result } = renderHook(() => useAvatarBuilds('p1'), { wrapper });
    await settle();
    expect(result.current.off).toBe(true);
    expect(result.current.builds).toEqual([]);
    await wait(60_000);
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('asks nothing without a project or while disabled', async () => {
    renderHook(() => useAvatarBuilds(null), { wrapper });
    renderHook(() => useAvatarBuilds('p1', false), { wrapper });
    await settle();
    expect(fetchMock).not.toHaveBeenCalled();
  });
});
