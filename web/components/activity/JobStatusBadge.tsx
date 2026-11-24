'use client';

import { Badge, Loader } from '@mantine/core';
import { IconAlertTriangle, IconBan, IconCheck, IconClock, IconHourglass, IconEyeCheck } from '@tabler/icons-react';
import type { ReactNode } from 'react';
import { useT } from '@/i18n';
import type { ActivityJobState } from '@/lib/activity/types';
import classes from './activity.module.css';

interface StateLook {
  color: string;
  icon: ReactNode;
}

const LOOK: Record<string, StateLook> = {
  waiting: { color: 'gray', icon: <IconClock size={12} aria-hidden /> },
  estimating: { color: 'gray', icon: <IconHourglass size={12} aria-hidden /> },
  running: { color: 'violet', icon: <Loader size={10} color="violet" className={classes.motion} aria-hidden /> },
  done: { color: 'teal', icon: <IconCheck size={12} aria-hidden /> },
  failed: { color: 'red', icon: <IconAlertTriangle size={12} aria-hidden /> },
  cancelled: { color: 'gray', icon: <IconBan size={12} aria-hidden /> },
  needsReview: { color: 'yellow', icon: <IconEyeCheck size={12} aria-hidden /> },
};

export interface JobStatusBadgeProps {
  state: ActivityJobState | (string & {});
}

/** One colour, icon and label per job state, the same everywhere a job is shown. */
export function JobStatusBadge({ state }: JobStatusBadgeProps) {
  const t = useT('activity');
  const look = LOOK[state] ?? LOOK.waiting;
  return (
    <Badge component="span" variant="light" color={look.color} size="md" fz={12} tt="none" leftSection={look.icon} data-state={state}>
      {t(`states.${LOOK[state] ? state : 'waiting'}`)}
    </Badge>
  );
}
