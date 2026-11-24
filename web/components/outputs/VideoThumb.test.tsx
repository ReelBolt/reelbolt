// @vitest-environment jsdom
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { VideoThumb } from './VideoThumb';

const SRC = '/api/v1/projects/p1/outputs/s1/download';
const pending = () => new Response(JSON.stringify({ code: 'CONTENT_MODERATION_PENDING', message: 'm' }), { status: 409, headers: { 'Content-Type': 'application/json' } });

beforeEach(() => {
  global.matchMedia = ((q: string) => ({ matches: false, media: q, addListener() {}, removeListener() {}, addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true })) as unknown as typeof matchMedia;
  vi.useFakeTimers();
});
afterEach(() => { cleanup(); vi.useRealTimers(); vi.unstubAllGlobals(); });

describe('VideoThumb', () => {
  it('keeps the placeholder with a Checking cue while pending, then mounts the video by itself', async () => {
    const fetchMock = vi.fn().mockResolvedValueOnce(pending()).mockResolvedValue(new Response(null, { status: 206 }));
    vi.stubGlobal('fetch', fetchMock);
    const { container } = render(<MantineProvider><VideoThumb src={SRC} /></MantineProvider>);
    const video = container.querySelector('video') as HTMLVideoElement;
    act(() => { video.dispatchEvent(new Event('error')); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(container.querySelector('video')).toBeNull();
    expect(screen.getByLabelText('Checking')).toBeTruthy();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(container.querySelector('video')).not.toBeNull();
    expect(screen.queryByLabelText('Checking')).toBeNull();
  });
});

describe('CheckedImage', () => {
  it('says "Checking this file" while pending and loads by itself', async () => {
    const { CheckedImage } = await import('./CheckedVideo');
    const fetchMock = vi.fn().mockResolvedValueOnce(pending()).mockResolvedValue(new Response(null, { status: 206 }));
    vi.stubGlobal('fetch', fetchMock);
    const { container } = render(<MantineProvider><CheckedImage src="/api/v1/projects/p/files/f/media" alt="pic" /></MantineProvider>);
    act(() => { container.querySelector('img')!.dispatchEvent(new Event('error')); });
    await act(async () => { await vi.advanceTimersByTimeAsync(0); });
    expect(screen.getByRole('status').textContent).toMatch(/Checking this file/);
    expect(container.querySelector('img')).toBeNull();
    await act(async () => { await vi.advanceTimersByTimeAsync(5000); });
    expect(container.querySelector('img')).not.toBeNull();
  });
});

describe('VideoThumb by file kind', () => {
  it('renders an img, not a video, for a png', () => {
    vi.stubGlobal('fetch', vi.fn());
    const { container } = render(<MantineProvider><VideoThumb src={SRC} fileName="step-3-image1.png" /></MantineProvider>);
    expect(container.querySelector('img')?.getAttribute('src')).toBe(SRC);
    expect(container.querySelector('video')).toBeNull();
  });
});
