// @vitest-environment jsdom
import type { ReactNode } from 'react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, renderHook, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Notifications } from '@mantine/notifications';
import { CheckedVideo } from '@/components/outputs/CheckedVideo';
import { OUTPUT_CHECK_INTERVAL_MS, probeOutput, useDownloadGuard, useOutputCheck } from './output-check';

const URL_ = '/api/v1/projects/p1/outputs/s1/download';
const json = (status: number, code: string, message = 'm', final?: boolean) =>
  new Response(JSON.stringify({ code, message, ...(final === undefined ? {} : { final }) }), { status, headers: { 'Content-Type': 'application/json' } });
const pending = () => json(409, 'CONTENT_MODERATION_PENDING');
const accepted = (retryAfter?: string) =>
  new Response(JSON.stringify({ code: 'CONTENT_MODERATION_PENDING', message: 'm' }), {
    status: 202,
    headers: { 'Content-Type': 'application/json', ...(retryAfter === undefined ? {} : { 'Retry-After': retryAfter }) },
  });
const ok = () => new Response(null, { status: 206 });

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  vi.useFakeTimers();
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); vi.restoreAllMocks(); });

async function flush() { await act(async () => { await vi.advanceTimersByTimeAsync(0); }); }

describe('probeOutput', () => {
  it('maps a 202 to pending and reads the Retry-After header', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(accepted('7')));
    await expect(probeOutput(URL_)).resolves.toEqual({ kind: 'pending', retryAfterMs: 7000 });
  });

  it('maps a 202 without Retry-After to pending, leaving the caller its own back-off', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(accepted()));
    await expect(probeOutput(URL_)).resolves.toEqual({ kind: 'pending', retryAfterMs: undefined });
  });

  it('keeps the older 409 pending answer as pending', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(pending()));
    await expect(probeOutput(URL_)).resolves.toEqual({ kind: 'pending' });
  });

  it('still reads 200/206 as ok, 404 as missing and 422 as blocked', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 200 })));
    await expect(probeOutput(URL_)).resolves.toEqual({ kind: 'ok' });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response(null, { status: 206 })));
    await expect(probeOutput(URL_)).resolves.toEqual({ kind: 'ok' });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(404, 'not_found')));
    await expect(probeOutput(URL_)).resolves.toEqual({ kind: 'missing' });
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(json(422, 'CONTENT_BLOCKED', 'This video cannot be shown.')));
    await expect(probeOutput(URL_)).resolves.toEqual({ kind: 'blocked', message: 'This video cannot be shown.' });
  });
});

