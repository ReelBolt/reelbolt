'use client';

import { Button, Group, Popover, Stack, Text } from '@mantine/core';
import { notifications } from '@mantine/notifications';
import { useState } from 'react';
import { useT } from '@/i18n';
import { useActivity } from '@/lib/activity/ActivityProvider';
import { isActiveState } from '@/lib/activity/notifier';
import { getJobType } from '@/lib/activity/registry';
import type { ActivityJob } from '@/lib/activity/types';
import classes from './activity.module.css';
import { JobProgress } from './JobProgress';
import { JobStatusBadge } from './JobStatusBadge';

export interface InlineJobStatusProps {
  job: ActivityJob;
  /** Smaller layout: badge only, no progress bar. */
  compact?: boolean;
  /** Replaces the default cancel (the registry's `cancel` through the activity model). */
  onCancel?: (job: ActivityJob) => void | Promise<void>;
}

/** The small in-place chip shown ON the thing being worked on: badge, progress and a confirmed cancel. */
export function InlineJobStatus({ job, compact, onCancel }: InlineJobStatusProps) {
  const t = useT('activity');
  const { cancel } = useActivity();
  const [confirming, setConfirming] = useState(false);
  const canCancel = isActiveState(job.state) && (onCancel != null || (job.cancellable && (job.origin === 'client' ? !!job.cancelClient : !!getJobType(job.type).cancel)));
  const run = async () => {
    setConfirming(false);
    try {
      await (onCancel ? onCancel(job) : cancel(job));
    } catch {
      notifications.show({ color: 'red', message: t('cancel.failed') });
    }
  };
  return (
    <Stack gap={4} role="status" aria-live="polite" data-testid="inline-job-status">
      <Group gap="xs" wrap="nowrap">
        <JobStatusBadge state={job.state} />
        {canCancel && (
          <Popover opened={confirming} onChange={setConfirming} withArrow position="bottom" withinPortal>
            <Popover.Target>
              <Button variant="subtle" color="gray" size="compact-sm" fz={12} className={classes.tapTarget} onClick={() => setConfirming((o) => !o)}>
                {t('cancel.button')}
              </Button>
            </Popover.Target>
            <Popover.Dropdown>
              <Stack gap="xs">
                <Text fz="sm">{t('cancel.question')}</Text>
                <Group gap="xs">
                  <Button color="red" size="xs" fz={12} className={classes.tapTarget} onClick={() => void run()}>
                    {t('cancel.confirm')}
                  </Button>
                  <Button variant="default" size="xs" fz={12} className={classes.tapTarget} onClick={() => setConfirming(false)}>
                    {t('cancel.keep')}
                  </Button>
                </Group>
              </Stack>
            </Popover.Dropdown>
          </Popover>
        )}
      </Group>
      {!compact && <JobProgress job={job} />}
    </Stack>
  );
}
