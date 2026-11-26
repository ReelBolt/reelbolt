'use client';

import { ActionIcon, Indicator, Loader, Text } from '@mantine/core';
import { IconActivity } from '@tabler/icons-react';
import { useState } from 'react';
import { useT } from '@/i18n';
import { useActivity } from '@/lib/activity/ActivityProvider';
import classes from './activity.module.css';
import { ActivityTray } from './ActivityTray';

/** The always-visible header button: active-job count, a spinner while running, a dot for failures or a pending OK. */
export function ActivityIndicator() {
  const t = useT('activity');
  const { jobs, activeCount, failedUnseenCount, markFailuresSeen } = useActivity();
  const [opened, setOpened] = useState(false);
  const running = jobs.some((j) => j.state === 'running');
  const needsReview = jobs.filter((j) => j.state === 'needsReview').length;
  const dot = failedUnseenCount > 0 || needsReview > 0;
  const count = activeCount;
  const change = (next: boolean) => {
    setOpened(next);
    if (next) markFailuresSeen();
  };
  return (
    // The Indicator wraps the tray, not the other way round: Popover.Target injects aria-haspopup/aria-expanded into its
    // direct child, and on the Indicator's plain div those attributes are invalid (axe aria-allowed-attr). The button gets them.
    <Indicator disabled={!dot} color={failedUnseenCount > 0 ? 'red' : 'yellow'} size={10} offset={4} processing={false} data-testid="activity-dot" data-dot={dot ? 'true' : undefined}>
      <ActivityTray opened={opened} onChange={change}>
        <ActionIcon
          variant="subtle"
          color="violet"
          size="lg"
          className={classes.tapTarget}
          aria-label={t('indicator.label', { count })}
          aria-haspopup="dialog"
          aria-expanded={opened}
          onClick={() => change(!opened)}
          data-testid="activity-indicator"
          w="auto"
          px={8}
        >
          {running ? <Loader size={16} color="violet" className={classes.motion} aria-hidden /> : <IconActivity size={18} aria-hidden />}
          {count > 0 && (
            <Text component="span" fz={12} fw={600} ml={4} data-testid="activity-count">
              {count}
            </Text>
          )}
        </ActionIcon>
      </ActivityTray>
    </Indicator>
  );
}
