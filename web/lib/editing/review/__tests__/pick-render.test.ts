/** @vitest-environment node */
/**
 * W0.6 (UI2-10): the review page plays the newest finished render that has a picture and covers the
 * whole timeline — never an audio-only or an in/out-range render when a full one exists.
 */
import { describe, expect, it } from 'vitest';
import { pickReviewRender } from '../pick-render';
import type { EditTimelineRenderSummary } from '../../sync/api';

const r = (id: string, extra: Partial<EditTimelineRenderSummary> = {}): EditTimelineRenderSummary => ({
  executionId: id,
  workflowId: 'w',
  timelineId: 't',
  status: 'Passed',
  startedAt: null,
  completedAt: null,
  revision: 3,
  preset: 'Hd1080p16x9',
  outputFileName: `${id}.mp4`,
  errorMessage: null,
  stepResultId: `sr-${id}`,
  hasOutput: true,
  regions: { total: 1, cached: 0, list: [{ startFrame: 0, endFrame: 300, cached: false }] },
  ...extra,
});

describe('pickReviewRender', () => {
  it('skips audio-only, range and unfinished renders (newest first)', () => {
    const renders = [
      r('running', { status: 'Running', stepResultId: null, hasOutput: false }),
      r('audio', { preset: 'AudioOnly' }),
      r('range', { regions: { total: 1, cached: 0, list: [{ startFrame: 60, endFrame: 120, cached: false }] } }),
      r('full'),
      r('older'),
    ];
    expect(pickReviewRender(renders, 300, 3)).toMatchObject({ render: { executionId: 'full' }, partial: false });
  });

  it('a range that starts at 0 but stops short of the end is partial for the current revision', () => {
    const renders = [r('head', { regions: { total: 1, cached: 0, list: [{ startFrame: 0, endFrame: 90, cached: false }] } }), r('full')];
    expect(pickReviewRender(renders, 300, 3)?.render.executionId).toBe('full');
  });

  it('falls back to the newest picture render, flagged partial, when no full one exists', () => {
    const renders = [r('audio', { preset: 'AudioOnly' }), r('range', { regions: { total: 1, cached: 0, list: [{ startFrame: 60, endFrame: 120, cached: false }] } })];
    expect(pickReviewRender(renders, 300, 3)).toMatchObject({ render: { executionId: 'range' }, partial: true });
    expect(pickReviewRender([r('audio', { preset: 'AudioOnly' })], 300, 3)).toBeNull();
    expect(pickReviewRender(undefined, 300, 3)).toBeNull();
  });
});
