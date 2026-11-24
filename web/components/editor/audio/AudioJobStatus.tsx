'use client';

import React from 'react';
import { InlineJobStatus } from '@/components/activity/InlineJobStatus';
import { useJobFor } from '@/lib/activity/ActivityProvider';
import { localJob } from '@/lib/editing/background/activity-job';

/** The running state of a loudness / audition job: the activity feed's row when it has caught up, else a local one. */
export function AudioJobStatus({ kind, executionId }: { kind: 'loudness' | 'audition'; executionId?: string }) {
  const fed = useJobFor((j) => !!executionId && j.executionId === executionId);
  return <InlineJobStatus job={fed ?? localJob({ state: 'running', type: kind, executionId })} />;
}
