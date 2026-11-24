// @vitest-environment jsdom
/**
 * W0.6 (UI2-01, UI2-10): the phone review page. The timecode follows the paused video even when the
 * render list arrived before the timeline (the video mounts after the first effect ran), so a comment
 * pins to the paused frame; and the page plays the newest full render with a picture.
 */

import { afterEach, describe, expect, it, vi } from 'vitest';
import { act, cleanup, fireEvent, render, screen } from '@testing-library/react';
import { MantineProvider } from '@mantine/core';
import type { EditTimelineRenderSummary } from '@/lib/editing/sync/api';

const state = vi.hoisted(() => ({ timeline: undefined as unknown, renders: [] as unknown[] }));
vi.mock('swr', () => ({ default: () => ({ data: state.timeline, error: undefined }) }));
vi.mock('@/lib/hooks/use-edit-timeline-renders', () => ({ useEditTimelineRenders: () => ({ data: state.renders }) }));
vi.mock('@/lib/hooks/use-editor-p9', () => ({ useReviewComments: () => ({ data: [], mutate: vi.fn(), isLoading: false, error: undefined }) }));
vi.mock('@/lib/api/outputs', () => ({ getOutputVideoUrl: (_p: string, id: string) => `/outputs/${id}` }));

import { MobileReview } from '../review/MobileReview';
import { standardDoc } from '@/lib/editing/__tests__/fixtures';

global.matchMedia = (query: string): MediaQueryList =>
  ({ matches: false, media: query, onchange: null, addListener: () => {}, removeListener: () => {}, addEventListener: () => {}, removeEventListener: () => {}, dispatchEvent: () => true }) as MediaQueryList;

afterEach(() => {
  cleanup();
  state.timeline = undefined;
  state.renders = [];
});

const summary = (id: string, extra: Partial<EditTimelineRenderSummary> = {}): EditTimelineRenderSummary => ({
  executionId: id, workflowId: 'w', timelineId: 't', status: 'Passed', startedAt: null, completedAt: null, revision: 4, preset: 'Hd1080p16x9',
  outputFileName: `${id}.mp4`, errorMessage: null, stepResultId: `sr-${id}`, hasOutput: true,
  regions: { total: 1, cached: 0, list: [{ startFrame: 0, endFrame: 240, cached: false }] }, ...extra,
});

describe('MobileReview', () => {
  it('pins the timecode to the paused frame when the video mounts after the timeline loads', () => {
    state.renders = [summary('audio', { preset: 'AudioOnly' }), summary('full')];
    const view = render(<MantineProvider><MobileReview projectId="p" timelineId="t" /></MantineProvider>);
    // The timeline arrives after the first render: the <video> mounts only now.
    state.timeline = { id: 't', name: 'Promo', revision: 4, document: standardDoc() };
    view.rerender(<MantineProvider><MobileReview projectId="p" timelineId="t" /></MantineProvider>);

    const video = screen.getByTestId('mobile-review-video') as HTMLVideoElement;
    expect(video.getAttribute('src')).toBe('/outputs/sr-full');
    Object.defineProperty(video, 'currentTime', { value: 2.5, configurable: true, writable: true });
    act(() => {
      fireEvent(video, new Event('seeked'));
    });
    expect(screen.getByTestId('mobile-review-timecode').textContent).toBe('00:00:02:15');
  });
});
