import { describe, expect, it, vi } from 'vitest';
import type { AvatarBuild } from '../types/avatar-builds';
import {
  anyBuildRunning, buildCount, buildFraction, buildPollDelayMs, buildStage, buildStates, buildsOf, buildsSignature, isBuildRunning,
  isBuildsOff, newlyReady, visibleBuilds,
} from './builds';
import { AvatarBuildEnded, watchAvatarBuild } from './build-watch';

const build = (jobId: string, state: string, extra: Partial<AvatarBuild> = {}): AvatarBuild => ({ jobId, kind: 'variant', state, name: 'Car · Red', ...extra });

describe('avatar builds: reading the list', () => {
  it('reads a bare array or an envelope, and nothing from anything else', () => {
    expect(buildsOf([build('j1', 'queued')]).map((b) => b.jobId)).toEqual(['j1']);
    expect(buildsOf({ items: [build('j2', 'ready')] }).map((b) => b.jobId)).toEqual(['j2']);
    expect(buildsOf({ builds: [build('j3', 'ready')] }).map((b) => b.jobId)).toEqual(['j3']);
    expect(buildsOf(undefined)).toEqual([]);
    expect(buildsOf({} as never)).toEqual([]);
    expect(buildsOf([{ state: 'queued' } as AvatarBuild])).toEqual([]);
  });

  it('knows a running build from a finished one, also for a word it does not know', () => {
    for (const state of ['queued', 'pictures', 'videos', 'finding', 'cutting', 'creating', 'something-new']) expect(isBuildRunning({ state })).toBe(true);
    for (const state of ['ready', 'failed', 'cancelled', 'Ready']) expect(isBuildRunning({ state })).toBe(false);
    expect(anyBuildRunning([build('a', 'ready'), build('b', 'pictures')])).toBe(true);
    expect(anyBuildRunning([build('a', 'ready')])).toBe(false);
    expect(isBuildsOff({ status: 404 })).toBe(true);
    expect(isBuildsOff({ status: 500 })).toBe(false);
  });

  it('reads the stage from the state, or from the step the server names', () => {
    expect(buildStage({ state: 'pictures' })).toBe('pictures');
    expect(buildStage({ state: 'running', stage: 'videos' })).toBe('videos');
    expect(buildStage({ state: 'creating', stage: 'saving-files' })).toBe('creating');
    expect(buildStage({ state: 'running' })).toBe('working');
    expect(buildStage({ state: 'failed', stage: 'pictures' })).toBe('failed');
    expect(buildStage({ state: 'ready' })).toBe('ready');
    expect(buildStage({ state: 'cancelled' })).toBe('cancelled');
  });

  it('counts k of n only when the server counts it', () => {
    expect(buildCount({ progress: { done: 2, total: 5 } })).toEqual({ done: 2, total: 5 });
    expect(buildCount({ progress: { done: 9, total: 5 } })).toEqual({ done: 5, total: 5 });
    expect(buildCount({ progress: { done: 0, total: 0 } })).toBeNull();
    expect(buildCount({ progress: null })).toBeNull();
    expect(buildCount({})).toBeNull();
    expect(buildFraction({ progress: { done: 1, total: 4 } })).toBe(0.25);
    expect(buildFraction({})).toBeNull();
  });

  it('waits longer each time, up to fifteen seconds', () => {
    expect(buildPollDelayMs(0)).toBe(2000);
    expect(buildPollDelayMs(1)).toBe(3000);
    expect(buildPollDelayMs(2)).toBe(4500);
    expect(buildPollDelayMs(50)).toBe(15000);
    expect(buildPollDelayMs(Number.NaN)).toBe(2000);
    for (let i = 0; i < 20; i += 1) expect(buildPollDelayMs(i + 1)).toBeGreaterThanOrEqual(buildPollDelayMs(i));
  });

  it('changes its signature whenever a build moves', () => {
    const a = buildsSignature([build('j', 'pictures', { progress: { done: 1, total: 3 } })]);
    expect(buildsSignature([build('j', 'pictures', { progress: { done: 1, total: 3 } })])).toBe(a);
    expect(buildsSignature([build('j', 'pictures', { progress: { done: 2, total: 3 } })])).not.toBe(a);
    expect(buildsSignature([build('j', 'creating')])).not.toBe(a);
  });

  it('shows running builds, failed ones until dismissed, and ready or cancelled ones only when it saw them running', () => {
    const all = [build('run', 'pictures'), build('bad', 'failed'), build('old', 'ready'), build('seen', 'ready'), build('stop', 'cancelled')];
    expect(visibleBuilds(all, new Set(['seen']), new Set()).map((b) => b.jobId)).toEqual(['run', 'bad', 'seen']);
    expect(visibleBuilds(all, new Set(['seen', 'stop']), new Set(['bad', 'seen'])).map((b) => b.jobId)).toEqual(['run', 'stop']);
  });

  it('notices a build that just turned ready, once', () => {
    const before = buildStates([build('a', 'creating'), build('b', 'ready'), build('c', 'failed')]);
    const now = [build('a', 'ready'), build('b', 'ready'), build('c', 'ready'), build('new', 'ready')];
    expect(newlyReady(before, now).map((b) => b.jobId)).toEqual(['a']);
    expect(newlyReady(buildStates(now), now)).toEqual([]);
  });
});

