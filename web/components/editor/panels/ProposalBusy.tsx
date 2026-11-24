'use client';

import React from 'react';
import { InlineJobStatus } from '@/components/activity/InlineJobStatus';
import { useJobFor } from '@/lib/activity/ActivityProvider';

/** The running state of the proposal request, drawn like the tray's row; nothing while no request is pending. */
export function ProposalBusy({ projectId }: { projectId: string }) {
  const job = useJobFor((j) => j.origin === 'client' && j.type === 'proposal' && j.state === 'running' && j.projectId === projectId);
  return job ? <InlineJobStatus job={job} /> : null;
}
