'use client';

import { ActionIcon, Anchor, Group, Stack, Text } from '@mantine/core';
import { IconX } from '@tabler/icons-react';
import Link from 'next/link';
import { useFormat, useT } from '@/i18n';
import { useActivity } from '@/lib/activity/ActivityProvider';
import { isFinishedState } from '@/lib/activity/notifier';
import { getJobType, jobHref } from '@/lib/activity/registry';
import type { ActivityJob } from '@/lib/activity/types';
import classes from './activity.module.css';
import { InlineJobStatus } from './InlineJobStatus';
import { JobCost } from './JobCost';
import { JobFailure } from './JobFailure';

export interface ActivityRowProps {
  job: ActivityJob;
  /** Called when the user follows "Open" (the tray closes itself, so a phone drawer does not cover the page). */
  onOpen?: () => void;
}

/** One background job in the tray: what it is, where it runs, how it is going, and where to open it. */
export function ActivityRow({ job, onOpen }: ActivityRowProps) {
  const t = useT('activity');
  const f = useFormat();
  const { dismiss } = useActivity();
  const def = getJobType(job.type);
  const Icon = def.icon;
  const href = jobHref(job);
  const finished = isFinishedState(job.state);
  const when = job.endedAt ?? job.startedAt ?? job.createdAt;
  return (
    <Group gap="sm" wrap="nowrap" align="flex-start" data-testid="activity-row" data-state={job.state}>
      <Icon size={20} aria-hidden style={{ flexShrink: 0, marginTop: 2 }} />
      <Stack gap={4} className={classes.rowTitle} style={{ flex: 1 }}>
        <Text fz="sm" fw={600} truncate>
          {t(def.labelKey)}
        </Text>
        {job.title && (
          <Text fz={12} c="dimmed" truncate>
            {job.title}
          </Text>
        )}
        {job.projectName && (
          <Text fz={12} c="dimmed" truncate>
            {job.projectName}
          </Text>
        )}
        <InlineJobStatus job={job} />
        <JobCost job={job} />
        <JobFailure job={job} />
        {job.state === 'needsReview' && job.review && (
          <Text fz={12}>{t(job.review.summaryKey, job.review.summaryValues as Record<string, string | number>)}</Text>
        )}
        <Group gap="md">
          <Text fz={12} c="dimmed">
            {f.relative(when)}
          </Text>
          {href && (
            <Anchor component={Link} href={href} fz={12} className={classes.tapTarget} onClickCapture={onOpen}>
              {t('row.open')}
            </Anchor>
          )}
        </Group>
      </Stack>
      {finished && (
        <ActionIcon variant="subtle" color="gray" size="lg" className={classes.tapTarget} onClick={() => dismiss(job.id)} aria-label={t('row.dismiss')}>
          <IconX size={16} aria-hidden />
        </ActionIcon>
      )}
    </Group>
  );
}