describe('useOutputCheck', () => {
  it('is quiet until the media errors, then re-checks every 5 s and gives a new key when ready', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(pending()).mockResolvedValueOnce(pending()).mockResolvedValue(ok());
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useOutputCheck(URL_));
    expect(result.current.status).toBe('unknown');
    await act(async () => { await vi.advanceTimersByTimeAsync(20000); });
    expect(fetchMock).not.toHaveBeenCalled();

    act(() => result.current.onError());
    await flush();
    expect(result.current.status).toBe('pending');
    expect(result.current.message).toMatch(/Checking your video/);
    const key = result.current.remountKey;

    await act(async () => { await vi.advanceTimersByTimeAsync(OUTPUT_CHECK_INTERVAL_MS); });
    expect(result.current.status).toBe('pending');
    await act(async () => { await vi.advanceTimersByTimeAsync(OUTPUT_CHECK_INTERVAL_MS * 2); });
    expect(result.current.status).toBe('unknown');
    expect(result.current.remountKey).toBe(key + 1);
    expect(fetchMock).toHaveBeenCalledTimes(3);

    // Ready: no more requests.
    await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('backs off to 60 s while pending', async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(pending()));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useOutputCheck(URL_));
    act(() => result.current.onError());
    await flush();
    // Polls at +5, +10, +20, +40, +60, +60 seconds.
    await act(async () => { await vi.advanceTimersByTimeAsync(5000 + 10000 + 20000 + 40000 + 60000); });
    expect(fetchMock).toHaveBeenCalledTimes(6);
    await act(async () => { await vi.advanceTimersByTimeAsync(60000); });
    expect(fetchMock).toHaveBeenCalledTimes(7);
  });

  it('keeps recovering through one network error, and gives up after three in a row', async () => {
    const fetchMock = vi.fn()
      .mockResolvedValueOnce(pending()).mockRejectedValueOnce(new TypeError('x')).mockResolvedValueOnce(ok());
    vi.stubGlobal('fetch', fetchMock);
    const first = renderHook(() => useOutputCheck(URL_));
    act(() => first.result.current.onError());
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000 + 10000 + 20000); });
    expect(first.result.current.status).toBe('unknown');
    expect(first.result.current.remountKey).toBe(1);
    first.unmount();

    const failing = vi.fn().mockResolvedValueOnce(pending()).mockRejectedValue(new TypeError('x'));
    vi.stubGlobal('fetch', failing);
    const second = renderHook(() => useOutputCheck(URL_));
    act(() => second.result.current.onError());
    await flush();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000 + 10000 + 20000 + 60000); });
    expect(second.result.current.status).toBe('failed');
    expect(failing).toHaveBeenCalledTimes(4);
  });

  it('re-checks an unavailable answer at 60 s and recovers when the verdict arrives', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(json(409, 'CONTENT_MODERATION_UNAVAILABLE')).mockResolvedValue(ok());
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useOutputCheck(URL_));
    act(() => result.current.onError());
    await flush();
    expect(result.current.status).toBe('unavailable');
    await act(async () => { await vi.advanceTimersByTimeAsync(59000); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1000); });
    expect(result.current.status).toBe('unknown');
    expect(result.current.remountKey).toBe(1);
  });

  it('shows the server message and stops for a file that cannot be checked as it is', async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(json(409, 'CONTENT_MODERATION_UNAVAILABLE', 'This file cannot be used because it could not be checked as it is.', true)));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useOutputCheck(URL_));
    act(() => result.current.onError());
    await flush();
    expect(result.current.status).toBe('unavailableFinal');
    expect(result.current.message).toBe('This file cannot be used because it could not be checked as it is.');
    await act(async () => { await vi.advanceTimersByTimeAsync(180000); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('uses neutral wording for a non-video outage', async () => {
    vi.stubGlobal('fetch', vi.fn().mockImplementation(() => Promise.resolve(json(409, 'CONTENT_MODERATION_UNAVAILABLE', 'Publication is temporarily unavailable.', false))));
    const { result } = renderHook(() => useOutputCheck(URL_, { neutral: true }));
    act(() => result.current.onError());
    await flush();
    expect(result.current.message).toMatch(/^This file is temporarily unavailable/);
  });

  it('re-probes a 202 at the server Retry-After, with the spinner kept', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(accepted('2')).mockResolvedValue(ok());
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useOutputCheck(URL_));
    act(() => result.current.onError());
    await flush();
    expect(result.current.status).toBe('pending');
    expect(result.current.message).toMatch(/Checking your video/);
    await act(async () => { await vi.advanceTimersByTimeAsync(1999); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(result.current.status).toBe('unknown');
    expect(result.current.remountKey).toBe(1);
  });

  it('falls back to the 5 s back-off when a 202 carries no Retry-After', async () => {
    const fetchMock = vi.fn().mockImplementation(() => Promise.resolve(accepted()));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useOutputCheck(URL_));
    act(() => result.current.onError());
    await flush();
    expect(result.current.status).toBe('pending');
    await act(async () => { await vi.advanceTimersByTimeAsync(4999); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await act(async () => { await vi.advanceTimersByTimeAsync(1); });
    expect(fetchMock).toHaveBeenCalledTimes(2);
    await act(async () => { await vi.advanceTimersByTimeAsync(10000); });
    expect(fetchMock).toHaveBeenCalledTimes(3);
  });

  it('stops polling on unmount', async () => {
    const fetchMock = vi.fn().mockResolvedValue(pending());
    vi.stubGlobal('fetch', fetchMock);
    const { result, unmount } = renderHook(() => useOutputCheck(URL_));
    act(() => result.current.onError());
    await flush();
    unmount();
    await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it('treats a blocked answer as final, with no polling', async () => {
    const fetchMock = vi.fn().mockResolvedValue(json(422, 'CONTENT_BLOCKED', 'This video cannot be shown.'));
    vi.stubGlobal('fetch', fetchMock);
    const { result } = renderHook(() => useOutputCheck(URL_));
    act(() => result.current.onError());
    await flush();
    expect(result.current.status).toBe('blocked');
    expect(result.current.message).toBe('This video cannot be shown.');
    await act(async () => { await vi.advanceTimersByTimeAsync(30000); });
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });
});

describe('CheckedVideo (a bare player)', () => {
  it('shows a checking note in place of the player, then the video again with no reload', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(pending()).mockResolvedValue(ok());
    vi.stubGlobal('fetch', fetchMock);
    const { container } = render(<MantineProvider><CheckedVideo src={URL_} controls aria-label="clip" /></MantineProvider>);
    const first = container.querySelector('video') as HTMLVideoElement;
    act(() => { first.dispatchEvent(new Event('error')); });
    await flush();
    expect(screen.getByRole('status').textContent).toMatch(/Checking your video/);
    expect(container.querySelector('video')).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(OUTPUT_CHECK_INTERVAL_MS); });
    const next = container.querySelector('video') as HTMLVideoElement;
    expect(next).not.toBeNull();
    expect(next).not.toBe(first);
    expect(next.getAttribute('src')).toBe(URL_);
  });
});

