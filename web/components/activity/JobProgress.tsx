'use client';

import { Progress, Stack, Text } from '@mantine/core';
import { useT } from '@/i18n';
import type { T } from '@/i18n/translator';
import type { ActivityJob } from '@/lib/activity/types';
import classes from './activity.module.css';

const slug = (s: string): string =>
  s
    .toLowerCase()
    .split(/[^a-z0-9]+/)
    .filter(Boolean)
    .map((w, i) => (i === 0 ? w : w[0].toUpperCase() + w.slice(1)))
    .join('');

/** A server stage is raw English: show it only when it maps to a translated `stages.*` key. Client stages are already ours. */
function knownStage(job: ActivityJob, t: T): string | null {
  const raw = job.liveStage;
  if (!raw) return null;
  if (job.origin === 'client') return raw;
  const key = `stages.${slug(raw)}`;
  return t.has(key) ? t(key) : null;
}

export interface JobProgressProps {
  job: ActivityJob;
  size?: 'xs' | 'sm' | 'md';
}

/** A determinate bar when progress is known, an animated one while running without it, plus the stage line. */
export function JobProgress({ job, size = 'sm' }: JobProgressProps) {
  const t = useT('activity');
  const known = job.liveProgress ?? job.progress;
  const running = job.state === 'running';
  const stage = knownStage(job, t) ?? (job.step ? t('progress.step', { order: job.step.order, count: job.step.count }) : null);
  const showBar = known != null || running;
  if (!showBar && !stage) return null;
  const pct = known != null ? Math.round(Math.min(1, Math.max(0, known)) * 100) : null;
  return (
    <Stack gap={4} className={classes.motion}>
      {showBar && (
        <Progress
          size={size}
          color="violet"
          value={pct ?? 100}
          striped={pct == null}
          animated={pct == null}
          aria-label={t('progress.label')}
          aria-valuenow={pct ?? undefined}
          data-indeterminate={pct == null ? 'true' : undefined}
        />
      )}
      {stage && (
        <Text fz={12} c="dimmed" data-testid="job-stage">
          {stage}
        </Text>
      )}
    </Stack>
  );
}
