// @vitest-environment jsdom
/** R10.4: the button shows the price first, a click starts a preview, the overlay plays the result and closing leaves it idle. */
import { afterEach, beforeAll, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import { Preview3sButton, Preview3sOverlay } from '../Preview3s';
import { preview3sApi } from '@/lib/editing/api/preview3s';
import { preview3sStore } from '@/lib/editing/preview/preview3s-store';

beforeAll(() => {
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
});
afterEach(() => {
  cleanup();
  preview3sStore.close();
  vi.restoreAllMocks();
});

const ui = (
  <MantineProvider>
    <Preview3sButton projectId="p" timelineId="t" atFrame={30} />
    <Preview3sOverlay />
  </MantineProvider>
);

describe('R10.4 Preview 3 s', () => {
  it('shows the price, then plays the result and closes', async () => {
    vi.spyOn(preview3sApi, 'estimate').mockResolvedValue({ billingEnabled: true, affordable: true, credits: 2 });
    vi.spyOn(preview3sApi, 'start').mockResolvedValue({ executionId: 'j1' });
    vi.spyOn(preview3sApi, 'status').mockResolvedValue({ status: 'Passed', ready: true });
    render(ui);
    await waitFor(() => expect(screen.getByTestId('preview3s-button').textContent).toContain('about 2 credits'));
    vi.useFakeTimers({ shouldAdvanceTime: true });
    fireEvent.click(screen.getByTestId('preview3s-button'));
    await act(async () => {
      await vi.advanceTimersByTimeAsync(2000);
    });
    vi.useRealTimers();
    const video = await screen.findByTestId('preview3s-video');
    expect(video.getAttribute('src')).toContain('/render/preview/j1/content');
    fireEvent.click(screen.getByTestId('preview3s-close'));
    expect(screen.queryByTestId('preview3s-overlay')).toBeNull();
  });
  it('a refused estimate says why and cannot start', async () => {
    const start = vi.spyOn(preview3sApi, 'start');
    vi.spyOn(preview3sApi, 'estimate').mockResolvedValue({ billingEnabled: true, affordable: false, credits: 5, balanceCredits: 3 });
    render(ui);
    await waitFor(() => expect(screen.getByTestId('preview3s-refused').textContent).toContain('You need 2 more credits'));
    expect((screen.getByTestId('preview3s-button') as HTMLButtonElement).disabled).toBe(true);
    expect(start).not.toHaveBeenCalled();
  });
});
