// @vitest-environment jsdom
import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, renderHook, waitFor } from '@testing-library/react';

const trackJob = vi.fn((_meta: unknown, work: (s: AbortSignal, id: string) => Promise<unknown>) => work(new AbortController().signal, 'client:7'));
const updateTrackedJob = vi.fn();
vi.mock('@/lib/activity/ActivityProvider', () => ({ useActivity: () => ({ trackJob, updateTrackedJob }) }));
vi.mock('@/i18n', () => ({ useT: () => Object.assign((k: string) => k, { msg: (k: string) => k }), useFormat: () => ({ locale: 'en' }) }));
const upload = vi.fn(async (_p: string, _f: File, onProgress: (pct: number) => void) => {
  onProgress(50);
  onProgress(100);
});
vi.mock('@/lib/api/files', () => ({ uploadFileWithProgress: (...a: Parameters<typeof upload>) => upload(...a) }));

import { useUploadQueue } from '@/lib/hooks/use-upload-queue';

afterEach(cleanup);

describe('useUploadQueue activity tracking', () => {
  it('tracks each upload as an activity job and feeds byte progress to it', async () => {
    const { result } = renderHook(() => useUploadQueue('p1', vi.fn()));
    act(() => result.current.enqueue([{ file: new File(['abc'], 'clip.mp4'), relativePath: 'clip.mp4' }], ''));
    await waitFor(() => expect(result.current.entries[0]?.status).toBe('done'));
    expect(trackJob).toHaveBeenCalledWith({ type: 'upload', title: 'clip.mp4', projectId: 'p1' }, expect.any(Function));
    expect(updateTrackedJob).toHaveBeenCalledWith('client:7', { progress: 0.5 });
    expect(updateTrackedJob).toHaveBeenCalledWith('client:7', { progress: 1 });
  });
});