describe('useDownloadGuard', () => {
  const wrapper = ({ children }: { children: ReactNode }) => <MantineProvider><Notifications />{children}</MantineProvider>;

  it('does not save an empty body when the output is still pending (202)', async () => {
    const fetchMock = vi.fn().mockResolvedValue(accepted('5'));
    vi.stubGlobal('fetch', fetchMock);
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const { result } = renderHook(() => useDownloadGuard(), { wrapper });
    const ev = { preventDefault: vi.fn() };
    await act(async () => { await result.current(URL_, 'clip.mp4')(ev); });
    expect(ev.preventDefault).toHaveBeenCalled();
    expect(fetchMock).toHaveBeenCalledWith(URL_, expect.objectContaining({ headers: { Range: 'bytes=0-0' } }));
    expect(click).not.toHaveBeenCalled();
    expect(document.querySelectorAll('a[download]')).toHaveLength(0);
  });

  it('still downloads when the output is ready', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(ok()));
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => {});
    const { result } = renderHook(() => useDownloadGuard(), { wrapper });
    await act(async () => { await result.current(URL_, 'clip.mp4')({ preventDefault: () => {} }); });
    expect(click).toHaveBeenCalledTimes(1);
  });
});

describe('retryWhilePending', () => {
  it('retries a pending refusal on the back-off, and throws anything else at once', async () => {
    const { ApiError } = await import('@/lib/api/client');
    const { retryWhilePending } = await import('./output-check');
    const pendingError = () => new ApiError('checking', 409, [], { code: 'CONTENT_MODERATION_PENDING' });
    const run = vi.fn().mockRejectedValueOnce(pendingError()).mockResolvedValue('rows');
    const onPending = vi.fn();
    const result = retryWhilePending(run, { isCancelled: () => false, onPending });
    await vi.advanceTimersByTimeAsync(5000);
    await expect(result).resolves.toBe('rows');
    expect(onPending).toHaveBeenCalledTimes(1);

    const other = vi.fn().mockRejectedValue(new ApiError('gone', 404, [], { code: 'not_found' }));
    await expect(retryWhilePending(other, { isCancelled: () => false })).rejects.toThrow('gone');
    expect(other).toHaveBeenCalledTimes(1);
  });
});