describe('avatar builds: following one to its end', () => {
  const noWait = () => Promise.resolve();

  it('resolves when the build is ready, reporting every reading', async () => {
    const readings = [build('j', 'pictures', { progress: { done: 1, total: 2 } }), build('j', 'creating'), build('j', 'ready', { avatarId: 'new' })];
    const read = vi.fn(async () => readings.shift()!);
    const seen: string[] = [];
    const done = await watchAvatarBuild('p1', 'j', { read, delay: noWait, initial: build('j', 'queued'), onUpdate: (b) => seen.push(String(b.state)) });
    expect(done.avatarId).toBe('new');
    expect(seen).toEqual(['queued', 'pictures', 'creating', 'ready']);
    expect(read).toHaveBeenCalledWith('p1', 'j');
  });

  it('rejects with the reason when the build fails, and says a stop apart from a failure', async () => {
    const failed = watchAvatarBuild('p1', 'j', { read: async () => build('j', 'failed', { message: 'No picture could be changed.' }), delay: noWait });
    await expect(failed).rejects.toMatchObject({ name: 'AvatarBuildEnded', message: 'No picture could be changed.', cancelled: false });
    const stopped = watchAvatarBuild('p1', 'j', { read: async () => build('j', 'cancelled'), delay: noWait });
    await expect(stopped).rejects.toBeInstanceOf(AvatarBuildEnded);
    await expect(watchAvatarBuild('p1', 'j', { read: async () => build('j', 'cancelled'), delay: noWait })).rejects.toMatchObject({ cancelled: true });
  });

  it('settles at once on a build that is already finished, without asking', async () => {
    const read = vi.fn();
    await expect(watchAvatarBuild('p1', 'j', { read, delay: noWait, initial: build('j', 'ready') })).resolves.toMatchObject({ state: 'ready' });
    expect(read).not.toHaveBeenCalled();
  });

  it('keeps going over a few failed readings and gives up after too many', async () => {
    let calls = 0;
    const flaky = async () => { calls += 1; if (calls < 3) throw new Error('network'); return build('j', 'ready'); };
    await expect(watchAvatarBuild('p1', 'j', { read: flaky, delay: noWait })).resolves.toMatchObject({ state: 'ready' });
    const down = vi.fn(async () => { throw new Error('network'); });
    await expect(watchAvatarBuild('p1', 'j', { read: down, delay: noWait, maxReadFailures: 3 })).rejects.toThrow('network');
    expect(down).toHaveBeenCalledTimes(3);
  });

  it('starts its waiting times over when the build moves, and stops when it is aborted', async () => {
    const waits: number[] = [];
    const readings = [build('j', 'queued'), build('j', 'queued'), build('j', 'pictures'), build('j', 'ready')];
    await watchAvatarBuild('p1', 'j', { read: async () => readings.shift()!, delay: async (ms) => { waits.push(ms); }, initial: build('j', 'queued') });
    expect(waits).toEqual([2000, 3000, 4500, 2000]);

    const controller = new AbortController();
    const aborted = watchAvatarBuild('p1', 'j', { signal: controller.signal, read: async () => build('j', 'queued') });
    controller.abort();
    await expect(aborted).rejects.toMatchObject({ name: 'AbortError' });
  });
});
