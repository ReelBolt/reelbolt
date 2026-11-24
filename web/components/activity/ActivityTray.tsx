'use client';

import { Button, Divider, Drawer, Group, Popover, ScrollArea, Stack, Switch, Text, type PopoverProps } from '@mantine/core';
import { useMediaQuery } from '@mantine/hooks';
import type { ReactNode } from 'react';
import { useT } from '@/i18n';
import { useActivity } from '@/lib/activity/ActivityProvider';
import { useActivityToasts } from '@/lib/activity/notifier';
import type { ActivityJob } from '@/lib/activity/types';
import classes from './activity.module.css';
import { ActivityRow } from './ActivityRow';

const RUNNING = new Set(['running', 'estimating', 'waiting']);
const FINISHED = new Set(['done', 'failed', 'cancelled']);

export interface ActivityTrayProps {
  opened: boolean;
  onChange: (opened: boolean) => void;
  /** The element that opens the tray (the indicator button). */
  children: ReactNode;
  position?: PopoverProps['position'];
}

function Section({ title, jobs, action, onOpen, children }: { title: string; jobs: ActivityJob[]; action?: ReactNode; onOpen: () => void; children?: (job: ActivityJob) => ReactNode }) {
  if (jobs.length === 0) return null;
  return (
    <Stack gap="sm" component="section" aria-label={title}>
      <Group justify="space-between" wrap="nowrap">
        <Text fz="sm" fw={700}>
          {title}
        </Text>
        {action}
      </Group>
      {jobs.map((job) => (
        <Stack key={job.id} gap="xs">
          <ActivityRow job={job} onOpen={onOpen} />
          {children?.(job)}
        </Stack>
      ))}
    </Stack>
  );
}

function TrayBody({ onClose }: { onClose: () => void }) {
  const t = useT('activity');
  const { jobs, clearFinished, resolveTrackedJob } = useActivity();
  const [toasts, setToasts] = useActivityToasts();
  const review = jobs.filter((j) => j.state === 'needsReview');
  const running = jobs.filter((j) => RUNNING.has(j.state));
  const finished = jobs.filter((j) => FINISHED.has(j.state));
  const empty = review.length + running.length + finished.length === 0;
  return (
    <Stack gap="md">
      <ScrollArea.Autosize mah={420} type="auto">
        <Stack gap="md" pr="xs">
          {empty && <Text fz="sm" c="dimmed">{t('tray.empty')}</Text>}
          <Section title={t('tray.needsReview')} jobs={review} onOpen={onClose}>
            {(job) => (
              <Group gap="xs">
                <Button size="xs" fz={12} className={classes.tapTarget} onClick={() => void resolveTrackedJob(job.id, 'applied')} data-testid="review-apply">
                  {t('tray.apply')}
                </Button>
                <Button size="xs" fz={12} variant="default" className={classes.tapTarget} onClick={() => void resolveTrackedJob(job.id, 'discarded')} data-testid="review-discard">
                  {t('tray.discard')}
                </Button>
              </Group>
            )}
          </Section>
          <Section title={t('tray.running')} jobs={running} onOpen={onClose} />
          <Section
            title={t('tray.finished')}
            jobs={finished}
            onOpen={onClose}
            action={
              <Button variant="subtle" size="compact-sm" fz={12} className={classes.tapTarget} onClick={clearFinished}>
                {t('tray.clear')}
              </Button>
            }
          />
        </Stack>
      </ScrollArea.Autosize>
      <Divider />
      <Switch checked={toasts} onChange={(e) => setToasts(e.currentTarget.checked)} label={t('toast.switchLabel')} color="violet" />
    </Stack>
  );
}

/** The tray: a popover on desktop and tablet, a bottom drawer on a phone. Needs-OK items come first. */
export function ActivityTray({ opened, onChange, children, position = 'bottom-end' }: ActivityTrayProps) {
  const t = useT('activity');
  const phone = useMediaQuery('(max-width: 47.99em)');
  if (phone) {
    return (
      <>
        {children}
        <Drawer opened={opened} onClose={() => onChange(false)} position="bottom" title={t('tray.title')} size="80%" zIndex={300}>
          <TrayBody onClose={() => onChange(false)} />
        </Drawer>
      </>
    );
  }
  return (
    <Popover opened={opened} onChange={onChange} width={380} position={position} shadow="md" withArrow trapFocus>
      <Popover.Target>{children}</Popover.Target>
      <Popover.Dropdown aria-label={t('tray.title')}>
        <TrayBody onClose={() => onChange(false)} />
      </Popover.Dropdown>
    </Popover>
  );
}
