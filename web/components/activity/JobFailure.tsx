'use client';

import { Stack, Text } from '@mantine/core';
import { useT } from '@/i18n';
import type { ActivityJob } from '@/lib/activity/types';

const KNOWN = new Set([
  'INSUFFICIENT_CREDITS',
  'NO_RUNNER_ONLINE',
  'BUDGET_EXCEEDED',
  'CONTENT_MODERATION_PENDING',
  'SOURCE_TOO_LARGE',
  'SOURCE_TOO_LONG',
  'SCRATCH_BUSY',
  'EGRESS_REFUSED',
  'RUN_RATE_LIMITED',
]);

export interface JobFailureProps {
  job: ActivityJob;
}

/** A plain-language reason (never the raw code) and whether the credits came back. */
export function JobFailure({ job }: JobFailureProps) {
  const t = useT('activity');
  if (job.state !== 'failed' && !job.failure) return null;
  const code = job.failure?.code ?? '';
  const refunded = job.failure?.refunded ?? null;
  return (
    <Stack gap={2}>
      <Text fz={12} c="red.7">
        {t(`failure.${KNOWN.has(code) ? code : 'generic'}`)}
      </Text>
      {refunded === true && (
        <Text fz={12} c="dimmed">
          {t('failure.refunded')}
        </Text>
      )}
      {refunded === false && (
        <Text fz={12} c="dimmed">
          {t('failure.kept')}
        </Text>
      )}
    </Stack>
  );
}
