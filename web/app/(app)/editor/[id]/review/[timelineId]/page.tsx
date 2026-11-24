'use client';

import React, { use } from 'react';
import { MobileReview } from '@/components/editor/review/MobileReview';

interface Props {
  params: Promise<{ id: string; timelineId: string }>;
}

/**
 * P9: the read-only review route, `/editor/{projectId}/review/{timelineId}` — the latest finished
 * render of the timeline and its timecoded comments, laid out for a phone. Nothing here edits the
 * document; a reviewer only watches and comments.
 */
export default function TimelineReviewPage({ params }: Props) {
  const { id: projectId, timelineId } = use(params);
  return <MobileReview projectId={projectId} timelineId={timelineId} />;
}
