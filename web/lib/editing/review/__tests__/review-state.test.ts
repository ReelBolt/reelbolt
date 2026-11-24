import { describe, expect, it } from 'vitest';
import { isRenderStale, markerPercent, pickerRenders, pinnedFrame, reviewLayout, renderLabel } from '../review-state';
import type { EditTimelineRenderSummary } from '../../sync/api';

const r = (id: string, extra: Partial<EditTimelineRenderSummary> = {}): EditTimelineRenderSummary => ({
  executionId: id, workflowId: 'w', timelineId: 't', status: 'Passed', startedAt: null, completedAt: '2026-01-02T10:00:00Z', revision: 4, preset: 'Hd1080p16x9',
  outputFileName: `${id}.mp4`, errorMessage: null, stepResultId: `sr-${id}`, hasOutput: true, regions: null, ...extra,
});

describe('review-state', () => {
  it('stale only when the timeline moved past the render', () => {
    expect(isRenderStale(4, 5)).toBe(true);
    expect(isRenderStale(5, 5)).toBe(false);
    expect(isRenderStale(null, 5)).toBe(false);
  });
  it('pins to the paused frame, never frame 0 by accident', () => {
    expect(pinnedFrame(2, 30)).toBe(60);
    expect(pinnedFrame(2.5, 30)).toBe(75);
    expect(pinnedFrame(NaN, 30)).toBe(0);
    expect(pinnedFrame(-1, 30)).toBe(0);
  });
  it('places markers as percent of the film', () => {
    expect(markerPercent(30, 120)).toBe(25);
    expect(markerPercent(500, 120)).toBe(100);
    expect(markerPercent(10, 0)).toBe(0);
  });
  it('switches layout at the md breakpoint', () => {
    expect(reviewLayout(390)).toBe('single-column');
    expect(reviewLayout(991)).toBe('single-column');
    expect(reviewLayout(992)).toBe('side-by-side');
    expect(reviewLayout(1280)).toBe('side-by-side');
  });
  it('lists only finished renders with a picture and marks older cuts', () => {
    const list = pickerRenders([r('a'), r('b', { preset: 'AudioOnly' }), r('c', { status: 'Failed' }), r('d', { hasOutput: false })]);
    expect(list.map((x) => x.executionId)).toEqual(['a']);
    expect(renderLabel(r('a'), 5)).toMatch(/older cut/);
    expect(renderLabel(r('a'), 4)).not.toMatch(/older cut/);
  });
});
