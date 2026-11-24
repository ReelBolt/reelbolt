/**
 * W0.6 (UI2-10): which render the review page plays. The render list is newest first; the page wants
 * the newest finished render that has a picture (not the `AudioOnly` preset) and covers the whole
 * timeline (not an in/out-range export). A render's regions are in timeline frames, so a full render
 * starts at frame 0 and, for the revision on screen, reaches the timeline's end; for an older revision
 * the end is unknown here and starting at 0 is taken as full. When no full render exists, the newest
 * render with a picture is returned flagged `partial`, so the page can say so.
 */

import type { EditTimelineRenderSummary } from '../sync/api';

export interface ReviewRenderChoice {
  render: EditTimelineRenderSummary;
  /** True when the render covers only part of the timeline. */
  partial: boolean;
}

const playable = (r: EditTimelineRenderSummary): boolean => r.status === 'Passed' && r.hasOutput && !!r.stepResultId && r.preset !== 'AudioOnly';

function coversWholeTimeline(r: EditTimelineRenderSummary, endFrame: number, currentRevision: number): boolean {
  const list = r.regions?.list ?? [];
  // A render with no region list predates the region report; nothing says it was a range.
  if (list.length === 0) return true;
  let start = Infinity;
  let end = -Infinity;
  for (const region of list) {
    if (region.startFrame < start) start = region.startFrame;
    if (region.endFrame > end) end = region.endFrame;
  }
  if (start > 0) return false;
  return r.revision !== currentRevision || end >= endFrame;
}

export function pickReviewRender(
  renders: readonly EditTimelineRenderSummary[] | undefined,
  timelineEndFrame: number,
  currentRevision: number,
): ReviewRenderChoice | null {
  const candidates = (renders ?? []).filter(playable);
  const full = candidates.find((r) => coversWholeTimeline(r, timelineEndFrame, currentRevision));
  if (full) return { render: full, partial: false };
  return candidates.length > 0 ? { render: candidates[0], partial: true } : null;
}
