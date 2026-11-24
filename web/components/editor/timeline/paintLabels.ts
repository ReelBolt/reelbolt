/**
 * Words the canvas paints itself. The editor always passes catalogue text (`editor-timeline.canvas.*`,
 * resolved in `TimelineCanvas`); this English fallback exists only for callers that paint without
 * a translator (the paint unit tests). It is not part of the migrated lint globs on purpose.
 */
export interface PaintLabels {
  offline: string;
  lengthUnchecked: string;
  yours: string;
  compound: string;
  /** The job chip on a clip a background job works on. */
  jobProcessing: string;
  jobReview: string;
  jobFailed: string;
}

export const FALLBACK_PAINT_LABELS: PaintLabels = {
  offline: 'Offline',
  lengthUnchecked: 'Length not checked',
  yours: 'yours',
  compound: 'Compound',
  jobProcessing: 'Processing',
  jobReview: 'Ready to review',
  jobFailed: 'Did not finish',
};
