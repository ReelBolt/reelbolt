'use client';

import { Text } from '@mantine/core';
import { useFormat, useT } from '@/i18n';
import type { ActivityJob } from '@/lib/activity/types';

export interface JobCostProps {
  job: ActivityJob;
}

/** "About N credits held" while a job runs, "N credits used" once it charged; nothing when unknown. */
export function JobCost({ job }: JobCostProps) {
  const t = useT('activity');
  const f = useFormat();
  const { chargedCredits, heldCredits } = job.credits;
  const charged = job.state === 'done' || job.state === 'failed' || job.state === 'cancelled';
  // The feed and trackJob carry CREDITS (the server divides millicredits by 1000); the formatter takes millicredits.
  const credits = charged ? chargedCredits : heldCredits;
  if (credits == null || (charged && credits === 0)) return null;
  const key = charged ? 'cost.used' : 'cost.held';
  return (
    <Text fz={12} c="dimmed">
      {t(key, { count: credits, amount: f.credits(credits * 1000) })}
    </Text>
  );
}
