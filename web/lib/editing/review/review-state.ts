/**
 * R11.5 / VF16: the small decisions of the review page, kept pure so they are tested without a browser.
 */

import type { EditTimelineRenderSummary } from '../sync/api';

/** Mantine's `md` breakpoint (62em): from here the video sits beside the comments (VF16). */
export const REVIEW_SIDE_BY_SIDE_QUERY = '(min-width: 62em)';

export type ReviewLayout = 'side-by-side' | 'single-column';

/** The width at which the layout switches, in CSS px (62em at the default 16 px root). */
export const REVIEW_SIDE_BY_SIDE_MIN_PX = 992;

export function reviewLayout(widthPx: number): ReviewLayout {
  return widthPx >= REVIEW_SIDE_BY_SIDE_MIN_PX ? 'side-by-side' : 'single-column';
}

/** True when the timeline has been edited since this render was made (the render shows an older cut). */
export function isRenderStale(renderRevision: number | null | undefined, timelineRevision: number): boolean {
  if (renderRevision === null || renderRevision === undefined) return false;
  return renderRevision < timelineRevision;
}

/** The frame a comment pins to: the paused video's time on the timeline's frame grid, never negative or NaN. */
export function pinnedFrame(currentTimeSec: number, fps: number): number {
  if (!Number.isFinite(currentTimeSec) || !Number.isFinite(fps) || fps <= 0) return 0;
  return Math.max(0, Math.floor(currentTimeSec * fps + 1e-6));
}

/** Where a comment's marker sits on the bar under the player, 0..100 (percent of the film). */
export function markerPercent(frame: number, totalFrames: number): number {
  if (!(totalFrames > 0)) return 0;
  return Math.min(100, Math.max(0, (frame / totalFrames) * 100));
}

/** The finished renders with a picture, newest first, for the render picker. */
export function pickerRenders(renders: readonly EditTimelineRenderSummary[] | undefined): EditTimelineRenderSummary[] {
  return (renders ?? []).filter((r) => r.status === 'Passed' && r.hasOutput && !!r.stepResultId && r.preset !== 'AudioOnly');
}

/** A plain label for one entry of the render picker. */
export function renderLabel(r: EditTimelineRenderSummary, timelineRevision: number): string {
  const when = r.completedAt ? new Date(r.completedAt).toLocaleString(undefined, { month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' }) : null;
  const old = isRenderStale(r.revision, timelineRevision) ? ' (older cut)' : '';
  return `${when ?? 'Finished render'}${old}`;
}
