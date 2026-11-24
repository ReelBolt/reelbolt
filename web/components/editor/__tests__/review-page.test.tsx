// @vitest-environment jsdom
/**
 * R11.5 / VF16: the one responsive review page. Stale-render banner, render picker, pinned-timecode chip,
 * layout switch by width, copy link, and the phone comment actions.
 */

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';
import type { ReviewComment } from '@/lib/editing/api/editor-p9';

const state = vi.hoisted(() => ({ timeline: undefined as unknown, renders: [] as unknown[], comments: [] as unknown[], wide: false }));
const api = vi.hoisted(() => ({
  createReviewComment: vi.fn(async () => ({})),
  updateReviewComment: vi.fn(async () => ({})),
  deleteReviewComment: vi.fn(async () => undefined),
}));
vi.mock('swr', () => ({ default: () => ({ data: state.timeline, error: undefined }) }));
vi.mock('@/lib/hooks/use-edit-timeline-renders', () => ({ useEditTimelineRenders: () => ({ data: state.renders }) }));
vi.mock('@/lib/hooks/use-editor-p9', () => ({ useReviewComments: () => ({ data: state.comments, mutate: vi.fn(), isLoading: false, error: undefined }) }));
vi.mock('@/lib/api/outputs', () => ({ getOutputVideoUrl: (_p: string, id: string) => `/outputs/${id}` }));
vi.mock('@/lib/editing/api/editor-p9', async (importOriginal) => ({ ...(await importOriginal<typeof import('@/lib/editing/api/editor-p9')>()), ...api }));

import { MobileReview } from '../review/MobileReview';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';

