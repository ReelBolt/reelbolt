// @vitest-environment jsdom
/**
 * Once per person: the moment is offered for a first finished video only, remembered when shown,
 * kept until closed, and never offered again for another run or to someone who is not new.
 */
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';
import type { ReactNode } from 'react';
import { SWRConfig } from 'swr';
import type { OnboardingServerState } from '@/lib/onboarding/state';

const env = vi.hoisted(() => ({
  mark: vi.fn(),
  onboarding: {} as Record<string, unknown>,
  overview: undefined as unknown,
  orgs: undefined as unknown,
  fetched: [] as string[],
}));
vi.mock('@/lib/hooks/use-onboarding', () => ({ useOnboarding: () => env.onboarding }));
vi.mock('@/lib/api/client', async (orig) => ({
  ...(await orig<typeof import('@/lib/api/client')>()),
  fetcher: (key: string) => {
    if (key === '/api/v1/orgs') return env.orgs instanceof Error ? Promise.reject(env.orgs) : Promise.resolve(env.orgs);
    env.fetched.push(key);
    return env.overview instanceof Error ? Promise.reject(env.overview) : Promise.resolve(env.overview);
  },
}));

import { useFirstResult } from './use-first-result';
import { firstResultStorageKey, readFirstResultMark } from './first-result';

const server = (openResult: boolean): OnboardingServerState => ({
  tour: { status: 'pending', version: null, updatedAt: null },
  checklist: { dismissed: false, items: [{ key: 'open_result', done: openResult }] },
});
const PASSED = { id: 'e1', status: 'Passed' as const };
const STARTED = '2026-11-11T10:00:00Z';
/** The workspace as the overview reports it: this run among the recent ones, and the outputs it has made. */
const workspace = (over: Record<string, unknown> = {}) => ({
  recentExecutions: [{ id: 'e1', status: 'Passed', startedAt: STARTED }],
  recentOutputs: [{ stepResultId: 'sr1', producedAt: '2026-11-11T10:05:00Z' }],
  totals: { renders: 1 },
  ...over,
});
const wrapper = ({ children }: { children: ReactNode }) => (
  <SWRConfig value={{ provider: () => new Map(), dedupingInterval: 0 }}>{children}</SWRConfig>
);
type Run = { id: string; status: 'Passed' | 'Failed' | 'Running' | 'Queued' };
function Probe(props: { execution: Run | undefined; executions: Run[]; videoReady?: boolean }) {
  return useFirstResult(props);
}
/** Mounts the hook for a run (and, by default, a history holding just that run). */
const mountFor = (execution: Run | undefined, executions: Run[] = execution ? [execution] : []) =>
  renderHook(Probe, { wrapper, initialProps: { execution, executions } });

beforeEach(() => {
  window.localStorage.clear();
  env.mark.mockReset();
  env.fetched.length = 0;
  env.overview = workspace();
  env.orgs = [{ id: 'o1', kind: 'Personal' }];
  env.onboarding = { server: server(false), mirror: {}, settled: true, userKey: 'u1', mark: env.mark };
});
afterEach(cleanup);

