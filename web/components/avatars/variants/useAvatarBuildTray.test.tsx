// @vitest-environment jsdom
/**
 * Avatar builds in the header tray (docs/background-activity.md): a running build is tracked once under a stable
 * id, follows its own route, reports its progress, and is stopped through its tray row when it has one.
 */
import { act, cleanup, renderHook } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import type { TrackJobMeta, TrackWork } from '@/lib/activity/ActivityProvider';
import type { AvatarBuild } from '@/lib/types/avatar-builds';

const H = vi.hoisted(() => ({
  tracked: [] as { meta: Record<string, unknown>; signal: AbortSignal; promise: Promise<unknown> }[],
  update: vi.fn(),
  cancelJob: vi.fn(),
  allJobs: [] as unknown[],
  watch: vi.fn(),
  cancelBuild: vi.fn(),
}));

vi.mock('@/lib/activity/ActivityProvider', () => ({
  useActivity: () => ({
    allJobs: H.allJobs,
    updateTrackedJob: H.update,
    cancel: H.cancelJob,
    trackJob: <T,>(meta: TrackJobMeta, work: TrackWork<T>) => {
      const controller = new AbortController();
      const promise = typeof work === 'function' ? work(controller.signal, String(meta.id)) : work;
      H.tracked.push({ meta: meta as unknown as Record<string, unknown>, signal: controller.signal, promise });
      return promise;
    },
  }),
}));
vi.mock('@/lib/avatars/build-watch', () => ({ watchAvatarBuild: (...a: unknown[]) => H.watch(...a) }));
vi.mock('@/lib/api/avatar-variants', () => ({ cancelAvatarBuild: (...a: unknown[]) => H.cancelBuild(...a) }));

import { getJobType } from '@/lib/activity/registry';
import '@/lib/activity/builtins';
import { AVATAR_BUILD_JOB_TYPE, avatarBuildJobId, useAvatarBuildTray } from './useAvatarBuildTray';

const build = (jobId: string, state = 'pictures', extra: Partial<AvatarBuild> = {}): AvatarBuild => ({ jobId, kind: 'variant', state, name: 'Car · Red', ...extra });

let finish: Map<string, { resolve: (b: AvatarBuild) => void; reject: (e: unknown) => void }>;

beforeEach(() => {
  H.tracked = [];
  H.allJobs = [];
  H.update.mockReset();
  H.cancelJob.mockReset().mockResolvedValue(undefined);
  H.cancelBuild.mockReset().mockResolvedValue(undefined);
  finish = new Map();
  H.watch.mockReset().mockImplementation((_projectId: string, jobId: string) => new Promise((resolve, reject) => { finish.set(jobId, { resolve, reject }); }));
});
afterEach(cleanup);

describe('useAvatarBuildTray', () => {
  it('lists a running build in the tray under a stable id, with a way back to the Avatars tab and a cancel', async () => {
    const { result } = renderHook(() => useAvatarBuildTray('p1'));
    const running = build('t1');
    act(() => result.current.track(running));
    expect(H.tracked).toHaveLength(1);
    const { meta } = H.tracked[0];
    expect(meta).toMatchObject({ type: 'avatarBuild', id: 'avatar-build:t1', title: 'Car · Red', projectId: 'p1', href: '/projects/p1?tab=avatars' });
    expect(avatarBuildJobId('t1')).toBe('avatar-build:t1');
    expect(H.watch).toHaveBeenCalledWith('p1', 't1', expect.objectContaining({ initial: running, signal: H.tracked[0].signal }));
    await (meta.cancel as () => Promise<void>)();
    expect(H.cancelBuild).toHaveBeenCalledWith('p1', 't1');
    await act(async () => { finish.get('t1')!.resolve(build('t1', 'ready')); await H.tracked[0].promise; });
  });

  it('has a registered job type with a label and the Avatars tab as its page', () => {
    const def = getJobType(AVATAR_BUILD_JOB_TYPE);
    expect(def.type).toBe('avatarBuild');
    expect(def.labelKey).toBe('types.avatarBuild');
    expect(def.href?.({ projectId: 'p1' } as never)).toBe('/projects/p1?tab=avatars');
  });

  it('follows the same build once however often it is seen, and again after it ended', async () => {
    const { result } = renderHook(() => useAvatarBuildTray('p1'));
    act(() => { result.current.track(build('t2')); result.current.track(build('t2', 'creating')); });
    expect(H.tracked).toHaveLength(1);
    await act(async () => { finish.get('t2')!.reject(new Error('failed')); await H.tracked[0].promise.catch(() => undefined); await Promise.resolve(); });
    act(() => result.current.track(build('t2')));
    expect(H.tracked).toHaveLength(2);
    await act(async () => { finish.get('t2')!.resolve(build('t2', 'ready')); await H.tracked[1].promise; });
  });

  it('does not track a build that is already finished', () => {
    const { result } = renderHook(() => useAvatarBuildTray('p1'));
    act(() => { result.current.track(build('t3', 'ready')); result.current.track(build('t3', 'failed')); result.current.track(build('t3', 'cancelled')); });
    expect(H.tracked).toHaveLength(0);
  });

  it('reports the progress of each reading to the tray row', async () => {
    const { result } = renderHook(() => useAvatarBuildTray('p1'));
    act(() => result.current.track(build('t4')));
    const options = H.watch.mock.calls[0][2] as { onUpdate: (b: AvatarBuild) => void };
    options.onUpdate(build('t4', 'pictures', { progress: { done: 1, total: 4 }, name: 'Car · Crimson' }));
    expect(H.update).toHaveBeenCalledWith('avatar-build:t4', { progress: 0.25, title: 'Car · Crimson' });
    options.onUpdate(build('t4', 'creating', { name: null }));
    expect(H.update).toHaveBeenLastCalledWith('avatar-build:t4', { progress: null });
    await act(async () => { finish.get('t4')!.resolve(build('t4', 'ready')); await H.tracked[0].promise; });
  });

  it('stops a build through its tray row when it has a running one, else directly', async () => {
    const { result, rerender } = renderHook(() => useAvatarBuildTray('p1'));
    await act(async () => { await result.current.cancel(build('t5')); });
    expect(H.cancelBuild).toHaveBeenCalledWith('p1', 't5');
    expect(H.cancelJob).not.toHaveBeenCalled();

    H.cancelBuild.mockClear();
    const row = { id: 'avatar-build:t5', state: 'running' };
    H.allJobs = [{ id: 'avatar-build:other', state: 'running' }, row];
    rerender();
    await act(async () => { await result.current.cancel(build('t5')); });
    expect(H.cancelJob).toHaveBeenCalledWith(row);
    expect(H.cancelBuild).not.toHaveBeenCalled();

    // A row that already ended is not a way to stop anything.
    H.cancelJob.mockClear();
    H.allJobs = [{ id: 'avatar-build:t5', state: 'failed' }];
    rerender();
    await act(async () => { await result.current.cancel(build('t5')); });
    expect(H.cancelJob).not.toHaveBeenCalled();
    expect(H.cancelBuild).toHaveBeenCalledWith('p1', 't5');
  });
});
