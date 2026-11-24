// @vitest-environment jsdom
import { describe, it, expect, afterEach, vi } from 'vitest';
import { render, screen, cleanup, fireEvent, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';

vi.mock('@/lib/hooks/use-video-spend-limit', () => ({ useVideoSpendLimit: vi.fn() }));
vi.mock('@/lib/api/video-spend-limit', () => ({ saveVideoSpendLimit: vi.fn() }));

import { useVideoSpendLimit } from '@/lib/hooks/use-video-spend-limit';
import { saveVideoSpendLimit, type VideoSpendLimit } from '@/lib/api/video-spend-limit';
import { VideoSpendLimitCard } from './VideoSpendLimitCard';

global.matchMedia = ((q: string) => ({
  matches: false, media: q, onchange: null, addListener() {}, removeListener() {},
  addEventListener() {}, removeEventListener() {}, dispatchEvent: () => true,
})) as unknown as typeof matchMedia;
global.ResizeObserver = vi.fn().mockImplementation(function () {
  return { observe: vi.fn(), unobserve: vi.fn(), disconnect: vi.fn() };
});

const view = (over: Partial<VideoSpendLimit> = {}): VideoSpendLimit => ({
  dailyLimitCredits: null, alertThresholdCredits: null, usedTodayCredits: 0, remainingTodayCredits: null,
  alertReached: false, limitReached: false, resetsAt: '2026-11-19T00:00:00Z', ...over,
});
const mutate = vi.fn();
const show = (data: VideoSpendLimit | undefined) =>
  vi.mocked(useVideoSpendLimit).mockReturnValue({ data, mutate } as unknown as ReturnType<typeof useVideoSpendLimit>);
const wrap = () => render(<MantineProvider><VideoSpendLimitCard /></MantineProvider>);

afterEach(() => { cleanup(); vi.clearAllMocks(); });

describe('VideoSpendLimitCard', () => {
  it('renders nothing until the setting is read', () => {
    show(undefined);
    wrap();
    expect(screen.queryByTestId('video-spend-limit-card')).toBeNull();
  });

  it('says there is no limit by default', () => {
    show(view({ usedTodayCredits: 12 }));
    wrap();
    expect(screen.getByTestId('video-limit-status').textContent).toBe('No daily limit. Used today: 12 credits.');
  });

  it('shows the limit, the use and the room left, and the alert when reached', () => {
    show(view({ dailyLimitCredits: 300, alertThresholdCredits: 100, usedTodayCredits: 120, remainingTodayCredits: 180, alertReached: true }));
    wrap();
    expect(screen.getByTestId('video-limit-status').textContent).toMatch(/Used today: 120 of 300 credits \(180 left\)/);
    expect(screen.getByTestId('video-limit-alert')).toBeTruthy();
  });

  it('saves the typed limit and clears it with Remove limit', async () => {
    show(view());
    vi.mocked(saveVideoSpendLimit).mockResolvedValue(view({ dailyLimitCredits: 250, remainingTodayCredits: 250 }));
    wrap();

    fireEvent.change(screen.getByTestId('video-limit-input'), { target: { value: '250' } });
    fireEvent.click(screen.getByRole('button', { name: 'Save' }));
    await waitFor(() => expect(saveVideoSpendLimit).toHaveBeenCalledWith({ dailyLimitCredits: 250, alertThresholdCredits: null }));

    cleanup();
    show(view({ dailyLimitCredits: 250, remainingTodayCredits: 250 }));
    wrap();
    fireEvent.click(screen.getByRole('button', { name: 'Remove limit' }));
    await waitFor(() => expect(saveVideoSpendLimit).toHaveBeenLastCalledWith({ dailyLimitCredits: null, alertThresholdCredits: null }));
  });
});