describe('useFirstResult', () => {
  it('shows the moment for a first finished video, and remembers it in the browser and on the account', async () => {
    const { result } = mountFor(PASSED);
    await waitFor(() => expect(result.current.active).toBe(true));
    expect(env.fetched).toEqual(['/api/v1/dashboard/overview']);
    expect(readFirstResultMark('u1')).toEqual({ id: 'e1', dismissed: false });
    expect(env.mark).toHaveBeenCalledWith('open_result');
    expect(result.current.failedFirst).toBe(false);
  });

  it('keeps showing it for that run on a later visit, without asking again, until it is closed', async () => {
    window.localStorage.setItem(firstResultStorageKey('u1'), JSON.stringify({ id: 'e1', dismissed: false }));
    // By now the account says a result was opened: the browser's own mark is what keeps the moment.
    env.onboarding = { ...env.onboarding, server: server(true) };
    const { result } = mountFor(PASSED);
    expect(result.current.active).toBe(true);
    expect(env.fetched).toEqual([]);
    act(() => result.current.dismiss());
    expect(result.current.active).toBe(false);
    expect(readFirstResultMark('u1')).toEqual({ id: 'e1', dismissed: true });
    cleanup();
    expect(mountFor(PASSED).result.current.active).toBe(false);
  });

  it('is never offered for a second run once it was had', async () => {
    window.localStorage.setItem(firstResultStorageKey('u1'), JSON.stringify({ id: 'e1', dismissed: true }));
    const next = { id: 'e2', status: 'Passed' as const };
    const { result } = mountFor(next, [next]);
    await new Promise((r) => setTimeout(r, 30));
    expect(result.current.active).toBe(false);
    expect(env.fetched).toEqual([]);
  });

  it('gives way when a newer run takes the place of the first', async () => {
    const { result, rerender } = mountFor(PASSED);
    await waitFor(() => expect(result.current.active).toBe(true));
    rerender({ execution: { id: 'e2', status: 'Running' }, executions: [PASSED, { id: 'e2', status: 'Running' }] });
    expect(result.current.active).toBe(false);
    rerender({ execution: { id: 'e2', status: 'Passed' }, executions: [PASSED, { id: 'e2', status: 'Passed' }] });
    expect(result.current.active).toBe(false);
  });

  it('stays out of the way of the shell\'s own "opened a result" mark during the same visit', async () => {
    // The page opens before the run is known; the shell then records the visit on the account.
    const { result, rerender } = mountFor(undefined);
    env.onboarding = { ...env.onboarding, server: server(true), mirror: { done: ['open_result'] } };
    rerender({ execution: PASSED, executions: [PASSED] });
    await waitFor(() => expect(result.current.active).toBe(true));
  });

  it('is not shown to someone who opened a result before, here or in another browser', async () => {
    env.onboarding = { ...env.onboarding, server: server(true) };
    const { result } = mountFor(PASSED);
    await new Promise((r) => setTimeout(r, 30));
    expect(result.current.active).toBe(false);
    // Nobody who is not a candidate costs a request.
    expect(env.fetched).toEqual([]);
    cleanup();
    env.onboarding = { ...env.onboarding, server: server(false), mirror: { done: ['open_result'] } };
    expect(mountFor(PASSED).result.current.active).toBe(false);
  });

  it('is not shown without the API\'s word, while it is still loading, or when the workspace has another video', async () => {
    env.onboarding = { ...env.onboarding, server: null };
    expect(mountFor(PASSED).result.current.active).toBe(false);
    cleanup();

    env.onboarding = { ...env.onboarding, server: server(false), settled: false };
    expect(mountFor(PASSED).result.current.active).toBe(false);
    cleanup();

    env.onboarding = { ...env.onboarding, settled: true };
    env.overview = workspace({ recentExecutions: [{ id: 'e1', status: 'Passed', startedAt: STARTED }, { id: 'older', status: 'Passed', startedAt: '2026-11-01T10:00:00Z' }] });
    const other = mountFor(PASSED);
    await waitFor(() => expect(env.fetched).toHaveLength(1));
    await new Promise((r) => setTimeout(r, 30));
    expect(other.result.current.active).toBe(false);
    expect(env.mark).not.toHaveBeenCalled();
    expect(readFirstResultMark('u1')).toBeNull();
    cleanup();

    env.overview = new Error('offline');
    const failed = mountFor(PASSED);
    await new Promise((r) => setTimeout(r, 30));
    expect(failed.result.current.active).toBe(false);
  });

  it('is not shown while the run is going, and an earlier video of this app rules it out', async () => {
    expect(mountFor({ id: 'e1', status: 'Running' }).result.current.active).toBe(false);
    cleanup();
    const { result } = mountFor(PASSED, [PASSED, { id: 'e0', status: 'Passed' }]);
    await new Promise((r) => setTimeout(r, 30));
    expect(result.current.active).toBe(false);
    expect(env.fetched).toEqual([]);
  });

  it('waits for a video: a run with nothing to play yet neither shows the moment nor uses it up', async () => {
    const { result, rerender } = renderHook(Probe, { wrapper, initialProps: { execution: PASSED as Run | undefined, executions: [PASSED] as Run[], videoReady: false } });
    await new Promise((r) => setTimeout(r, 30));
    expect(result.current.active).toBe(false);
    expect(env.fetched).toEqual([]);
    expect(env.mark).not.toHaveBeenCalled();
    expect(readFirstResultMark('u1')).toBeNull();
    rerender({ execution: PASSED, executions: [PASSED], videoReady: true });
    await waitFor(() => expect(result.current.active).toBe(true));
  });

  it('still works for the page it is on when the browser cannot store anything', async () => {
    const setItem = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked'); });
    try {
      const { result } = mountFor(PASSED);
      await waitFor(() => expect(result.current.active).toBe(true));
      act(() => result.current.dismiss());
      expect(result.current.active).toBe(false);
    } finally {
      setItem.mockRestore();
    }
  });

  it('knows a first run that failed, and only that', () => {
    const failed = { id: 'e1', status: 'Failed' as const };
    expect(mountFor(failed).result.current.failedFirst).toBe(true);
    cleanup();
    expect(mountFor(failed, [failed, { id: 'e0', status: 'Passed' }]).result.current.failedFirst).toBe(false);
    cleanup();
    env.onboarding = { ...env.onboarding, server: server(true) };
    expect(mountFor(failed).result.current.failedFirst).toBe(false);
    cleanup();
    env.onboarding = { ...env.onboarding, server: null };
    expect(mountFor(failed).result.current.failedFirst).toBe(false);
  });
});