beforeEach(() => {
  (globalThis as { ResizeObserver?: unknown }).ResizeObserver ??= class {
    observe() {}
    unobserve() {}
    disconnect() {}
  };
  global.matchMedia = (query: string): MediaQueryList =>
    ({ matches: state.wide && query.includes('min-width: 62em'), media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;
  state.timeline = { id: 't', name: 'Promo', revision: 5, document: standardDoc() };
});
afterEach(() => {
  cleanup();
  Object.assign(state, { timeline: undefined, renders: [], comments: [], wide: false });
  vi.clearAllMocks();
});

const summary = (id: string, extra: Partial<EditTimelineRenderSummary> = {}): EditTimelineRenderSummary => ({
  executionId: id, workflowId: 'w', timelineId: 't', status: 'Passed', startedAt: null, completedAt: '2026-01-02T10:00:00Z', revision: 5, preset: 'Hd1080p16x9',
  outputFileName: `${id}.mp4`, errorMessage: null, stepResultId: `sr-${id}`, hasOutput: true,
  regions: { total: 1, cached: 0, list: [{ startFrame: 0, endFrame: 240, cached: false }] }, ...extra,
});
const comment = (id: string, extra: Partial<ReviewComment> = {}): ReviewComment => ({
  id, timelineId: 't', parentId: null, authorUserId: 'u', authorName: 'Ana', frame: 60, durationFrames: null, body: 'Cut here', resolved: false, createdAt: '2026-01-02T10:00:00Z', updatedAt: '2026-01-02T10:00:00Z', ...extra,
});
const wrap = () => render(<MantineProvider><MobileReview projectId="p" timelineId="t" /></MantineProvider>);

describe('R11.5 review page', () => {
  it('shows no stale banner for a render of the current revision', () => {
    state.renders = [summary('a')];
    wrap();
    expect(screen.queryByTestId('review-stale-banner')).toBeNull();
    expect(screen.getByTestId('review-access-line').textContent).toMatch(/Anyone in this workspace with access can open it/);
  });

  it('shows "Newer edits exist" with a link to the editor when the render is older than the timeline', () => {
    state.renders = [summary('old', { revision: 3 })];
    wrap();
    const banner = screen.getByTestId('review-stale-banner');
    expect(banner.textContent).toMatch(/Newer edits exist/);
    expect(screen.getByTestId('review-stale-open-editor').getAttribute('href')).toMatch(/\/editor\/p\/timeline\/t$/);
  });

  it('offers a render picker and plays the picked render', () => {
    state.renders = [summary('new'), summary('prev', { revision: 4 })];
    wrap();
    expect(screen.getByTestId('mobile-review-video').getAttribute('src')).toBe('/outputs/sr-new');
    expect(screen.getByTestId('review-render-picker')).toBeTruthy();
  });

  it('pins the chip to the paused frame, and a comment posts that frame, not frame 0', async () => {
    state.renders = [summary('a')];
    wrap();
    const video = screen.getByTestId('mobile-review-video') as HTMLVideoElement;
    Object.defineProperty(video, 'currentTime', { value: 2, configurable: true, writable: true });
    act(() => {
      fireEvent(video, new Event('pause'));
    });
    expect(screen.getByTestId('review-pinned-chip').textContent).toMatch(/00:00:02:00/);
    fireEvent.change(screen.getByTestId('review-body'), { target: { value: 'Nice' } });
    fireEvent.click(screen.getByTestId('review-add'));
    await waitFor(() => expect(api.createReviewComment).toHaveBeenCalledWith('p', 't', { body: 'Nice', frame: 60, durationFrames: null }));
  });

  it('updates the chip when the video metadata arrives late', () => {
    state.renders = [summary('a')];
    wrap();
    const video = screen.getByTestId('mobile-review-video') as HTMLVideoElement;
    Object.defineProperty(video, 'currentTime', { value: 1, configurable: true, writable: true });
    act(() => {
      fireEvent(video, new Event('loadedmetadata'));
    });
    expect(screen.getByTestId('mobile-review-timecode').textContent).toBe('00:00:01:00');
  });

  it('switches layout by width: single column on a phone, side by side from md', () => {
    state.renders = [summary('a')];
    wrap();
    expect(screen.getByTestId('mobile-review').getAttribute('data-layout')).toBe('single-column');
    cleanup();
    state.wide = true;
    wrap();
    expect(screen.getByTestId('mobile-review').getAttribute('data-layout')).toBe('side-by-side');
  });

  it('copies the review link and says so', async () => {
    state.renders = [summary('a')];
    const writeText = vi.fn(async () => undefined);
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true });
    wrap();
    fireEvent.click(screen.getByTestId('review-copy-link'));
    await waitFor(() => expect(writeText).toHaveBeenCalled());
    await waitFor(() => expect(screen.getByTestId('review-access-line').textContent).toMatch(/Link copied/));
  });

  it('puts a marker per open thread on the bar and seeks when it is tapped', () => {
    state.renders = [summary('a')];
    state.comments = [comment('c1', { frame: 60 })];
    wrap();
    fireEvent.click(screen.getByTestId('review-marker-c1'));
    expect(screen.getByTestId('mobile-review-timecode').textContent).toBe('00:00:02:00');
  });

  it('phone comment actions: reply, resolve, delete (confirmed) with the right calls', async () => {
    state.renders = [summary('a')];
    state.comments = [comment('c1')];
    wrap();
    fireEvent.click(screen.getByTestId('review-reply-c1'));
    fireEvent.change(screen.getByTestId('review-reply-body-c1'), { target: { value: 'ok' } });
    fireEvent.click(screen.getByTestId('review-reply-send-c1'));
    await waitFor(() => expect(api.createReviewComment).toHaveBeenCalledWith('p', 't', { body: 'ok', frame: 60, parentId: 'c1' }));
    fireEvent.click(screen.getByTestId('review-resolve-c1'));
    await waitFor(() => expect(api.updateReviewComment).toHaveBeenCalledWith('p', 't', 'c1', { resolved: true }));
    fireEvent.click(screen.getByTestId('review-delete-c1'));
    expect(api.deleteReviewComment).not.toHaveBeenCalled();
    fireEvent.click(screen.getByTestId('review-delete-confirm-c1'));
    await waitFor(() => expect(api.deleteReviewComment).toHaveBeenCalledWith('p', 't', 'c1'));
  });

  it('says plainly when deleting fails', async () => {
    state.renders = [summary('a')];
    state.comments = [comment('c1')];
    api.deleteReviewComment.mockRejectedValueOnce(new Error('x{"stack":1}'));
    wrap();
    fireEvent.click(screen.getByTestId('review-delete-c1'));
    fireEvent.click(screen.getByTestId('review-delete-confirm-c1'));
    await waitFor(() => expect(screen.getByText(/could not be deleted/)).toBeTruthy());
  });
});
