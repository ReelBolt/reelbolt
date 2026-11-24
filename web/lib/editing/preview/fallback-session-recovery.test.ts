// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createFallbackPreviewSession } from './fallback-session';
import { decodeWhenReady } from '../audio/decode-when-ready';
import { assetRef, doc as fixtureDoc, media, track } from '../__tests__/fixtures';

beforeEach(() => { vi.useFakeTimers(); });
afterEach(() => { vi.useRealTimers(); });

const flags = (t: { locked: boolean; enabled: boolean; mute: boolean; solo: boolean }) => ({ locked: t.locked, enabled: t.enabled, mute: t.mute, solo: t.solo });
const doc = fixtureDoc([track('v1', 'video', [media('c1', 0, 60, 0, { assetRef: assetRef(1) })])]);
const makeVideo = () => {
  const video = document.createElement('video');
  video.pause = () => undefined;
  return video;
};

describe('fallback session with a source that is still being prepared', () => {
  it('sets the source again once the probe stops saying pending, and polls only while pending', async () => {
    const video = makeVideo();
    const probe = vi.fn()
      .mockResolvedValueOnce({ kind: 'pending' }).mockResolvedValueOnce({ kind: 'pending' }).mockResolvedValue({ kind: 'ok' });
    const srcs: string[] = [];
    Object.defineProperty(video, 'src', { set: (v: string) => { srcs.push(v); }, get: () => srcs[srcs.length - 1] ?? '', configurable: true });
    const session = createFallbackPreviewSession({
      video, getDoc: () => doc, flags, proxyUrlFor: () => '/api/v1/projects/p/outputs/s1/download', probe,
    });
    session.seek(0);
    expect(srcs).toHaveLength(1);
    video.dispatchEvent(new Event('error'));
    await vi.advanceTimersByTimeAsync(0);
    expect(probe).toHaveBeenCalledTimes(1);
    await vi.advanceTimersByTimeAsync(10000);
    await vi.advanceTimersByTimeAsync(20000);
    expect(probe).toHaveBeenCalledTimes(3);
    expect(srcs).toHaveLength(2);
    await vi.advanceTimersByTimeAsync(30000);
    expect(probe).toHaveBeenCalledTimes(3);
    session.dispose();
  });

  it('stops asking when disposed', async () => {
    const video = makeVideo();
    const probe = vi.fn().mockResolvedValue({ kind: 'pending' });
    const session = createFallbackPreviewSession({
      video, getDoc: () => doc, flags, proxyUrlFor: () => '/x/outputs/s1/download', probe,
    });
    session.seek(0);
    video.dispatchEvent(new Event('error'));
    await vi.advanceTimersByTimeAsync(0);
    session.dispose();
    await vi.advanceTimersByTimeAsync(30000);
    expect(probe).toHaveBeenCalledTimes(1);
  });
});

describe('two clips failing in turn', () => {
  it('rechecks each one, so an error on the second clip is not dropped', async () => {
    const two = fixtureDoc([track('v1', 'video', [media('c1', 0, 60, 0, { assetRef: assetRef(1) }), media('c2', 60, 120, 0, { assetRef: assetRef(2) })])]);
    const video = makeVideo();
    const asked: string[] = [];
    const probe = vi.fn((url: string) => { asked.push(url); return Promise.resolve({ kind: 'pending' as const }); });
    const session = createFallbackPreviewSession({
      video, getDoc: () => two, flags, proxyUrlFor: (c) => `/o/${c.id}`, probe,
    });
    session.seek(0);
    video.dispatchEvent(new Event('error'));
    session.seek(70);
    video.dispatchEvent(new Event('error'));
    await vi.advanceTimersByTimeAsync(0);
    expect(asked.sort()).toEqual(['/o/c1', '/o/c2']);
    session.dispose();
  });
});

describe('decodeWhenReady', () => {
  it('decodes at once without asking when the first decode works', async () => {
    const probe = vi.fn();
    const decode = vi.fn().mockResolvedValue(new Float32Array(1));
    await decodeWhenReady('/a/files/f/media', { probe, decode });
    expect(probe).not.toHaveBeenCalled();
    expect(decode).toHaveBeenCalledTimes(1);
  });
  it('after a failed decode waits while pending, then decodes again (any route)', async () => {
    const probe = vi.fn().mockResolvedValue({ kind: 'pending' });
    const wait = vi.fn().mockResolvedValue(undefined);
    const decode = vi.fn().mockRejectedValueOnce(new Error('409')).mockResolvedValue(new Float32Array(1));
    await decodeWhenReady('/a/files/f/media', { probe, wait, decode });
    expect(probe).toHaveBeenCalledTimes(1);
    expect(wait).toHaveBeenCalledTimes(1);
    expect(decode).toHaveBeenCalledTimes(2);
  });
  it('rethrows the first error when the answer is not pending', async () => {
    const probe = vi.fn().mockResolvedValue({ kind: 'missing' });
    const decode = vi.fn().mockRejectedValue(new Error('boom'));
    await expect(decodeWhenReady('/a/files/f/media', { probe, decode })).rejects.toThrow('boom');
  });
});
